namespace RazorGraph.Tests;

using System.Text.Json;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Serialization;
using Xunit;

public class GraphSerializerTests
{
    private static CodeGraph BuildGraph()
    {
        var graph = new CodeGraph();

        var page = new GraphNode
        {
            Id = "page:Pages/Index.cshtml",
            Type = NodeType.RazorPage,
            Name = "Index",
            FilePath = @"C:\app\Pages\Index.cshtml",
            LineStart = 1,
            LineEnd = 20
        };
        page.SetProperty("modelType", "IndexModel");
        page.SetProperty("viewDataKeys", new List<string> { "Title", "Description" });
        graph.AddNode(page);

        graph.AddNode(new GraphNode { Id = "pm:App.IndexModel", Type = NodeType.PageModel, Name = "IndexModel" });
        graph.AddNode(new GraphNode { Id = "page:_Card", Type = NodeType.PartialView, Name = "_Card" });

        graph.AddEdge(new GraphEdge { FromId = "page:Pages/Index.cshtml", ToId = "pm:App.IndexModel", Type = EdgeType.PageServedBy });

        var partialEdge = new GraphEdge { FromId = "page:Pages/Index.cshtml", ToId = "page:_Card", Type = EdgeType.RendersPartial };
        partialEdge.Properties["line"] = 8;
        partialEdge.Properties["isTagHelper"] = true;
        graph.AddEdge(partialEdge);

        return graph;
    }

    [Fact]
    public void RoundTrip_PreservesCountsAndScalars()
    {
        var original = BuildGraph();

        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(original));

        Assert.Equal(original.Nodes.Count, restored.Nodes.Count);
        Assert.Equal(original.Edges.Count, restored.Edges.Count);

