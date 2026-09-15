namespace RazorGraph.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor.Attributes;
using RazorGraph.Extractor.Roslyn;
using Xunit;
using SymbolInfo = RazorGraph.Extractor.Roslyn.SymbolInfo;

/// <summary>
/// What actually promotes a type to ServiceInterface or ServiceImplementation:
/// an attribute registration naming it, OR a name ending in "Service".
/// </summary>
/// <remarks>
/// The catalog records these kinds as the ones DI registration promotes. Until
/// 2026-09-15 the code promoted on the name alone and consulted no registration;
/// measured on ImageSelectorV2 that left 23 registered interfaces and 13
/// registered implementations as plain Class. Classification now reads the
/// registrations gathered ahead of the walk (SymbolClassifier
/// .RegisteredServiceTypes) as well as the name.
/// <para>
/// These fixtures are a PAIR chosen so the two rules disagree in opposite
/// directions: one type is registered and unconventionally named, the other is
/// conventionally named and registered nowhere. A registration-only classifier
/// promotes the first and not the second; a name-only one does the reverse. The
/// rule that runs promotes BOTH, and the table below says so.
/// </para>
/// <para>
/// The name fallback is retained deliberately. A codebase that registers at
/// call sites (services.AddScoped&lt;IFoo, Foo&gt;()) rather than by attribute --
/// RetirementCore, and this repository itself -- shows the extractor no
/// registration at all, so a registration-only rule would demote every service
/// in it. The fallback is a convention guess standing in for a registration the
/// extractor cannot observe, and the rule change adds promotions and removes
/// none.
/// </para>
/// <para>
/// The registration is deliberately VISIBLE to the extractor rather than merely
/// present in the source: the assertions below check the attribute's recorded
/// registered type before checking the classification, so a failure cannot be
/// read as "the extractor could not see it".
/// </para>
/// </remarks>
public class ServiceClassificationTests
{
    private const string Source = """
        namespace Fixture;

        using System;

        /// <summary>Stands in for the real RegisterDependency attribute.</summary>
        public sealed class RegisterDependencyAttribute<T> : Attribute { }

        /// <summary>Registered, and named for none of the conventions.</summary>
        public interface ILori { }

        /// <summary>The implementation, registered against ILori by attribute.</summary>
        [RegisterDependency<ILori>]
        public sealed class LoriImpl2 : ILori
        {
            public string Describe() => "registered, unconventionally named";
        }

        /// <summary>Named by the convention, registered nowhere.</summary>
        public interface IWidgetService { }

        /// <summary>Same: the name is the only thing suggesting it is a service.</summary>
        public sealed class WidgetService : IWidgetService
        {
            public string Describe() => "unregistered, conventionally named";
        }
        """;

    private static SymbolInfo Classify(string typeName)
    {
        var tree = CSharpSyntaxTree.ParseText(
            Source,
            new CSharpParseOptions(documentationMode: DocumentationMode.Parse),
            path: "Fixture.cs");

        var compilation = CSharpCompilation.Create(
            "Fx",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var symbol = compilation.GetTypeByMetadataName($"Fixture.{typeName}")
            ?? throw new InvalidOperationException($"Fixture type not found: {typeName}");

        // The real collector, not a hand-built set: a set written here would
        // prove nothing about what the pipeline actually gathers.
        var inScope = new HashSet<string> { "Fx" };
        var registeredServices = SymbolClassifier.RegisteredServiceTypes(compilation, inScope);

        return SymbolClassifier.ClassifySymbol(
            symbol, "Fx", compilation, inScope, registeredServices, AttributePolicy.Default)
            ?? throw new InvalidOperationException($"ClassifySymbol returned null for {typeName}");
    }

    private static List<string> RegisteredTypes(SymbolInfo info) =>
        info.Attributes.SelectMany(a => a.RegisteredTypeFullNames ?? []).ToList();

    /// <summary>
    /// The registration reaches the node. This is the premise the rest depends
    /// on: without it, a classifier ignoring registration and a classifier that
    /// never received one are indistinguishable.
    /// </summary>
    [Fact]
    public void TheRegistrationIsVisibleToTheExtractor() =>
        Assert.Contains("Fixture.ILori", RegisteredTypes(Classify("LoriImpl2")));

    /// <summary>
    /// The conventionally named pair really is registered nowhere, so its
    /// promotion below cannot be credited to a registration.
    /// </summary>
    [Fact]
    public void TheConventionallyNamedTypeIsRegisteredNowhere() =>
        Assert.Empty(RegisteredTypes(Classify("WidgetService")));

    /// <summary>
    /// The rule. A registered pair is promoted whether or not its names follow
    /// the convention -- the interface through the attribute on its
    /// implementation, which is why the set is gathered before the walk -- and
    /// a conventionally named pair is still promoted with no registration in
    /// sight.
    /// </summary>
    /// <remarks>
    /// The first two rows were inverted on 2026-09-15 when classification
    /// learned to read registrations; before that they read Class. The last
    /// two are unchanged, and are the executable statement that the name
    /// fallback survived: WidgetService is registered nowhere (asserted above),
    /// so only the name can be carrying it. Mutating RegisteredServiceTypes to
    /// return an empty set fails the first two rows and leaves these two green,
    /// which is the proof that the new rule and not the old one carries them.
    /// </remarks>
    [Theory]
    [InlineData("LoriImpl2", NodeType.ServiceImplementation, "registered by attribute, promoted")]
    [InlineData("ILori", NodeType.ServiceInterface, "the registered interface, promoted through its implementation's attribute")]
    [InlineData("WidgetService", NodeType.ServiceImplementation, "unregistered, promoted on its name")]
    [InlineData("IWidgetService", NodeType.ServiceInterface, "unregistered, promoted on its name")]
    public void RegistrationPromotes_AndTheNameFallbackStillDoes(
        string typeName, NodeType expected, string why)
    {
        var actual = Classify(typeName).Type;

        Assert.True(expected == actual,
            $"{typeName} was {actual}, expected {expected} -- {why}. The name fallback is deliberate: "
            + "call-site registration is invisible to the extractor, so do not remove it.");
    }
}
