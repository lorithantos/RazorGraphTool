namespace RazorGraph.Extractor.Roslyn;

using System.Diagnostics;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

/// <summary>
/// Guards the one failure this extractor is structurally blind to: an SDK
/// source generator that will not load into our Roslyn.
///
/// The SDK builds its generators against the Roslyn it ships. Load one into an
/// older Roslyn and it fails — and AnalyzerFileReference.GetGenerators SWALLOWS
/// that: it raises AnalyzerLoadFailed and hands back an empty list. Nothing
/// throws, nothing is logged, and the compilation simply comes back without the
/// trees that generator would have produced. Every downstream pass then works
/// correctly over code that is quietly incomplete, which is the worst shape a
/// bug can take in a tool whose whole product is "what is in this codebase".
///
/// Measured, not hypothetical: pinned at Roslyn 5.6 against an SDK shipping 5.9,
/// Microsoft.CodeAnalysis.Razor.Compiler.dll contributed zero generators, so no
/// Razor page class ever reached the graph. Twelve other generators loaded fine,
/// which is exactly why it was invisible — the graph looked populated.
///
/// Two checks, because they fail at different times. The version comparison is
/// predictive: it fires before anything is missing and names the fix. The
/// load-failure hook is detective: it catches the real event whatever the cause,
/// including causes no version comparison would predict. Neither throws — a
/// partial graph still answers most questions, and refusing to build would be a
/// worse trade than saying loudly what is missing.
/// </summary>
internal static class AnalyzerHostCheck
{
    /// <summary>
    /// The Roslyn this process actually loaded, by file version — the metric
    /// that tracks a Roslyn release. Assembly version is deliberately not used:
    /// it does not always move between minor releases, so it can report two
    /// incompatible builds as identical.
    /// </summary>
    internal static Version? HostRoslynVersion() => FileVersionOf(typeof(Compilation).Assembly.Location);

    /// <summary>
    /// The Roslyn the located SDK ships, or null when the layout is not the one
    /// we know. Null is "cannot tell", never "compatible" — see VersionWarning,
    /// which stays silent rather than guessing.
    /// </summary>
    internal static Version? SdkRoslynVersion(string? msbuildPath) =>
        string.IsNullOrEmpty(msbuildPath)
            ? null
            : FileVersionOf(Path.Combine(msbuildPath, "Roslyn", "bincore", "Microsoft.CodeAnalysis.dll"));

    private static Version? FileVersionOf(string? path)
    {
        // Location is empty under single-file publish, and the SDK path is a
        // guess about someone else's install layout. Both are "cannot tell".
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var raw = FileVersionInfo.GetVersionInfo(path).FileVersion;
            return Version.TryParse(raw, out var version) ? version : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The warning to report when our Roslyn is older than the SDK's, or null
    /// when it is equal, newer, or unknown.
    /// </summary>
    /// <remarks>
    /// Older-only on purpose. A host newer than the SDK loads the SDK's
    /// generators fine — that is the direction Roslyn supports — so warning on
    /// any difference would cry wolf on every machine whose SDK trails the
    /// package feed, and a warning that is usually noise stops being read.
    ///
    /// Compared at the RELEASE LINE, not the full file version. Roslyn's
    /// compatibility boundary is the release; the build number moves with
    /// servicing and carries no such meaning. Measured: package 5.9.0 is
    /// 5.900.26.35703 while SDK 10.0.400 carries 5.900.26.38015, and the older
    /// build loads every one of that SDK's generators. A strict four-part
    /// comparison called that a mismatch — the first thing this check did was
    /// cry wolf about a working configuration.
    ///
    /// Note the encoding: Roslyn puts minor and patch together in the second
    /// field, so 5.6.0 is 5.600 and 5.9.0 is 5.900. Taking two fields is
    /// therefore the whole release identity, not just its major.
    /// </remarks>
    internal static string? VersionWarning(Version? host, Version? sdk) =>
        host is not null && sdk is not null && ReleaseLine(host) < ReleaseLine(sdk)
            ? $"Roslyn {host} is older than the {sdk} the installed .NET SDK ships. "
              + "SDK source generators are built against the SDK's Roslyn and fail to load into an older host — "
              + "silently, as an empty generator list — so this graph may be missing every type they generate "
              + "(Razor page classes above all). Raise the Microsoft.CodeAnalysis.* package versions in "
              + "RazorGraph.Extractor.csproj to at least " + sdk.ToString(2) + "."
            : null;

    private static Version ReleaseLine(Version v) => new(v.Major, v.Minor);

    /// <summary>
    /// Subscribe to the load failures of one project's analyzer references, so
    /// a generator that declines to load is reported instead of vanishing.
    /// Must run before the compilation is requested — that is what triggers the
    /// load, and the event fires once per reference.
    /// </summary>
    internal static void WatchLoadFailures(Project project, List<string> sink)
    {
        foreach (var reference in project.AnalyzerReferences.OfType<AnalyzerFileReference>())
        {
            reference.AnalyzerLoadFailed += (sender, e) =>
            {
                var file = Path.GetFileName((sender as AnalyzerFileReference)?.FullPath ?? "<unknown>");
                lock (sink)
                {
                    sink.Add($"{project.Name}: analyzer '{file}' failed to load ({e.ErrorCode})"
                        + $" — {e.Message}"
                        + (e.Exception is { } ex ? $" [{ex.GetType().Name}: {ex.Message}]" : "")
                        + ". Any type it generates is absent from this graph.");
                }
            };
        }
    }
}
