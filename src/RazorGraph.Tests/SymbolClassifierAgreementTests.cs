namespace RazorGraph.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor.Attributes;
using RazorGraph.Extractor.Roslyn;
using Xunit;
using SymbolInfo = RazorGraph.Extractor.Roslyn.SymbolInfo;

/// <summary>
/// ClassifySymbol routes a type by shape into five branches, each returning its
/// own SymbolInfo object initialiser. Nothing in the language makes those five
/// set the same fields, and three separate defects have now been exactly that:
/// a field wired into some branches and not others, producing answers that are
/// well-formed and quietly different depending on which branch a type's name
/// sent it to.
/// </summary>
/// <remarks>
/// Why the obvious test does not catch this: the fixture you reach for while
/// editing a branch is a type that routes down that branch, so it passes while
/// the divergence sits in the branches you did not look at. The only test that
/// can see it is one that classifies all five at once and compares them to each
/// other rather than to an expectation written per branch.
/// <para>
/// These fixtures are the only coverage four of the five branches can have here.
/// Every type in this solution's own source reaches the fallback branch: its
/// graph carries 197 Class nodes and not one PageModel, controller, service or
/// view model, so none of the four can be exercised by graphing this repository
/// against itself -- a fixture deleted from below is coverage nothing else in
/// the suite replaces.
/// </para>
/// </remarks>
public class SymbolClassifierAgreementTests
{
    /// <summary>
    /// One fixture per branch, each deliberately identical in every respect the
    /// classifier reads: a documented, attributed, public type deriving from a
    /// base, implementing an interface, with a property, a method, and a public
    /// constructor taking a dependency. Only the NAME and the BASE differ, which
    /// is the only thing that should decide the branch.
    /// </summary>
    private const string Source = """
        namespace Fixture;

        using System;

        public sealed class MarkAttribute : Attribute { }

        public class PageModel { }
        public class ControllerBase { }
        public class WidgetBase { }
        public interface IWidget { }
        public interface IWidgetDep { }

        /// <summary>Routes to the PageModel branch, on its base type.</summary>
        [Mark]
        public class IndexPage : PageModel, IWidget
        {
            public IndexPage(IWidgetDep dep) { Dep = dep; }
            public IWidgetDep Dep { get; }
            public string Describe() => "page";
        }

        /// <summary>Routes to the controller branch, on its base type.</summary>
        [Mark]
        public class WidgetController : ControllerBase, IWidget
        {
            public WidgetController(IWidgetDep dep) { Dep = dep; }
            public IWidgetDep Dep { get; }
            public string Describe() => "controller";
        }

        /// <summary>Routes to the service branch, on its name.</summary>
        [Mark]
        public class WidgetService : WidgetBase, IWidget
        {
            public WidgetService(IWidgetDep dep) { Dep = dep; }
            public IWidgetDep Dep { get; }
            public string Describe() => "service";
        }

        /// <summary>Routes to the view-model branch, on its name.</summary>
        [Mark]
        public class WidgetViewModel : WidgetBase, IWidget
        {
            public WidgetViewModel(IWidgetDep dep) { Dep = dep; }
            public IWidgetDep Dep { get; }
            public string Describe() => "view model";
        }

        /// <summary>Routes to the plain-class fallback: named for none of the heuristics.</summary>
        [Mark]
        public class WidgetHelper : WidgetBase, IWidget
        {
            public WidgetHelper(IWidgetDep dep) { Dep = dep; }
            public IWidgetDep Dep { get; }
            public string Describe() => "helper";
        }
        """;

    /// <summary>The fallback branch, used as the reference the others are held to.</summary>
    private const string Reference = "WidgetHelper";

    private static readonly string[] Fixtures =
        ["IndexPage", "WidgetController", "WidgetService", "WidgetViewModel", Reference];

    /// <summary>
    /// Every field a branch can populate, as "did this branch fill it in". The
    /// fixtures are built so that a correct branch fills in all of them, which
    /// is what turns a false here into "this branch forgot", not "this type had
    /// nothing to say".
    /// </summary>
    private static Dictionary<string, bool> Populated(SymbolInfo info) => new()
    {
        ["Project"] = !string.IsNullOrEmpty(info.Project),
        ["FilePath"] = !string.IsNullOrEmpty(info.FilePath),
        ["LineStart"] = info.LineStart is not null,
        ["LineEnd"] = info.LineEnd is not null,
        ["LineDocs"] = info.LineDocs is not null,
        ["BaseType"] = !string.IsNullOrEmpty(info.BaseType),
        ["ImplementedInterfaces"] = info.ImplementedInterfaces.Count > 0,
        ["Attributes"] = info.Attributes.Count > 0,
        ["Properties"] = info.Properties.Count > 0,
        ["Methods"] = info.Methods.Count > 0,
        ["MethodNodes"] = info.MethodNodes.Count > 0,
        ["MemberNodes"] = info.MemberNodes.Count > 0,
        ["InjectedServices"] = info.InjectedServices.Count > 0
    };

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

    /// <summary>
    /// Agreement is only meaningful if the five fixtures really did take five
    /// different branches. Without this, two fixtures collapsing onto one branch
    /// would make the comparison below pass by having nothing to compare.
    /// </summary>
    [Theory]
    [InlineData("IndexPage", NodeType.PageModel)]
    [InlineData("WidgetController", NodeType.ApiController)]
    [InlineData("WidgetService", NodeType.ServiceImplementation)]
    [InlineData("WidgetViewModel", NodeType.ViewModel)]
    [InlineData(Reference, NodeType.Class)]
    public void EachFixture_TakesItsOwnBranch(string typeName, NodeType expected) =>
        Assert.Equal(expected, Classify(typeName).Type);

    /// <summary>
    /// The fixtures must exercise every field, or the comparison below goes
    /// quietly vacuous: a field no fixture populates is "absent everywhere",
    /// which reads as agreement. The fallback branch sets everything, so it is
    /// the honest reference.
    /// </summary>
    [Fact]
    public void TheFixtures_ExerciseEveryField()
    {
        var inert = Populated(Classify(Reference))
            .Where(f => !f.Value).Select(f => f.Key).Order().ToList();

        Assert.True(inert.Count == 0,
            $"The fixture no longer exercises {string.Join(", ", inert)}, so a branch could "
            + "drop those fields without this suite noticing. Fix the fixture, not this assert.");
    }

    /// <summary>
    /// The invariant: whatever a branch chooses to populate, all five choose the
    /// same. A field absent from every branch is agreement, not a gap — this
    /// test is about the branches disagreeing with each other.
    /// </summary>
    [Fact]
    public void EveryBranch_PopulatesTheSameFields()
    {
        var byFixture = Fixtures.ToDictionary(name => name, name => Populated(Classify(name)));

        var divergences = byFixture[Reference].Keys.Order()
            .Select(field => (field, missing: byFixture
                .Where(f => !f.Value[field]).Select(f => f.Key).Order().ToList()))
            .Where(d => d.missing.Count > 0 && d.missing.Count < byFixture.Count)
            .Select(d => $"  {d.field}: not populated by {string.Join(", ", d.missing)}")
            .ToList();

        Assert.True(divergences.Count == 0,
            "ClassifySymbol's branches disagree about which fields a type node carries, so the "
            + "same type gets a different node depending on which branch its name sent it to:\n"
            + string.Join("\n", divergences));
    }
}
