namespace RazorGraph.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RazorGraph.Extractor.Binding;
using Xunit;

/// <summary>
/// Working out which view a controller action renders. The cases here are the
/// measured shapes in OrchardCore: 28 View(), 64 View("literal"), 253
/// View(identifier), 1 interpolated.
/// </summary>
public class ViewCallScannerTests
{
    /// <summary>
    /// A stand-in for Microsoft.AspNetCore.Mvc.Controller carrying the overloads
    /// that matter. Compiling against the real MVC assembly would make these
    /// tests depend on a package for behaviour that is entirely about overload
    /// resolution.
    /// </summary>
    private const string ControllerShim = """
        namespace Microsoft.AspNetCore.Mvc
        {
            public class ViewResult { }
            public sealed class ActionNameAttribute : System.Attribute
            {
                public ActionNameAttribute(string name) { }
            }
            public class Controller
            {
                public ViewResult View() => new ViewResult();
                public ViewResult View(object model) => new ViewResult();
                public ViewResult View(string viewName) => new ViewResult();
                public ViewResult View(string viewName, object model) => new ViewResult();
                public ViewResult PartialView(string viewName) => new ViewResult();
            }
        }
        """;

    private static List<ViewCall> Scan(string source, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var shim = CSharpSyntaxTree.ParseText(ControllerShim);
        var compilation = CSharpCompilation.Create(
            "T",
            [tree, shim],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == methodName);

        return ViewCallScanner.ViewCalls(method, model).ToList();
    }

    private static ActionMethod? ActionMethodFor(string source, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var shim = CSharpSyntaxTree.ParseText(ControllerShim);
        var compilation = CSharpCompilation.Create(
            "T",
            [tree, shim],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(tree);
        var method = tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == methodName);

        return ViewCallScanner.ActionMethodOf(method, model);
    }

    private static string Controller(string body) => $$"""
        using Microsoft.AspNetCore.Mvc;
        public class MenuController : Controller
        {
            private const string Known = "FromConst";
            {{body}}
        }
        """;

    [Fact]
    public void NoArguments_RendersTheActionsOwnName()
    {
        // 28 of OrchardCore's 346 calls.
        var call = Assert.Single(Scan(Controller("public ViewResult Edit() => View();"), "Edit"));

        Assert.Equal("Edit", call.Name);
        Assert.Equal(ViewNameSource.ActionName, call.Source);
        Assert.Equal("Menu", call.Controller);
    }

    [Fact]
    public void ModelArgument_RendersTheActionsOwnName_NotTheModel()
    {
        // The 253-call case, and the one that made this need a semantic model:
        // View(model) and View(viewName) are the same syntax. Treating the
        // argument as a name would invent a view called "model".
        var call = Assert.Single(Scan(
            Controller("public ViewResult Edit() { object model = null; return View(model); }"), "Edit"));

        Assert.Equal("Edit", call.Name);
        Assert.Equal(ViewNameSource.ActionName, call.Source);
    }

    [Fact]
    public void StringLiteral_IsTakenAsTheName()
    {
        var call = Assert.Single(Scan(
            Controller("public ViewResult Edit() => View(\"Other\");"), "Edit"));

        Assert.Equal("Other", call.Name);
        Assert.Equal(ViewNameSource.Constant, call.Source);
    }

    [Fact]
    public void NameAndModel_TakesTheName()
    {
        var call = Assert.Single(Scan(
            Controller("public ViewResult Edit() { object m = null; return View(\"Other\", m); }"), "Edit"));

        Assert.Equal("Other", call.Name);
    }

    [Fact]
    public void ConstantExpression_FoldsRatherThanBeingCalledDynamic()
    {
        // Constant folding, not literal matching: a const field is as knowable as
        // a literal, and calling it dynamic would under-report coverage.
        var call = Assert.Single(Scan(
            Controller("public ViewResult Edit() => View(Known);"), "Edit"));

        Assert.Equal("FromConst", call.Name);
        Assert.Equal(ViewNameSource.Constant, call.Source);
    }

