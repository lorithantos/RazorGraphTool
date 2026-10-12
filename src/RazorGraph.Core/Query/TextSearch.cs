namespace RazorGraph.Core.Query;

using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;
using RazorGraph.Core.Graph;

/// <summary>
/// A regex search scoped to one folder, in two parts: the graph's names and literal
/// values when a graph covers the folder, and the files on disk always, less the lines
/// the graph already reported.
/// </summary>
/// <remarks>
/// Public because the MCP server is a different assembly; the server owns WHICH graph
/// answers, since only it holds the loaded set, and everything that decides what a
/// match is lives here. The graph sees declared names and literal values and nothing
/// else -- no comments, no code text -- so a graph answer is never the whole answer,
/// which is why the disk always runs as well.
/// </remarks>
public static class TextSearch
{
    /// <summary>Longest text a hit carries: a minified line is one line, and a model pays for every character of it.</summary>
    public const int TextCap = 200;

    /// <summary>Files larger than this are skipped rather than scanned; generated dumps are not what a text search is for.</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    // The probe git itself uses to call a file binary: a NUL in the first 8000-odd bytes.
    private const int BinaryProbeBytes = 8 * 1024;

    // A pathological pattern on one long line must cost a second, not the call.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    // Outside a git repository there is no ignore file to honour, so the same
    // directory names ClientAssetExtractor treats as vendor or build output are
    // skipped, plus .git. A copy rather than a reference: Core cannot see the
    // extractor assembly, and the list is a convention, not a contract.
    private static readonly HashSet<string> SkippedDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "lib", "lib_npm", "node_modules", "bower_components", "vendor", "bin", "obj"
    };

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
    /// One graph-tier hit. <paramref name="MatchedOn"/> is "name", "qualifiedName" or
    /// "literal". For a literal, the node is the Literal and <paramref name="FromId"/> is
    /// the member whose code produces it -- the path and line are that member's, because a
    /// Literal node is one per distinct value and has no file of its own.
    /// </summary>
    public sealed record GraphHit(
        string NodeId, string NodeType, string Name, string MatchedOn, string Path, int? Line, string? FromId);

    /// <summary>Every graph hit in a total order, and how many names timed out rather than answered.</summary>
    public sealed record GraphResult(IReadOnlyList<GraphHit> Hits, int RegexTimeouts);

    /// <summary>
    /// Nodes under the folder whose name or qualified name matches, and literal values
    /// that match at a site under the folder.
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

        foreach (var node in graph.Nodes)
        {
            if (!IsUnder(node.FilePath, folder) || !GlobMatches(glob, node.FilePath!)) continue;

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

        return new GraphResult(ordered, timeouts);
    }

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
        var (candidates, ignoreSource) = CandidateFiles(folder);

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
            if (!IsScannable(full)) { skipped++; continue; }

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

    /// <summary>
    /// A file is scanned when it exists, is no larger than <see cref="MaxFileBytes"/>,
    /// and has no NUL byte in its first 8 KB.
    /// </summary>
    private static bool IsScannable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return false;

            using var stream = info.OpenRead();
            var buffer = new byte[BinaryProbeBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, read) < 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Relative paths of every file worth reading under the folder, and what decided that.
    /// Git's own answer when the folder is in a working tree, so .gitignore is honoured
    /// exactly rather than approximately; a directory walk otherwise.
    /// </summary>
    private static (IReadOnlyList<string> Files, string IgnoreSource) CandidateFiles(string folder)
    {
        if (GraphOutput.FindRepositoryRoot(folder) is not null)
        {
            if (GitListFiles(folder) is { } tracked)
                return (tracked, "git ls-files --cached --others --exclude-standard");

            return (WalkFiles(folder),
                "directory walk (git repository, but git ls-files failed): skipped .git, vendor and build-output directories");
        }

        return (WalkFiles(folder), "directory walk (not a git repository): skipped .git, vendor and build-output directories");
    }

    /// <summary>
    /// Tracked plus untracked-but-not-ignored files under the folder, relative to it. Null
    /// when git could not answer, so the caller walks instead of reporting an empty tree.
    /// </summary>
    private static List<string>? GitListFiles(string folder)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                // Run IN the folder: ls-files then lists only what is beneath it, relative to it.
                // -z: NUL-separated and unquoted, so no path needs unescaping.
                ArgumentList = { "ls-files", "--cached", "--others", "--exclude-standard", "-z" },
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
            });

            if (process is null) return null;

            // Drain stderr concurrently: a full pipe on either stream blocks git forever.
            var stderr = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000) || process.ExitCode != 0) return null;
            _ = stderr.Result;

            return output
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                // An untracked nested repository is listed as "dir/"; it is not a file.
                .Where(p => !p.EndsWith('/'))
                .Select(p => p.Replace('/', Path.DirectorySeparatorChar))
                .ToList();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // git is not installed or not on PATH.
            return null;
        }
    }

    private static List<string> WalkFiles(string folder)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            // Junctions and symlinks can loop; hidden files are still text worth finding.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(folder);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            files.AddRange(Directory.EnumerateFiles(dir, "*", options).Select(f => Path.GetRelativePath(folder, f)));

            foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
            {
                if (!SkippedDirNames.Contains(Path.GetFileName(sub))) pending.Push(sub);
            }
        }

        return files;
    }

    private static string Cap(string text) => text.Length <= TextCap ? text : text[..TextCap];
}