        var page = restored.GetNode("page:Pages/Index.cshtml")!;
        Assert.Equal(NodeType.RazorPage, page.Type);
        Assert.Equal(1, page.LineStart);
        Assert.Equal(20, page.LineEnd);
    }

    [Fact]
    public void RoundTrip_KeepsATextFilesKindAndLines()
    {
        var graph = new CodeGraph { BuiltAt = DateTimeOffset.UtcNow };
        var node = new GraphNode
        {
            Id = "text:docs/notes.md", Type = NodeType.TextFile, Name = "notes.md",
            FilePath = @"C:\app\docs\notes.md", LineStart = 1, LineEnd = 3
        };
        node.SetProperty("extension", ".md");
        node.SetProperty("sizeBytes", 31);
        node.SetProperty("lines", new List<string> { "# Notes", "", "Deploy with main.bicep" });
        graph.AddNode(node);

        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(graph)).GetNode("text:docs/notes.md")!;

        Assert.Equal(NodeType.TextFile, restored.Type);
        Assert.Null(restored.ForeignType);
        Assert.Equal(3, restored.LineEnd);
        Assert.Equal(["# Notes", "", "Deploy with main.bicep"], restored.GetProperty<List<string>>("lines"));
        Assert.Equal(".md", restored.GetProperty<string>("extension"));
        Assert.Equal(31, restored.GetProperty<int>("sizeBytes"));
    }

    [Fact]
    public void RoundTrip_PreservesTypedPropertyAccess()
    {
        // Regression: deserialized properties were left as JsonElement, making
        // every typed GetProperty call return default on a loaded graph.
        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(BuildGraph()));

        var page = restored.GetNode("page:Pages/Index.cshtml")!;
        Assert.Equal("IndexModel", page.GetProperty<string>("modelType"));
        Assert.Equal(new List<string> { "Title", "Description" }, page.GetProperty<List<string>>("viewDataKeys"));

        var edge = restored.Outgoing("page:Pages/Index.cshtml").First(e => e.Type == EdgeType.RendersPartial);
        Assert.Equal(8, edge.GetProperty<int>("line"));
        Assert.True(edge.GetProperty<bool>("isTagHelper"));
    }

    /// <summary>
    /// The same regression one level down: objects were the one JSON kind
    /// NormalizeValue did not rebuild, so a nested value was a Dictionary on the
    /// graph that built it and a raw JsonElement on the graph that read it back,
    /// from identical bytes and under an identical formatVersion.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesTypedAccessThroughNestedObjects()
    {
        var graph = new CodeGraph();
        graph.AddNode(new GraphNode { Id = "m:A()", Type = NodeType.Method, Name = "A" });
        graph.AddNode(new GraphNode { Id = "ext:X", Type = NodeType.ExternalType, Name = "X" });

        var edge = new GraphEdge { FromId = "m:A()", ToId = "ext:X", Type = EdgeType.DecoratedBy };
        edge.Properties["named"] = new Dictionary<string, object>
        {
            ["Skip"] = "flaky on CI",
            ["Timeout"] = 500
        };
        graph.AddEdge(edge);

        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(graph));
        var named = restored.Outgoing("m:A()").Single().GetProperty<Dictionary<string, object>>("named");

        Assert.NotNull(named);
        Assert.Equal("flaky on CI", named!["Skip"]);
        Assert.Equal(500, named["Timeout"]);
    }

    /// <summary>
    /// The 3.0 constructorDependencies shape -- a list of {type, kind} objects,
    /// kind as its camelCase enum name -- rides the generic object path rather
    /// than a bespoke DTO field, so this pins that the path really does hand
    /// back the same shape it was given. The emitter stores exactly this
    /// List&lt;object&gt; of dictionaries for that reason; a typed list would be
    /// a different CLR shape on the loaded side from identical bytes.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesConstructorDependencies_AsTypeAndKind()
    {
        var graph = new CodeGraph();
        var node = new GraphNode { Id = "type:App.Widget", Type = NodeType.Class, Name = "Widget" };
        node.SetProperty("constructorDependencies", new List<object>
        {
            new Dictionary<string, object> { ["type"] = "App.IWidgetDep", ["kind"] = "interface" },
            new Dictionary<string, object> { ["type"] = "int", ["kind"] = "value" }
        });
        graph.AddNode(node);

        var json = GraphSerializer.ToJson(graph);
        Assert.Contains("\"constructorDependencies\"", json);
        Assert.Contains("\"kind\": \"interface\"", json);
        Assert.DoesNotContain("injectedServices", json);

        var deps = GraphSerializer.FromJson(json).GetNode("type:App.Widget")!
            .GetProperty<List<object>>("constructorDependencies");

        Assert.NotNull(deps);
        Assert.Equal(2, deps!.Count);
        var first = Assert.IsType<Dictionary<string, object>>(deps[0]);
        Assert.Equal("App.IWidgetDep", first["type"]);
        Assert.Equal("interface", first["kind"]);
        var second = Assert.IsType<Dictionary<string, object>>(deps[1]);
        Assert.Equal("value", second["kind"]);
    }

    /// <summary>
    /// A null slot in an argument list must come back as null. Attribute args
    /// keep one slot per argument so indices never shift, and a failed or
    /// genuinely-null slot holds null — which deserialized as a JsonElement of
    /// kind Null before this, reading as "some value present".
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesNullSlotsInsideLists()
    {
        var graph = new CodeGraph();
        graph.AddNode(new GraphNode { Id = "m:A()", Type = NodeType.Method, Name = "A" });
        graph.AddNode(new GraphNode { Id = "ext:X", Type = NodeType.ExternalType, Name = "X" });

        var edge = new GraphEdge { FromId = "m:A()", ToId = "ext:X", Type = EdgeType.DecoratedBy };
        edge.Properties["args"] = new List<object?> { "first", null, 3 };
        graph.AddEdge(edge);

        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(graph));
        var args = restored.Outgoing("m:A()").Single().GetProperty<List<object>>("args");

        Assert.NotNull(args);
        Assert.Equal(3, args!.Count);
        Assert.Equal("first", args[0]);
        Assert.Null(args[1]);
        Assert.Equal(3, args[2]);
    }

    // The escape data is exactly the shape the serializer round-trips
    // generically (string lists); this pins that no bespoke handling is needed.
    [Fact]
    public void RoundTrip_PreservesThrowsAndGuardLists()
    {
        var graph = new CodeGraph();
        var thrower = new GraphNode { Id = "m:App.T.Boom()", Type = NodeType.Method, Name = "Boom" };
        thrower.SetProperty("throws", new List<string> { "System.InvalidOperationException" });
        thrower.SetProperty("entryPointKind", "asyncVoid");
        graph.AddNode(thrower);
        graph.AddNode(new GraphNode { Id = "m:App.T.Caller()", Type = NodeType.Method, Name = "Caller" });

        var call = new GraphEdge { FromId = "m:App.T.Caller()", ToId = "m:App.T.Boom()", Type = EdgeType.Calls };
        call.Properties["guardedBy"] = new List<string> { "*" };
        graph.AddEdge(call);

        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(graph));

        var node = restored.GetNode("m:App.T.Boom()")!;
        Assert.Equal(
            new List<string> { "System.InvalidOperationException" },
            node.GetProperty<List<string>>("throws"));
        Assert.Equal("asyncVoid", node.GetProperty<string>("entryPointKind"));
        Assert.Equal(
            new List<string> { "*" },
            restored.Outgoing("m:App.T.Caller()").Single().GetProperty<List<string>>("guardedBy"));
    }

    [Fact]
    public void RoundTrip_RestoredGraphHasWorkingAdjacency()
    {
        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(BuildGraph()));

        Assert.Equal(2, restored.Outgoing("page:Pages/Index.cshtml").Count);
        Assert.Single(restored.Incoming("pm:App.IndexModel"));
    }

    /// <summary>
    /// Literal nodes and Mentions edges survive a save and a load. They are the
    /// newest vocabulary in the format, and a kind that round-trips as Unknown
    /// would be preserved and unqueryable at once -- the graph still loading is
    /// exactly what would make the loss quiet.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesLiteralNodesAndMentionsEdges()
    {
        var graph = BuildGraph();

        // A value with the characters that made a hashed id necessary in the first
        // place: quotes, a colon, and a newline all survive JSON as data.
        const string value = "waves.ini: \"roster\"\nline two";
        var literal = new GraphNode { Id = "lit:0123456789abcdef", Type = NodeType.Literal, Name = value };
        literal.SetProperty("value", value);
        graph.AddNode(literal);

        foreach (var line in new[] { 12, 40 })
        {
            var edge = new GraphEdge
            {
                FromId = "pm:App.IndexModel",
                ToId = literal.Id,
                Type = EdgeType.Mentions
            };
            edge.Properties["line"] = line;
            edge.Properties["provenance"] = "literal";
            graph.AddEdge(edge);
        }

        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(graph));

        Assert.Equal(graph.Nodes.Count, restored.Nodes.Count);
        Assert.Equal(graph.Edges.Count, restored.Edges.Count);

        var back = restored.GetNode("lit:0123456789abcdef")!;
        Assert.Equal(NodeType.Literal, back.Type);
        Assert.Null(back.ForeignType);
        Assert.Equal(value, back.Name);
        Assert.Equal(value, back.GetProperty<string>("value"));

        var mentions = restored.Incoming(back.Id).Where(e => e.Type == EdgeType.Mentions).ToList();
        Assert.Equal(2, mentions.Count);
        Assert.Equal(new[] { 12, 40 }, mentions.Select(e => e.GetProperty<int>("line")).OrderBy(l => l));
        Assert.All(mentions, e => Assert.Equal("literal", e.GetProperty<string>("provenance")));
    }

    // ---- Format version ----------------------------------------------------
    // The stamp exists for readers that did not write the file. Every assertion
    // below is about a mismatch being SAID, not merely survived.

    private static string GraphJson(string? formatVersion) =>
        formatVersion is null
            ? """{"nodes":[],"edges":[]}"""
            : $$"""{"formatVersion":"{{formatVersion}}","nodes":[],"edges":[]}""";

    [Fact]
    public void ToJson_StampsCurrentFormatVersion()
    {
        using var doc = JsonDocument.Parse(GraphSerializer.ToJson(BuildGraph()));

        Assert.Equal(GraphFormat.Current.ToString(), doc.RootElement.GetProperty("formatVersion").GetString());
    }

    [Fact]
    public void RoundTrip_PreservesBuiltAt()
    {
        var graph = BuildGraph();
        graph.BuiltAt = new DateTimeOffset(2026, 8, 29, 12, 41, 0, TimeSpan.Zero);

        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(graph));

        Assert.Equal(graph.BuiltAt, restored.BuiltAt);
    }

    /// <summary>
    /// A graph written before the stamp existed must read back as "cannot tell",
    /// never as fresh: a freshness probe that defaults to clean is the
    /// reassuring silence the caveat fields exist to prevent.
    /// </summary>
    [Fact]
    public void Read_GraphWithoutBuiltAt_ReportsUnknownRatherThanFresh()
    {
        var graph = BuildGraph();
        graph.BuiltAt = null;

        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(graph));

        Assert.Null(restored.BuiltAt);
        Assert.Null(new GraphFreshness(restored).WrittenSinceBuild(restored.Nodes.First()));
    }

    [Fact]
    public void RoundTrip_PreservesExtractorBuiltAt()
    {
        var graph = BuildGraph();
        graph.ExtractorBuiltAt = new DateTimeOffset(2026, 9, 12, 8, 3, 0, TimeSpan.Zero);

        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(graph));

        Assert.Equal(graph.ExtractorBuiltAt, restored.ExtractorBuiltAt);
    }

    /// <summary>
    /// A graph written without the extractor stamp must read back as "cannot
    /// tell", never as current: the field exists to expose a stale binary, and
    /// a fabricated value would hide exactly that.
    /// </summary>
    [Fact]
    public void Read_GraphWithoutExtractorBuiltAt_ReportsUnknownRatherThanCurrent()
    {
        var graph = BuildGraph();
        graph.ExtractorBuiltAt = null;

        var json = GraphSerializer.ToJson(graph);
        using (var doc = JsonDocument.Parse(json))
            Assert.False(doc.RootElement.TryGetProperty("extractorBuiltAt", out _));

        Assert.Null(GraphSerializer.FromJson(json).ExtractorBuiltAt);
    }

    [Fact]
    public void RoundTrip_PreservesSource()
    {
        var graph = BuildGraph();
        graph.Source = @"C:\repos\App\App.slnx";

        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(graph));

        Assert.Equal(graph.Source, restored.Source);
    }

    /// <summary>
    /// A file written before the field existed has no origin to give, and must not
    /// invent one: null is what tells the loader to name the file instead.
    /// </summary>
    [Fact]
    public void Read_GraphWithoutSource_LeavesItNull()
    {
        Assert.Null(GraphSerializer.FromJson(GraphJson("3.0")).Source);
    }

    [Fact]
    public void RoundTrip_PreservesWhatTheGraphWasBuiltFor()
    {
        var graph = BuildGraph();
        graph.HostArchitecture = "arm64";
        graph.Platform = "x64";
        graph.PlatformFrom = "requested";

        var json = GraphSerializer.ToJson(graph);
        var restored = GraphSerializer.FromJson(json);

        Assert.Equal("arm64", restored.HostArchitecture);
        Assert.Equal("x64", restored.Platform);
        Assert.Equal("requested", restored.PlatformFrom);

        // Flat and top-level, beside builtAt and source, where a reader of the
        // file looks for document stamps.
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("arm64", doc.RootElement.GetProperty("hostArchitecture").GetString());
        Assert.Equal("x64", doc.RootElement.GetProperty("platform").GetString());
        Assert.Equal("requested", doc.RootElement.GetProperty("platformFrom").GetString());
    }

    /// <summary>
    /// A 3.3 file has no stamp, and that must read as "cannot tell". Filling in
    /// projectDefault would claim the build saw no Platform, which is exactly what
    /// a graph built under Platform=x64 in the environment could not say.
    /// </summary>
    [Fact]
    public void Read_GraphWithoutPlatformStamp_LeavesAllThreeNull()
    {
        var graph = GraphSerializer.FromJson(GraphJson("3.3"));

        Assert.Null(graph.HostArchitecture);
        Assert.Null(graph.Platform);
        Assert.Null(graph.PlatformFrom);
    }

    [Fact]
    public void RoundTrip_PreservesUnrestoredProjects_AndEmptyStaysEmpty()
    {
        var graph = BuildGraph();
        graph.UnrestoredProjects = ["App.Tests", "App.Core"];

        var json = GraphSerializer.ToJson(graph);

        Assert.Equal(new[] { "App.Tests", "App.Core" }, GraphSerializer.FromJson(json).UnrestoredProjects);
        using (var doc = JsonDocument.Parse(json))
            Assert.Equal(2, doc.RootElement.GetProperty("unrestoredProjects").GetArrayLength());

        // Checked and all restored is written as [], so it reads back as checked:
        // omitting it would make a clean build indistinguishable from an old one.
        graph.UnrestoredProjects = [];
        var clean = GraphSerializer.ToJson(graph);
        using (var doc = JsonDocument.Parse(clean))
            Assert.Equal(0, doc.RootElement.GetProperty("unrestoredProjects").GetArrayLength());
        var restored = GraphSerializer.FromJson(clean).UnrestoredProjects;
        Assert.NotNull(restored);
        Assert.Empty(restored);
    }

    /// <summary>
    /// A 3.5 file never checked, and must read as "not recorded". An empty list
    /// would claim every project was restored, which that build never looked at.
    /// </summary>
    [Fact]
    public void Read_GraphWithoutUnrestoredProjects_LeavesItNull()
    {
        Assert.Null(GraphSerializer.FromJson(GraphJson("3.5")).UnrestoredProjects);
    }

    [Fact]
    public void Read_CurrentVersion_HasNoCaveat()
    {
        var result = GraphSerializer.Read(GraphSerializer.ToJson(BuildGraph()));

        Assert.Equal(GraphFormat.Current, result.Format.Version);
        Assert.Null(result.Format.Caveat);
    }

    [Fact]
    public void Read_UnstampedGraph_LoadsAndSaysSo()
    {
        // Every graph saved before this change is unstamped. Refusing them would
        // be a regression; loading them silently is the drift the stamp prevents.
        var result = GraphSerializer.Read(GraphJson(null));

        Assert.Null(result.Format.Version);
        Assert.Equal("unstamped", result.Format.Display);
        Assert.Contains("no formatVersion", result.Format.Caveat);
    }

    [Fact]
    public void Read_NewerMajor_Refuses()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GraphSerializer.Read(GraphJson($"{GraphFormat.Current.Major + 1}.0")));

        Assert.Contains("newer than this build reads", ex.Message);
    }

    [Fact]
    public void Read_NewerMinor_LoadsWithCaveat()
    {
        // Minor is additive by contract: unknown vocabulary must not be fatal,
        // or a third-party extractor can never ship a new node kind.
        var result = GraphSerializer.Read(GraphJson($"{GraphFormat.Current.Major}.{GraphFormat.Current.Minor + 1}"));

        Assert.Contains("newer minor", result.Format.Caveat);
    }

    [Fact]
    public void Read_OlderMajor_LoadsWithCaveat()
    {
        var result = GraphSerializer.Read(GraphJson("0.9"));

        Assert.Equal(new GraphFormatVersion(0, 9), result.Format.Version);
        Assert.Contains("predates", result.Format.Caveat);
    }

    /// <summary>
    /// 2.0 renamed lineDocs to docsStart. A 2.0 reader given a 1.x graph would
    /// otherwise find no docsStart, take the absence at face value and report
    /// every node as undocumented — a well-formed answer, silently wrong, on a
    /// file that parsed perfectly. Reading the old key is what keeps those
    /// graphs whole.
    /// </summary>
    [Fact]
    public void Read_OneDotXGraph_ReadsLineDocsAsDocsStart()
    {
        const string json = """
            {"formatVersion":"1.8","nodes":[{"id":"m:App.Go()","type":"method","name":"Go","lineStart":12,"lineEnd":20,"lineDocs":9}],"edges":[]}
            """;

        var result = GraphSerializer.Read(json);

        Assert.Equal(9, result.Graph.GetNode("m:App.Go()")!.DocsStart);
        Assert.Contains("predates", result.Format.Caveat);
    }

    [Fact]
    public void RoundTrip_WritesDocsStart_AndNeverTheOldKey()
    {
        // The alias is read-only: a 2.0 file carries one spelling, or a reader
        // could never tell which one to believe when the two disagreed.
        var graph = new CodeGraph();
        graph.AddNode(new GraphNode { Id = "m:App.Go()", Type = NodeType.Method, Name = "Go", LineStart = 12, LineEnd = 20, DocsStart = 9 });

        var json = GraphSerializer.ToJson(graph);
        using (var doc = JsonDocument.Parse(json))
        {
            var node = doc.RootElement.GetProperty("nodes")[0];
            Assert.Equal(9, node.GetProperty("docsStart").GetInt32());
            Assert.False(node.TryGetProperty("lineDocs", out _));
        }

        Assert.Equal(9, GraphSerializer.FromJson(json).GetNode("m:App.Go()")!.DocsStart);
    }

    [Fact]
    public void RoundTrip_PreservesDocsEnd()
    {
        var graph = new CodeGraph();
        graph.AddNode(new GraphNode { Id = "m:App.Go()", Type = NodeType.Method, Name = "Go", LineStart = 12, LineEnd = 20, DocsStart = 7, DocsEnd = 10 });

        var json = GraphSerializer.ToJson(graph);
        using (var doc = JsonDocument.Parse(json))
            Assert.Equal(10, doc.RootElement.GetProperty("nodes")[0].GetProperty("docsEnd").GetInt32());

        Assert.Equal(10, GraphSerializer.FromJson(json).GetNode("m:App.Go()")!.DocsEnd);
    }

    [Fact]
    public void Read_UnparseableVersion_Refuses()
    {
        // Disagreeing about the format of the format is worse evidence of drift
        // than no stamp, so this is louder than the unstamped case, not quieter.
        var ex = Assert.Throws<InvalidOperationException>(() => GraphSerializer.Read(GraphJson("v1")));

        Assert.Contains("Unreadable formatVersion", ex.Message);
    }

    [Fact]
    public void Read_PreservesGraphContentAlongsideVersion()
    {
        var result = GraphSerializer.Read(GraphSerializer.ToJson(BuildGraph()));

        Assert.Equal(3, result.Graph.Nodes.Count);
        Assert.Equal(2, result.Graph.Edges.Count);
    }

    // ---- Foreign vocabulary ------------------------------------------------
    // A kind this build has no enum member for must load, survive a save
    // unchanged, and be declared in the file as untrustworthy. All three:
    // throwing blocks third-party extractors, rewriting corrupts their graph,
    // and staying quiet is the drift the format stamp exists to catch.

    private static string ForeignGraphJson(string? formatVersion = null) =>
        $$"""
        {
          {{(formatVersion is null ? "" : $"\"formatVersion\": \"{formatVersion}\",")}}
          "nodes": [
            { "id": "mod:app.init", "type": "module", "name": "init" },
            { "id": "m:App.Go()", "type": "method", "name": "Go" }
          ],
          "edges": [
            { "from": "mod:app.init", "to": "m:App.Go()", "type": "requires" }
          ]
        }
        """;

    [Fact]
    public void Read_UnknownNodeAndEdgeKinds_LoadInsteadOfThrowing()
    {
        // The blocker this whole change exists to remove: a Lua extractor's first
        // file used to take load_graph down entirely.
        var graph = GraphSerializer.Read(ForeignGraphJson()).Graph;

        var module = graph.GetNode("mod:app.init")!;
        Assert.Equal(NodeType.Unknown, module.Type);
        Assert.Equal("module", module.ForeignType);
        Assert.Equal(EdgeType.Unknown, graph.Edges.Single().Type);
        Assert.Equal("requires", graph.Edges.Single().ForeignType);
    }

    [Fact]
    public void Read_KnownKindsAlongsideForeignOnes_StillResolve()
    {
        // Tolerance must be per-kind, not a whole-document fallback.
        var graph = GraphSerializer.Read(ForeignGraphJson()).Graph;

        var method = graph.GetNode("m:App.Go()")!;
        Assert.Equal(NodeType.Method, method.Type);
        Assert.Null(method.ForeignType);
    }

    [Fact]
    public void Read_GenuineUnknownKind_IsNotMarkedForeign()
    {
        // "unknown" is a real member of the vocabulary. Only a name we cannot map
        // is foreign, or every unclassified node would claim foreign provenance.
        var graph = GraphSerializer.Read(
            """{"nodes":[{"id":"x","type":"unknown","name":"x"}],"edges":[]}""").Graph;

        Assert.Equal(NodeType.Unknown, graph.GetNode("x")!.Type);
        Assert.Null(graph.GetNode("x")!.ForeignType);
    }

    [Fact]
    public void DisplayType_ShowsForeignNameRatherThanUnknown()
    {
        // Preserving a kind in storage and rendering it as "Unknown" would discard
        // it at the one moment it matters. Every report and tool response reads
        // this property.
        var graph = GraphSerializer.Read(ForeignGraphJson()).Graph;

        Assert.Equal("module", graph.GetNode("mod:app.init")!.DisplayType);
        Assert.Equal("Method", graph.GetNode("m:App.Go()")!.DisplayType);
        Assert.Equal("requires", graph.Edges.Single().DisplayType);
    }

    [Fact]
    public void RoundTrip_ForeignKindNamesSurviveVerbatim()
    {
        var once = GraphSerializer.ToJson(GraphSerializer.Read(ForeignGraphJson()).Graph);
        using var doc = JsonDocument.Parse(once);

        var types = doc.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("type").GetString()).ToList();
        Assert.Contains("module", types);
        Assert.Equal("requires", doc.RootElement.GetProperty("edges").EnumerateArray()
            .Single().GetProperty("type").GetString());
    }

    [Fact]
    public void ToJson_KnownKinds_KeepTheirExistingWireNames()
    {
        // Regression guard on the string-typed DTO: the previously published
        // server strict-parses these names, so a casing change would lock it out
        // of every graph written from here on.
        using var doc = JsonDocument.Parse(GraphSerializer.ToJson(BuildGraph()));

        var types = doc.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("type").GetString()).ToList();
        Assert.Contains("razorPage", types);
        Assert.Contains("pageModel", types);
        Assert.Contains("pageServedBy", doc.RootElement.GetProperty("edges").EnumerateArray()
            .Select(e => e.GetProperty("type").GetString()).ToList());
    }

    [Fact]
    public void ToJson_ForeignData_DeclaredInTheFileWithItsCaveat()
    {
        // The caveat goes in the FILE, not only in the load response: the file
        // outlives the session that wrote it and is what gets reused.
        var json = GraphSerializer.ToJson(GraphSerializer.Read(ForeignGraphJson()).Graph);
        using var doc = JsonDocument.Parse(json);

        var foreign = doc.RootElement.GetProperty("foreignData");
        var comment = string.Join(" ", foreign.GetProperty(".comment").EnumerateArray().Select(l => l.GetString()));

        Assert.Contains("not uniformly trustworthy", comment);
        Assert.Contains($"Only data belonging to format {GraphFormat.Current}", comment);
        Assert.Equal("module", foreign.GetProperty("nodeTypes").EnumerateArray().Single().GetString());
        Assert.Equal("requires", foreign.GetProperty("edgeTypes").EnumerateArray().Single().GetString());
        Assert.Equal(1, foreign.GetProperty("nodes").GetInt32());
        Assert.Equal(1, foreign.GetProperty("edges").GetInt32());
    }

    [Fact]
    public void ToJson_CleanGraph_HasNoForeignDataBlock()
    {
        // Presence is the signal, so an empty block would cry wolf on every graph.
        using var doc = JsonDocument.Parse(GraphSerializer.ToJson(BuildGraph()));

        Assert.False(doc.RootElement.TryGetProperty("foreignData", out _));
    }

    [Fact]
    public void ToJson_NewerMinorOrigin_IsNamedInTheFile()
    {
        var newerMinor = $"{GraphFormat.Current.Major}.{GraphFormat.Current.Minor + 1}";

        var json = GraphSerializer.ToJson(GraphSerializer.Read(ForeignGraphJson(newerMinor)).Graph);
        using var doc = JsonDocument.Parse(json);

        var foreign = doc.RootElement.GetProperty("foreignData");
        Assert.Equal(newerMinor, foreign.GetProperty("fromVersions").EnumerateArray().Single().GetString());

        // The writer stamps ITS OWN version -- claiming the newer one would assert
        // semantics this build never applied.
        Assert.Equal(GraphFormat.Current.ToString(), doc.RootElement.GetProperty("formatVersion").GetString());

        var comment = string.Join(" ", foreign.GetProperty(".comment").EnumerateArray().Select(l => l.GetString()));
        Assert.Contains($"read from format {newerMinor}", comment);
    }

    [Fact]
    public void RoundTrip_ForeignProvenanceSurvivesRepeatedSaves()
    {
        // Provenance has to survive the chain, not just the first hop: a graph
        // that loses its origin on the second save has laundered itself clean.
        var newerMinor = $"{GraphFormat.Current.Major}.{GraphFormat.Current.Minor + 1}";

        var first = GraphSerializer.ToJson(GraphSerializer.Read(ForeignGraphJson(newerMinor)).Graph);
        var second = GraphSerializer.ToJson(GraphSerializer.Read(first).Graph);

        using var doc = JsonDocument.Parse(second);
        var foreign = doc.RootElement.GetProperty("foreignData");
        Assert.Equal(newerMinor, foreign.GetProperty("fromVersions").EnumerateArray().Single().GetString());
        Assert.Equal("module", foreign.GetProperty("nodeTypes").EnumerateArray().Single().GetString());
    }

    [Fact]
    public void ToJson_ForeignKindsWithoutANewerVersion_SayTheyCameFromAnExtension()
    {
        // Same hazard, different origin: an extractor extending this version.
        // Claiming a newer version produced it would be a fabricated provenance.
        var json = GraphSerializer.ToJson(
            GraphSerializer.Read(ForeignGraphJson(GraphFormat.Current.ToString())).Graph);
        using var doc = JsonDocument.Parse(json);

        var foreign = doc.RootElement.GetProperty("foreignData");
        Assert.False(foreign.TryGetProperty("fromVersions", out _));

        var comment = string.Join(" ", foreign.GetProperty(".comment").EnumerateArray().Select(l => l.GetString()));
        Assert.Contains("extractor extending the format", comment);
    }

    [Fact]
    public void ResearchDocument_FiltersByRelevanceThreshold()
    {
        var graph = BuildGraph();
        var relevance = new Dictionary<string, double>
        {
            ["page:Pages/Index.cshtml"] = 1.0,
            ["pm:App.IndexModel"] = 0.5,
            ["page:_Card"] = 0.2
        };

        var json = GraphSerializer.ToResearchDocument(graph, relevance, "how is Index served?", relevanceThreshold: 0.5);
        using var doc = JsonDocument.Parse(json);

        var nodeIds = doc.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("id").GetString())
            .ToList();

        Assert.Contains("page:Pages/Index.cshtml", nodeIds);
        Assert.Contains("pm:App.IndexModel", nodeIds);
        Assert.DoesNotContain("page:_Card", nodeIds);

        // Edges touching a dropped node are dropped with it.
        var edges = doc.RootElement.GetProperty("edges").EnumerateArray().ToList();
        Assert.Single(edges);
        Assert.Equal("pm:App.IndexModel", edges[0].GetProperty("to").GetString());

        Assert.Equal("how is Index served?", doc.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public void ResearchDocument_EmitsRelevanceAndLineAnchors()
    {
        var graph = BuildGraph();
        var relevance = new Dictionary<string, double> { ["page:Pages/Index.cshtml"] = 0.75 };

        var json = GraphSerializer.ToResearchDocument(graph, relevance, "q");
        using var doc = JsonDocument.Parse(json);

        var node = doc.RootElement.GetProperty("nodes").EnumerateArray().Single();
        Assert.Equal(0.75, node.GetProperty("relevance").GetDouble());
        Assert.Equal(1, node.GetProperty("lineStart").GetInt32());
    }

    [Fact]
    public void ResearchDocument_IdOverloadIncludesAllGivenIds()
    {
        var graph = BuildGraph();

        var json = GraphSerializer.ToResearchDocument(graph, new[] { "page:Pages/Index.cshtml", "pm:App.IndexModel" }, "q");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(2, doc.RootElement.GetProperty("nodes").GetArrayLength());
    }
}
