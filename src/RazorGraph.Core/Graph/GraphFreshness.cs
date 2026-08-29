namespace RazorGraph.Core.Graph;

/// <summary>
/// Whether the file behind a node has been written since the graph was built.
///
/// The graph already reports staleness for the whole repository, but that signal
/// saturates: one keystroke into a session it is true and it never comes back,
/// so it stops distinguishing the answer you should doubt from the two hundred
/// you should not. Per-node inverts it — you edit two files, and only nodes in
/// those two carry the flag.
/// </summary>
/// <remarks>
/// Two things this deliberately does NOT claim, because a reassuring silence is
/// the failure mode the graph's own caveats exist to prevent:
///
/// <list type="bullet">
/// <item>It reports a WRITE, not a change. A checkout, a formatter, or a touch
/// trips it with the content identical. Cheap and wrong in the safe direction.</item>
/// <item>It is necessary, not sufficient. A node can be wrong because a
/// DIFFERENT file changed — rename a method elsewhere and the Calls edge into an
/// untouched file is already a lie. An unflagged node means "its own file is
/// unchanged", never "this is still true".</item>
/// </list>
///
/// Hashing contents would fix the first and not the second, at a cost per query
/// that a stat does not have.
/// </remarks>
public sealed class GraphFreshness
{
    // One stat per DISTINCT file, not per node: a neighbours dump is dozens of
    // nodes across two or three files, and the same path recurs throughout.
    private readonly Dictionary<string, bool> _written = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeOffset? _builtAt;

    public GraphFreshness(CodeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        _builtAt = graph.BuiltAt;
    }

    /// <summary>Null when the graph carries no build stamp, or the node has no
    /// file — "cannot tell", which is not the same answer as "unchanged".</summary>
    public bool? WrittenSinceBuild(GraphNode? node)
    {
        if (_builtAt is not { } builtAt || node?.FilePath is not { Length: > 0 } path)
        {
            return null;
        }

        if (_written.TryGetValue(path, out bool cached))
        {
            return cached;
        }

        bool written;
        try
        {
            // A path that no longer exists is a change of the most emphatic kind.
            written = !File.Exists(path)
                || File.GetLastWriteTimeUtc(path) > builtAt.UtcDateTime;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable is not evidence of freshness; say so rather than clear it.
            written = true;
        }

        _written[path] = written;
        return written;
    }
}
