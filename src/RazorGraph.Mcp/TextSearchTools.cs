namespace RazorGraph.Mcp;

using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RazorGraph.Core.Query;

/// <summary>
/// One entry point for text questions, so a caller does not have to decide between the
/// graph and grep before asking. The graph section answers from a loaded graph covering
/// the folder; the disk section is the full text search with the graph's lines removed,
/// so the two together are the whole answer and neither repeats the other.
/// </summary>
[McpServerToolType]
internal sealed class TextSearchTools(GraphStore store)
{
    [McpServerTool(Name = "grep")]
    [Description("Regex search over one folder, returned in two sections that together are the complete answer with no line reported twice. GRAPH section: answered from a loaded graph that covers the folder (any node's file lies under it). It matches node NAMES, qualified names (a type's full name, a member's declaringType.Name) and LITERAL string values; a literal hit is located at the line of the member that produces it, and carries that member as fromId. It holds names and literal values ONLY -- no comments, doc text or arbitrary code -- so raw code text in C# is found in the disk section, not here. When no loaded graph covers the folder, the section is just a 'reason' and empty hits. DISK section: always runs. It is the full text search of the folder with every line the graph section reported removed -- a match at the same path and line as ANY graph hit, not only the returned page, is counted in alreadyInGraph instead of being returned. In a git working tree it reads what `git ls-files --cached --others --exclude-standard` lists, so .gitignore is honoured; elsewhere it walks the folder skipping .git, bin, obj, node_modules and the other vendor directories ('ignoreSource' says which). One hit per matching line: path, line, and the trimmed line text capped at 200 characters. Binaries (a NUL in the first 8 KB) and files over 2 MB are skipped and counted in filesSkipped. Paths in every hit are relative to folder, and glob filters BOTH sections by file name. The pattern is a .NET regular expression for both sections, culture-invariant, with a 1-second match timeout; a file the pattern timed out on is named in timedOutFiles and its remaining matches are missing. When several loaded graphs cover the folder, the current one is preferred, then the newest build, and the rest are named in otherCoveringGraphs. Each section is paged on its own over a total order, limit rows each: continue the graph section with offset=graph.nextOffset and the disk section with diskOffset=disk.nextOffset; nextOffset is absent when that section has no more, and totalMatches counts every match in that section, not just the page.")]
    public string Grep(
        [Description(".NET regular expression, e.g. \"arrivalTicks\" or \"Get[A-Z]\\w+Async\". Escape metacharacters (\\. \\( \\[) to match them literally.")] string pattern,
        [Description("Absolute path of the folder to search. Hits outside it are never returned, from either section.")] string folder,
        [Description("Optional wildcard on the file NAME, applied to both sections, e.g. \"*.ps1\" or \"*.json\": with \"*.json\" no .cs graph hit is returned.")] string? glob = null,
        [Description("Case-insensitive matching (default true)")] bool ignoreCase = true,
        [Description("Max hits to return in EACH section (default 50)")] int limit = 50,
        [Description("Where the GRAPH section starts, for continuation. Pass graph.nextOffset from the previous page. Does not move the disk section.")] int offset = 0,
        [Description("Where the DISK section starts, for continuation. Pass disk.nextOffset from the previous page. Does not move the graph section.")] int diskOffset = 0,
        [Description("Graph to answer the graph section from. Omit to use whichever loaded graph covers the folder, preferring the current one.")] string? graphId = null)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new McpException($"folder must be an absolute path, e.g. C:\\repos\\MyApp\\src; got '{folder}'.");
        if (!Directory.Exists(folder))
            throw new McpException($"folder '{folder}' does not exist. Pass an existing directory.");
        if (string.IsNullOrEmpty(pattern))
            throw new McpException("pattern is empty. Pass a .NET regular expression.");

        Regex regex;
        try { regex = TextSearch.Compile(pattern, ignoreCase); }
        catch (ArgumentException ex)
        {
            throw new McpException(
                $"pattern is not a valid .NET regular expression: {ex.Message} " +
                "Escape metacharacters with a backslash (\\. \\( \\[) to match them literally.");
        }

