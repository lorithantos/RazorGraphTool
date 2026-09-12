namespace RazorGraph.Tests;

using System.Diagnostics;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Extractor;
using Xunit;

/// <summary>
/// End-to-end over a three-project fixture solution: a class library, a Razor web
/// app that consumes it, and a test project that exercises it.
///
/// The point of these tests is the boundary. Every assertion here is about an
/// edge whose two ends live in different projects — the class of fact a graph
/// built one project at a time cannot represent at all, however many times it is
/// rebuilt.
/// </summary>
[Trait("Category", "Integration")]
public class SolutionGraphIntegrationTests : IAsyncLifetime
{
    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static CodeGraph? _solutionGraph;
    private static CodeGraph? _webOnlyGraph;
    private static CodeGraph? _noTestsGraph;
    private static IReadOnlyList<string>? _noTestsSkipped;
    private static CodeGraph? _noLiteralsGraph;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RazorGraphTool.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate RazorGraphTool.slnx above the test directory.");
    }

    private static string FixtureDir() =>
        Path.Combine(RepoRoot(), "tests", "fixtures", "MultiProject");

    public async Task InitializeAsync()
    {
        await BuildGate.WaitAsync();
        try
        {
            if (_solutionGraph != null) return;

            var fixtureDir = FixtureDir();
            var solutionPath = Path.Combine(fixtureDir, "MultiProject.sln");
            Assert.True(File.Exists(solutionPath), $"Fixture solution missing: {solutionPath}");

            EnsureRestored(fixtureDir);

            await using (var builder = new GraphBuilder())
            {
                _solutionGraph = await builder.BuildFromSolutionAllAsync(solutionPath);
            }

            // The contrast case: the same web project on its own.
            await using (var builder = new GraphBuilder())
            {
                _webOnlyGraph = await builder.BuildFromProjectAsync(
                    Path.Combine(fixtureDir, "SampleWeb", "SampleWeb.csproj"));
            }

            // The same solution without its tests — the lean navigation build.
            await using (var builder = new GraphBuilder { ExcludeTestProjects = true })
            {
                _noTestsGraph = await builder.BuildFromSolutionAllAsync(solutionPath);
                _noTestsSkipped = builder.SkippedTestProjects;
            }

            // And without the literal index. A separate build rather than a filter
            // over the first: the claim is about what the builder emits, and a test
            // that deletes the nodes itself would pass with the option ignored.
            await using (var builder = new GraphBuilder { ExcludeLiterals = true })
            {
                _noLiteralsGraph = await builder.BuildFromSolutionAllAsync(solutionPath);
            }
        }
        finally
        {
            BuildGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static void EnsureRestored(string fixtureDir)
    {
        if (File.Exists(Path.Combine(fixtureDir, "SampleLib", "obj", "project.assets.json"))) return;

        var psi = new ProcessStartInfo("dotnet", "restore MultiProject.sln")
        {
            WorkingDirectory = fixtureDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dotnet restore of fixture solution failed:\n{output}");
    }

    private static GraphNode Method(CodeGraph graph, string declaringType, string name) =>
        graph.Nodes.Single(n =>
            n.Type == NodeType.Method &&
            n.Name == name &&
            n.GetProperty<string>("declaringType") == declaringType);

    // ---- Projects ------------------------------------------------------------

    [Fact]
    public void Builds_ProjectNodes_ForEveryProjectInTheSolution()
    {
        var projects = _solutionGraph!.NodesOfType(NodeType.Project).Select(p => p.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "SampleLib", "SampleWeb", "SampleWeb.Tests" }, projects);
    }

    [Fact]
    public void Builds_DecoratedBy_ForTheHandWrittenAssemblyAttribute_AndNoGeneratedOnes()
    {
        // The SDK writes ~10 assembly attributes per project into obj\
        // (AssemblyInfo.cs, .NETCoreApp…AssemblyAttributes.cs); the fixture
        // hand-writes exactly one. Exactly one edge is therefore both the
        // positive case and the proof the generated-site gate held.
        var fromProjects = _solutionGraph!.Edges
            .Where(e => e.Type == EdgeType.DecoratedBy && e.FromId.StartsWith("proj:", StringComparison.Ordinal))
            .ToList();

        var edge = Assert.Single(fromProjects);
        Assert.Equal("proj:SampleLib", edge.FromId);
        Assert.Equal("ext:System.Reflection.AssemblyMetadataAttribute", edge.ToId);
        Assert.Equal("assembly", edge.GetProperty<string>("target"));
        Assert.Equal(new List<object?> { "fixture-marker", "sample-lib" }, edge.GetProperty<List<object?>>("args"));
    }

    [Fact]
    public void Builds_DependsOnEdges_BetweenProjects()
    {
        var edges = _solutionGraph!.Edges
            .Where(e => e.Type == EdgeType.DependsOn)
            .Select(e => (From: _solutionGraph.GetNode(e.FromId)!.Name, To: _solutionGraph.GetNode(e.ToId)!.Name))
            .ToList();

        Assert.Contains(("SampleWeb", "SampleLib"), edges);
        Assert.Contains(("SampleWeb.Tests", "SampleLib"), edges);
    }

    [Fact]
    public void EveryCodeNode_IsAttributedToItsProject()
    {
        var methods = _solutionGraph!.NodesOfType(NodeType.Method).ToList();

        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.False(
            string.IsNullOrWhiteSpace(m.GetProperty<string>("project")),
            $"{m.Id} has no project attribution"));
    }

    // ---- The cross-project edges that motivated all of this -----------------

    [Fact]
    public void Builds_CallEdge_FromWebProjectIntoClassLibrary()
    {
        var onGet = Method(_solutionGraph!, "SampleWeb.Pages.IndexModel", "OnGet");

        var callees = _solutionGraph!.Outgoing(onGet.Id)
            .Where(e => e.Type == EdgeType.Calls)
            .Select(e => _solutionGraph.GetNode(e.ToId)!)
            .ToList();

        var crossProject = callees.Where(c => c.GetProperty<string>("project") == "SampleLib").ToList();

        Assert.NotEmpty(crossProject);
        Assert.Contains(crossProject, c => c.Name == "List");
    }

    [Fact]
    public void SingleProjectBuild_CannotSeeTheCrossProjectCall()
    {
        // The control for the test above. OnGet still exists, and still calls
        // into SampleLib in the source — but with only one assembly compiled,
        // the callee was never a node, so no edge could be recorded.
        var onGet = Method(_webOnlyGraph!, "SampleWeb.Pages.IndexModel", "OnGet");

        var callees = _webOnlyGraph!.Outgoing(onGet.Id)
            .Where(e => e.Type == EdgeType.Calls)
            .Select(e => _webOnlyGraph.GetNode(e.ToId)!.Name)
            .ToList();

        Assert.DoesNotContain("List", callees);
    }

    [Fact]
    public void Builds_InjectedIntoEdge_AcrossProjects()
    {
        var pageModel = _solutionGraph!.Nodes.Single(n => n.Type == NodeType.PageModel && n.Name == "IndexModel");

        var injectors = _solutionGraph.Incoming(pageModel.Id)
            .Where(e => e.Type == EdgeType.InjectedInto)
            .Select(e => _solutionGraph.GetNode(e.FromId)!)
            .ToList();

        Assert.Contains(injectors, i => i.Name == "ICatalogStore" && i.GetProperty<string>("project") == "SampleLib");
    }

    // ---- Coverage ------------------------------------------------------------

    [Fact]
    public void Marks_TestMethods_ByAttribute()
    {
        var tests = _solutionGraph!.NodesOfType(NodeType.Method)
            .Where(m => m.GetProperty<bool>("isTest"))
            .Select(m => m.Name)
            .ToList();

        Assert.Equal(
            new[]
            {
                "Advance_ThroughARunnerThatHoldsTheBase", "Advance_ThroughTheBaseDeclaration",
                "Cache_SeedsItself", "Channel_ThroughTheInterface",
                "Greet_ThroughTheInterface", "List_ReturnsSortedCatalogs",
                "Preload_CountsCatalogs", "Warm_IsPositive"
            },
            tests.OrderBy(t => t, StringComparer.Ordinal));
        // NotATest calls production code but carries no attribute, and
        // InitializeAsync is a lifecycle hook, not a test.
        Assert.DoesNotContain("NotATest", tests);
        Assert.DoesNotContain("InitializeAsync", tests);
    }

    [Fact]
    public void Builds_CoversEdges_FromTestToCodeUnderTest()
    {
        var query = new GraphQuery(_solutionGraph!);
        var load = Method(_solutionGraph!, "SampleLib.CatalogStore", "List");

        var covering = query.GetCoveringTests(load.Id).ToList();

        Assert.Single(covering);
        Assert.Equal("List_ReturnsSortedCatalogs", covering[0].Test.Name);
        Assert.Equal(1, covering[0].Depth);
    }

    [Fact]
    public void CoversEdges_ReachTransitivelyAndRecordDepth()
    {
        var query = new GraphQuery(_solutionGraph!);
        var normalize = Method(_solutionGraph!, "SampleLib.CatalogStore", "Normalize");

        var covering = query.GetCoveringTests(normalize.Id).ToList();

        // Normalize is private and only reachable through List, so a direct-call
        // model would report it as untested.
        Assert.Single(covering);
        Assert.Equal(2, covering[0].Depth);
    }

    [Fact]
    public void TypeNodes_SayWhetherTheyAreInterfaces()
    {
        // An interface that DI never registers is a plain Class node, so without
        // the flag it reads as a class: measured 30 of 37 on one app. The flag is
        // stamped on every type, so a graph built before it existed is told apart
        // from a class by the property being absent rather than false.
        var plainInterface = Type(_solutionGraph!, "SampleLib.IGreeter");
        var registeredInterface = Type(_solutionGraph!, "SampleLib.ICatalogStore");
        var implementation = Type(_solutionGraph!, "SampleLib.Greeter");

        Assert.Equal(NodeType.Class, plainInterface.Type);
        Assert.True(plainInterface.GetProperty<bool>("isInterface"));
        Assert.True(registeredInterface.GetProperty<bool>("isInterface"));
        Assert.False(implementation.GetProperty<bool>("isInterface"));
        Assert.True(implementation.Properties.ContainsKey("isInterface"));
    }

    private static GraphNode Type(CodeGraph graph, string fullName) =>
        graph.Nodes.Single(n => n.GetProperty<string>("fullName") == fullName);

    // ---- Quoted names --------------------------------------------------------

    /// <summary>Quotes edges out of one method of the NameCoupling fixture.</summary>
    private static List<GraphEdge> QuotesFrom(string method) =>
        _solutionGraph!.Outgoing(Method(_solutionGraph, "SampleWeb.NameCoupling", method).Id)
            .Where(e => e.Type == EdgeType.Quotes)
            .ToList();

    [Fact]
    public void QuotesEdges_LinkATypedNameToTheDeclarationItNames()
    {
        // "CatalogStore" is a string in SampleWeb naming a type in SampleLib.
        // Nothing in the compiler's model connects the two, which is the point:
        // renaming the type leaves this string spelling the old name.
        var edge = Assert.Single(QuotesFrom("TypedName"));

        Assert.Equal(Type(_solutionGraph!, "SampleLib.CatalogStore").Id, edge.ToId);
        Assert.Equal("CatalogStore", edge.GetProperty<string>("value"));
        Assert.Equal("literal", edge.GetProperty<string>("provenance"));
        Assert.True(edge.GetProperty<int>("line") > 0);
    }

    [Fact]
    public void QuotesEdges_TellTheSafeFormFromTheBreakableOne()
    {
        // nameof(SampleLib.CatalogStore) is the identical coupling to the string
        // above and is not a defect, because a rename rewrites it. An IL reader
        // cannot separate them; this is why the scan is over syntax.
        var edge = Assert.Single(QuotesFrom("SafeName"));

        Assert.Equal(Type(_solutionGraph!, "SampleLib.CatalogStore").Id, edge.ToId);
        Assert.Equal("nameof", edge.GetProperty<string>("provenance"));
    }

    [Fact]
    public void QuotesEdges_CoverInterpolatedSegmentsAndAttributeArguments()
    {
        var interpolated = Assert.Single(QuotesFrom("Interpolated"));
        Assert.Equal("Normalize", interpolated.GetProperty<string>("value"));
        Assert.Equal("interpolated", interpolated.GetProperty<string>("provenance"));

        // An attribute argument fails differently: the build stays green and the
        // framework binding silently stops happening.
        var attributed = Assert.Single(QuotesFrom("Attributed"));
        Assert.Equal("PriceBook", attributed.GetProperty<string>("value"));
        Assert.Equal("attributeArgument", attributed.GetProperty<string>("provenance"));
    }

    [Fact]
    public void QuotesEdges_IgnoreStringsThatNameNothing()
    {
        // Prose is most of the strings in any codebase. Indexing it would bury
        // every real finding under every message in the solution.
        Assert.Empty(QuotesFrom("Prose"));
    }

    [Fact]
    public void QuotesEdges_IgnoreNamesSeveralDeclarationsShare()
    {
        // "Dispose" is declared by CatalogStore and by the fixture's own
        // disposable, so a string naming it cannot say which is meant -- and
        // usually neither is, the author meaning IDisposable.Dispose, which has
        // no node. The first build of this reported an edge per match and 174
        // rows came back with almost nothing in them.
        var shared = _solutionGraph!.Nodes
            .Where(n => n.Type == NodeType.Method && n.Name == "Dispose")
            .ToList();
        Assert.True(shared.Count > 1, "fixture no longer has an ambiguous name to test");

        Assert.Empty(QuotesFrom("AmbiguousName"));
    }

    [Fact]
    public void FindQuotedSymbols_DefaultsToTheBreakableCrossProjectSet()
    {
        var query = new GraphQuery(_solutionGraph!);

        var breakable = query.FindQuotedSymbols().ToList();
        Assert.Contains(breakable, q => q.Value == "CatalogStore" && q.Provenance == "literal");
        Assert.DoesNotContain(breakable, q => q.Provenance == "nameof");

        // The safe form is present in the graph either way; only the report hides it.
        Assert.Contains(query.FindQuotedSymbols(includeSafe: true), q => q.Provenance == "nameof");

        // Scoping to the quoting side's project is by the FROM node.
        Assert.All(query.FindQuotedSymbols(project: "SampleWeb"),
            q => Assert.Equal("SampleWeb", q.From.GetProperty<string>("project")));
        Assert.DoesNotContain(query.FindQuotedSymbols(project: "SampleLib"),
            q => q.From.GetProperty<string>("declaringType") == "SampleWeb.NameCoupling");
    }

    // ---- Literal index -------------------------------------------------------

    /// <summary>Mentions edges out of one method of the NameCoupling fixture.</summary>
    private static List<GraphEdge> MentionsFrom(string method) =>
        _solutionGraph!.Outgoing(Method(_solutionGraph, "SampleWeb.NameCoupling", method).Id)
            .Where(e => e.Type == EdgeType.Mentions)
            .ToList();

    private static GraphNode Literal(CodeGraph graph, string value) =>
        graph.Nodes.Single(n => n.Type == NodeType.Literal && n.Name == value);

    [Fact]
    public void LiteralIndex_KeepsTheStringsQuotesThrowsAway()
    {
        // The gap this exists to close. "nothing in this sentence is declared"
        // names no declaration, so it is not a Quotes edge and never was -- and it
        // used to be unfindable in the graph at all, which sent the question to
        // grep. QuotesEdges_IgnoreStringsThatNameNothing is the mirror of this
        // test and must keep passing: the same string, two different answers,
        // because they are two different questions.
        Assert.Empty(QuotesFrom("Prose"));

        var edge = Assert.Single(MentionsFrom("Prose"));
        var literal = _solutionGraph!.GetNode(edge.ToId)!;

        Assert.Equal(NodeType.Literal, literal.Type);
        Assert.Equal("nothing in this sentence is declared", literal.Name);
        Assert.Equal("nothing in this sentence is declared", literal.GetProperty<string>("value"));
    }

    [Fact]
    public void LiteralNodes_AreOnePerValue_HoweverManyPlacesProduceIt()
    {
        // Dedup by value is what makes the node worth having: it is the string,
        // and its incoming set is every site. Minting a node per occurrence would
        // give the same answer only by walking a list nobody can name.
        var literal = Literal(_solutionGraph!, "waves.ini");

        var sites = _solutionGraph!.Incoming(literal.Id).Where(e => e.Type == EdgeType.Mentions).ToList();
        Assert.Equal(2, sites.Count);

        var producers = sites.Select(e => _solutionGraph.GetNode(e.FromId)!.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "AlsoTheConfigFile", "ConfigFile" }, producers);
    }

    [Fact]
    public void LiteralNodes_CarryNoSiteOfTheirOwn_AndAHashedId()
    {
        var literal = Literal(_solutionGraph!, "waves.ini");

        // No file, line or project, for ExternalType's reason: a value used in two
        // places has no single site, and putting one of them on the node would
        // make the other one invisible.
        Assert.Null(literal.FilePath);
        Assert.Null(literal.LineStart);
        Assert.Null(literal.GetProperty<string>("project"));

        // The id cannot be the text -- values hold quotes, colons and newlines.
        Assert.StartsWith("lit:", literal.Id, StringComparison.Ordinal);
        Assert.Equal(4 + 16, literal.Id.Length);
        Assert.DoesNotContain("waves.ini", literal.Id, StringComparison.Ordinal);

        // Stable across builds rather than per-process: the same value in a graph
        // built separately has the same id.
        Assert.Equal(literal.Id, Literal(_webOnlyGraph!, "waves.ini").Id);
    }

    [Fact]
    public void MentionsEdges_CarryTheLineAndTheProvenance()
    {
        // The same two facts a Quotes edge carries, and for the same reason: a
        // nameof and a typed literal of one value are one string and two different
        // situations, and the line is what lets a reader go and look.
        var typed = Assert.Single(MentionsFrom("TypedName"));
        Assert.Equal("CatalogStore", _solutionGraph!.GetNode(typed.ToId)!.Name);
        Assert.Equal("literal", typed.GetProperty<string>("provenance"));
        Assert.True(typed.GetProperty<int>("line") > 0);

        var interpolated = Assert.Single(MentionsFrom("Interpolated"));
        Assert.Equal("Normalize", _solutionGraph.GetNode(interpolated.ToId)!.Name);
        Assert.Equal("interpolated", interpolated.GetProperty<string>("provenance"));

        // An attribute argument is read by the framework, not by this assembly, so
        // it lands with its own provenance rather than as a plain literal.
        var attributed = Assert.Single(MentionsFrom("Attributed"));
        Assert.Equal("PriceBook", _solutionGraph.GetNode(attributed.ToId)!.Name);
        Assert.Equal("attributeArgument", attributed.GetProperty<string>("provenance"));

        var safe = Assert.Single(MentionsFrom("SafeName"));
        Assert.Equal("nameof", safe.GetProperty<string>("provenance"));
    }

    [Fact]
    public void LiteralIndex_SkipsStringsBelowTheThreeCharacterFloor()
    {
        // Attributed returns "ok". The floor is the scanner's, reused rather than
        // re-decided: "Id" and "To" are in every codebase and would bury the rest.
        // The method's ONE Mentions edge is the attribute argument, not the body.
        Assert.DoesNotContain(_solutionGraph!.Nodes, n => n.Type == NodeType.Literal && n.Name == "ok");
        Assert.Equal("PriceBook",
            _solutionGraph.GetNode(Assert.Single(MentionsFrom("Attributed")).ToId)!.Name);
    }

    [Fact]
    public void ExcludeLiterals_DropsTheIndexEntirely_AndLeavesQuotesAlone()
    {
        Assert.Empty(_noLiteralsGraph!.NodesOfType(NodeType.Literal));
        Assert.DoesNotContain(_noLiteralsGraph.Edges, e => e.Type == EdgeType.Mentions);

        // The option narrows one thing. Quotes runs off the same scan and must be
        // untouched by it, or "build it lean" would quietly change an answer that
        // has nothing to do with literals.
        Assert.Equal(
            _solutionGraph!.Edges.Count(e => e.Type == EdgeType.Quotes),
            _noLiteralsGraph.Edges.Count(e => e.Type == EdgeType.Quotes));
        Assert.Contains(_noLiteralsGraph.Edges, e => e.Type == EdgeType.Quotes);
    }

    [Fact]
    public void QuotesEdgeCount_IsUnchangedByTheLiteralIndex()
    {
        // The non-regression pin, and a hardcoded number on purpose. Every other
        // assertion here is about one method; this one is about the whole report,
        // and the hazard it guards is invisible from any single method: a Literal
        // node's Name IS its value, so if those nodes reached the uniqueness index
        // AddQuotesEdges builds, every matched name would collide with itself and
        // the Quotes edges would silently disappear. Nothing about "CatalogStore"
        // read alone would show it.
        Assert.Equal(5, _solutionGraph!.Edges.Count(e => e.Type == EdgeType.Quotes));
    }

    [Fact]
    public void FindLiterals_JoinsEachOccurrenceToItsProducingMembersFile()
    {
        var page = new GraphQuery(_solutionGraph!).FindLiterals("waves.ini");

        Assert.Equal(2, page.TotalMatches);
        Assert.Null(page.NextOffset);
        Assert.All(page.Rows, r =>
        {
            Assert.Equal("waves.ini", r.Value);
            // The file is the MEMBER's, since the Literal node has none.
            Assert.EndsWith("NameCoupling.cs", r.File, StringComparison.Ordinal);
            Assert.True(r.Line > 0);
            Assert.Equal("literal", r.Provenance);
        });

        // Substring, case-insensitively, and scoped to the quoting side's project.
        Assert.Equal(2, new GraphQuery(_solutionGraph!).FindLiterals("WAVES", project: "SampleWeb").TotalMatches);
        Assert.Equal(0, new GraphQuery(_solutionGraph!).FindLiterals("waves.ini", project: "SampleLib").TotalMatches);
    }

    /// <summary>
    /// The result's declared order, compared exactly as the query claims to sort:
    /// value, producing member, line, provenance, all ordinal.
    /// </summary>
    private static int CompareLiteralKeys(GraphQuery.LiteralMention a, GraphQuery.LiteralMention b)
    {
        var c = StringComparer.Ordinal.Compare(a.Value, b.Value);
        if (c != 0) return c;
        c = StringComparer.Ordinal.Compare(a.FromId, b.FromId);
        if (c != 0) return c;
        c = a.Line.CompareTo(b.Line);
        return c != 0 ? c : StringComparer.Ordinal.Compare(a.Provenance, b.Provenance);
    }

    [Fact]
    public void FindLiterals_OrdersTotally_SoPagingVisitsEveryRowExactlyOnce()
    {
        var query = new GraphQuery(_solutionGraph!);

        var whole = query.FindLiterals(limit: 1000);
        Assert.True(whole.TotalMatches > 20, $"fixture is too small to page: {whole.TotalMatches} rows");
        Assert.Null(whole.NextOffset);

        // STRICTLY increasing, which is the claim. Merely sorted would be satisfied
        // by ties, and a tie is exactly where an offset stops meaning one row --
        // the failure the coverage paging work found the hard way.
        for (var i = 1; i < whole.Rows.Count; i++)
            Assert.True(CompareLiteralKeys(whole.Rows[i - 1], whole.Rows[i]) < 0,
                $"rows {i - 1} and {i} do not increase: {whole.Rows[i - 1]} then {whole.Rows[i]}");

        var walked = new List<GraphQuery.LiteralMention>();
        int? offset = 0;
        var pages = 0;
        while (offset is { } from)
        {
            var page = query.FindLiterals(limit: 3, offset: from);
            Assert.Equal(whole.TotalMatches, page.TotalMatches);
            Assert.Equal(from, walked.Count);
            walked.AddRange(page.Rows);
            offset = page.NextOffset;
            Assert.True(++pages <= whole.TotalMatches, "paging did not terminate");
        }

        // Every row, once, in the order one whole page gives.
        Assert.Equal(whole.Rows, walked);
    }

    [Fact]
    public void HasLiteralIndex_SeparatesNoMatchFromNoIndex()
    {
        // Zero rows has two causes and only one of them is a fact about the code.
        Assert.True(new GraphQuery(_solutionGraph!).HasLiteralIndex());
        Assert.False(new GraphQuery(_noLiteralsGraph!).HasLiteralIndex());
        Assert.Equal(0, new GraphQuery(_noLiteralsGraph!).FindLiterals("waves.ini").TotalMatches);
    }

    [Fact]
    public void AttributedDeclarations_ReportTheirOwnLine_NotTheAttributes()
    {
        // Attributes are part of the declaration node, not leading trivia, so a
        // syntax reference's span starts at the '[' and every attributed node
        // reported the attribute's line. Asserted against the source itself
        // rather than a hardcoded number, so editing the fixture cannot make
        // this pass by accident.
        var attributedType = Type(_solutionGraph!, "SampleLib.Greeter");
        Assert.Contains("class Greeter", SourceLine(attributedType), StringComparison.Ordinal);

        var attributedMethod = Method(_solutionGraph!, "SampleWeb.Tests.GreeterTests", "Greet_ThroughTheInterface");
        Assert.Contains("void Greet_ThroughTheInterface", SourceLine(attributedMethod), StringComparison.Ordinal);

        // An unattributed declaration was always right and must stay right.
        Assert.Contains("string Greet(", SourceLine(Method(_solutionGraph!, "SampleLib.Greeter", "Greet")));
    }

    /// <summary>The one line of source a node points at, read from disk.</summary>
    private static string SourceLine(GraphNode node) =>
        File.ReadAllLines(node.FilePath!)[node.LineStart!.Value - 1];

    [Fact]
    public void Methods_SayWhenTheirAccessibilityIsNotTheirOwn()
    {
        // An override takes the base declaration's accessibility; a positional
        // record's constructor takes the type header's. The visibility audit
        // needs both facts to stop offering edits that have no line.
        var primaryCtor = _solutionGraph!.GetNode("m:SampleLib.PriceTag..ctor(string,decimal)");
        Assert.NotNull(primaryCtor);
        Assert.True(primaryCtor!.GetProperty<bool>("isPrimaryConstructor"));

        Assert.True(Method(_solutionGraph, "SampleLib.Greeter", "ToString").GetProperty<bool>("isOverride"));

        var ordinary = Method(_solutionGraph, "SampleLib.Greeter", "Greet");
        Assert.False(ordinary.GetProperty<bool>("isOverride"));
        Assert.False(ordinary.GetProperty<bool>("isPrimaryConstructor"));
    }

    [Fact]
    public void CoversEdges_FollowInterfaceDispatch()
    {
        // The test binds to IGreeter.Greet and never names Greeter.Greet. Coverage
        // used to park on the interface member and report the implementation as
        // untested; the implementation is what runs, so it is what the test covers.
        var query = new GraphQuery(_solutionGraph!);
        var declaration = Method(_solutionGraph!, "SampleLib.IGreeter", "Greet");
        var implementation = Method(_solutionGraph!, "SampleLib.Greeter", "Greet");
        var beyond = Method(_solutionGraph!, "SampleLib.Greeter", "Shape");

        var onDeclaration = Assert.Single(query.GetCoveringTests(declaration.Id));
        var onImplementation = Assert.Single(query.GetCoveringTests(implementation.Id));
        var onBeyond = Assert.Single(query.GetCoveringTests(beyond.Id));

        Assert.Equal("Greet_ThroughTheInterface", onImplementation.Test.Name);
        // The dispatch step costs a hop, so depth keeps its meaning of "calls away".
        Assert.Equal(1, onDeclaration.Depth);
        Assert.Equal(2, onImplementation.Depth);
        Assert.Equal(3, onBeyond.Depth);
    }

    [Fact]
    public void CoversEdges_AreNotEmittedForNonTestCallers()
    {
        var orphan = Method(_solutionGraph!, "SampleLib.CatalogStore", "Orphan");

        // Orphan is called only by NotATest, which has no test attribute.
        Assert.DoesNotContain(_solutionGraph!.Incoming(orphan.Id), e => e.Type == EdgeType.Covers);
    }

    [Fact]
    public void CoversEdges_FlowThroughLifecycleSetup()
    {
        var query = new GraphQuery(_solutionGraph!);
        var preload = Method(_solutionGraph!, "SampleLib.CatalogStore", "Preload");

        // Preload is called only from LifecycleCatalogTests.InitializeAsync,
        // which xUnit runs around each test — no test method calls it. Before
        // lifecycle seeding this reported Preload as uncovered while runtime
        // coverage showed it exercised.
        var covering = query.GetCoveringTests(preload.Id).ToList();

        Assert.Single(covering);
        Assert.Equal("Preload_CountsCatalogs", covering[0].Test.Name);
        Assert.Equal(1, covering[0].Depth);
    }

    [Fact]
    public void LifecycleHooks_AreFlagged_ButAreNotTests()
    {
        var initialize = Method(_solutionGraph!, "SampleWeb.Tests.LifecycleCatalogTests", "InitializeAsync");

        Assert.True(initialize.GetProperty<bool>("isTestLifecycle"));
        Assert.False(initialize.GetProperty<bool>("isTest"));
        // The hook itself must never be the source of a Covers edge.
        Assert.DoesNotContain(_solutionGraph!.Outgoing(initialize.Id), e => e.Type == EdgeType.Covers);
    }

    [Fact]
    public void Dispose_OnProductionTypes_IsNotFlaggedAsLifecycle()
    {
        // CatalogStore is IDisposable but has no tests on the type, so its
        // Dispose must not be flagged — the gate that keeps every production
        // Dispose out of the lifecycle set.
        var dispose = Method(_solutionGraph!, "SampleLib.CatalogStore", "Dispose");

        Assert.False(dispose.GetProperty<bool>("isTestLifecycle"));
    }

    [Fact]
    public void CoversEdges_FlowThroughCtorSetup()
    {
        var warm = Method(_solutionGraph!, "SampleLib.CatalogStore", "Warm");

        // Warm is called only from CtorSetupCatalogTests' constructor, which
        // xUnit runs before every test — no test method calls it.
        var covering = new GraphQuery(_solutionGraph!).GetCoveringTests(warm.Id).ToList();

        Assert.Single(covering);
        Assert.Equal("Warm_IsPositive", covering[0].Test.Name);
        Assert.Equal(1, covering[0].Depth);
    }

    [Fact]
    public void TestClassCtor_IsLifecycle_AndNeverCovered()
    {
        var ctor = Method(_solutionGraph!, "SampleWeb.Tests.CtorSetupCatalogTests", ".ctor");

        Assert.True(ctor.GetProperty<bool>("isTestLifecycle"));
        Assert.False(ctor.GetProperty<bool>("isTest"));
        Assert.DoesNotContain(_solutionGraph!.Incoming(ctor.Id), e => e.Type == EdgeType.Covers);
    }

    [Fact]
    public void ProductionCtor_IsCovered_AndSeedsItsCallees()
    {
        var query = new GraphQuery(_solutionGraph!);

        // InitializeAsync news up CatalogSession: the new-expression is a call
        // edge to the ctor, and Open is only reachable through it.
        var ctor = Method(_solutionGraph!, "SampleLib.CatalogSession", ".ctor");
        var ctorCovering = query.GetCoveringTests(ctor.Id).ToList();
        Assert.Single(ctorCovering);
        Assert.Equal("Preload_CountsCatalogs", ctorCovering[0].Test.Name);
        Assert.Equal(1, ctorCovering[0].Depth);

        var open = Method(_solutionGraph!, "SampleLib.CatalogSession", "Open");
        var openCovering = query.GetCoveringTests(open.Id).ToList();
        Assert.Single(openCovering);
        Assert.Equal(2, openCovering[0].Depth);
    }

    [Fact]
    public void ImplicitCtor_WithInitializers_IsANode_AndRunsThem()
    {
        var query = new GraphQuery(_solutionGraph!);

        // CatalogCache declares no ctor; its implicit ctor is a node because
        // the field initializer is the code it runs. Reaching Seed proves the
        // initializer's call was attributed to that ctor.
        var ctor = Method(_solutionGraph!, "SampleLib.CatalogCache", ".ctor");
        var ctorCovering = query.GetCoveringTests(ctor.Id).ToList();
        Assert.Single(ctorCovering);
        Assert.Equal("Cache_SeedsItself", ctorCovering[0].Test.Name);
        Assert.Equal(1, ctorCovering[0].Depth);

        var seed = Method(_solutionGraph!, "SampleLib.CatalogStore", "Seed");
        var seedCovering = query.GetCoveringTests(seed.Id).ToList();
        Assert.Single(seedCovering);
        Assert.Equal(2, seedCovering[0].Depth);
    }

    [Fact]
    public void ImplicitCtor_WithoutInitializers_IsNotANode()
    {
        // CatalogStore has no explicit ctor and no initializers: its implicit
        // ctor runs nothing, so a node for it would only ever read as noise.
        Assert.DoesNotContain(_solutionGraph!.Nodes, n =>
            n.Type == NodeType.Method &&
            n.Name == ".ctor" &&
            n.GetProperty<string>("declaringType") == "SampleLib.CatalogStore");
    }

    [Fact]
    public void CoversEdges_IncludeUsingDisposal()
    {
        var dispose = Method(_solutionGraph!, "SampleLib.CatalogStore", "Dispose");

        // The test body has `using var store = ...` and never calls Dispose
        // explicitly; the implicit disposal must still count as exercise.
        var covering = new GraphQuery(_solutionGraph!).GetCoveringTests(dispose.Id).ToList();

        Assert.Single(covering);
        Assert.Equal("List_ReturnsSortedCatalogs", covering[0].Test.Name);
        Assert.Equal(1, covering[0].Depth);
    }

    [Fact]
    public void CoversEdges_IncludeAwaitUsingDisposal_ThroughLifecycleSetup()
    {
        var disposeAsync = Method(_solutionGraph!, "SampleLib.CatalogSession", "DisposeAsync");

        // The compounding case: DisposeAsync is reached only via an await using
        // inside InitializeAsync — implicit call and lifecycle seeding together.
        var covering = new GraphQuery(_solutionGraph!).GetCoveringTests(disposeAsync.Id).ToList();

        Assert.Single(covering);
        Assert.Equal("Preload_CountsCatalogs", covering[0].Test.Name);
        Assert.Equal(1, covering[0].Depth);
    }

    [Fact]
    public void MethodNodes_CarryBodyDepth()
    {
        // NestedFlow.Tally is deliberately foreach > if > for > if.
        var tally = Method(_solutionGraph!, "SampleLib.NestedFlow", "Tally");
        Assert.Equal(4, tally.GetProperty<int>("bodyDepth"));

        // Flat methods carry no property at all; typed access defaults to 0.
        var zero = Method(_solutionGraph!, "SampleLib.NestedFlow", "Zero");
        Assert.False(zero.Properties.ContainsKey("bodyDepth"));
        Assert.Equal(0, zero.GetProperty<int>("bodyDepth"));
    }

    [Fact]
    public void FindDeepMethods_ReportsOnlyDeepBodies_DeepestFirst()
    {
        var deep = new GraphQuery(_solutionGraph!).FindDeepMethods(4, "SampleLib").ToList();

        Assert.Contains(deep, m => m.Name == "Tally");
        Assert.DoesNotContain(deep, m => m.Name == "List");
        Assert.Equal(deep.OrderByDescending(m => m.GetProperty<int>("bodyDepth")).Select(m => m.Id), deep.Select(m => m.Id));
    }

    [Fact]
    public void FindUncoveredMethods_ReportsTheUnreachedMethod()
    {
        var uncovered = new GraphQuery(_solutionGraph!)
            .FindUncoveredMethods("SampleLib")
            .Select(m => m.Id)
            .ToList();

        Assert.Contains(Method(_solutionGraph!, "SampleLib.CatalogStore", "Orphan").Id, uncovered);
        Assert.DoesNotContain(Method(_solutionGraph!, "SampleLib.CatalogStore", "List").Id, uncovered);
        Assert.DoesNotContain(Method(_solutionGraph!, "SampleLib.CatalogStore", "Normalize").Id, uncovered);
        Assert.DoesNotContain(Method(_solutionGraph!, "SampleLib.CatalogStore", "Preload").Id, uncovered);
    }

    [Fact]
    public void FindUncoveredMethods_ExcludesInterfaceDeclarations()
    {
        // ICatalogStore.List has no body, so it can never carry a Covers edge.
        // Reporting it as untested would be noise that discredits the report.
        var declaration = Method(_solutionGraph!, "SampleLib.ICatalogStore", "List");
        Assert.True(declaration.GetProperty<bool>("isAbstract"));

        var uncovered = new GraphQuery(_solutionGraph!)
            .FindUncoveredMethods("SampleLib")
            .Select(m => m.Id)
            .ToList();

        Assert.DoesNotContain(declaration.Id, uncovered);
    }

    // ---- Generated code ------------------------------------------------------

    [Fact]
    public void GeneratedRazorClasses_AreMarked_AndLinkedToTheirPage()
    {
        var page = _solutionGraph!.Nodes.Single(n => n.Type == NodeType.RazorPage);

        // The Razor-layer page node and the Roslyn-layer generated class are
        // two views of the same artifact; the compiledInto edge is the join.
        var links = _solutionGraph.Outgoing(page.Id)
            .Where(e => e.Type == EdgeType.References && e.GetProperty<bool>("compiledInto"))
            .Select(e => _solutionGraph.GetNode(e.ToId)!)
            .ToList();

        var generatedClass = Assert.Single(links);
        Assert.True(generatedClass.GetProperty<bool>("generated"));
        Assert.EndsWith(".g.cs", generatedClass.FilePath!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Index.cshtml",
            generatedClass.GetProperty<string>("generatedFrom")!.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandWrittenCode_IsNeverMarkedGenerated()
    {
        // The marker must not leak onto ordinary code: the PageModel lives in
        // Index.cshtml.cs, which is authored, not generated.
        var pageModel = _solutionGraph!.Nodes.Single(n => n.Type == NodeType.PageModel && n.Name == "IndexModel");
        Assert.False(pageModel.GetProperty<bool>("generated"));

        var onGet = Method(_solutionGraph!, "SampleWeb.Pages.IndexModel", "OnGet");
        Assert.False(onGet.GetProperty<bool>("generated"));
        Assert.EndsWith("Index.cshtml.cs", onGet.FilePath!.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    // ---- Excluding tests -----------------------------------------------------

    [Fact]
    public void NoTestsBuild_SkipsTestProjects_AndSaysSo()
    {
        var projects = _noTestsGraph!.NodesOfType(NodeType.Project).Select(p => p.Name).OrderBy(n => n).ToList();

        // The test project was never compiled, and the skip is on record —
        // a graph missing its tests must say so.
        Assert.Equal(new[] { "SampleLib", "SampleWeb" }, projects);
        Assert.Equal(new[] { "SampleWeb.Tests" }, _noTestsSkipped);
    }

    [Fact]
    public void NoTestsBuild_HasNoTestMethodsAndNoCoversEdges()
    {
        Assert.DoesNotContain(_noTestsGraph!.NodesOfType(NodeType.Method), m => m.GetProperty<bool>("isTest"));
        Assert.DoesNotContain(_noTestsGraph!.Edges, e => e.Type == EdgeType.Covers);

        // Production topology is intact: the cross-project call is still there.
        var onGet = Method(_noTestsGraph!, "SampleWeb.Pages.IndexModel", "OnGet");
        Assert.Contains(_noTestsGraph!.Outgoing(onGet.Id),
            e => e.Type == EdgeType.Calls && _noTestsGraph.GetNode(e.ToId)!.Name == "List");
    }

    [Fact]
    public void CoverageQueries_RefuseTheNoTestsGraph()
    {
        // The guard that keeps "tests excluded" from ever reading as
        // "everything is uncovered".
        var query = new GraphQuery(_noTestsGraph!);
        var load = Method(_noTestsGraph!, "SampleLib.CatalogStore", "List");

        Assert.Throws<InvalidOperationException>(() => query.FindUncoveredMethods("SampleLib"));
        Assert.Throws<InvalidOperationException>(() => query.GetCoveringTests(load.Id));
    }

    // ---- Members: properties, fields, statics --------------------------------

    private static bool HasEdge(CodeGraph graph, string fromId, EdgeType type, string toId) =>
        graph.Edges.Any(e => e.Type == type && e.FromId == fromId && e.ToId == toId);

    [Fact]
    public void MemberNodes_ExistAndAreContainedByTheirType()
    {
        var basePrice = _solutionGraph!.GetNode("prop:SampleLib.PriceBook.BasePrice");
        Assert.NotNull(basePrice);
        Assert.Equal(NodeType.Property, basePrice!.Type);
        Assert.Equal("SampleLib.PriceBook", basePrice.GetProperty<string>("declaringType"));
        Assert.Equal("decimal", basePrice.GetProperty<string>("memberType"));
        Assert.True(HasEdge(_solutionGraph, "type:SampleLib.PriceBook", EdgeType.Contains, basePrice.Id));

        // A private field is a node too — the read/write story of a type runs
        // through its fields, whatever their accessibility.
        var store = _solutionGraph.GetNode("field:SampleLib.PriceBook._store");
        Assert.NotNull(store);
        Assert.Equal(NodeType.Field, store!.Type);
        Assert.False(store.GetProperty<bool>("isPublic"));
    }

    [Fact]
    public void StaticAndConstMembers_AreNodes_AndMarked()
    {
        var lookups = _solutionGraph!.GetNode("field:SampleLib.PriceBook.Lookups");
        Assert.True(lookups!.GetProperty<bool>("isStatic"));
        Assert.False(lookups.GetProperty<bool>("isConst"));

        var region = _solutionGraph.GetNode("prop:SampleLib.PriceBook.Region");
        Assert.True(region!.GetProperty<bool>("isStatic"));

        var defaultRegion = _solutionGraph.GetNode("field:SampleLib.PriceBook.DefaultRegion");
        Assert.True(defaultRegion!.GetProperty<bool>("isConst"));
        Assert.True(defaultRegion.GetProperty<bool>("isStatic"));
    }

    [Fact]
    public void RecordPositionalProperties_AreNodes_CompilerPlumbingIsNot()
    {
        Assert.NotNull(_solutionGraph!.GetNode("prop:SampleLib.PriceTag.Label"));
        Assert.NotNull(_solutionGraph.GetNode("prop:SampleLib.PriceTag.Amount"));

        Assert.Null(_solutionGraph.GetNode("prop:SampleLib.PriceTag.EqualityContract"));
        // No auto-property backing field may leak through as a Field node.
        Assert.DoesNotContain(_solutionGraph.NodesOfType(NodeType.Field), f => f.Name.Contains("k__BackingField"));
    }

    [Fact]
    public void ReadsAndWrites_AreAttributedToTheAccessingMethod()
    {
        // The DI idiom: the ctor writes the field, the method reads it.
        var ctorId = Method(_solutionGraph!, "SampleLib.PriceBook", ".ctor").Id;
        Assert.True(HasEdge(_solutionGraph!, ctorId, EdgeType.Writes, "field:SampleLib.PriceBook._store"));

        var totalId = Method(_solutionGraph!, "SampleLib.PriceBook", "Total").Id;
        Assert.True(HasEdge(_solutionGraph!, totalId, EdgeType.Reads, "field:SampleLib.PriceBook._store"));
        Assert.True(HasEdge(_solutionGraph!, totalId, EdgeType.Reads, "prop:SampleLib.PriceBook.BasePrice"));
        Assert.True(HasEdge(_solutionGraph!, totalId, EdgeType.Writes, "prop:SampleLib.PriceBook.Current"));
    }

    [Fact]
    public void CompoundAssignment_YieldsBothReadAndWrite()
    {
        // Lookups++ reads the old value and writes the new one.
        var totalId = Method(_solutionGraph!, "SampleLib.PriceBook", "Total").Id;
        Assert.True(HasEdge(_solutionGraph!, totalId, EdgeType.Reads, "field:SampleLib.PriceBook.Lookups"));
        Assert.True(HasEdge(_solutionGraph!, totalId, EdgeType.Writes, "field:SampleLib.PriceBook.Lookups"));
    }

    [Fact]
    public void ComputedProperty_ReadsItsInputs()
    {
        // Markup => BasePrice * 2: accessor bodies attribute to the property
        // node, because accessors are not Method nodes.
        Assert.True(HasEdge(_solutionGraph!,
            "prop:SampleLib.PriceBook.Markup", EdgeType.Reads, "prop:SampleLib.PriceBook.BasePrice"));
    }

    [Fact]
    public void StaticInitializerAccess_IsNotAttributedAnywhere()
    {
        // Region's initializer reads DefaultRegion, but it runs in the static
        // ctor, which is not a node. No edge may claim that access.
        Assert.DoesNotContain(_solutionGraph!.Edges, e =>
            e.ToId == "field:SampleLib.PriceBook.DefaultRegion" && e.Type is EdgeType.Reads or EdgeType.Writes);
    }

    [Fact]
    public void MemberDeclaredTypes_BecomeReferencesEdges()
    {
        // "Who uses PriceTag" was unanswerable while types only participated
        // in signatures: now the property typed by it references it…
        Assert.True(HasEdge(_solutionGraph!,
            "prop:SampleLib.PriceBook.Current", EdgeType.References, "type:SampleLib.PriceTag"));

        // …including through a List<> wrapper. History lives in the second
        // half of a partial class: exactly ONE edge, not one per declaration —
        // each declaration's SymbolInfo carries the full member list, and the
        // MVVM-toolkit generator makes partials the norm in WPF view models.
        Assert.Single(_solutionGraph!.Edges, e =>
            e.Type == EdgeType.References
            && e.FromId == "prop:SampleLib.PriceBook.History"
            && e.ToId == "type:SampleLib.PriceTag");
    }

    [Fact]
    public void MemberRead_CrossesProjectBoundary_InSolutionGraph()
    {
        // The member twin of Builds_CallEdge_FromWebProjectIntoClassLibrary:
        // OnGet reads SampleLib's static field across the project boundary.
        var onGetId = Method(_solutionGraph!, "SampleWeb.Pages.IndexModel", "OnGet").Id;
        Assert.True(HasEdge(_solutionGraph!, onGetId, EdgeType.Reads, "field:SampleLib.PriceBook.Lookups"));

        // And the control: the single-project build has no node to anchor it.
        var webOnlyOnGet = Method(_webOnlyGraph!, "SampleWeb.Pages.IndexModel", "OnGet").Id;
        Assert.DoesNotContain(_webOnlyGraph!.Edges, e =>
            e.FromId == webOnlyOnGet && e.Type == EdgeType.Reads && e.ToId.StartsWith("field:SampleLib."));
    }

    // ---- C# 14 extension members ---------------------------------------------

    // An extension block is a TypeDeclarationSyntax with an empty name, so the
    // type walk visits it and the compiler-generated filter used to drop it —
    // taking every member with it. The containing static class still advertised
    // the lowered implementation names in its methods list, so the type looked
    // present and nothing signalled that the answer was partial.

    [Fact]
    public void ExtensionBlockMethods_AreNodesUnderTheContainingType()
    {
        // Keyed off the lowered implementation, so the id is the same shape a
        // classic extension method gets: containing static class, receiver
        // first in the parameter list.
        var doubled = _solutionGraph!.GetNode("m:SampleLib.PriceExtensions.Doubled(SampleLib.Price)");
        Assert.NotNull(doubled);
        Assert.Equal(NodeType.Method, doubled!.Type);
        Assert.Equal("SampleLib.PriceExtensions", doubled.GetProperty<string>("declaringType"));
        Assert.True(HasEdge(_solutionGraph, "type:SampleLib.PriceExtensions", EdgeType.Contains, doubled.Id));

        // A static extension member carries no receiver parameter, exactly as
        // its implementation does not.
        var fromCents = _solutionGraph.GetNode("m:SampleLib.PriceExtensions.FromCents(int)");
        Assert.NotNull(fromCents);
        Assert.True(HasEdge(_solutionGraph, "type:SampleLib.PriceExtensions", EdgeType.Contains, fromCents!.Id));

        // The control: the classic syntax in the same class, same id shape.
        Assert.NotNull(_solutionGraph.GetNode("m:SampleLib.PriceExtensions.Tripled(SampleLib.Price)"));
    }

    [Fact]
    public void ExtensionBlockProperties_AreNodesUnderTheContainingType()
    {
        var isFree = _solutionGraph!.GetNode("prop:SampleLib.PriceExtensions.IsFree(SampleLib.Price)");
        Assert.NotNull(isFree);
        Assert.Equal(NodeType.Property, isFree!.Type);
        Assert.True(HasEdge(_solutionGraph, "type:SampleLib.PriceExtensions", EdgeType.Contains, isFree.Id));

        var free = _solutionGraph.GetNode("prop:SampleLib.PriceExtensions.Free()");
        Assert.NotNull(free);
        Assert.True(free!.GetProperty<bool>("isStatic"));
    }

    [Fact]
    public void ExtensionBlockCalls_BindToTheDeclarationNode()
    {
        Assert.True(HasEdge(_solutionGraph!,
            "m:SampleLib.PriceCallers.AskDoubled(SampleLib.Price)",
            EdgeType.Calls,
            "m:SampleLib.PriceExtensions.Doubled(SampleLib.Price)"));

        Assert.True(HasEdge(_solutionGraph!,
            "m:SampleLib.PriceCallers.AskFromCents(int)",
            EdgeType.Calls,
            "m:SampleLib.PriceExtensions.FromCents(int)"));
    }

    [Fact]
    public void ExtensionBlockCalls_CrossTheProjectBoundary()
    {
        // The gamehub shape: every call site in another project, which is why
        // the visibility audit called the containing type unused.
        Assert.True(HasEdge(_solutionGraph!,
            "m:SampleWeb.PriceUse.DoubledAcrossTheBoundary(SampleLib.Price)",
            EdgeType.Calls,
            "m:SampleLib.PriceExtensions.Doubled(SampleLib.Price)"));

        Assert.True(HasEdge(_solutionGraph!,
            "m:SampleWeb.PriceUse.MadeAcrossTheBoundary(int)",
            EdgeType.Calls,
            "m:SampleLib.PriceExtensions.FromCents(int)"));

        // The consequence the defect actually cost: the type has consumers.
        Assert.Contains(_solutionGraph!.Incoming("m:SampleLib.PriceExtensions.Doubled(SampleLib.Price)"),
            e => e.Type == EdgeType.Calls);
    }

    [Fact]
    public void ExtensionBlockMembers_CarryExtendsToTheirReceiver()
    {
        var doubled = _solutionGraph!.GetNode("m:SampleLib.PriceExtensions.Doubled(SampleLib.Price)");
        Assert.Equal("SampleLib.Price", doubled!.GetProperty<string>("extendsType"));
        Assert.True(HasEdge(_solutionGraph, doubled.Id, EdgeType.Extends, "type:SampleLib.Price"));

        // A static extension member extends its type just as much.
        var fromCents = _solutionGraph.GetNode("m:SampleLib.PriceExtensions.FromCents(int)");
        Assert.Equal("SampleLib.Price", fromCents!.GetProperty<string>("extendsType"));
    }

    [Fact]
    public void ExtensionBlockItself_IsNotANode()
    {
        // The block is syntax, not something a reader navigates to: its members
        // belong to the static class that holds it.
        Assert.DoesNotContain(_solutionGraph!.Nodes, n => n.Id.Contains(".extension("));
        Assert.DoesNotContain(_solutionGraph.Nodes, n => n.Name.Length == 0);
    }

    [Fact]
    public void ContainingType_AdvertisesDeclaredMembers_NotLoweredAccessors()
    {
        // The dangerous half of the original defect: the type advertised
        // get_Files and SourceFor in its own properties while none of them was
        // a node, so the type looked present and complete.
        var host = _solutionGraph!.GetNode("type:SampleLib.PriceExtensions");
        var advertised = host!.GetProperty<List<string>>("methods") ?? [];

        Assert.DoesNotContain("get_IsFree", advertised);
        Assert.DoesNotContain("get_Free", advertised);
        Assert.Contains("Doubled", advertised);
        Assert.Contains("FromCents", advertised);

        // And the invariant that would have caught this: every name the type
        // advertises has a node under it.
        var contained = _solutionGraph.Outgoing(host.Id)
            .Where(e => e.Type == EdgeType.Contains)
            .Select(e => _solutionGraph.GetNode(e.ToId)!.Name)
            .ToHashSet();
        Assert.All(advertised, name => Assert.Contains(name, contained));
    }

    // ---- Class dispatch: coverage across an override ---------------------------

    // The twin of the interface case. Nothing calls either override by name, so
    // every assertion here fails unless the walk crosses the override relation.
    // Until 2026-09-05 it did not, and the result was not a smaller answer but a
    // wrong one: every override in a solution reported zero tests.

    private const string AbstractAdvance = "m:SampleLib.Doctrine.Advance(int)";
    private const string PatrolAdvance = "m:SampleLib.PatrolDoctrine.Advance(int)";
    private const string EscortAdvance = "m:SampleLib.EscortDoctrine.Advance(int)";

    [Fact]
    public void Overrides_CarryMethodLevelOverridesEdges()
    {
        // The edge the graph did not have. Inherits is class-to-class and
        // cannot say which body runs.
        Assert.True(HasEdge(_solutionGraph!, PatrolAdvance, EdgeType.Overrides, AbstractAdvance));
        Assert.True(HasEdge(_solutionGraph!, EscortAdvance, EdgeType.Overrides, PatrolAdvance));

        // The whole chain, not just one hop: the leaf reaches the root too, so a
        // caller of the root reaches the leaf that actually runs.
        Assert.True(HasEdge(_solutionGraph!, EscortAdvance, EdgeType.Overrides, AbstractAdvance));

        var direct = _solutionGraph!.Outgoing(EscortAdvance)
            .Single(e => e.Type == EdgeType.Overrides && e.ToId == PatrolAdvance);
        Assert.Equal(1, direct.GetProperty<int>("depth"));
    }

    [Fact]
    public void Overrides_AreNotReportedAsInterfaceImplementations()
    {
        // Deliberately a separate edge type. "What interfaces does this
        // implement" is a question people ask of Implements, and answering it
        // with overrides mixed in would be a fuller answer that is also wrong.
        Assert.DoesNotContain(_solutionGraph!.Outgoing(PatrolAdvance), e => e.Type == EdgeType.Implements);
    }

    // ---- Type-level interface implementation ---------------------------------
    //
    // "Which classes implement this interface" is the question a reader asks
    // FIRST, and until 2026-09-11 the graph answered it with a well-formed zero:
    // the relation existed only between MEMBERS, so the interface TYPE had no
    // incoming Implements at all. Every assertion below fails without the
    // type-level pass, and each shape here reaches the interface by a different
    // route, because handling one route is what makes a partial answer look
    // complete.

    private const string Sink = "type:SampleLib.ISink";
    private const string AuditSink = "type:SampleLib.IAuditSink";
    private const string SinkBase = "type:SampleLib.SinkBase";
    private const string RelaySink = "type:SampleLib.RelaySink";
    private const string LedgerSink = "type:SampleLib.LedgerSink";
    private const string ScratchSink = "type:SampleLib.ScratchSink";
    private const string HiddenSink = "type:SampleLib.HiddenSink";

    [Fact]
    public void TypeLevelImplements_NamesEveryImplementingClass()
    {
        // The question, asked at the node a reader actually tries.
        var implementers = _solutionGraph!.Incoming(Sink)
            .Where(e => e.Type == EdgeType.Implements)
            .Select(e => e.FromId)
            .ToList();

        Assert.Contains(SinkBase, implementers);    // declares it outright
        Assert.Contains(RelaySink, implementers);   // through a base CLASS
        Assert.Contains(LedgerSink, implementers);  // through a base INTERFACE
        Assert.Contains(ScratchSink, implementers); // beside an out-of-solution one
        Assert.Contains(HiddenSink, implementers);  // explicit implementation
    }

    [Fact]
    public void TypeLevelImplements_CarryTheDistanceToTheDeclaration()
    {
        // Depth is what keeps both questions askable: "what does this class
        // itself declare" is depth 1, and a flat list cannot answer it.
        Assert.Equal(1, Implements(SinkBase, Sink).GetProperty<int>("depth"));
        Assert.Equal(1, Implements(LedgerSink, AuditSink).GetProperty<int>("depth"));

        // Reached through the base class, and through the base interface. Both
        // are two hops, and both read as zero before this change.
        Assert.Equal(2, Implements(RelaySink, Sink).GetProperty<int>("depth"));
        Assert.Equal(2, Implements(LedgerSink, Sink).GetProperty<int>("depth"));
    }

    [Fact]
    public void TypeLevelImplements_RecordsInterfaceInheritance_WhichInheritsCannotSee()
    {
        // Inherits is driven by BaseType, which is null on an interface, so
        // without this edge nothing in the graph records that IAuditSink is an
        // ISink at all.
        Assert.Equal(1, Implements(AuditSink, Sink).GetProperty<int>("depth"));
        Assert.DoesNotContain(_solutionGraph!.Outgoing(AuditSink), e => e.Type == EdgeType.Inherits);
    }

    [Fact]
    public void TypeLevelImplements_SkipsInterfacesOutsideTheSolution()
    {
        // The control, and the only case that separates "emit an edge per
        // declared interface" from "emit one per interface that is a node".
        // ScratchSink declares ISink and System.IComparable on one line.
        Assert.NotNull(Implements(ScratchSink, Sink));
        Assert.DoesNotContain(
            _solutionGraph!.Outgoing(ScratchSink),
            e => e.Type == EdgeType.Implements && e.ToId.Contains("IComparable", StringComparison.Ordinal));
    }

    [Fact]
    public void TypeNodes_ListEveryInterface_IncludingTheOnesWithNoNode()
    {
        // The node's own answer, so a reader who has one node in hand does not
        // need a second call. baseType said "object" for all of these.
        var scratch = Type(_solutionGraph!, "SampleLib.ScratchSink");
        var interfaces = scratch.GetProperty<List<string>>("interfaces");

        Assert.NotNull(interfaces);
        Assert.Contains("SampleLib.ISink", interfaces);

        // Kept here precisely because it gets no edge: the list is a fact about
        // the declaration, and an absent far end does not make it untrue.
        Assert.Contains("System.IComparable", interfaces);
        Assert.Equal("object", scratch.GetProperty<string>("baseType"));
    }

    [Fact]
    public void TypeLevelImplements_ResolvesAGenericInterfaceToItsDefinition()
    {
        // CountTally implements ITally<int>; the node that exists is ITally<T>.
        // Recording what is written at the use site would produce a name that
        // matches no node, so the generic case would fail silently while every
        // non-generic one passed.
        var tally = Type(_solutionGraph!, "SampleLib.ITally<T>");
        var implementers = _solutionGraph!.Incoming(tally.Id)
            .Where(e => e.Type == EdgeType.Implements)
            .Select(e => e.FromId)
            .ToList();

        Assert.Contains("type:SampleLib.CountTally", implementers);
    }

    // ---- XAML views ----------------------------------------------------------
    //
    // A desktop app's view layer was absent from the graph entirely: measured on
    // one app, 8 files and 248 bindings contributing nothing, which left every
    // member reachable only from markup reading as used by tests alone. XAML has
    // no compiler for its bindings, so a renamed property leaves an expression
    // that resolves to nothing and fails by drawing an empty control at runtime.

    private const string SinkViewId = "xaml:SampleLib/Views/SinkView.xaml";

    private GraphEdge DataBinds(string toId) =>
        _solutionGraph!.Outgoing(SinkViewId).Single(e => e.Type == EdgeType.DataBinds && e.ToId == toId);

    [Fact]
    public void XamlViews_AreNodes_ScopedByProject()
    {
        var view = _solutionGraph!.GetNode(SinkViewId);

        Assert.NotNull(view);
        Assert.Equal(NodeType.XamlView, view!.Type);
        Assert.Equal("SinkView.xaml", view.Name);
        Assert.Equal("SampleLib", view.GetProperty<string>("project"));
        Assert.Equal("SampleLib.SinkView", view.GetProperty<string>("codeBehind"));
    }

    [Fact]
    public void XamlView_WithNoCodeBehind_SaysSoByAbsence()
    {
        // A theme declares no type. Absent rather than empty, so it is
        // distinguishable from a file whose x:Class could not be read.
        var theme = _solutionGraph!.GetNode("xaml:SampleLib/Themes/Palette.xaml");

        Assert.NotNull(theme);
        Assert.False(theme!.Properties.ContainsKey("codeBehind"));
        Assert.Equal(0, theme.GetProperty<int>("bindingCount"));
    }

    [Fact]
    public void DataBinds_ReachesTheViewModelMember_HoweverTheBindingIsWritten()
    {
        // Three spellings of one member: positional, Path=, and a RelativeSource
        // walk whose leading DataContext is the route rather than the member.
        // One relationship, so one edge, with the other two sites counted.
        var edge = DataBinds("prop:SampleLib.SinkViewModel.Channel");

        Assert.Equal(3, edge.GetProperty<int>("sites"));
        Assert.Equal(1, edge.GetProperty<int>("candidates"));
        Assert.True(edge.GetProperty<int>("line") > 0);
    }

    [Fact]
    public void DataBinds_ReachesAMemberOfTheCodeBehindClass()
    {
        // Nothing classified as a view model declares a Caption, so this binding
        // is reachable only through the type named by x:Class. A candidate walk
        // over view models alone would report it unresolved — a false finding,
        // which is worse than the missing edge it replaces.
        Assert.Equal(1, DataBinds("prop:SampleLib.SinkView.Caption").GetProperty<int>("candidates"));
    }

    [Fact]
    public void DataBinds_TakesTheFirstSegmentOfADottedPath()
    {
        // {Binding Write.Length} — Write is what the DataContext supplies; Length
        // belongs to whatever type that is, and resolving it would mean matching a
        // name against a type this layer has no model of.
        Assert.Equal(1, DataBinds("m:SampleLib.SinkViewModel.Write(int)").GetProperty<int>("candidates"));
    }

    [Fact]
    public void DataBinds_ResolvesAgainstTheTypeATemplateDeclares()
    {
        // LedgerSink is an ordinary class, not named like a view model, so the
        // name heuristic cannot reach its members at all. DataType is the author
        // stating the context, which turns a candidate set into an answer.
        Assert.Equal(1, DataBinds("m:SampleLib.LedgerSink.Seal()").GetProperty<int>("candidates"));
    }

    [Fact]
    public void DataBinds_FindsAMemberTheDeclaredTypeInherits()
    {
        // A member node records the type that DECLARES it, so Origin is filed
        // under SinkOrigin and not under DerivedSink. Without a base walk this
        // ordinary binding is reported as a member that does not exist — a defect
        // claim naming a real type, which is the failure mode that matters.
        Assert.Equal(1, DataBinds("prop:SampleLib.SinkOrigin.Origin").GetProperty<int>("candidates"));

        Assert.DoesNotContain(
            _solutionGraph!.GetNode(SinkViewId)!.GetProperty<List<string>>("missingOnDataType")!,
            n => n.EndsWith(".Origin", StringComparison.Ordinal));
    }

    [Fact]
    public void ABindingMissingFromItsDeclaredDataType_IsSeparatedFromAMereUnknown()
    {
        // The two facts a reader acts on differently. With a declared DataType,
        // a name the type does not have is a defect — the author said what the
        // context is. Without one, an unmatched name may only mean the DataContext
        // is a type outside the candidate bound.
        var view = _solutionGraph!.GetNode(SinkViewId)!;

        var missing = view.GetProperty<List<string>>("missingOnDataType");
        Assert.NotNull(missing);
        Assert.Contains("SampleLib.LedgerSink.Nonexistent", missing!);

        // And it is NOT filed under the weaker heading.
        Assert.DoesNotContain(
            view.GetProperty<List<string>>("unresolvedBindings")!,
            n => n.Contains("Nonexistent", StringComparison.Ordinal));
    }

    [Fact]
    public void ABindingThatLeavesTheTemplateContext_IsNotScopedToTheDeclaredType()
    {
        // RelativeSource walks out of the template, so the declared type is the
        // wrong answer rather than a better one. LedgerSink declares Write too,
        // so scoping this binding would resolve it to the wrong member and look
        // exactly like a success — which is the failure mode worth guarding.
        Assert.Contains(
            _solutionGraph!.Outgoing(SinkViewId),
            e => e.Type == EdgeType.DataBinds && e.ToId == "m:SampleLib.SinkViewModel.Write(int)");

        Assert.DoesNotContain(
            _solutionGraph!.Outgoing(SinkViewId),
            e => e.Type == EdgeType.DataBinds && e.ToId == "m:SampleLib.LedgerSink.Write(int)");
    }

    [Fact]
    public void ABindingThatNamesNothing_IsRecordedOnTheView()
    {
        // The failure XAML has no compiler for. Recorded rather than raised: this
        // layer cannot prove a typo, because a DataContext outside the candidate
        // bound produces the same zero — so it reports the name and leaves the
        // adjudicating to a reader who can open the file.
        var unresolved = _solutionGraph!.GetNode(SinkViewId)!.GetProperty<List<string>>("unresolvedBindings");

        Assert.NotNull(unresolved);
        Assert.Contains("Missing", unresolved!);

        // And these are not bindings at all, so they are not failures either:
        // {Binding} names the context object, {Binding Items[0]} names an indexer,
        // and Text="Channel" is a string. Reporting any of them as unresolved
        // would be the noise that teaches a reader to skim the list.
        Assert.DoesNotContain(unresolved, n => n.Contains("Items", StringComparison.Ordinal));

        // Seal is here because a NESTED template declares its own context. It
        // carries no DataType, so the context is unknown — and Seal exists on the
        // enclosing template's LedgerSink and on no view model, so walking past
        // the inner template would resolve it confidently to the wrong member
        // instead of admitting it does not know.
        // Rows is here for the same reason as Seal by a different construct: a
        // grid column binds against the row item, not against the context the
        // grid sits in. Scoping it to the enclosing LedgerSink would report
        // "LedgerSink has no member Rows" — a defect claim naming a real type,
        // and wrong.
        Assert.Equal(new[] { "Missing", "Rows", "Seal" }, unresolved);

        // Neither reaches the stronger list, which is the whole point: a
        // confident defect claim is only earned where the context is known.
        Assert.DoesNotContain(
            _solutionGraph!.GetNode(SinkViewId)!.GetProperty<List<string>>("missingOnDataType")!,
            n => n.EndsWith(".Rows", StringComparison.Ordinal) || n.EndsWith(".Seal", StringComparison.Ordinal));
    }

    [Fact]
    public void TypeNodes_SayWhetherTheyAreStatic()
    {
        // What makes a zero readable. A static class cannot be constructed by
        // anyone, so an empty incoming set is a fact about C# rather than about
        // the codebase — measured on one stack, sixteen of seventeen zero-degree
        // types were static classes and the most-called type in the whole
        // codebase was among them. Telling those apart needed a grep before this.
        var staticRoot = Type(_solutionGraph!, "SampleLib.PartRegistry");
        var instantiable = Type(_solutionGraph!, "SampleLib.PartFactory");

        Assert.True(staticRoot.GetProperty<bool>("isStatic"));
        Assert.False(instantiable.GetProperty<bool>("isStatic"));

        // Stamped on every type, like isInterface: absent means "built before the
        // flag existed", which a reader can tell from false.
        Assert.True(instantiable.Properties.ContainsKey("isStatic"));
    }

    [Fact]
    public void TypeLevelImplements_IsEmittedForEveryClassifiedKind_NotOnlyPlainClasses()
    {
        // The classifier routes a type by name — page model, controller, service,
        // view model, plain class — and only two of those branches recorded the
        // interfaces the type implements. So the edge appeared for ordinary
        // classes and silently not for the rest: the same partial answer this
        // change set exists to remove, occurring one level up in the classifier
        // rather than in the interface walk itself.
        var viewModel = Type(_solutionGraph!, "SampleLib.SinkViewModel");

        Assert.Equal(NodeType.ViewModel, viewModel.Type);
        Assert.Equal(1, Implements(viewModel.Id, Sink).GetProperty<int>("depth"));
    }

    [Fact]
    public void PropertyImplementations_CarryTheirOwnImplementsEdge()
    {
        // The quietest of the three gaps, because it degraded by degree: an
        // interface with a method AND a property still reported implementers
        // through the method, so the answer looked complete while being partial.
        var implementers = _solutionGraph!.Incoming("prop:SampleLib.ISink.Channel")
            .Where(e => e.Type == EdgeType.Implements)
            .Select(e => e.FromId)
            .ToList();

        Assert.Contains("prop:SampleLib.LedgerSink.Channel", implementers);
        Assert.Contains("prop:SampleLib.ScratchSink.Channel", implementers);

        // Explicit implementation: the declared name carries the interface, so
        // anything matching on simple name misses it while the type plainly
        // implements the interface.
        Assert.Contains(implementers, id => id.Contains("HiddenSink", StringComparison.Ordinal));

        // The ABSTRACT declaration is the implementer, not the override that
        // replaces it: Roslyn maps the interface member to SinkBase.Channel, and
        // RelaySink.Channel merely overrides that. Recorded here because it is
        // the boundary of what this relation claims — "which body runs" for a
        // property is the override chain, and properties carry no Overrides edge
        // at all, so that question is still unanswerable. Methods got their
        // Overrides edge on 2026-09-05; the property twin does not exist.
        Assert.Contains("prop:SampleLib.SinkBase.Channel", implementers);
        Assert.DoesNotContain("prop:SampleLib.RelaySink.Channel", implementers);
    }

    [Fact]
    public void PropertyImplements_DoNotWidenCoverage_AndThatIsDeliberate()
    {
        // Pinned as a LIMIT, not asserted as a success. Reading a property is a
        // Reads edge and the Covers walk steps along Calls, so the property node
        // is never reached and there is nothing to widen from — SinkTests binds
        // to ISink.Channel and still covers no implementation.
        //
        // Here so that whoever makes the walk follow Reads finds a failing test
        // telling them what changed, rather than a silent shift in every
        // coverage number in the repository.
        var query = new GraphQuery(_solutionGraph!);

        Assert.Empty(query.GetCoveringTests("prop:SampleLib.LedgerSink.Channel"));
    }

    // ---- Construction --------------------------------------------------------
    //
    // `new X()` put no incoming edge on X. A new-expression already produced a
    // Calls edge to the CONSTRUCTOR, but a type whose constructor the compiler
    // supplied has no .ctor node, so the edge was dropped for want of a far end —
    // and the plainest types, the ones a composition root builds and passes as an
    // interface, were zero-degree nodes that read as dead.

    private const string PlainPart = "type:SampleLib.PlainPart";
    private const string SeededPart = "type:SampleLib.SeededPart";

    private List<GraphEdge> Constructs(string toId) =>
        _solutionGraph!.Incoming(toId).Where(e => e.Type == EdgeType.Constructs).ToList();

    [Fact]
    public void Constructs_ReachesATypeWhoseConstructorTheCompilerSupplied()
    {
        // The motivating case. PlainPart is built once, in a field initializer,
        // and has no constructor of its own.
        Assert.DoesNotContain(
            _solutionGraph!.Nodes,
            n => n.Id.StartsWith("m:SampleLib.PlainPart..ctor", StringComparison.Ordinal));

        var built = Constructs(PlainPart);

        // Attributed to the constructor, because that is what runs an initializer.
        Assert.Contains(built, e => e.FromId.Contains("PartFactory..ctor", StringComparison.Ordinal));
        Assert.All(built, e => Assert.True(e.GetProperty<int>("line") > 0));
    }

    [Fact]
    public void Constructs_ReachesATypeWithAPrimaryConstructor()
    {
        // Different syntax, same hole — pinned separately rather than assumed.
        Assert.Contains(
            Constructs("type:SampleLib.PrimaryPart"),
            e => e.FromId.Contains("PartFactory.Sized", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructs_SaysWhichImplementationTheCompositionRootPicked()
    {
        // The whole point of the edge. The interface's implementers are the
        // candidates; only this says which one production builds.
        Assert.Contains(
            Constructs(SeededPart),
            e => e.FromId.Contains("PartFactory.Pick", StringComparison.Ordinal));

        // And through a lambda body, which is not a scope of its own.
        Assert.Contains(
            Constructs(SeededPart),
            e => e.FromId.Contains("PartFactory.Later", StringComparison.Ordinal));
    }

    [Fact]
    public void Constructs_ResolvesAGenericToItsDefinition_AndCountsAnInitialiserOnce()
    {
        // new Holder<int> { Item = 5 } — the object initialiser hangs off the
        // creation node rather than being one, so this is a single site.
        var built = Constructs("type:SampleLib.Holder<T>");

        var edge = Assert.Single(built, e => e.FromId.Contains("PartFactory.Held", StringComparison.Ordinal));
        Assert.Null(edge.GetProperty<object>("sites"));
    }

    [Fact]
    public void Constructs_IsNotMintedForNamingATypeOrForAnUnboundTypeParameter()
    {
        // The assertions that stop the walk being wrong in a way that reads as
        // thorough. typeof names a type without building one — Registers is the
        // edge for "the framework will consult this" — and new T() resolves to no
        // constructor at all, so an edge would name a type the site does not build.
        Assert.DoesNotContain(Constructs(PlainPart), e => e.FromId.Contains("Named", StringComparison.Ordinal));

        // Resolve the node first. Asking a mistyped id for its edges returns an
        // empty list, and a "no Constructs edge" assertion would then pass for
        // the one reason this whole change set exists to stop trusting.
        var fresh = _solutionGraph!.Nodes.Single(n =>
            n.Id.StartsWith("m:SampleLib.PartFactory.Fresh", StringComparison.Ordinal));

        Assert.DoesNotContain(_solutionGraph!.Outgoing(fresh.Id), e => e.Type == EdgeType.Constructs);
    }

    [Fact]
    public void Constructs_ReachesATypeBuiltOnlyByAStaticFieldInitializer()
    {
        // The gap found by running the acceptance query on RazorGraph's own
        // source an hour after Constructs shipped: LuaGraphBuilder holds
        // `private static readonly IReadOnlyList<ILuaRule> LanguageRules =
        // [new DialectRule()];` and DialectRule still reported zero incoming.
        // The instance walk cannot see it, because a static initializer runs in
        // the static constructor and not in any instance one.
        var built = Constructs("type:SampleLib.StaticPart");

        var edge = Assert.Single(built);
        Assert.Equal("type:SampleLib.PartRegistry", edge.FromId);
        Assert.True(edge.GetProperty<int>("line") > 0);
    }

    [Fact]
    public void Constructs_ReachesATypeBuiltInAnExplicitStaticConstructor()
    {
        // Walked all along — a static ctor is a method declaration like any other
        // — and dropped all along, for want of a node to start from.
        Assert.Equal(
            "type:SampleLib.PartRegistry",
            Assert.Single(Constructs("type:SampleLib.FallbackPart")).FromId);
    }

    [Fact]
    public void StaticConstruction_StartsAtTheTypeAndMintsNoStaticConstructorNode()
    {
        // The design decision, pinned so it cannot be undone by accident. Minting
        // a .cctor node would name nothing a reader can open, and since nothing
        // models "first access runs it", every static constructor in the solution
        // would arrive in uncovered_methods as a confident untested claim.
        Assert.DoesNotContain(
            _solutionGraph!.Nodes,
            n => n.Id.Contains("..cctor", StringComparison.Ordinal)
                 || n.Id.EndsWith(".cctor", StringComparison.Ordinal));

        // So this is the one Constructs edge whose near end is a type. A reader
        // tells the shapes apart by the node kind, which responses already carry.
        var from = _solutionGraph!.GetNode(Assert.Single(Constructs("type:SampleLib.StaticPart")).FromId);

        Assert.NotNull(from);
        Assert.Equal(NodeType.Class, from!.Type);
    }

    /// <summary>
    /// The Implements edge between two types. Single rather than FirstOrDefault
    /// so a missing edge fails saying so, instead of a null that the next line
    /// reports as a depth mismatch.
    /// </summary>
    private GraphEdge Implements(string fromId, string toId) =>
        _solutionGraph!.Outgoing(fromId).Single(e => e.Type == EdgeType.Implements && e.ToId == toId);

    [Fact]
    public void CoveringTests_ReachTheOverrideThatActuallyRan()
    {
        // The acceptance test for the whole change. Both tests bind to the
        // abstract declaration; neither names an override.
        var query = new GraphQuery(_solutionGraph!);

        var onAbstract = query.GetCoveringTests(AbstractAdvance).ToList();
        Assert.NotEmpty(onAbstract);

        var onOverride = query.GetCoveringTests(PatrolAdvance).ToList();
        Assert.Contains(onOverride, t => t.Test.Name == "Advance_ThroughTheBaseDeclaration");

        // And the leaf, two hops from the declaration the test called.
        var onLeaf = query.GetCoveringTests(EscortAdvance).ToList();
        Assert.NotEmpty(onLeaf);
    }

    [Fact]
    public void UncoveredMethods_NoLongerListsEveryOverride()
    {
        // The sibling that shares the walk, and the one someone acts on: it
        // used to name every override in the solution as untested.
        var query = new GraphQuery(_solutionGraph!);
        var uncovered = query.FindUncoveredMethods("SampleLib").Select(m => m.Id).ToList();

        Assert.DoesNotContain(PatrolAdvance, uncovered);
        Assert.DoesNotContain(EscortAdvance, uncovered);
    }

    // ---- The span a reader needs ----------------------------------------------

    // A node used to say "line 91" and nothing else, so opening it meant
    // guessing how far down the member ran and how far up its documentation
    // started. lineDocs ?? lineStart through lineEnd is now the exact chunk.
    // Asserted against the file rather than against constants, so the test
    // proves the numbers point at the right text instead of matching whatever
    // the fixture happened to say the day it was written.

    private static string[] LinesOf(GraphNode node) => File.ReadAllLines(node.FilePath!);

    [Fact]
    public void DocumentedMethod_PointsAtItsDocCommentAndItsClosingBrace()
    {
        var node = Method(_solutionGraph!, "SampleLib.Throwing", "UnguardedThrow");
        var lines = LinesOf(node);

        Assert.NotNull(node.LineDocs);
        Assert.NotNull(node.LineEnd);
        Assert.True(node.LineDocs < node.LineStart, "documentation precedes the declaration");
        Assert.True(node.LineEnd >= node.LineStart, "the end is at or after the start");

        // The lines are 1-based; the array is not.
        Assert.StartsWith("///", lines[node.LineDocs!.Value - 1].TrimStart(), StringComparison.Ordinal);
        Assert.Contains("UnguardedThrow", lines[node.LineStart!.Value - 1], StringComparison.Ordinal);
        Assert.Equal("}", lines[node.LineEnd!.Value - 1].Trim());

        // And the whole point: that range is the member with its documentation.
        var chunk = lines[(node.LineDocs.Value - 1)..node.LineEnd.Value];
        Assert.Contains(chunk, l => l.Contains("<summary>", StringComparison.Ordinal));
        Assert.Contains(chunk, l => l.Contains("throw new CustomException", StringComparison.Ordinal));
    }

    [Fact]
    public void UndocumentedDeclaration_HasNoDocsLine_ButStillHasAnEnd()
    {
        // Absent lineDocs is a fact worth having, not a missing value: this
        // member is undocumented, and the node says so.
        var node = Method(_solutionGraph!, "SampleLib.PriceCallers", "AskIsFree");

        Assert.Null(node.LineDocs);
        Assert.NotNull(node.LineEnd);
        Assert.Contains("AskIsFree", LinesOf(node)[node.LineStart!.Value - 1], StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentedTypeAndMember_CarryTheirDocLineToo()
    {
        // Not methods only: the same span is on type, property and field nodes.
        var type = _solutionGraph!.GetNode("type:SampleLib.PriceExtensions")!;
        Assert.NotNull(type.LineDocs);
        Assert.StartsWith("///", LinesOf(type)[type.LineDocs!.Value - 1].TrimStart(), StringComparison.Ordinal);

        var member = _solutionGraph.GetNode("prop:SampleLib.PriceExtensions.IsFree(SampleLib.Price)")!;
        Assert.NotNull(member.LineDocs);
        Assert.NotNull(member.LineEnd);
        Assert.StartsWith("///", LinesOf(member)[member.LineDocs!.Value - 1].TrimStart(), StringComparison.Ordinal);
    }

    [Fact]
    public void AttributedMethod_StartsAtTheDeclaration_AndAnAttributeIsNotDocumentation()
    {
        // The two interact, so pin them together. lineStart deliberately points
        // at the declaration rather than at the '[' (attributes are part of the
        // declaration node, not leading trivia), and an attribute list does not
        // make a member documented — lineDocs stays absent, which is why an
        // attributed but undocumented member needs lineStart - 1 read by hand if
        // you want to see its attributes.
        var test = Method(_solutionGraph!, "SampleWeb.Tests.GreeterTests", "Greet_ThroughTheInterface");
        var lines = LinesOf(test);

        Assert.Contains("Greet_ThroughTheInterface", lines[test.LineStart!.Value - 1], StringComparison.Ordinal);
        Assert.StartsWith("[", lines[test.LineStart.Value - 2].TrimStart(), StringComparison.Ordinal);
        Assert.Null(test.LineDocs);

        // The class above it IS documented, so the two are genuinely distinguished
        // rather than lineDocs simply never being set in this fixture.
        Assert.NotNull(_solutionGraph!.GetNode("type:SampleWeb.Tests.GreeterTests")!.LineDocs);
    }

    // ---- Id scoping ----------------------------------------------------------

    [Fact]
    public void RazorAndAssetIds_AreScopedByProject()
    {
        var page = _solutionGraph!.Nodes.Single(n => n.Type == NodeType.RazorPage);

        Assert.StartsWith("page:SampleWeb/", page.Id);
        Assert.Equal("SampleWeb", page.GetProperty<string>("project"));

        var siteJs = _solutionGraph.Nodes.Single(n => n.Type == NodeType.JavaScriptFile && n.Name == "site.js");
        Assert.Equal("js:SampleWeb/wwwroot/js/site.js", siteJs.Id);
    }

    [Fact]
    public void SingleProjectBuild_KeepsUnscopedIds()
    {
        // Existing saved graphs and their ids must survive this change.
        var page = _webOnlyGraph!.Nodes.Single(n => n.Type == NodeType.RazorPage);

        Assert.StartsWith("page:Pages", page.Id);
        Assert.Contains(_webOnlyGraph.Nodes, n => n.Id == "js:wwwroot/js/site.js");
    }

    // ---- Inline scripts ------------------------------------------------------

    [Fact]
    public void Builds_InlineScriptNode_AlongsideTheExternalFile()
    {
        var scripts = _solutionGraph!.NodesOfType(NodeType.JavaScriptFile).ToList();

        var inline = scripts.SingleOrDefault(s => s.GetProperty<bool>("inline"));
        Assert.NotNull(inline);
        Assert.Contains("#inline-", inline!.Id);
        Assert.NotNull(inline.LineStart);

        // The external file is still its own node, not absorbed into the page.
        Assert.Contains(scripts, s => s.Name == "site.js" && !s.GetProperty<bool>("inline"));
    }

    [Fact]
    public void InlineScript_IsWiredToItsPage()
    {
        var inline = _solutionGraph!.NodesOfType(NodeType.JavaScriptFile).Single(s => s.GetProperty<bool>("inline"));

        var referencedBy = _solutionGraph.Incoming(inline.Id)
            .Where(e => e.Type == EdgeType.References)
            .Select(e => _solutionGraph.GetNode(e.FromId)!.Type)
            .ToList();

        Assert.Contains(NodeType.RazorPage, referencedBy);
    }

    [Fact]
    public void InlineScript_ParticipatesInServerToClientCoupling()
    {
        var inline = _solutionGraph!.NodesOfType(NodeType.JavaScriptFile).Single(s => s.GetProperty<bool>("inline"));

        var coupling = _solutionGraph.Incoming(inline.Id).SingleOrDefault(e => e.Type == EdgeType.ViewDataReadBy);

        // data-catalog-count is rendered from server state and read by the inline
        // block. Before inline extraction this handoff had no node to attach to.
        Assert.NotNull(coupling);
        var keys = Assert.IsType<List<string>>(coupling!.Properties["dataKeys"]);
        Assert.Contains("catalog-count", keys);
    }

    [Fact]
    public void InlineScript_ShowsUpInTheMismatchReport()
    {
        var mismatches = new GraphQuery(_solutionGraph!).FindServerToJsMismatches().ToList();

        Assert.Contains(mismatches, m => m.JsNode.GetProperty<bool>("inline"));
    }
}
