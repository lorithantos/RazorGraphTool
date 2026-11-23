namespace RazorGraph.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RazorGraph.Extractor.Roslyn;
using Xunit;

/// <summary>
/// An element access names no member, so the scan for named accesses never saw
/// one, and an in-solution indexer had no incoming Reads or Writes. Each site
/// below is an access of the indexer itself, classified by the rules a named
/// property has; an indexer outside the solution gets nothing.
/// </summary>
public class IndexerAccessTests
{
    private const string Source = """
        namespace A;
        using System.Collections.Generic;

        public class Grid
        {
            private readonly int[] _cells = new int[4];
            private int _spare;
            public int this[int i] { get => _cells[i]; set => _cells[i] = value; }
            public ref int this[string name] => ref _spare;
        }

        public class Box
        {
            public Grid Grid { get; set; } = new Grid();
        }

        public class User
        {
            public int Read(Grid g) => g[0];
            public void Write(Grid g) { g[1] = 5; }
            public void Compound(Grid g) { g[2] += 1; }
            public void Increment(Grid g) { g[3]++; }
            public int? Conditional(Grid? g) => g?[0];
            public int? ConditionalOnMember(Box? b) => b?.Grid[0];
            public void ConditionalAssign(Grid? g) { g?[1] = 5; }
            public void ByRef(Grid g) { Bump(ref g["spare"]); }
            public void ByOut(Grid g) { Fill(out g["spare"]); }
            public Grid Initialize() => new Grid { [1] = 2 };
            public int FromCollections(List<int> xs, Dictionary<string, int> d)
            {
                xs[0] = 1;
                d["k"] += 1;
                return xs[0] + d["k"];
            }
            private static void Bump(ref int x) { x++; }
            private static void Fill(out int x) { x = 0; }
        }
        """;

    private const string ByInt = "prop:A.Grid.this[int]";
    private const string ByName = "prop:A.Grid.this[string]";

    private static readonly IReadOnlyList<MemberAccessInfo> Accesses = Scan();

    private static IReadOnlyList<MemberAccessInfo> Scan()
    {
        var tree = CSharpSyntaxTree.ParseText(Source);
        var compilation = CSharpCompilation.Create(
            "A",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        // A source that does not compile binds nothing, and every test below
        // would pass or fail for that reason instead of its own.
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        var inScope = new HashSet<string>(["A"], StringComparer.Ordinal);
        return MemberAccessScanner
            .TreeMemberAccesses(tree.GetRoot(), compilation.GetSemanticModel(tree), inScope)
            .ToList();
    }

    /// <summary>The indexer accesses one User method makes, as (indexer, read, write).</summary>
    private static (string ToId, bool IsRead, bool IsWrite)[] IndexerAccessesFrom(string method) =>
        Accesses
            .Where(a => a.FromId.StartsWith($"m:A.User.{method}(", StringComparison.Ordinal))
            .Where(a => a.ToId.StartsWith("prop:A.Grid.this[", StringComparison.Ordinal))
            .Select(a => (a.ToId, a.IsRead, a.IsWrite))
            .ToArray();

    [Fact]
    public void ReadingThroughAnIndexer_ReadsIt()
    {
        Assert.Equal(new[] { (ByInt, true, false) }, IndexerAccessesFrom("Read"));
    }

    [Fact]
    public void AssigningThroughAnIndexer_WritesIt()
    {
        Assert.Equal(new[] { (ByInt, false, true) }, IndexerAccessesFrom("Write"));
    }

    [Fact]
    public void CompoundAssignmentAndIncrement_BothReadAndWrite()
    {
        Assert.Equal(new[] { (ByInt, true, true) }, IndexerAccessesFrom("Compound"));
        Assert.Equal(new[] { (ByInt, true, true) }, IndexerAccessesFrom("Increment"));
    }

    [Fact]
    public void ConditionalIndexing_IsAnAccessLikeAnyOther()
    {
        // g?[0] is an element BINDING, not an element access, and an
        // assignment through it sits under the conditional access.
        Assert.Equal(new[] { (ByInt, true, false) }, IndexerAccessesFrom("Conditional"));
        Assert.Equal(new[] { (ByInt, false, true) }, IndexerAccessesFrom("ConditionalAssign"));

        // b?.Grid[0]: the element access is the conditional's tail.
        Assert.Equal(new[] { (ByInt, true, false) }, IndexerAccessesFrom("ConditionalOnMember"));
    }

    [Fact]
    public void RefAndOutArguments_FollowTheNamedMemberRules()
    {
        Assert.Equal(new[] { (ByName, true, true) }, IndexerAccessesFrom("ByRef"));
        Assert.Equal(new[] { (ByName, false, true) }, IndexerAccessesFrom("ByOut"));
    }

    [Fact]
    public void AnIndexInAnObjectInitializer_WritesTheIndexer()
    {
        Assert.Equal(new[] { (ByInt, false, true) }, IndexerAccessesFrom("Initialize"));
    }

    [Fact]
    public void IndexersOutsideTheSolution_GiveNoAccess()
    {
        // List<T> and Dictionary indexers live in the core library: no edge,
        // as for any member declared outside the solution.
        Assert.DoesNotContain(Accesses, a => a.FromId.StartsWith("m:A.User.FromCollections(", StringComparison.Ordinal));
    }

    [Fact]
    public void ArrayElementAccess_BindsToNoIndexer()
    {
        // The int indexer's own bodies index an array: the field is read, and
        // that is all they access.
        var fromIndexer = Accesses.Where(a => a.FromId == ByInt).Select(a => a.ToId).Distinct().ToArray();

        Assert.Equal(new[] { "field:A.Grid._cells" }, fromIndexer);
    }
}
