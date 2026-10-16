namespace RazorGraph.Extractor;

using System.Collections.Frozen;
using System.Text;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;

/// <summary>
/// Emits one TextFile node per non-C# text file in the source tree -- Bicep, JSON,
/// Markdown, PowerShell, project files, YAML -- carrying its lines, so grep's graph
/// section answers for those files instead of leaving them all to the disk.
/// </summary>
/// <remarks>
/// The files are the ones grep's disk section reads, listed by the same code
/// (<see cref="SourceFiles"/>): git's own list in a working tree, a walk that skips
/// vendor and build output otherwise, binaries and files over 2 MB turned away. Lines
/// are read the way the disk reads them, so a graph hit and a disk hit for the same
/// line carry the same number and the disk's dedupe can recognise it.
/// </remarks>
internal sealed class TextFileEmitter(CodeGraph graph)
{
    internal const string IdPrefix = "text:";

    // Files the graph already models from source. Holding their lines as well would
    // put every C# line in the graph twice, once as structure and once as text.
    private static readonly FrozenSet<string> ModelledExtensions = new[]
    {
        ".cs", ".cshtml", ".razor", ".xaml"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// What the population left out and why, one line per reason. Empty when nothing
    /// was skipped. A skip must reach the build report: a file missing from the graph
    /// without a word reads as a file with nothing in it.
    /// </summary>
    internal IReadOnlyList<string> SkipSummaries => _skipSummaries;
    private readonly List<string> _skipSummaries = new();

    /// <summary>
    /// Add a TextFile node for every candidate under <paramref name="sourceRoot"/>.
    /// </summary>
    /// <param name="sourceRoot">The solution's or project's directory; ids are relative to it.</param>
    /// <param name="alreadyFiles">
    /// Full paths the graph already holds as file nodes (JavaScriptFile, CssFile) or that
    /// the client-asset vendor policy dropped; neither becomes a TextFile.
    /// </param>
    internal void AddTextFiles(string sourceRoot, IReadOnlySet<string> alreadyFiles)
    {
        var root = TextSearch.NormaliseFolder(sourceRoot);
        var (candidates, _) = SourceFiles.List(root);

        var binary = new List<string>();
        var tooLarge = new List<string>();
        var unreadable = new List<string>();
        var vendor = 0;

        foreach (var relative in candidates.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (ModelledExtensions.Contains(Path.GetExtension(relative))) continue;

            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (alreadyFiles.Contains(full)) continue;

            // Generated and third-party text the client-asset policy would drop if it
            // were a script: source maps and minified bundles beside it, a package's
            // README and LICENSE under lib/. A git tree lists them when they are
            // committed, which is exactly the case wwwroot/lib usually is.
            if (IsVendorOrGenerated(relative)) { vendor++; continue; }

            switch (SourceFiles.Probe(full))
            {
                case SourceFiles.Verdict.Binary: binary.Add(Normalise(relative)); continue;
                case SourceFiles.Verdict.TooLarge: tooLarge.Add(Normalise(relative)); continue;
                case SourceFiles.Verdict.Unreadable: unreadable.Add(Normalise(relative)); continue;
            }

            if (ReadLines(full) is not { } lines) { unreadable.Add(Normalise(relative)); continue; }
            AddNode(relative, full, lines);
        }

        ReportSkips(binary, tooLarge, unreadable, vendor);
    }

    private void AddNode(string relative, string full, List<string> lines)
    {
        var node = new GraphNode
        {
            Id = IdPrefix + Normalise(relative),
            Type = NodeType.TextFile,
            Name = Path.GetFileName(relative),
            FilePath = full,
            LineStart = 1,
            LineEnd = lines.Count
        };

        node.SetProperty("extension", Path.GetExtension(relative));
        // Int, not long: every file here is under 2 MB, and a long would come back from
        // a saved graph as an int anyway, breaking a typed read either side of the save.
        node.SetProperty("sizeBytes", (int)new FileInfo(full).Length);
        node.SetProperty(TextSearch.TextFileLinesProperty, lines);

        graph.AddNode(node);
    }

    /// <summary>
    /// The file's lines as the disk section reads them -- BOM-detected UTF-8, split on
    /// any line terminator -- so line N here is line N there. Null when it cannot be read.
    /// </summary>
    private static List<string>? ReadLines(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsVendorOrGenerated(string relative)
    {
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (SourceFiles.VendorDirNames.Contains(segments[i]) || SourceFiles.BuildOutputDirNames.Contains(segments[i]))
                return true;
        }

        // The client-asset extractor's minified rule, applied to every file name, so a
        // bundle's .min.css.map goes where its .min.css went.
        return segments[^1].Contains(".min.", StringComparison.OrdinalIgnoreCase);
    }

    private void ReportSkips(List<string> binary, List<string> tooLarge, List<string> unreadable, int vendor)
    {
        const int Examples = 10;

        if (binary.Count > 0)
        {
            var more = binary.Count > Examples ? $" (+{binary.Count - Examples} more)" : string.Empty;
            _skipSummaries.Add(
                $"{binary.Count} binary file(s) not graphed as text (a NUL in the first 8 KB): " +
                $"{string.Join(", ", binary.Take(Examples))}{more}");
        }

        // Named in full: an oversized file is rare, and is the skip most likely to be
        // text someone will search for.
        if (tooLarge.Count > 0)
            _skipSummaries.Add(
                $"{tooLarge.Count} file(s) over {SourceFiles.MaxFileBytes / (1024 * 1024)} MB not graphed as text: " +
                string.Join(", ", tooLarge));

        if (unreadable.Count > 0)
            _skipSummaries.Add(
                $"{unreadable.Count} file(s) listed but missing or unreadable (git still lists a file deleted " +
                $"from the working tree): {string.Join(", ", unreadable)}");

        if (vendor > 0)
            _skipSummaries.Add(
                $"{vendor} file(s) under vendor or build-output directories, or minified, not graphed as text.");
    }

    private static string Normalise(string relative) => relative.Replace('\\', '/');
}
