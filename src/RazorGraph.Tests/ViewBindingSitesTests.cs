namespace RazorGraph.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using RazorGraph.Extractor.Binding;
using RazorGraph.Extractor.Roslyn;
using Xunit;

/// <summary>
/// The view-call, shape-name and action-method scans share one semantic model
/// per tree and run as one walk. What that must not change is the answer: each
/// list is pinned against its scan run alone, over a fresh model, in the order
/// the separate passes used to produce -- compilation, then tree, then
/// declaration order within the tree.
/// </summary>
public class ViewBindingSitesTests
{
    /// <summary>A stand-in for Microsoft.AspNetCore.Mvc.Controller; see ViewCallScannerTests.</summary>
    private const string ControllerShim = """
        namespace Microsoft.AspNetCore.Mvc
        {
            public class ViewResult { }
            public class Controller
            {
                public ViewResult View() => new ViewResult();
                public ViewResult View(object model) => new ViewResult();
                public ViewResult View(string viewName) => new ViewResult();
                public ViewResult PartialView(string viewName) => new ViewResult();
            }
        }
        """;

    // Every tree holds a controller and a display driver, so every tree
    // contributes to every list and the order checks have something to order.
    private const string MenuSource = """
        namespace A;
        using Microsoft.AspNetCore.Mvc;
        public class MenuController : Controller
        {
            public ViewResult Index() => View();
            public ViewResult Edit() { object m = null; return View("MenuEdit"); }
            private ViewResult Helper() => PartialView("_MenuRow");
        }
        public class MenuDisplayDriver
        {
            public void Build() { Initialize("Menu_Edit"); Shape("Menu_Summary"); }
            private void Initialize(string name) { }
            private void Shape(string name) { }
        }
        """;

    private const string OrderSource = """
        namespace A;
        using Microsoft.AspNetCore.Mvc;
        public class OrderController : Controller
        {
            public ViewResult List() => View("Orders");
            public ViewResult Detail() { object m = null; return View(m); }
        }
        public class OrderDisplayDriver
        {
            public void Build() { Initialize("Order_Line"); }
            private void Initialize(string name) { }
        }
        """;

    private const string FixtureSource = """
        namespace B;
        using Microsoft.AspNetCore.Mvc;
        public class FixtureController : Controller
        {
            public ViewResult Show() => View("Fixture");
        }
        public class FixtureDisplayDriver
        {
            public void Build() { Shape("Fixture_Zone"); }
            private void Shape(string name) { }
        }
        """;

    private static CSharpCompilation Compile(string name, params string[] sources) =>
        CSharpCompilation.Create(
            name,
            sources.Select(s => CSharpSyntaxTree.ParseText(s)),
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    [Fact]
    public void OneWalk_GivesEachScanTheSitesItFindsAlone_InTheSameOrder()
    {
        // The second compilation stands for a test project: its shapes are stamped
        // InTestCode, and the first's are not.
        var projects = new (Compilation Compilation, bool InTestCode)[]
        {
            (Compile("A", MenuSource, OrderSource, ControllerShim), false),
            (Compile("B", FixtureSource, ControllerShim), true)
        };

        var expectedViews = new List<ViewCall>();
        var expectedShapes = new List<ShapeReference>();
        var expectedActions = new List<ActionMethod>();
        foreach (var (compilation, inTestCode) in projects)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = tree.GetRoot();
                expectedViews.AddRange(RoslynExtractor.ViewCallsIn(root, compilation.GetSemanticModel(tree)));
                expectedShapes.AddRange(
                    RoslynExtractor.ShapeNamesIn(root, compilation.GetSemanticModel(tree), inTestCode));
                expectedActions.AddRange(RoslynExtractor.ActionMethodsIn(root, compilation.GetSemanticModel(tree)));
            }
        }

        var sites = RoslynExtractor.ViewBindingSitesOf(projects);

        Assert.Equal(expectedViews, sites.ViewCalls);
        Assert.Equal(expectedShapes, sites.ShapeNames);
        Assert.Equal(expectedActions, sites.ActionMethods);

        // Every source tree contributes to every list -- an empty or one-tree
        // list would pass the order checks above anyway.
        foreach (var type in new[] { "A.Menu", "A.Order", "B.Fixture" })
        {
            Assert.Contains(sites.ViewCalls, v => v.MethodId.Contains(type + "Controller"));
            Assert.Contains(sites.ShapeNames, s => s.MethodId.Contains(type + "DisplayDriver"));
            Assert.Contains(sites.ActionMethods, a => a.MethodId.Contains(type + "Controller"));
        }

        // The test-code stamp follows the project, not the scan.
        Assert.All(sites.ShapeNames, s => Assert.Equal(s.MethodId.Contains("B.Fixture"), s.InTestCode));
    }
}
