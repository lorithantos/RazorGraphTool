namespace RazorGraph.Extractor.Roslyn;

/// <summary>
/// Decides whether two method bodies have provably the same flow: a
/// bisimulation from entry to entry in which matched blocks carry the same
/// operations (source text modulo whitespace), the same call sequence, the
/// same canonicalized branch condition with matched true/false successors,
/// and the same exception-region context.
///
/// The claim is deliberately conservative. Guard inversion, block reordering,
/// and renumbering all compare Equivalent because the CFG and the condition
/// canonicalization normalize them away; a renamed local or a rewritten
/// expression compares Different even when a human can see it is fine. A
/// prover that can refuse a correct transform is a gate; one that can bless
/// a wrong one is a liability.
/// </summary>
public static class BodyGraphComparer
{
    private const int MaxDifferences = 25;

    public static BodyGraphDiff Compare(BodyGraph left, BodyGraph right)
    {
        var differences = new List<string>();

        var leftBlocks = left.Blocks.ToDictionary(b => b.Ordinal);
        var rightBlocks = right.Blocks.ToDictionary(b => b.Ordinal);
        var leftEntry = left.Blocks.Single(b => b.Kind == "Entry");
        var rightEntry = right.Blocks.Single(b => b.Kind == "Entry");

        var visited = new HashSet<(int, int)>();
        var queue = new Queue<(int Left, int Right)>();
        queue.Enqueue((leftEntry.Ordinal, rightEntry.Ordinal));

        while (queue.Count > 0 && differences.Count < MaxDifferences)
        {
            var (l, r) = queue.Dequeue();
            if (!visited.Add((l, r))) continue;

            var a = leftBlocks[l];
            var b = rightBlocks[r];

            if (!BlocksMatch(a, b, left, right, differences)) continue;

            // Successors are only followed for matched pairs, so one mismatch
            // yields one difference instead of cascading through the suffix.
            if (a.Condition != null)
            {
                EnqueuePair(a.WhenTrue, b.WhenTrue, "whenTrue", a, b, queue, differences);
                EnqueuePair(a.WhenFalse, b.WhenFalse, "whenFalse", a, b, queue, differences);
            }
            else
            {
                EnqueuePair(a.FallsTo, b.FallsTo, "fallsTo", a, b, queue, differences);
            }
        }

        var leftCalls = CallTargets(left);
        var rightCalls = CallTargets(right);
        var callsOnlyInLeft = MultisetExcess(leftCalls, rightCalls);
        var callsOnlyInRight = MultisetExcess(rightCalls, leftCalls);

        return new BodyGraphDiff
        {
            // The multiset check covers what bisimulation cannot see:
            // differences confined to blocks unreachable from entry.
            Equivalent = differences.Count == 0 && callsOnlyInLeft.Count == 0 && callsOnlyInRight.Count == 0,
            NestingDepthLeft = left.NestingDepth,
            NestingDepthRight = right.NestingDepth,
            CallsOnlyInLeft = callsOnlyInLeft,
            CallsOnlyInRight = callsOnlyInRight,
            Differences = differences
        };
    }

    private static bool BlocksMatch(
        BodyBlock a, BodyBlock b, BodyGraph left, BodyGraph right, List<string> differences)
    {
        var where = $"blocks L{a.Ordinal}/R{b.Ordinal}";

        if (a.Kind != b.Kind)
        {
            differences.Add($"{where}: kind {a.Kind} vs {b.Kind}");
            return false;
        }

        if (!a.Operations.SequenceEqual(b.Operations, StringComparer.Ordinal))
        {
            differences.Add($"{where}: operations differ: [{string.Join("; ", a.Operations)}] vs [{string.Join("; ", b.Operations)}]");
            return false;
        }

        if (!a.Calls.Select(c => c.TargetId).SequenceEqual(b.Calls.Select(c => c.TargetId), StringComparer.Ordinal))
        {
            differences.Add($"{where}: calls differ: [{string.Join("; ", a.Calls.Select(c => c.TargetId))}] vs [{string.Join("; ", b.Calls.Select(c => c.TargetId))}]");
            return false;
        }

        if (!string.Equals(a.Condition, b.Condition, StringComparison.Ordinal))
        {
            differences.Add($"{where}: condition '{a.Condition ?? "(none)"}' vs '{b.Condition ?? "(none)"}'");
            return false;
        }

        var contextA = ExceptionContext(a, left);
        var contextB = ExceptionContext(b, right);
        if (contextA != contextB)
        {
            differences.Add($"{where}: exception context '{contextA}' vs '{contextB}' — code moved into or out of a try/catch/finally");
            return false;
        }

        return true;
    }

    private static void EnqueuePair(
        int? a, int? b, string edge, BodyBlock fromA, BodyBlock fromB,
        Queue<(int, int)> queue, List<string> differences)
    {
        if (a is { } l && b is { } r) queue.Enqueue((l, r));
        else if (a != b)
            differences.Add($"blocks L{fromA.Ordinal}/R{fromB.Ordinal}: {edge} present on one side only");
    }

    /// <summary>
    /// The chain of exception-handling region kinds enclosing a block, root
    /// last. Root and LocalLifetime are skipped: locals moving between scopes
    /// is not a flow change, but code moving in or out of a finally is.
    /// </summary>
    private static string ExceptionContext(BodyBlock block, BodyGraph graph)
    {
        var byId = graph.Regions.ToDictionary(r => r.Id);
        var kinds = new List<string>();
        for (var region = byId[block.RegionId]; ; region = byId[region.ParentId!.Value])
        {
            if (region.Kind is not ("Root" or "LocalLifetime")) kinds.Add(region.Kind);
            if (region.ParentId == null) break;
        }
        return string.Join(" > ", kinds);
    }

    private static List<string> CallTargets(BodyGraph graph) =>
        graph.Blocks.SelectMany(b => b.Calls).Select(c => c.TargetId).OrderBy(t => t, StringComparer.Ordinal).ToList();

    private static List<string> MultisetExcess(List<string> from, List<string> other)
    {
        var counts = other.GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        var excess = new List<string>();
        foreach (var target in from)
        {
            if (counts.TryGetValue(target, out var n) && n > 0) counts[target] = n - 1;
            else excess.Add(target);
        }
        return excess;
    }
}

/// <summary>Comparison verdict: equivalent, or a bounded list of precise reasons why not.</summary>
public sealed class BodyGraphDiff
{
    public bool Equivalent { get; init; }
    public int NestingDepthLeft { get; init; }
    public int NestingDepthRight { get; init; }
    public required List<string> CallsOnlyInLeft { get; init; }
    public required List<string> CallsOnlyInRight { get; init; }
    public required List<string> Differences { get; init; }
}
