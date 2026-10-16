namespace RazorGraph.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor;
using RazorGraph.Extractor.Attributes;
using RazorGraph.Extractor.Roslyn;
using Xunit;

/// <summary>
/// A constructor parameter typed as a constructed generic reaches the open
/// definition's node. The solution declares <c>IRepo&lt;T&gt;</c> and never
/// <c>IRepo&lt;Item&gt;</c>, so matching on the name as written found nothing:
/// on eShopOnWeb the generic repository, the most-used seam in the codebase,
/// emitted no InjectedInto edge while the constructorDependencies beside it
/// marked it interface.
/// </summary>
/// <remarks>
/// Classified and emitted through the real SymbolClassifier and
/// DeclarationEmitter, not hand-built nodes, because the fix depends on two
/// renderings agreeing -- the definition name the classifier records and the
/// fullName the node is stamped with -- and a hand-built node would assert that
/// agreement instead of testing it.
/// </remarks>
public class GenericInjectionEdgeTests
{
    private const string Source = """
        namespace Fixture;

        public interface IRepo<T> { }
        public interface IPlain { }
        public class Item { }
        public class Brand { }

        public class Catalog
        {
            public Catalog(IRepo<Item> items, IRepo<Brand> brands, IPlain plain) { }
        }
        """;

    private static readonly string[] FixtureTypes = ["IRepo`1", "IPlain", "Item", "Brand", "Catalog"];

    private static (CodeGraph Graph, string CatalogId) Build()
    {
        var tree = CSharpSyntaxTree.ParseText(Source, path: "Fixture.cs");
        var compilation = CSharpCompilation.Create(
            "Fx",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var inScope = new HashSet<string> { "Fx" };
        var registeredServices = SymbolClassifier.RegisteredServiceTypes(compilation, inScope, AttributePolicy.Default);

        var symbols = FixtureTypes
            .Select(name => compilation.GetTypeByMetadataName($"Fixture.{name}")
                ?? throw new InvalidOperationException($"Fixture type not found: {name}"))
            .Select(symbol => SymbolClassifier.ClassifySymbol(
                    symbol, "Fx", compilation, inScope, registeredServices, AttributePolicy.Default)
                ?? throw new InvalidOperationException($"ClassifySymbol returned null for {symbol.Name}"))
            .ToList();

        var graph = new CodeGraph();
        var emitter = new DeclarationEmitter(graph);
        foreach (var sym in symbols) emitter.AddSymbolNode(sym);
        foreach (var sym in symbols) emitter.AddInjectionEdges(sym);

        return (graph, symbols.Single(s => s.Name == "Catalog").Id);
    }

    [Fact]
    public void ConstructedGenericParameter_ResolvesToTheOpenDefinition_OneEdgePerParameter()
    {
        var (graph, catalogId) = Build();

        var fromRepo = graph.Incoming(catalogId)
            .Where(e => e.Type == EdgeType.InjectedInto
                && graph.GetNode(e.FromId)!.GetProperty<string>("fullName") == "Fixture.IRepo<T>")
            .Select(e => e.GetProperty<string>("closedType"))
            .Order()
            .ToList();

        Assert.Equal(["Fixture.IRepo<Fixture.Brand>", "Fixture.IRepo<Fixture.Item>"], fromRepo);
    }

    /// <summary>
    /// The other half of "no edge changed": a parameter that matched by its
    /// written name still does, and carries no closedType, because there the
    /// written name IS the node's name.
    /// </summary>
    [Fact]
    public void NonGenericParameter_KeepsItsPlainEdge_WithNoClosedType()
    {
        var (graph, catalogId) = Build();

        var plain = Assert.Single(graph.Incoming(catalogId), e =>
            e.Type == EdgeType.InjectedInto
            && graph.GetNode(e.FromId)!.GetProperty<string>("fullName") == "Fixture.IPlain");

        Assert.False(plain.Properties.ContainsKey("closedType"));
    }
}
