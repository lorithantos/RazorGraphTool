namespace RazorGraph.Tests;

using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Extractor;
using RazorGraph.Extractor.Razor;
using Xunit;

/// <summary>
/// @inject directives as InjectedInto edges, over hand-built graphs shaped like
/// the extractor's: a page node, the class it compiles into (References with
/// compiledInto=true), and that class's property per @inject. RazorPageInfo's
/// InjectedServices used to be extracted and dropped, so a page or component
/// that took its services through @inject showed none of them (2026-11-23).
/// </summary>
public class InjectDirectiveEdgeTests
{
    private const string PageId = "page:Pages/Shop.cshtml";
    private const string ClassId = "type:App.Pages.Pages_Shop";

    private static RazorPageInfo Page(params InjectDirective[] injects) => new()
    {
        Id = PageId,
        FilePath = @"C:\app\Pages\Shop.cshtml",
        RelativePath = @"Pages\Shop.cshtml",
        IsPage = true,
        InjectedServices = injects.ToList()
    };

    private static GraphNode TypeNode(string id, NodeType type, string fullName)
    {
        var node = new GraphNode { Id = id, Type = type, Name = fullName[(fullName.LastIndexOf('.') + 1)..] };
        node.SetProperty("fullName", fullName);
        return node;
    }

    /// <summary>
    /// Two services named ICartService in different namespaces, so the text
    /// "ICartService" alone cannot say which one a page means -- only the
    /// compiled property's type can.
    /// </summary>
    private static CodeGraph Graph(bool withGeneratedClass)
    {
        var graph = new CodeGraph();
        graph.AddNode(new GraphNode { Id = PageId, Type = NodeType.RazorPage, Name = "Shop", FilePath = @"C:\app\Pages\Shop.cshtml" });
        graph.AddNode(TypeNode("svc:App.Services.ICartService", NodeType.ServiceInterface, "App.Services.ICartService"));
        graph.AddNode(TypeNode("svc:Legacy.ICartService", NodeType.ServiceInterface, "Legacy.ICartService"));
        graph.AddNode(TypeNode("type:App.Data.IRepository<T>", NodeType.Class, "App.Data.IRepository<T>"));
        // An attribute minted outside the solution; never an injection target.
        graph.AddNode(TypeNode("ext:Microsoft.Extensions.Logging.ILogger<TCategoryName>", NodeType.ExternalType,
            "Microsoft.Extensions.Logging.ILogger<TCategoryName>"));
        graph.AddNode(TypeNode(RazorInjectId, NodeType.ExternalType, RazorInjectId["ext:".Length..]));

        if (!withGeneratedClass) return graph;

        AddClass(graph, PageId, ClassId,
            ("Cart", "App.Services.ICartService"),
            ("Items", "App.Data.IRepository<App.Models.Item>"),
            ("Logger", "Microsoft.Extensions.Logging.ILogger<App.Pages.Pages_Shop>"));
        return graph;
    }

    private const string RazorInjectId = "ext:Microsoft.AspNetCore.Mvc.Razor.Internal.RazorInjectAttribute";

    /// <summary>
    /// The class a page compiles into, with one [RazorInject] property per
    /// injection -- the DecoratedBy edge is what marks a property as one.
    /// </summary>
    private static void AddClass(CodeGraph graph, string pageId, string classId, params (string Name, string MemberType)[] properties)
    {
        graph.AddNode(TypeNode(classId, NodeType.Class, classId["type:".Length..]));
        graph.AddEdge(new GraphEdge { FromId = pageId, ToId = classId, Type = EdgeType.References, Properties = { ["compiledInto"] = true } });
        foreach (var (name, memberType) in properties)
        {
            var id = $"prop:{classId["type:".Length..]}.{name}";
            var node = new GraphNode { Id = id, Type = NodeType.Property, Name = name };
            node.SetProperty("memberType", memberType);
            graph.AddNode(node);
            graph.AddEdge(new GraphEdge { FromId = classId, ToId = id, Type = EdgeType.Contains });
            graph.AddEdge(new GraphEdge { FromId = id, ToId = RazorInjectId, Type = EdgeType.DecoratedBy });
        }
    }

    private static void Emit(CodeGraph graph, params RazorPageInfo[] infos) =>
        new RazorLayerEmitter(graph, new ClientAssetEmitter(graph)).AddInjectDirectiveEdges(infos);

