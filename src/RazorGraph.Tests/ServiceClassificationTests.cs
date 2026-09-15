namespace RazorGraph.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor.Attributes;
using RazorGraph.Extractor.Roslyn;
using Xunit;
using SymbolInfo = RazorGraph.Extractor.Roslyn.SymbolInfo;

/// <summary>
/// What actually promotes a type to ServiceInterface or ServiceImplementation.
/// </summary>
/// <remarks>
/// The catalog records these kinds as the ones DI registration promotes. The
/// code promotes on a name ending in "Service" and consults no registration at
/// all. Both rules agree on every type in every corpus measured, because a
/// codebase that registers a service also calls it one -- so the claim has never
/// been tested by a case that could falsify it.
/// <para>
/// These fixtures are that case. They are a PAIR chosen so the two rules
/// disagree in opposite directions: one type is registered and unconventionally
/// named, the other is conventionally named and registered nowhere. A
/// registration-driven classifier promotes the first and not the second. A
/// name-driven one does the reverse, and there is no third answer, so whichever
/// way this test reads it names the rule that is really running.
/// </para>
/// <para>
/// The registration is deliberately VISIBLE to the extractor rather than merely
/// present in the source: the assertions below check the attribute's recorded
/// registered type before checking the classification, so a failure cannot be
/// read as "the extractor could not see it". The information is there and the
/// classifier does not ask for it.
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

        return SymbolClassifier.ClassifySymbol(
            symbol, "Fx", compilation, new HashSet<string> { "Fx" }, AttributePolicy.Default)
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
    /// The finding. A registered implementation is NOT promoted, and an
    /// unregistered one IS -- so the kind that reads as "this participates in
    /// DI" is reporting what a type is called.
    /// </summary>
    /// <remarks>
    /// This test passes today and is expected to. It is written to be INVERTED
    /// when classification learns to read registrations: at that point
    /// LoriImpl2 becomes ServiceImplementation, ILori becomes ServiceInterface,
    /// and WidgetService stops being either unless something registers it. Until
    /// then this is the executable statement of which rule is running, and it is
    /// deliberately not phrased as an approval of it.
    /// </remarks>
    [Theory]
    [InlineData("LoriImpl2", NodeType.Class, "registered, and not promoted")]
    [InlineData("ILori", NodeType.Class, "the registered interface, also not promoted")]
    [InlineData("WidgetService", NodeType.ServiceImplementation, "unregistered, promoted on its name")]
    [InlineData("IWidgetService", NodeType.ServiceInterface, "unregistered, promoted on its name")]
    public void ClassificationFollowsTheName_NotTheRegistration(
        string typeName, NodeType expected, string why)
    {
        var actual = Classify(typeName).Type;

        Assert.True(expected == actual,
            $"{typeName} was {actual}, expected {expected} -- {why}. If this failed because "
            + "classification now reads registrations, invert the table rather than restoring it.");
    }
}
