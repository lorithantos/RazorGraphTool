namespace RazorGraph.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RazorGraph.Extractor.Roslyn;
using Xunit;

/// <summary>
/// The call, construction and member-access scans share one semantic model per
/// tree and run as one walk. What that must not change is the answer: each list
/// is pinned against its scan run alone, over a fresh model, in the order the
/// separate passes used to produce -- compilation, then tree, then the scan's
/// own order.
/// </summary>
public class UsageSitesTests
{
    private const string StoreSource = """
        namespace A;
        public class Store
        {
            public static readonly Store Shared = Make();
            public int Count { get; set; }
            private int _hits;
            public static Store Make() => new Store();
            public void Add() { Count = Count + 1; _hits++; Log(); }
            public void Log() { }
        }
        """;

    private const string ClientSource = """
        namespace A;
        public class Client
        {
            private readonly Store _store = new Store();
            public void Run()
            {
                var s = new Store();
                s.Add();
                _store.Log();
                var n = s.Count;
            }
        }
        """;

    private const string OtherSource = """
        namespace B;
        public class Counter
        {
            public int Total;
            public void Bump() { Total = Total + 1; Reset(new Counter()); }
            public void Reset(Counter c) { c.Total = 0; }
        }
        """;

    private static CSharpCompilation Compile(string name, params string[] sources) =>
        CSharpCompilation.Create(
            name,
            sources.Select(s => CSharpSyntaxTree.ParseText(s)),
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static string Text(CallSiteInfo c) =>
        $"{c.FromId}|{c.FromTypeFullName}|{c.ToId}|{string.Join(",", c.GuardedBy)}|{string.Join(",", c.FilteredBy)}|{c.IsDelegate}";

    [Fact]
    public void OneWalk_GivesEachScanTheSitesItFindsAlone_InTheSameOrder()
    {
        var compilations = new[] { Compile("A", StoreSource, ClientSource), Compile("B", OtherSource) };
        var inScope = new HashSet<string>(["A", "B"], StringComparer.Ordinal);

        var expectedCalls = new List<string>();
        var expectedConstructions = new List<ConstructionSiteInfo>();
        var expectedAccesses = new List<MemberAccessInfo>();
        foreach (var compilation in compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = tree.GetRoot();
                expectedCalls.AddRange(RoslynExtractor
                    .CallSitesIn(root, compilation.GetSemanticModel(tree), inScope).Select(Text));
                expectedConstructions.AddRange(
                    RoslynExtractor.ConstructionSitesIn(root, compilation.GetSemanticModel(tree), inScope));
                expectedAccesses.AddRange(
                    MemberAccessScanner.TreeMemberAccesses(root, compilation.GetSemanticModel(tree), inScope));
            }
        }

        var sites = RoslynExtractor.UsageSitesOf(compilations, inScope);

        Assert.Equal(expectedCalls, sites.Calls.Select(Text));
        Assert.Equal(expectedConstructions, sites.Constructions);
        Assert.Equal(expectedAccesses, sites.MemberAccesses);

        // Every tree contributes to every list, so the order checks above have
        // something to order -- an empty or one-tree list would pass them anyway.
        foreach (var type in new[] { "A.Store", "A.Client", "B.Counter" })
        {
            Assert.Contains(sites.Calls, c => (c.FromId ?? c.FromTypeFullName)!.Contains(type));
            Assert.Contains(sites.Constructions, c => (c.FromId ?? c.FromTypeFullName)!.Contains(type));
            Assert.Contains(sites.MemberAccesses, a => a.FromId.Contains(type));
        }
    }
}