    /// <summary>A .cshtml under C:\app, and its node.</summary>
    private static RazorPageInfo RazorFile(CodeGraph graph, string relativePath, params InjectDirective[] injects)
    {
        var info = new RazorPageInfo
        {
            Id = "page:" + relativePath,
            FilePath = @"C:\app\" + relativePath.Replace('/', '\\'),
            RelativePath = relativePath.Replace('/', '\\'),
            InjectedServices = injects.ToList()
        };
        graph.AddNode(new GraphNode { Id = info.Id, Type = NodeType.PartialView, Name = Path.GetFileNameWithoutExtension(relativePath), FilePath = info.FilePath });
        return info;
    }

    /// <summary>
    /// The nearest _ViewImports.cshtml beats one further up, and an imports file
    /// reaches only its own folder and below. Pages/_ViewImports.cshtml injects
    /// App's ICartService as Cart, Pages/Admin/_ViewImports.cshtml Legacy's under
    /// the same name: Admin/Users gets Legacy's, declared in the Admin file, and
    /// Public/Home -- in a sibling folder of Admin -- gets App's, declared in the
    /// Pages file, and nothing from Admin's. Each compiled class carries the one
    /// property the compiler kept, so the type agrees with the declaring file.
    /// </summary>
    [Fact]
    public void NearestImportsFileWins_AndASiblingFolderGetsNothingFromIt()
    {
        var graph = Graph(withGeneratedClass: false);
        var pagesImports = RazorFile(graph, "Pages/_ViewImports.cshtml", new InjectDirective("App.Services.ICartService", "Cart", 2));
        var adminImports = RazorFile(graph, "Pages/Admin/_ViewImports.cshtml", new InjectDirective("Legacy.ICartService", "Cart", 1));
        var users = RazorFile(graph, "Pages/Admin/Users.cshtml");
        var home = RazorFile(graph, "Pages/Public/Home.cshtml");
        AddClass(graph, users.Id, "type:App.Pages.Admin.Users", ("Cart", "Legacy.ICartService"), ("Html", "Microsoft.AspNetCore.Mvc.Rendering.IHtmlHelper<dynamic>"));
        AddClass(graph, home.Id, "type:App.Pages.Public.Home", ("Cart", "App.Services.ICartService"), ("Html", "Microsoft.AspNetCore.Mvc.Rendering.IHtmlHelper<dynamic>"));

        Emit(graph, pagesImports, adminImports, users, home);

        var usersEdge = Assert.Single(graph.Incoming(users.Id), e => e.Type == EdgeType.InjectedInto);
        Assert.Equal("svc:Legacy.ICartService", usersEdge.FromId);
        Assert.Equal("Pages/Admin/_ViewImports.cshtml", usersEdge.GetProperty<string>("declaredIn"));
        Assert.Equal(1, usersEdge.GetProperty<int>("line"));
        Assert.Equal("Legacy.ICartService", usersEdge.GetProperty<string>("declaredType"));

        var homeEdge = Assert.Single(graph.Incoming(home.Id), e => e.Type == EdgeType.InjectedInto);
        Assert.Equal("svc:App.Services.ICartService", homeEdge.FromId);
        Assert.Equal("Pages/_ViewImports.cshtml", homeEdge.GetProperty<string>("declaredIn"));
        Assert.Equal(2, homeEdge.GetProperty<int>("line"));

        // The [RazorInject] Html no directive declares is not an injection, and
        // the imports files receive nothing.
        Assert.Null(graph.GetNode(users.Id)!.GetProperty<List<object>>("unresolvedInjections"));
        Assert.Null(graph.GetNode(home.Id)!.GetProperty<List<object>>("unresolvedInjections"));
        Assert.DoesNotContain(graph.Incoming(pagesImports.Id), e => e.Type == EdgeType.InjectedInto);
        Assert.DoesNotContain(graph.Incoming(adminImports.Id), e => e.Type == EdgeType.InjectedInto);
    }

    /// <summary>
    /// With no compiled class to ask, the chain is walked from the text alone,
    /// with the same precedence: the page's own line, then the last line of a
    /// name in the nearest imports file.
    /// </summary>
    [Fact]
    public void WithoutAGeneratedClass_InheritedDirectivesFollowTheSamePrecedence()
    {
        var graph = Graph(withGeneratedClass: false);
        var imports = RazorFile(graph, "Pages/_ViewImports.cshtml",
            new InjectDirective("Legacy.ICartService", "Cart", 2),
            new InjectDirective("App.Services.ICartService", "Cart", 3),
            new InjectDirective("Legacy.ICartService", "Old", 4));
        var page = RazorFile(graph, "Pages/Basket.cshtml", new InjectDirective("App.Services.ICartService", "Old", 7));

        Emit(graph, imports, page);

        var edges = graph.Incoming(page.Id).Where(e => e.Type == EdgeType.InjectedInto)
            .ToDictionary(e => e.GetProperty<string>("name")!);
        Assert.Equal(["Cart", "Old"], edges.Keys.Order().ToArray());
        Assert.Equal(("svc:App.Services.ICartService", "Pages/_ViewImports.cshtml", 3),
            (edges["Cart"].FromId, edges["Cart"].GetProperty<string>("declaredIn")!, edges["Cart"].GetProperty<int>("line")));
        Assert.Equal(("svc:App.Services.ICartService", "Pages/Basket.cshtml", 7),
            (edges["Old"].FromId, edges["Old"].GetProperty<string>("declaredIn")!, edges["Old"].GetProperty<int>("line")));
        Assert.All(edges.Values, e => Assert.Equal("directiveText", e.GetProperty<string>("typeFrom")));
    }

