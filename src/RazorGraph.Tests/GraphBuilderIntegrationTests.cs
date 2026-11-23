namespace RazorGraph.Tests;

using System.Diagnostics;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Extractor;
using Xunit;

/// <summary>
/// End-to-end: builds a graph from the fixture Razor Pages app via MSBuildWorkspace.
/// The fixture is restored on first use; failures here should be loud, not skipped.
/// </summary>
[Trait("Category", "Integration")]
public class GraphBuilderIntegrationTests : IAsyncLifetime
{
    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static CodeGraph? _graph;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RazorGraphTool.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate RazorGraphTool.slnx above the test directory.");
    }

    public async Task InitializeAsync()
    {
        await BuildGate.WaitAsync();
        try
        {
            if (_graph != null) return;

            var root = RepoRoot();
            var fixtureDir = Path.Combine(root, "tests", "fixtures", "SampleApp");
            var projectPath = Path.Combine(fixtureDir, "SampleApp.csproj");
            Assert.True(File.Exists(projectPath), $"Fixture project missing: {projectPath}");

            EnsureRestored(fixtureDir);

            await using var builder = new GraphBuilder();
            _graph = await builder.BuildFromProjectAsync(projectPath);
        }
        finally
        {
            BuildGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static void EnsureRestored(string fixtureDir)
    {
        if (File.Exists(Path.Combine(fixtureDir, "obj", "project.assets.json"))) return;

        var psi = new ProcessStartInfo("dotnet", "restore")
        {
            WorkingDirectory = fixtureDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dotnet restore of fixture failed:\n{output}");
    }

    /// <summary>
    /// The extractor stamp is actually produced, not merely carried.
    /// </summary>
    /// <remarks>
    /// The serializer tests round-trip a value someone handed them, and the tool
    /// tests assert the field is emitted -- neither runs the code that has to
    /// come up with it. If ExtractorBuildTime ever returns null on every path,
    /// all of those still pass and every graph reports "cannot tell", which is a
    /// well-formed answer and a useless one. This is the only test that builds a
    /// graph the way a caller does and looks at what the stamp came out as.
    /// </remarks>
    [Fact]
    public void Builds_ExtractorStamp_FromTheAssemblyThatRan()
    {
        Assert.NotNull(_graph!.ExtractorBuiltAt);

        // Not in the future, and not the default value dressed up as a time:
        // either would mean the helper invented something rather than read it.
        Assert.True(_graph.ExtractorBuiltAt > DateTimeOffset.UnixEpoch,
            $"Extractor stamp is not a real file time: {_graph.ExtractorBuiltAt}");
        Assert.True(_graph.ExtractorBuiltAt <= DateTimeOffset.UtcNow,
            $"Extractor stamp is in the future: {_graph.ExtractorBuiltAt}");
    }

    /// <summary>
    /// The builder names what it read, so a graph the CLI writes carries its
    /// source into the file the same way one built inside the server does.
    /// </summary>
    [Fact]
    public void Builds_Source_AsTheProjectItWasBuiltFrom()
    {
        var expected = Path.Combine(RepoRoot(), "tests", "fixtures", "SampleApp", "SampleApp.csproj");

        Assert.Equal(expected, _graph!.Source);
    }

    [Fact]
    public void Builds_HostArchitecture_AsTheMachineThatRan()
    {
        var expected = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

        Assert.Equal(expected, _graph!.HostArchitecture);
    }

    /// <summary>
    /// A build that asked for no Platform records what it inherited. The test
    /// cannot set the variable -- it is process-wide, and the parallel run shares
    /// it -- so it asserts the rule against whatever this process carries.
    /// </summary>
    [Fact]
    public void Builds_PlatformStamp_FromTheEnvironmentWhenNoneWasRequested()
    {
        var environment = Environment.GetEnvironmentVariable("Platform");

        if (string.IsNullOrEmpty(environment))
        {
            Assert.Null(_graph!.Platform);
            Assert.Equal("projectDefault", _graph.PlatformFrom);
        }
        else
        {
            Assert.Equal(environment, _graph!.Platform);
            Assert.Equal("environment", _graph.PlatformFrom);
        }
    }

    [Fact]
    public void Builds_RazorPageNode_WithRouteTemplate()
    {
        var page = _graph!.Nodes.SingleOrDefault(n => n.Type == NodeType.RazorPage && n.Name == "Index");

        Assert.NotNull(page);
        Assert.Equal("{id?}", page!.GetProperty<string>("routeTemplate"));
        Assert.Equal("IndexModel", page.GetProperty<string>("modelType"));
        Assert.Contains("Title", page.GetProperty<List<string>>("viewDataKeys") ?? new());
    }

    [Fact]
    public void Builds_PageServedByEdge_ToPageModel()
    {
        var page = _graph!.Nodes.Single(n => n.Type == NodeType.RazorPage && n.Name == "Index");

        var served = _graph.Outgoing(page.Id).SingleOrDefault(e => e.Type == EdgeType.PageServedBy);

        Assert.NotNull(served);
        var pageModel = _graph.GetNode(served!.ToId);
        Assert.Equal(NodeType.PageModel, pageModel!.Type);
        Assert.Equal("IndexModel", pageModel.Name);
    }

    [Fact]
    public void Builds_InjectedIntoEdge_FromServiceToPageModel()
    {
        var pageModel = _graph!.Nodes.Single(n => n.Type == NodeType.PageModel && n.Name == "IndexModel");

        var injectors = _graph.Incoming(pageModel.Id)
            .Where(e => e.Type == EdgeType.InjectedInto)
            .Select(e => _graph.GetNode(e.FromId)!.Name)
            .ToList();

        Assert.Contains("IGreetingService", injectors);
    }

    [Fact]
    public void Builds_RendersPartialEdge_ToCard()
    {
        var page = _graph!.Nodes.Single(n => n.Type == NodeType.RazorPage && n.Name == "Index");

        var partials = _graph.Outgoing(page.Id)
            .Where(e => e.Type == EdgeType.RendersPartial)
            .Select(e => _graph.GetNode(e.ToId)!.Name)
            .ToList();

        Assert.Contains("_Card", partials);
    }

    // ---- Blazor components --------------------------------------------------

    /// <summary>
    /// What the graph holds for a Blazor component, pinned so a change to it is
    /// a decision rather than a drift: GreetingPanel.razor (@page, two @inject,
    /// one child component) becomes a RazorPage whose in-solution @inject is an
    /// edge and whose framework one is listed, with no child edge, and the
    /// honest answer for the children is a caveat, never a bare empty list.
    /// The Razor Pages page beside it carries no caveat.
    /// </summary>
    [Fact]
    public void Builds_AComponentPage_WhoseContextAndRenderTreeCarryACaveat()
    {
        var component = _graph!.Nodes.Single(n => n.Type == NodeType.RazorPage && n.Name == "GreetingPanel");
        var query = new GraphQuery(_graph);

        var context = query.GetPageContext(component.Id)!;
        var injection = Assert.Single(context.Injections);
        Assert.Equal("IGreetingService", injection.Service.Name);
        Assert.Equal(GraphQuery.ViaInjectDirective, injection.Via);
        Assert.Single(context.UnresolvedInjections!);
        Assert.Equal(GraphQuery.ComponentContextCaveat, context.Caveat);

        Assert.Empty(query.GetRenderTree(component.Id));
        Assert.Equal(GraphQuery.ComponentChildrenCaveat, query.GetRenderTreeCaveat(component.Id));

        // A component without @page is a PartialView, and still a component.
        var child = _graph.Nodes.Single(n => n.Type == NodeType.PartialView && n.Name == "GreetingBadge");
        Assert.Equal(GraphQuery.ComponentChildrenCaveat, query.GetRenderTreeCaveat(child.Id));

        var page = _graph.Nodes.Single(n => n.Type == NodeType.RazorPage && n.Name == "Index");
        Assert.Null(query.GetPageContext(page.Id)!.Caveat);
        Assert.Null(query.GetRenderTreeCaveat(page.Id));
    }

    // ---- @inject -------------------------------------------------------------

    /// <summary>
    /// Index.cshtml's <c>@inject IGreetingService Greeter</c> (line 3) is an
    /// InjectedInto edge from the service to the PAGE node -- service to
    /// consumer, as a constructor edge runs -- carrying what the directive says.
    /// The type comes from the [RazorInject] property the compiled class
    /// declares, not from the text.
    /// </summary>
    [Fact]
    public void Builds_InjectedIntoEdge_FromServiceToACshtmlPage_ForItsInjectDirective()
    {
        var page = _graph!.Nodes.Single(n => n.Type == NodeType.RazorPage && n.Name == "Index");

        var edge = Assert.Single(_graph.Incoming(page.Id), e => e.Type == EdgeType.InjectedInto);
        Assert.Equal("svc:SampleApp.Services.IGreetingService", edge.FromId);
        Assert.Equal("@inject", edge.GetProperty<string>("via"));
        Assert.Equal("Greeter", edge.GetProperty<string>("name"));
        Assert.Equal("IGreetingService", edge.GetProperty<string>("declaredType"));
        Assert.Equal(3, edge.GetProperty<int>("line"));
        Assert.Equal("generatedProperty", edge.GetProperty<string>("typeFrom"));

        // Nothing points the other way: the page is the consumer.
        Assert.DoesNotContain(_graph.Outgoing(page.Id), e => e.Type == EdgeType.InjectedInto);
        Assert.Null(page.GetProperty<List<object>>("unresolvedInjections"));
    }

    /// <summary>
    /// GreetingPanel.razor's two @inject: IGreetingService is in the solution
    /// and becomes an edge; ILogger&lt;GreetingPanel&gt; is not, so it is listed
    /// on the page with the type the compiler resolved -- which only the
    /// compiled class's [Inject] property knows, the file saying just
    /// <c>ILogger&lt;GreetingPanel&gt;</c>.
    /// </summary>
    [Fact]
    public void Builds_ComponentInjects_AsOneEdgeAndOneUnresolvedEntry()
    {
        var component = _graph!.Nodes.Single(n => n.Type == NodeType.RazorPage && n.Name == "GreetingPanel");

        var edge = Assert.Single(_graph.Incoming(component.Id), e => e.Type == EdgeType.InjectedInto);
        Assert.Equal("svc:SampleApp.Services.IGreetingService", edge.FromId);
        Assert.Equal("Greetings", edge.GetProperty<string>("name"));
        Assert.Equal(3, edge.GetProperty<int>("line"));
        Assert.Equal("generatedProperty", edge.GetProperty<string>("typeFrom"));

        var unresolved = Assert.Single(new GraphQuery(_graph).GetPageContext(component.Id)!.UnresolvedInjections!);
        Assert.Equal(new UnresolvedInjection(
                "Logger", "ILogger<GreetingPanel>",
                "Microsoft.Extensions.Logging.ILogger<SampleApp.Components.GreetingPanel>", 4),
            unresolved);
    }

    /// <summary>
    /// page_context on Index lists IGreetingService twice, once per mechanism:
    /// IndexModel's constructor, and the page's own @inject.
    /// </summary>
    [Fact]
    public void PageContext_ListsBothInjectionMechanisms()
    {
        var page = _graph!.Nodes.Single(n => n.Type == NodeType.RazorPage && n.Name == "Index");

        var context = new GraphQuery(_graph).GetPageContext(page.Id)!;

        Assert.Equal(
            new[] { ("IGreetingService", "constructor", (string?)null, (int?)null), ("IGreetingService", "@inject", "Greeter", 3) },
            context.Injections.Select(i => (i.Service.Name, i.Via, i.Name, i.Line)).ToArray());
        Assert.Empty(context.UnresolvedInjections!);
    }

    [Fact]
    public void Builds_ApiControllerAndViewModelNodes()
    {
        Assert.Contains(_graph!.Nodes, n => n.Type == NodeType.ApiController && n.Name == "GreetingsController");
        Assert.Contains(_graph.Nodes, n => n.Type == NodeType.ViewModel && n.Name == "IndexViewModel");
        Assert.Contains(_graph.Nodes, n => n.Type == NodeType.ServiceInterface && n.Name == "IGreetingService");
        Assert.Contains(_graph.Nodes, n => n.Type == NodeType.ServiceImplementation && n.Name == "GreetingService");
    }

    [Fact]
    public void SymbolNodes_HaveRealLineNumbers()
    {
        var symbolNodes = _graph!.Nodes
            .Where(n => n.Type is NodeType.PageModel or NodeType.ApiController
                     or NodeType.ServiceInterface or NodeType.ServiceImplementation or NodeType.ViewModel)
            .ToList();

        Assert.NotEmpty(symbolNodes);
        Assert.All(symbolNodes, n =>
        {
            Assert.NotNull(n.LineStart);
            Assert.True(n.LineStart > 0, $"{n.Id} has LineStart {n.LineStart}");
            Assert.True(n.LineEnd >= n.LineStart, $"{n.Id} has LineEnd {n.LineEnd} < LineStart {n.LineStart}");
        });

        // The regression this guards: every symbol previously got the placeholder LineStart = 1.
        Assert.Contains(symbolNodes, n => n.LineStart > 1);
    }

    // ---- Attribute extraction ----------------------------------------------

    [Fact]
    public void Builds_DecoratedByEdge_WithTheArgumentAsWritten()
    {
        // [Route("api/greetings")] on GreetingsController.
        var controller = _graph!.Nodes.Single(n => n.Type == NodeType.ApiController && n.Name == "GreetingsController");
        var route = _graph.Outgoing(controller.Id)
            .Single(e => e.Type == EdgeType.DecoratedBy && e.ToId == "ext:Microsoft.AspNetCore.Mvc.RouteAttribute");

        Assert.Equal(new List<object?> { "api/greetings" }, route.GetProperty<List<object?>>("args"));
        Assert.Equal("\"api/greetings\"", route.GetProperty<string>("source"));
        Assert.True(route.GetProperty<int>("line") > 0);
    }

    [Fact]
    public void Builds_DecoratedByEdge_OnMethodsAndProperties()
    {
        // [HttpGet("{name}")] on the action; [BindProperty] on the page model property.
        var get = _graph!.Outgoing("m:SampleApp.Api.GreetingsController.Get(string)")
            .Single(e => e.Type == EdgeType.DecoratedBy && e.ToId == "ext:Microsoft.AspNetCore.Mvc.HttpGetAttribute");
        Assert.Equal(new List<object?> { "{name}" }, get.GetProperty<List<object?>>("args"));

        var bind = _graph.Outgoing("prop:SampleApp.Pages.IndexModel.Name")
            .Single(e => e.Type == EdgeType.DecoratedBy);
        Assert.Equal("ext:Microsoft.AspNetCore.Mvc.BindPropertyAttribute", bind.ToId);
    }

    /// <summary>
    /// Absence of args must mean "no arguments", which only holds if an
    /// argument-less usage carries neither args nor source.
    /// </summary>
    [Fact]
    public void Builds_DecoratedByEdge_WithoutArgs_CarriesNoArgPayload()
    {
        var controller = _graph!.Nodes.Single(n => n.Type == NodeType.ApiController && n.Name == "GreetingsController");
        var apiController = _graph.Outgoing(controller.Id)
            .Single(e => e.Type == EdgeType.DecoratedBy && e.ToId == "ext:Microsoft.AspNetCore.Mvc.ApiControllerAttribute");

        Assert.False(apiController.Properties.ContainsKey("args"));
        Assert.False(apiController.Properties.ContainsKey("source"));
        Assert.False(apiController.Properties.ContainsKey("unresolvedArgs"));
    }

    // ---- Registers edges ---------------------------------------------------

    [Fact]
    public void Builds_RegistersEdge_FromAGenericAttributeTypeArgument()
    {
        // [RegisterService<IGreetingService>] on GreetingService: the framework
        // consults IGreetingService because of the annotation, no call site anywhere.
        var service = _graph!.Nodes.Single(n => n.Type == NodeType.ServiceImplementation && n.Name == "GreetingService");
        var registers = _graph.Outgoing(service.Id).Single(e => e.Type == EdgeType.Registers);

        Assert.Equal("IGreetingService", _graph.GetNode(registers.ToId)!.Name);

        // The attribute is declared in the fixture, so this must be its own
        // type: node, not a minted external one — and the DecoratedBy sibling
        // must join on (from, attribute, line).
        var attributeId = registers.GetProperty<string>("attribute");
        Assert.Equal("type:SampleApp.Infrastructure.RegisterServiceAttribute<TService>", attributeId);

        var decorated = _graph.Outgoing(service.Id)
            .Single(e => e.Type == EdgeType.DecoratedBy && e.ToId == attributeId);
        Assert.Equal(decorated.GetProperty<int>("line"), registers.GetProperty<int>("line"));
        Assert.Equal(new List<string> { "SampleApp.Services.IGreetingService" },
            decorated.GetProperty<List<string>>("typeArgs"));
    }

    [Fact]
    public void Builds_RegistersEdge_FromATypeofArgument()
    {
        // [ModelBinder(typeof(GreetingNameBinder))] on IndexViewModel.BoundName.
        var registers = _graph!.Outgoing("prop:SampleApp.Models.IndexViewModel.BoundName")
            .Single(e => e.Type == EdgeType.Registers);

        Assert.Equal("type:SampleApp.Infrastructure.GreetingNameBinder", registers.ToId);
        Assert.Equal("ext:Microsoft.AspNetCore.Mvc.ModelBinderAttribute", registers.GetProperty<string>("attribute"));

        // The payload keeps the typeof spelling, so the edge and what a reader
        // sees in the arguments name the same type.
        var decorated = _graph.Outgoing("prop:SampleApp.Models.IndexViewModel.BoundName")
            .Single(e => e.Type == EdgeType.DecoratedBy);
        Assert.Equal(new List<object?> { "typeof(SampleApp.Infrastructure.GreetingNameBinder)" },
            decorated.GetProperty<List<object?>>("args"));
    }

    [Fact]
    public void Builds_NoRegistersEdge_ForATypeofPointingOutsideTheSolution()
    {
        // [TypeConverter(typeof(StringConverter))]: the fact survives in the
        // DecoratedBy payload; an edge to a node that could not say what it is
        // would add reachability without meaning.
        var outgoing = _graph!.Outgoing("prop:SampleApp.Models.IndexViewModel.ConvertedTitle").ToList();

        Assert.DoesNotContain(outgoing, e => e.Type == EdgeType.Registers);
        var decorated = outgoing.Single(e => e.Type == EdgeType.DecoratedBy);
        Assert.Equal(new List<object?> { "typeof(System.ComponentModel.StringConverter)" },
            decorated.GetProperty<List<object?>>("args"));
    }

    [Fact]
    public void Renders_ANullArrayArgument_AsAuthoredNull_NotAsAFailure()
    {
        // [RegisterService<GreetingNameBinder>(null)]: null where string[] is
        // expected still has TypedConstantKind.Array, and reading its Values
        // used to throw. An authored null is an argument, not a failure — the
        // failure signal is unresolvedArgs, and it must stay absent.
        var edge = _graph!.Outgoing("type:SampleApp.Infrastructure.GreetingNameBinder")
            .Single(e => e.Type == EdgeType.DecoratedBy);

        Assert.Equal(new List<object?> { null }, edge.GetProperty<List<object?>>("args"));
        Assert.False(edge.Properties.ContainsKey("unresolvedArgs"));
    }

    // ---- Parameter nodes ---------------------------------------------------

    [Fact]
    public void Builds_AParameterNode_OnlyForTheDecoratedParameter()
    {
        // [FromRoute] on Get's name parameter is the fixture's only decorated
        // parameter, so exactly one Parameter node may exist — the same
        // assertion also proves every undecorated parameter (GreetingService
        // .Greet's, the ctors') got none, which is what lets an absent param:
        // node mean "undecorated" rather than "unmodelled".
        var parameter = _graph!.Nodes.Single(n => n.Type == NodeType.Parameter);

        Assert.Equal("param:SampleApp.Api.GreetingsController.Get(string)#name", parameter.Id);
        Assert.Equal("name", parameter.Name);
        Assert.Equal(0, parameter.GetProperty<int>("ordinal"));
        Assert.Equal("string", parameter.GetProperty<string>("parameterType"));
    }

    [Fact]
    public void Builds_ContainsAndDecoratedBy_ForTheParameterNode()
    {
        const string parameterId = "param:SampleApp.Api.GreetingsController.Get(string)#name";

        // The parent method id is the text before the '#' — the shared
        // MethodBody is what guarantees that surgery always works.
        var contains = _graph!.Incoming(parameterId).Single(e => e.Type == EdgeType.Contains);
        Assert.Equal("m:SampleApp.Api.GreetingsController.Get(string)", contains.FromId);

        var decorated = _graph.Outgoing(parameterId).Single(e => e.Type == EdgeType.DecoratedBy);
        Assert.Equal("ext:Microsoft.AspNetCore.Mvc.FromRouteAttribute", decorated.ToId);
        Assert.True(decorated.GetProperty<int>("line") > 0);
    }

    // ---- Method-level extraction -------------------------------------------

    [Fact]
    public void Builds_MethodNodes_ContainedByTheirDeclaringType()
    {
        var service = _graph!.Nodes.Single(n => n.Type == NodeType.ServiceImplementation && n.Name == "GreetingService");

        var methods = _graph.Outgoing(service.Id)
            .Where(e => e.Type == EdgeType.Contains)
            .Select(e => _graph.GetNode(e.ToId)!)
            .ToList();

        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.Equal(NodeType.Method, m.Type));
        Assert.All(methods, m => Assert.True(m.LineStart > 0, $"{m.Id} has no line number"));
    }

    [Fact]
    public void Builds_ClassNodes_ForTypesTheClassifierUsedToDrop()
    {
        // Program is neither page, controller, service, nor view model. Before
        // Class emission it was absent, and any call through it was unreachable.
        Assert.Contains(_graph!.Nodes, n => n.Type == NodeType.Class);
    }

    [Fact]
    public void Builds_CallEdges_BetweenMethodNodesInThisCompilation()
    {
        var callEdges = _graph!.Edges.Where(e => e.Type == EdgeType.Calls).ToList();

        Assert.NotEmpty(callEdges);

        // Both endpoints must resolve, or "who calls this" returns a dangling id.
        Assert.All(callEdges, e =>
        {
            Assert.NotNull(_graph.GetNode(e.FromId));
            Assert.NotNull(_graph.GetNode(e.ToId));
        });
    }

    // ---- Client assets ------------------------------------------------------

    [Fact]
    public void Builds_JsAndCssNodes_AndSkipsVendorBundles()
    {
        Assert.Contains(_graph!.Nodes, n => n.Type == NodeType.JavaScriptFile && n.Name == "site.js");
        Assert.Contains(_graph.Nodes, n => n.Type == NodeType.CssFile && n.Name == "site.css");

        // wwwroot/lib is third-party; graphing it is cost without signal.
        Assert.DoesNotContain(_graph.Nodes, n => n.Name == "jquery.js");

        // wwwroot/lib_npm is the same thing under the name the old substring
        // rule missed; it must be matched as a whole segment.
        Assert.DoesNotContain(_graph.Nodes, n => n.Name == "fakepkg.js");
    }

    [Fact]
    public void Builds_ReferencesEdge_FromPageToScript()
    {
        var page = _graph!.Nodes.Single(n => n.Type == NodeType.RazorPage && n.Name == "Index");

        var referenced = _graph.Outgoing(page.Id)
            .Where(e => e.Type == EdgeType.References)
            .Select(e => _graph.GetNode(e.ToId)!.Name)
            .ToList();

        Assert.Contains("site.js", referenced);
    }

    [Fact]
    public void Builds_ViewDataReadByEdge_ForKeyRenderedByAPartial()
    {
        var js = _graph!.Nodes.Single(n => n.Type == NodeType.JavaScriptFile && n.Name == "site.js");

        var edge = _graph.Incoming(js.Id).SingleOrDefault(e => e.Type == EdgeType.ViewDataReadBy);

        // data-greeting is rendered by _Card, not by Index. The coupling is only
        // visible if partial markup counts as part of the page's DOM.
        Assert.NotNull(edge);
        var keys = Assert.IsType<List<string>>(edge!.Properties["dataKeys"]);
        Assert.Contains("greeting", keys);
    }

    [Fact]
    public void Reports_OnlyTheGenuinelyUnboundKey()
    {
        var js = _graph!.Nodes.Single(n => n.Type == NodeType.JavaScriptFile && n.Name == "site.js");

        var unbound = js.GetProperty<List<string>>("unboundDataKeys") ?? new();

        // missing-key: read, never rendered anywhere -> a real broken contract.
        Assert.Contains("missing-key", unbound);
        // greeting: rendered by the partial.
        Assert.DoesNotContain("greeting", unbound);
        // state: rendered as a constant, so the attribute does exist in the DOM.
        Assert.DoesNotContain("state", unbound);
        // client-owned: the script assigns it, so the server owes nothing.
        Assert.DoesNotContain("client-owned", unbound);
    }

    [Fact]
    public void Builds_DomSelectedByEdge_ForIdsThePageCompositionRenders()
    {
        var edge = _graph!.Edges.Single(e => e.Type == EdgeType.DomSelectedBy);
        var ids = edge.GetProperty<List<string>>("ids");

        // Single-project page ids keep the OS path separator; only solution
        // builds normalize (see SolutionGraphIntegrationTests).
        Assert.Equal($"page:Pages{Path.DirectorySeparatorChar}Index.cshtml", edge.FromId);
        Assert.Equal("js:wwwroot/js/site.js", edge.ToId);
        // "Name" comes from asp-for on Index itself; "card-title" arrives
        // through the _Card partial — the composed DOM, not just the page.
        Assert.Equal(new[] { "Name", "card-title" }, ids);
    }

    [Fact]
    public void Reports_OnlyTheGenuinelyUnboundSelectorId()
    {
        var script = _graph!.Nodes.Single(n => n.Id == "js:wwwroot/js/site.js");
        var unbound = script.GetProperty<List<string>>("unboundSelectorIds");

        // cart-count: selected, never rendered. popup-host: selected but
        // self-created. Name/card-title: bound. Only the first is a defect.
        Assert.Equal(new[] { "cart-count" }, unbound);
    }

    [Fact]
    public void Builds_CallsEdge_FromScriptToApiController()
    {
        var js = _graph!.Nodes.Single(n => n.Type == NodeType.JavaScriptFile && n.Name == "site.js");

        var called = _graph.Outgoing(js.Id)
            .Where(e => e.Type == EdgeType.Calls)
            .Select(e => _graph.GetNode(e.ToId)!.Name)
            .ToList();

        Assert.Contains("GreetingsController", called);
    }
}