    [Fact]
    public void NonConstantName_IsDynamicWithAReason()
    {
        // The 1-in-346 case. Reported, never guessed — the same discipline the
        // Lua extractor uses for require(<expression>).
        var call = Assert.Single(Scan(
            Controller("public ViewResult Edit(string s) => View($\"Edit{s}\");"), "Edit"));

        Assert.Null(call.Name);
        Assert.Equal(ViewNameSource.Dynamic, call.Source);
        Assert.Contains("not a compile-time constant", call.Reason);
    }

    [Fact]
    public void ActionNameAttribute_OverridesTheMethodName()
    {
        // The POST half of a form is a separate method put back under the
        // original action name. It renders Login.cshtml; taking the method name
        // invents LoginPOST.cshtml, which exists nowhere. Eleven of
        // OrchardCore.Users' view calls were mis-reported this way.
        var call = Assert.Single(Scan(Controller("""
            public ViewResult Login() => View();
            [ActionName(nameof(Login))]
            public ViewResult LoginPOST() => View();
            """), "LoginPOST"));

        Assert.Equal("Login", call.Name);
        Assert.Equal(ViewNameSource.ActionName, call.Source);
    }

    [Fact]
    public void PrivateHelper_DoesNotClaimItsOwnNameAsTheView()
    {
        // OrchardCore renders through private helpers -- CreateInternalAsync,
        // ProcessSaveAsync -- called by several actions. At runtime the view name
        // comes from route data, so it is the INVOKING action's. Claiming the
        // helper''s own name reported two templates missing that never existed.
        var call = Assert.Single(Scan(
            Controller("private ViewResult CreateInternalAsync() => View();"), "CreateInternalAsync"));

        Assert.Null(call.Name);
        Assert.Contains("route data", call.Reason);

        // Not Dynamic: the name is unknown HERE, not unknowable. It is the
        // invoking action's, and the graph knows the callers — so the call is
        // marked for that resolution rather than written off.
        Assert.Equal(ViewNameSource.InvokingAction, call.Source);
    }

    [Fact]
    public void ActionMethodOf_ReportsTheRoutingName_ForHelperAttribution()
    {
        // What the helper case resolves against. The routing name is what a view
        // lookup uses, so an [ActionName] override has to travel with it: a caller
        // recorded as LoginPOST would send its helper looking for LoginPOST.cshtml.
        var source = Controller("""
            [ActionName("Login")]
            public ViewResult LoginPOST() => Helper();
            private ViewResult Helper() => View();
            """);

        Assert.Equal("Login", ActionMethodFor(source, "LoginPOST")?.ActionName);

        // The helper is not an action, so it contributes no name of its own.
        Assert.Null(ActionMethodFor(source, "Helper"));
    }

    [Fact]
    public void PartialView_IsScannedToo()
    {
        var call = Assert.Single(Scan(
            Controller("public ViewResult Edit() => PartialView(\"_Row\");"), "Edit"));

        Assert.Equal("_Row", call.Name);
    }

    [Fact]
    public void NamedArgument_IsHonouredRegardlessOfPosition()
    {
        var call = Assert.Single(Scan(
            Controller("public ViewResult Edit() { object m = null; return View(model: m, viewName: \"Other\"); }"), "Edit"));

        Assert.Equal("Other", call.Name);
    }

    [Fact]
    public void ControllerName_LosesItsSuffix_BecauseTheFolderDoes()
    {
        // Views/Menu/Edit.cshtml, not Views/MenuController/Edit.cshtml. This is
        // what separates the 34 files named Edit across OrchardCore.
        var call = Assert.Single(Scan(Controller("public ViewResult Edit() => View();"), "Edit"));

        Assert.Equal("Menu", call.Controller);
    }

    [Fact]
    public void NonController_IsIgnored()
    {
        // A View() call on something that is not a controller is somebody else's
        // method with a colliding name.
        var source = """
            public class Helper
            {
                public object View() => null;
                public object Edit() => View();
            }
            """;

        Assert.Empty(Scan(source, "Edit"));
    }

    [Fact]
    public void MultipleRenders_AreAllReported()
    {
        var calls = Scan(Controller("""
            public ViewResult Edit(bool b)
            {
                if (b) return View("A");
                return View();
            }
            """), "Edit");

        Assert.Equal(2, calls.Count);
        Assert.Equal(["A", "Edit"], calls.Select(c => c.Name));
    }
}