    [Fact]
    public void Directive_IsAnEdgeFromTheServiceToThePage_CarryingWhatItSays()
    {
        var graph = Graph(withGeneratedClass: true);
        Emit(graph, Page(new InjectDirective("ICartService", "Cart", 4)));

        var edge = Assert.Single(graph.Edges, e => e.Type == EdgeType.InjectedInto);
        Assert.Equal("svc:App.Services.ICartService", edge.FromId);
        Assert.Equal(PageId, edge.ToId);
        Assert.Equal("@inject", edge.GetProperty<string>("via"));
        Assert.Equal("Cart", edge.GetProperty<string>("name"));
        Assert.Equal("ICartService", edge.GetProperty<string>("declaredType"));
        Assert.Equal(4, edge.GetProperty<int>("line"));
        Assert.Equal("generatedProperty", edge.GetProperty<string>("typeFrom"));
    }

    /// <summary>
    /// A constructed generic reaches the open definition the solution
    /// declares, with the written type kept as closedType -- as a constructor
    /// edge does.
    /// </summary>
    [Fact]
    public void ConstructedGeneric_ResolvesToItsOpenDefinition()
    {
        var graph = Graph(withGeneratedClass: true);
        Emit(graph, Page(new InjectDirective("IRepository<Item>", "Items", 5)));

        var edge = Assert.Single(graph.Edges, e => e.Type == EdgeType.InjectedInto);
        Assert.Equal("type:App.Data.IRepository<T>", edge.FromId);
        Assert.Equal("App.Data.IRepository<App.Models.Item>", edge.GetProperty<string>("closedType"));
    }

    /// <summary>
    /// A type with no in-solution node -- a framework service, or a name that
    /// matches nothing -- is not an edge and is not lost: it is listed on the
    /// page, and page_context returns it.
    /// </summary>
    [Fact]
    public void UnresolvableTypes_AreListedOnThePage_NotDropped()
    {
        var graph = Graph(withGeneratedClass: true);
        Emit(graph, Page(
            new InjectDirective("ILogger<Shop>", "Logger", 6),
            new InjectDirective("IDoesNotExist", "Missing", 7)));

        Assert.DoesNotContain(graph.Edges, e => e.Type == EdgeType.InjectedInto);

        var context = new GraphQuery(graph).GetPageContext(PageId)!;
        Assert.Empty(context.Injections);
        Assert.Equal(
            new[]
            {
                new UnresolvedInjection("Logger", "ILogger<Shop>", "Microsoft.Extensions.Logging.ILogger<App.Pages.Pages_Shop>", 6, "Pages/Shop.cshtml"),
                new UnresolvedInjection("Missing", "IDoesNotExist", null, 7, "Pages/Shop.cshtml")
            },
            context.UnresolvedInjections);
    }

    /// <summary>
    /// With no compiled class in the graph the text is all there is: a name
    /// that matches one type resolves, marked as read from the text; a name
    /// two types share is listed rather than guessed.
    /// </summary>
    [Fact]
    public void WithoutAGeneratedClass_TheTextResolvesOnlyWhatItNamesUniquely()
    {
        var graph = Graph(withGeneratedClass: false);
        Emit(graph, Page(
            new InjectDirective("App.Services.ICartService", "Cart", 4),
            new InjectDirective("ICartService", "Ambiguous", 5)));

        var edge = Assert.Single(graph.Edges, e => e.Type == EdgeType.InjectedInto);
        Assert.Equal("svc:App.Services.ICartService", edge.FromId);
        Assert.Equal("directiveText", edge.GetProperty<string>("typeFrom"));

        var unresolved = Assert.Single(GraphQuery.UnresolvedInjections(graph.GetNode(PageId)!));
        Assert.Equal("Ambiguous", unresolved.Name);
        Assert.Null(unresolved.ResolvedType);
    }
}
