namespace RazorGraph.Mcp;

/// <summary>
/// Which build directory this process is running from, resolved once at startup, and what the
/// directory it was launched through resolves to now.
/// </summary>
/// <remarks>
/// The two differ during a rotation. Update-McpServer.ps1 repoints the 'current' junction
/// before it stops the old process, so for a few seconds the old process is still answering
/// while the junction already names the new build. Resolving per call made server_info in that
/// window report the new build beside the old process's pid and uptime (observed 2026-11-23).
/// The junction is followed once, at startup, when it still names the build this process
/// loaded; later readings of it are reported separately, never in its place.
/// </remarks>
internal sealed class BuildLocation
{
    private static BuildLocation? captured;

    internal BuildLocation(string launchedFrom)
    {
        LaunchedFrom = launchedFrom.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Build = Resolve(LaunchedFrom);
    }

    /// <summary>This process's, as the entry point captured it; captured at first use in a host that never called Capture.</summary>
    internal static BuildLocation Running => captured ?? Capture();

    /// <summary>The directory as launched: the junction itself, when started through the rotation.</summary>
    internal string LaunchedFrom { get; }

    /// <summary>The real directory the launched path resolved to when this was captured.</summary>
    internal string Build { get; }

    /// <summary>
    /// Resolves this process's build directory from AppContext.BaseDirectory. Called first thing
    /// by the entry point, before a rotation can have moved the junction; later calls return the
    /// first capture.
    /// </summary>
    internal static BuildLocation Capture() => captured ??= new BuildLocation(AppContext.BaseDirectory);

    /// <summary>What the launched path resolves to now, or null while it still resolves to <see cref="Build"/>.</summary>
    internal string? CurrentNow()
    {
        var now = Resolve(LaunchedFrom);
        return string.Equals(now, Build, StringComparison.OrdinalIgnoreCase) ? null : now;
    }

    /// <summary>The real directory behind a path, following it if it is a junction.</summary>
    /// <remarks>
    /// ResolveLinkTarget returns null for an ordinary directory, which is the answer when the
    /// server was started directly out of a build output rather than through the rotation -- so
    /// falling back to the path itself is correct rather than a failure case.
    /// </remarks>
    private static string Resolve(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? directory;
        }
        catch (IOException)
        {
            return directory;
        }
    }
}