        var root = TextSearch.NormaliseFolder(folder);
        var (entry, others, reason) = ChooseGraph(root, graphId);

        // The whole graph result, not a page of it, is what the disk leaves out: excluding
        // only the page would hand the next graph page back as disk hits.
        IReadOnlyList<TextSearch.GraphHit> graphHits = [];

        // An empty list with a reason, never null: "no graph covers this" is an answer.
        object graphSection = new { reason, hits = Array.Empty<object>() };
        if (entry is not null)
        {
            var result = TextSearch.SearchGraph(entry.Graph, root, regex, glob);
            graphHits = result.Hits;

            var (window, hits) = ToolResponses.Paginate(graphHits, offset, limit, h => new
            {
                nodeId = h.NodeId,
                nodeType = h.NodeType,
                name = h.Name,
                matchedOn = h.MatchedOn,
                path = h.Path,
                line = h.Line,
                fromId = h.FromId
            });

            graphSection = new
            {
                graphId = entry.Id,
                builtAt = entry.Graph.BuiltAt,
                extractorBuiltAt = entry.Graph.ExtractorBuiltAt,
                freshnessCaveat = ToolResponses.FreshnessCaveat(entry.Graph),
                otherCoveringGraphs = others,
                reason,
                returned = window.Returned,
                totalMatches = window.TotalMatches,
                offset = window.Offset,
                truncated = window.Truncated,
                nextOffset = window.NextOffset,
                regexTimeouts = result.RegexTimeouts > 0 ? result.RegexTimeouts : (int?)null,
                hits
            };
        }

        var disk = TextSearch.SearchDisk(root, regex, glob, limit, diskOffset, graphHits);

        return ToolResponses.ToJson(new
        {
            folder = root,
            pattern,
            graph = graphSection,
            disk = new
            {
                returned = disk.Hits.Count,
                totalMatches = disk.TotalMatches,
                offset = disk.Offset,
                truncated = disk.NextOffset is not null,
                nextOffset = disk.NextOffset,
                filesScanned = disk.FilesScanned,
                filesSkipped = disk.FilesSkipped,
                ignoreSource = disk.IgnoreSource,
                timedOutFiles = disk.TimedOutFiles.Count > 0 ? disk.TimedOutFiles : null,
                alreadyInGraph = disk.AlreadyInGraph,
                hits = disk.Hits.Select(h => new { path = h.Path, line = h.Line, text = h.Text })
            }
        });
    }

    /// <summary>
    /// The graph that answers for this folder, the other loaded graphs that also cover it,
    /// and why -- or no graph, with the reason the graph section will carry.
    /// </summary>
    private (GraphStore.GraphEntry? Entry, List<string>? Others, string Reason) ChooseGraph(string folder, string? graphId)
    {
        if (!string.IsNullOrWhiteSpace(graphId))
        {
            var named = store.Require(graphId);
            return TextSearch.Covers(named.Graph, folder)
                ? (named, null, $"graph '{named.Id}' was requested and covers folder")
                : (null, null, $"graph '{named.Id}' was requested but covers nothing under folder");
        }

        var covering = store.List().Where(e => TextSearch.Covers(e.Graph, folder)).ToList();
        if (covering.Count == 0) return (null, null, "no loaded graph covers folder");

        var current = store.CurrentId;
        var chosen = covering.FirstOrDefault(e => string.Equals(e.Id, current, StringComparison.OrdinalIgnoreCase))
            ?? covering
                .OrderByDescending(e => e.Graph.BuiltAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(e => e.LoadedAt)
                .First();

        var others = covering.Where(e => e != chosen).Select(e => e.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var why = chosen.Id.Equals(current, StringComparison.OrdinalIgnoreCase)
            ? $"graph '{chosen.Id}' is current and covers folder"
            : $"graph '{chosen.Id}' is the newest loaded graph covering folder";

        return (chosen, others.Count > 0 ? others : null, why);
    }
}
