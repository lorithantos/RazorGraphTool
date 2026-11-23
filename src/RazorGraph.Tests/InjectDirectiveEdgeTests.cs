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

        if (!withGeneratedClass) return graph;

        graph.AddNode(TypeNode(ClassId, NodeType.Class, "App.Pages.Pages_Shop"));
        graph.AddEdge(new GraphEdge { FromId = PageId, ToId = ClassId, Type = EdgeType.References, Properties = { ["compiledInto"] = true } });
        AddProperty(graph, "Cart", "App.Services.ICartService");
        AddProperty(graph, "Items", "App.Data.IRepository<App.Models.Item>");
        AddProperty(graph, "Logger", "Microsoft.Extensions.Logging.ILogger<App.Pages.Pages_Shop>");
        return graph;
    }

    private static void AddProperty(CodeGraph graph, string name, string memberType)
    {
        var id = $"prop:App.Pages.Pages_Shop.{name}";
        var node = new GraphNode { Id = id, Type = NodeType.Property, Name = name };
        node.SetProperty("memberType", memberType);
        graph.AddNode(node);
        graph.AddEdge(new GraphEdge { FromId = ClassId, ToId = id, Type = EdgeType.Contains });
    }

    private static void Emit(CodeGraph graph, RazorPageInfo info) =>
        new RazorLayerEmitter(graph, new ClientAssetEmitter(graph)).AddInjectDirectiveEdges([info]);

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
                new UnresolvedInjection("Logger", "ILogger<Shop>", "Microsoft.Extensions.Logging.ILogger<App.Pages.Pages_Shop>", 6),
                new UnresolvedInjection("Missing", "IDoesNotExist", null, 7)
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
