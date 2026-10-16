namespace RazorGraph.Core.Query;

using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using RazorGraph.Core.Graph;

/// <summary>
/// A regex search scoped to one folder, in two parts: the graph's names, literal values
/// and text-file lines when a graph covers the folder, and the files on disk always,
/// less the lines the graph already reported.
/// </summary>
/// <remarks>
/// Public because the MCP server is a different assembly; the server owns WHICH graph
/// answers, since only it holds the loaded set, and everything that decides what a
/// match is lives here. In C# the graph sees declared names and literal values and
/// nothing else -- no comments, no code text -- so a graph answer is never the whole
/// answer, which is why the disk always runs as well. A non-C# text file is different:
/// its TextFile node carries every line as the build read it, so the graph answers for
/// it whole -- unless the file has been written since, when those lines may no longer
/// be true and the disk answers for it instead.
/// </remarks>
public static class TextSearch
{
    /// <summary>Longest text a hit carries: a minified line is one line, and a model pays for every character of it.</summary>
    public const int TextCap = 200;

    /// <summary>Files larger than this are skipped rather than scanned; generated dumps are not what a text search is for.</summary>
    public const long MaxFileBytes = SourceFiles.MaxFileBytes;

    // A pathological pattern on one long line must cost a second, not the call.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The pattern as both tiers run it: culture-invariant, optionally case-blind, with
    /// a one-second match timeout. Throws <see cref="ArgumentException"/> when it is not
    /// a valid .NET regular expression.
    /// </summary>
    public static Regex Compile(string pattern, bool ignoreCase) =>
        new(pattern,
            RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None),
            MatchTimeout);

    /// <summary>
    /// An absolute folder in the one form every comparison here uses: full, and with no
    /// trailing separator, so "C:\a\" and "C:\a" are the same folder.
    /// </summary>
    public static string NormaliseFolder(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    /// <summary>
    /// Whether <paramref name="path"/> lies at or under <paramref name="folder"/>, which
    /// must already be normalised. A separator boundary is required, so C:\a\b does not
    /// contain C:\a\bc. A relative path is never under anything: resolving it against
    /// the server's working directory would invent a location.
    /// </summary>
    public static bool IsUnder(string? path, string folder)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return false;

        var full = Path.GetFullPath(path);
        if (!full.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;

        return full.Length == folder.Length
            || full[folder.Length] == Path.DirectorySeparatorChar
            || full[folder.Length] == Path.AltDirectorySeparatorChar
            // A drive root normalises to "C:\" and keeps its separator.
            || Path.EndsInDirectorySeparator(folder);
    }

    /// <summary>Whether any node in the graph has its file under the folder.</summary>
    public static bool Covers(CodeGraph graph, string folder) =>
        graph.Nodes.Any(n => IsUnder(n.FilePath, folder));

    /// <summary>
    /// One graph-tier hit. <paramref name="MatchedOn"/> is "name", "qualifiedName",
    /// "literal" or "text". For a literal, the node is the Literal and
    /// <paramref name="FromId"/> is the member whose code produces it -- the path and line
    /// are that member's, because a Literal node is one per distinct value and has no file
    /// of its own. For text, the node is a TextFile, the line is the matching line's, and
    /// <paramref name="Text"/> is that line trimmed and capped as a disk hit's is.
    /// </summary>
    public sealed record GraphHit(
        string NodeId, string NodeType, string Name, string MatchedOn, string Path, int? Line, string? FromId,
        string? Text = null);

    /// <summary>
    /// Every graph hit in a total order, how many names timed out rather than answered,
    /// and how many TextFile nodes under the folder were left to the disk because their
    /// file has been written since the build.
    /// </summary>
    public sealed record GraphResult(IReadOnlyList<GraphHit> Hits, int RegexTimeouts, int StaleTextFiles = 0);

    /// <summary>
    /// Nodes under the folder whose name or qualified name matches, literal values that
    /// match at a site under the folder, and matching lines of the text files the graph
    /// holds -- except a text file written since the build, which is counted in
    /// <see cref="GraphResult.StaleTextFiles"/> and left for the disk to answer.
    /// </summary>
    /// <param name="glob">Optional wildcard on the hit's file NAME, the same filter the disk applies.</param>
    public static GraphResult SearchGraph(CodeGraph graph, string folder, Regex regex, string? glob = null)
    {
        var timeouts = 0;
        bool Matches(string? text)
        {
            if (text is null) return false;
            try { return regex.IsMatch(text); }
            catch (RegexMatchTimeoutException) { timeouts++; return false; }
        }

        var hits = new List<GraphHit>();
        var freshness = new GraphFreshness(graph);
        var staleTextFiles = 0;

        foreach (var node in graph.Nodes)
        {
            if (!IsUnder(node.FilePath, folder) || !GlobMatches(glob, node.FilePath!)) continue;

            if (node.Type == NodeType.TextFile)
            {
                // The same probe behind get_node's fileWrittenSince. Anything but a
                // definite "not written" falls to the disk: lines read at build time are
                // only an answer while the file still holds them, and "cannot tell" is
                // not that.
                if (freshness.WrittenSinceBuild(node) != false) { staleTextFiles++; continue; }

                if (!SearchTextLines(node, Path.GetRelativePath(folder, node.FilePath!), regex, hits)) timeouts++;
                continue;
            }

            var matchedOn = Matches(node.Name) ? "name"
                : Matches(QualifiedName(node)) ? "qualifiedName"
                : null;
            if (matchedOn is null) continue;

            hits.Add(new GraphHit(
                node.Id, node.DisplayType, node.Name, matchedOn,
                Path.GetRelativePath(folder, node.FilePath!), node.LineStart, FromId: null));
        }

        foreach (var (literal, _, site) in new GraphQuery(graph).LiteralSites(Matches))
        {
            if (!IsUnder(site.File, folder) || !GlobMatches(glob, site.File!)) continue;

            hits.Add(new GraphHit(
                literal.Id, literal.DisplayType, Cap(site.Value), "literal",
                Path.GetRelativePath(folder, site.File!), site.Line, site.FromId));
        }

        // Total, because the caller pages by offset: path and line are what a reader
        // wants grouped, and the node id and match kind close every tie.
        var ordered = hits
            .OrderBy(h => h.Path, StringComparer.Ordinal)
            .ThenBy(h => h.Line ?? 0)
            .ThenBy(h => h.NodeId, StringComparer.Ordinal)
            .ThenBy(h => h.MatchedOn, StringComparer.Ordinal)
            .ThenBy(h => h.FromId, StringComparer.Ordinal)
            .ToList();

        return new GraphResult(ordered, timeouts, staleTextFiles);
    }

    /// <summary>
    /// One hit per matching line of a TextFile node, numbered as the disk numbers them.
    /// A TextFile is matched on its lines alone: its name is the file's name, which a
    /// line search never reports, and find_nodes is where a file is found by name.
    /// False when the pattern timed out, in which case the rest of the file is abandoned
    /// as the disk abandons one: a timeout predicts the next on the same content.
    /// </summary>
    private static bool SearchTextLines(GraphNode node, string relativePath, Regex regex, List<GraphHit> hits)
    {
        if (node.GetProperty<List<string>>(TextFileLinesProperty) is not { } lines) return true;

        for (var i = 0; i < lines.Count; i++)
        {
            try
            {
                if (!regex.IsMatch(lines[i])) continue;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }

            hits.Add(new GraphHit(
                node.Id, node.DisplayType, node.Name, "text", relativePath, i + 1, FromId: null,
                Text: Cap(lines[i].Trim())));
        }

        return true;
    }

    /// <summary>The TextFile property holding the file's lines, one string per line, in order.</summary>
    public const string TextFileLinesProperty = "lines";

    // fullName is what a type carries; a member carries its declaring type instead.
    private static string? QualifiedName(GraphNode node) =>
        node.GetProperty<string>("fullName")
        ?? (node.GetProperty<string>("declaringType") is { } declaring ? $"{declaring}.{node.Name}" : null);

    /// <summary>One matching line on disk, its path relative to the searched folder.</summary>
    public sealed record DiskHit(string Path, int Line, string Text);

    /// <summary>
    /// One page of disk hits. <paramref name="TotalMatches"/> counts every matching line
    /// in every scanned file, not only the page, and not the lines the graph already
    /// reported -- those are <paramref name="AlreadyInGraph"/>. <paramref name="IgnoreSource"/>
    /// says what decided which files were candidates. <paramref name="TimedOutFiles"/> are
    /// files the pattern timed out on; the rest of each was abandoned, so their matches are missing.
    /// </summary>
    public sealed record DiskResult(
        IReadOnlyList<DiskHit> Hits, int TotalMatches, int Offset, int? NextOffset,
        int FilesScanned, int FilesSkipped, string IgnoreSource, IReadOnlyList<string> TimedOutFiles,
        int AlreadyInGraph);

    /// <summary>
    /// Every line under the folder that matches, one hit per line, paged. Only the page
    /// is kept; every match is still counted.
    /// </summary>
    /// <param name="glob">Optional wildcard (* and ?) on the file NAME, case-insensitive.</param>
    /// <param name="alreadyFound">
    /// Graph hits to leave out: a line at the same relative path and line number as any of
    /// them is counted in <see cref="DiskResult.AlreadyInGraph"/> instead of returned. Pass
    /// the WHOLE graph result, not a page of it, or the next graph page reappears here.
    /// </param>
    public static DiskResult SearchDisk(
        string folder, Regex regex, string? glob, int limit, int offset, IEnumerable<GraphHit>? alreadyFound = null)
    {
        var (candidates, ignoreSource) = SourceFiles.List(folder);

        // Same case rule as IsUnder, and one separator, so a git path and a graph path agree.
        var found = new HashSet<string>(
            (alreadyFound ?? []).Where(h => h.Line is not null).Select(h => SiteKey(h.Path, h.Line!.Value)),
            StringComparer.OrdinalIgnoreCase);

        var from = Math.Max(0, offset);
        var take = Math.Clamp(limit, 1, 1000);
        var page = new List<DiskHit>();
        var timedOut = new List<string>();
        int total = 0, scanned = 0, skipped = 0, inGraph = 0;

        // Ordinal on the relative path, then ascending line: the order an offset needs.
        foreach (var relative in candidates.OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!GlobMatches(glob, relative)) continue;

            var full = Path.Combine(folder, relative);
            if (SourceFiles.Probe(full) != SourceFiles.Verdict.Text) { skipped++; continue; }

            try
            {
                using var reader = new StreamReader(full, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var lineNumber = 0;
                while (reader.ReadLine() is { } line)
                {
                    lineNumber++;
                    bool isMatch;
                    try { isMatch = regex.IsMatch(line); }
                    catch (RegexMatchTimeoutException)
                    {
                        // One timeout predicts the next on the same content; a second
                        // per line over a large file is the hang this exists to stop.
                        timedOut.Add(relative);
                        break;
                    }
                    if (!isMatch) continue;
                    if (found.Contains(SiteKey(relative, lineNumber))) { inGraph++; continue; }

                    if (total >= from && page.Count < take)
                        page.Add(new DiskHit(relative, lineNumber, Cap(line.Trim())));
                    total++;
                }
                scanned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        var end = from + page.Count;
        return new DiskResult(
            page, total, Math.Min(from, total), end < total ? end : null,
            scanned, skipped, ignoreSource, timedOut, inGraph);
    }

    private static string SiteKey(string relativePath, int line) =>
        $"{line}|{relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)}";

    // No glob passes everything; a glob is matched against the file NAME only.
    private static bool GlobMatches(string? glob, string path) =>
        string.IsNullOrEmpty(glob)
        || FileSystemName.MatchesSimpleExpression(glob, Path.GetFileName(path), ignoreCase: true);

    private static string Cap(string text) => text.Length <= TextCap ? text : text[..TextCap];
}
