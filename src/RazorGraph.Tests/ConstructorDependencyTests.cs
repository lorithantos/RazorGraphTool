namespace RazorGraph.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RazorGraph.Extractor.Attributes;
using RazorGraph.Extractor.Roslyn;
using Xunit;
using SymbolInfo = RazorGraph.Extractor.Roslyn.SymbolInfo;

/// <summary>
/// What a constructor parameter classifies as: the distinction the flat
/// injectedServices list could not carry. Measured on one corpus, 181 of 286
/// entries in that list were primitives, framework types or collections, and a
/// type named ContourGates listed <c>double, int</c> as "injected services" --
/// two defaulted tuning numbers on a primary constructor. The kind is what lets
/// a reader tell a seam from a number.
/// </summary>
/// <remarks>
/// One fixture, five parameters, one per case the rule distinguishes: an
/// in-scope interface, an in-scope class, a framework reference type, a
/// primitive, and an in-scope enum. The enum is the one that pins the rule's
/// ORDER -- Value is decided by the type before scope is consulted, so an
/// in-solution enum is Value and not Type, which is what stops it reading as a
/// dependency. Stream is the framework case because it lives in the one
/// assembly the compilation references, so no extra reference is needed.
/// </remarks>
public class ConstructorDependencyTests
{
    private const string Source = """
        namespace Fixture;

        using System.IO;

        public interface IWidgetDep { }
        public class WidgetClock { }
        public enum WidgetMode { Fast, Slow }

        public class Widget
        {
            public Widget(IWidgetDep dep, WidgetClock clock, Stream output, int retries, WidgetMode mode) { }
        }

        /// <summary>Several public constructors: only the first is read.</summary>
        public class TwoDoors
        {
            public TwoDoors(IWidgetDep dep) { }
            public TwoDoors(WidgetClock clock, int retries) { }
        }
        """;

    private static SymbolInfo Classify(string typeName)
    {
        var tree = CSharpSyntaxTree.ParseText(Source, path: "Fixture.cs");

        var compilation = CSharpCompilation.Create(
            "Fx",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var symbol = compilation.GetTypeByMetadataName($"Fixture.{typeName}")
            ?? throw new InvalidOperationException($"Fixture type not found: {typeName}");

        var inScope = new HashSet<string> { "Fx" };
        var registeredServices = SymbolClassifier.RegisteredServiceTypes(compilation, inScope, AttributePolicy.Default);

        return SymbolClassifier.ClassifySymbol(
            symbol, "Fx", compilation, inScope, registeredServices, AttributePolicy.Default)
            ?? throw new InvalidOperationException($"ClassifySymbol returned null for {typeName}");
    }

    [Fact]
    public void EachParameter_ClassifiesByWhatItIs()
    {
        var deps = Classify("Widget").ConstructorDependencies;

        Assert.Equal(
            [
                new InjectedDependency("Fixture.IWidgetDep", DependencyKind.Interface),
                new InjectedDependency("Fixture.WidgetClock", DependencyKind.Type),
                new InjectedDependency("System.IO.Stream", DependencyKind.Framework),
                new InjectedDependency("int", DependencyKind.Value),
                new InjectedDependency("Fixture.WidgetMode", DependencyKind.Value)
            ],
            deps);
    }

    /// <summary>
    /// The documented limitation, pinned so it cannot widen silently: a type
    /// with several public constructors reports the first and only the first.
    /// </summary>
    [Fact]
    public void OnlyTheFirstPublicConstructor_IsRead()
    {
        var deps = Classify("TwoDoors").ConstructorDependencies;

        Assert.Equal([new InjectedDependency("Fixture.IWidgetDep", DependencyKind.Interface)], deps);
    }
}
