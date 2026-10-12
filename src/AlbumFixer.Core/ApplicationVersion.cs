using System.Reflection;

namespace AlbumFixer.Core;

/// <summary>The version that builds stamp on Album Fixer, as the window title, the log and every conversion report show it.</summary>
public static class ApplicationVersion
{
    /// <summary>The informational version without the commit hash the SDK appends, for example "0.1.0-dev" or "0.1.0-ci.42".</summary>
    public static string Current { get; } = Of(typeof(ApplicationVersion).Assembly);

    public static string Of(Assembly assembly)
    {
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
