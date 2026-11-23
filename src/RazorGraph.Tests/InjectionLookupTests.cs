namespace RazorGraph.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor;
using RazorGraph.Extractor.Attributes;
using RazorGraph.Extractor.Roslyn;
using Xunit;

/// <summary>
/// An InjectedInto edge comes from a node whose full name IS the constructor
/// parameter's type, and from nothing else. A parameter type the solution has
/// no node for gets no edge: the emitter used to strip a leading "I" and take
/// the first ServiceImplementation anywhere whose short name matched the rest,
/// which is a guess, and a guess at a different type.
/// </summary>
/// <remarks>
/// <para>
/// The fixture is the one shape that guess could ever fire on. The lookup is by
/// full name, so a namespaced type never starts with "I" unless its namespace
/// does, and then the stripped string still holds a dot no short name has. Only
/// a global-namespace type passes, and only one with no node of its own: every
/// class, interface, struct and record declaration is walked, but a delegate is
/// not a TypeDeclarationSyntax, so IHandler below is a parameter type with no
/// node. The guess joined it to Handler, a class it has nothing to do with.
/// </para>
/// <para>
/// Symbols are found the way RoslynExtractor.ExtractSymbols finds them, by
/// walking TypeDeclarationSyntax, rather than picked by name: which types have
/// no node is the whole question, and choosing them by hand would answer it.
/// </para>
/// </remarks>
public class InjectionLookupTests
{
    private const string Members = """
        public delegate void IHandler();
        public interface IAuditService { }
        public class Handler : IAuditService { }

        public class Consumer
        {
            public Consumer(IHandler handler, IAuditService audit) { }
        }
        """;

    private static CodeGraph Build(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "Fixture.cs");
        var compilation = CSharpCompilation.Create(
            "Fx",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var inScope = new HashSet<string> { "Fx" };
        var registeredServices = SymbolClassifier.RegisteredServiceTypes(compilation, inScope, AttributePolicy.Default);
        var model = compilation.GetSemanticModel(tree);

        var symbols = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Select(declaration => model.GetDeclaredSymbol(declaration)!)
            .Select(symbol => SymbolClassifier.ClassifySymbol(
                symbol, "Fx", compilation, inScope, registeredServices, AttributePolicy.Default))
            .OfType<RazorGraph.Extractor.Roslyn.SymbolInfo>()
            .ToList();

        var graph = new CodeGraph();
        var emitter = new DeclarationEmitter(graph);
        foreach (var sym in symbols) emitter.AddSymbolNode(sym);
        foreach (var sym in symbols) emitter.AddInjectionEdges(sym);
        return graph;
    }

    /// <summary>The full names of the nodes injected into Consumer, sorted.</summary>
    private static List<string> InjectedIntoConsumer(CodeGraph graph, string consumerFullName)
    {
        var consumer = graph.Nodes.Single(n => n.GetProperty<string>("fullName") == consumerFullName);
        return graph.Incoming(consumer.Id)
            .Where(e => e.Type == EdgeType.InjectedInto)
            .Select(e => graph.GetNode(e.FromId)!.GetProperty<string>("fullName")!)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    [Fact]
    public void GlobalNamespace_ParameterWithNoNode_IsNotGuessedFromAStrippedI()
    {
        var graph = Build(Members);

        // The delegate really has no node, so this is the case the guess fired on.
        Assert.DoesNotContain(graph.Nodes, n => n.GetProperty<string>("fullName") == "IHandler");
        Assert.Equal(["IAuditService"], InjectedIntoConsumer(graph, "Consumer"));
    }

    /// <summary>
    /// The ordinary case, and the one the guess never reached: "Fixture.IHandler"
    /// does not start with "I". Passes with or without the guess; it is here to
    /// show the exact-name edge beside it is untouched.
    /// </summary>
    [Fact]
    public void Namespaced_ParameterWithNoNode_GetsNoEdge_AndTheExactMatchStillDoes()
    {
        var graph = Build("namespace Fixture;\n" + Members);

        Assert.Equal(["Fixture.IAuditService"], InjectedIntoConsumer(graph, "Fixture.Consumer"));
    }

    /// <summary>
    /// A namespace starting with "I" is the one namespaced name the guess did
    /// run on, and "nventory.IHandler" is no type's short name, so it found
    /// nothing. Like the case above, this holds either way.
    /// </summary>
    [Fact]
    public void NamespaceStartingWithI_ParameterWithNoNode_GetsNoEdge()
    {
        var graph = Build("namespace Inventory;\n" + Members);

        Assert.Equal(["Inventory.IAuditService"], InjectedIntoConsumer(graph, "Inventory.Consumer"));
    }
}
