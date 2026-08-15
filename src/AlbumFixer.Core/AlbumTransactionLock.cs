namespace AlbumFixer.Core;

/// <summary>
/// Serializes transactions for one album across workers and application processes.
/// The lock file is harmless if a process exits unexpectedly; ownership is the open,
/// non-shared handle rather than the continued existence of the file.
/// </summary>
public sealed class AlbumTransactionLock : IAsyncDisposable
{
    public const string FileName = ".album-fixer.transaction.lock";

    private readonly string _path;
    private FileStream? _stream;

    private AlbumTransactionLock(string path, FileStream stream)
    {
        _path = path;
        _stream = stream;
    }

    public static async Task<AlbumTransactionLock> AcquireAsync(
        string albumRoot,
        CancellationToken token = default)
    {
        var root = Path.GetFullPath(albumRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var path = HostStagingService.SafeCombine(root, FileName);

        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 4096, FileOptions.Asynchronous);
                return new(path, stream);
            }
            catch (IOException error) when (IsSharingViolation(error))
            {
                await Task.Delay(250, token).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is null) return;
        var stream = _stream;
        _stream = null;
        await stream.DisposeAsync().ConfigureAwait(false);
        try { File.Delete(_path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsSharingViolation(IOException error) =>
        (error.HResult & 0xFFFF) is 32 or 33;
}
