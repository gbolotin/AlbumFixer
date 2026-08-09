namespace AlbumFixer.Core;

public sealed record BatchPipelineLimits(
    int MaxInFlight,
    int CopyInWorkers,
    int ProcessingWorkers,
    int DsdProcessingWorkers,
    int CopyBackWorkers)
{
    private const long GiB = 1024L * 1024 * 1024;

    public static BatchPipelineLimits None { get; } = new(0, 0, 0, 0, 0);

    public string Description =>
        $"up to {MaxInFlight} active; {CopyInWorkers} NAS read, {ProcessingWorkers} local processing " +
        $"({DsdProcessingWorkers} SACD), and {CopyBackWorkers} NAS write lane{(CopyBackWorkers == 1 ? "" : "s")}";

    public static BatchPipelineLimits Recommend(
        IReadOnlyList<long> requiredBytes,
        long availableStagingBytes,
        int logicalProcessors,
        long availableMemoryBytes)
    {
        ArgumentNullException.ThrowIfNull(requiredBytes);
        if (requiredBytes.Count == 0) return None;

        logicalProcessors = Math.Max(1, logicalProcessors);
        availableMemoryBytes = Math.Max(0, availableMemoryBytes);
        var processingWorkers = logicalProcessors >= 12 && availableMemoryBytes >= 24 * GiB ? 4
            : logicalProcessors >= 8 && availableMemoryBytes >= 16 * GiB ? 3
            : logicalProcessors >= 4 && availableMemoryBytes >= 8 * GiB ? 2
            : 1;
        var networkWorkers = availableMemoryBytes >= 8 * GiB && requiredBytes.Count > 1 ? 2 : 1;
        var hardwareInFlight = Math.Min(requiredBytes.Count, processingWorkers + networkWorkers);

        // Keep a 20% free-space reserve while the bounded pipeline overlaps stages.
        var stagingBudget = availableStagingBytes <= 0 ? 0 : availableStagingBytes - availableStagingBytes / 5;
        long reserved = 0;
        var capacityInFlight = 0;
        foreach (var requirement in requiredBytes.OrderByDescending(bytes => bytes).Take(hardwareInFlight))
        {
            var positive = Math.Max(0, requirement);
            if (capacityInFlight > 0 && (reserved > stagingBudget - Math.Min(stagingBudget, positive))) break;
            if (positive > stagingBudget && capacityInFlight == 0)
            {
                capacityInFlight = 1;
                break;
            }
            reserved += positive;
            capacityInFlight++;
        }

        var maxInFlight = Math.Max(1, Math.Min(hardwareInFlight, capacityInFlight));
        var copyIn = Math.Min(networkWorkers, maxInFlight);
        var processing = Math.Min(processingWorkers, maxInFlight);
        var copyBack = Math.Min(networkWorkers, maxInFlight);
        var dsd = Math.Min(2, processing);
        return new(maxInFlight, copyIn, processing, dsd, copyBack);
    }
}

public sealed record BatchPipelineTelemetry(
    int MaximumCopyIn,
    int MaximumProcessing,
    int MaximumDsdProcessing,
    int MaximumCopyBack);

public sealed class BatchPipelineScheduler : IDisposable
{
    private readonly GateState _copyIn;
    private readonly GateState _processing;
    private readonly GateState _dsdProcessing;
    private readonly GateState _copyBack;

    public BatchPipelineScheduler(BatchPipelineLimits limits)
    {
        if (limits.MaxInFlight < 1 || limits.CopyInWorkers < 1 || limits.ProcessingWorkers < 1 ||
            limits.DsdProcessingWorkers < 1 || limits.CopyBackWorkers < 1)
            throw new ArgumentOutOfRangeException(nameof(limits), "Pipeline worker limits must all be positive.");
        if (limits.DsdProcessingWorkers > limits.ProcessingWorkers)
            throw new ArgumentException("The SACD worker limit cannot exceed the total processing limit.", nameof(limits));
        Limits = limits;
        _copyIn = new(limits.CopyInWorkers);
        _processing = new(limits.ProcessingWorkers);
        _dsdProcessing = new(limits.DsdProcessingWorkers);
        _copyBack = new(limits.CopyBackWorkers);
    }

    public BatchPipelineLimits Limits { get; }
    public BatchPipelineTelemetry Telemetry => new(
        _copyIn.Maximum,
        _processing.Maximum,
        _dsdProcessing.Maximum,
        _copyBack.Maximum);

    public Task<T> RunCopyInAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token = default) =>
        RunWithGateAsync(_copyIn, action, token);

    public Task<T> RunCopyBackAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token = default) =>
        RunWithGateAsync(_copyBack, action, token);

    public async Task<T> RunProcessingAsync<T>(bool isDsd, Func<CancellationToken, Task<T>> action, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var enteredDsd = false;
        var enteredProcessing = false;
        try
        {
            // Take the narrower SACD gate first so waiting SACD jobs do not occupy
            // general processing slots that ready FLAC jobs can use.
            if (isDsd)
            {
                await _dsdProcessing.Gate.WaitAsync(token);
                enteredDsd = true;
            }
            await _processing.Gate.WaitAsync(token);
            enteredProcessing = true;
            _processing.Enter();
            if (isDsd) _dsdProcessing.Enter();
            return await action(token);
        }
        finally
        {
            if (enteredProcessing)
            {
                if (isDsd) _dsdProcessing.Exit();
                _processing.Exit();
                _processing.Gate.Release();
            }
            if (enteredDsd) _dsdProcessing.Gate.Release();
        }
    }

    private static async Task<T> RunWithGateAsync<T>(
        GateState state,
        Func<CancellationToken, Task<T>> action,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(action);
        await state.Gate.WaitAsync(token);
        try
        {
            state.Enter();
            return await action(token);
        }
        finally
        {
            state.Exit();
            state.Gate.Release();
        }
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        var observed = Volatile.Read(ref maximum);
        while (value > observed)
        {
            var prior = Interlocked.CompareExchange(ref maximum, value, observed);
            if (prior == observed) return;
            observed = prior;
        }
    }

    public void Dispose()
    {
        _copyIn.Gate.Dispose();
        _processing.Gate.Dispose();
        _dsdProcessing.Gate.Dispose();
        _copyBack.Gate.Dispose();
    }

    private sealed class GateState(int limit)
    {
        private int _active;
        private int _maximum;
        public SemaphoreSlim Gate { get; } = new(limit);
        public int Maximum => Volatile.Read(ref _maximum);
        public void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _maximum, active);
        }
        public void Exit() => Interlocked.Decrement(ref _active);
    }
}

public sealed record BatchItemResult<TItem, TResult>(
    int Index,
    TItem Item,
    TResult? Value,
    Exception? Error)
{
    public bool Succeeded => Error is null;
    public bool Canceled => Error is OperationCanceledException;
}

public static class BoundedBatchProcessor
{
    public const int DefaultMaxParallelism = 2;

    public static async Task<IReadOnlyList<BatchItemResult<TItem, TResult>>> RunAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        Func<TItem, int, CancellationToken, Task<TResult>> process,
        int maxParallelism = DefaultMaxParallelism,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(process);
        if (maxParallelism < 1) throw new ArgumentOutOfRangeException(nameof(maxParallelism));
        if (items.Count == 0) return [];

        using var gate = new SemaphoreSlim(Math.Min(maxParallelism, items.Count));
        var tasks = items.Select((item, index) => RunItemAsync(item, index, process, gate, token)).ToArray();
        var results = await Task.WhenAll(tasks);
        return results.OrderBy(result => result.Index).ToArray();
    }

    private static async Task<BatchItemResult<TItem, TResult>> RunItemAsync<TItem, TResult>(
        TItem item,
        int index,
        Func<TItem, int, CancellationToken, Task<TResult>> process,
        SemaphoreSlim gate,
        CancellationToken token)
    {
        var entered = false;
        try
        {
            await gate.WaitAsync(token);
            entered = true;
            token.ThrowIfCancellationRequested();
            var value = await process(item, index, token);
            return new(index, item, value, null);
        }
        catch (Exception error)
        {
            return new(index, item, default, error);
        }
        finally
        {
            if (entered) gate.Release();
        }
    }
}
