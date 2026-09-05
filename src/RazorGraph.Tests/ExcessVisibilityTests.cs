namespace RazorGraph.Tests;

using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using Xunit;

/// <summary>
/// The visibility audit, over hand-built graphs so each rule is isolated.
/// </summary>
public class ExcessVisibilityTests
{
    private static GraphNode Type(string id, string name, string project, bool isPublic = true)
    {
        var n = new GraphNode { Id = id, Type = NodeType.Class, Name = name };
        n.SetProperty("project", project);
        n.SetProperty("isPublic", isPublic);
        return n;
    }

    private static GraphNode Method(string id, string name, string project, bool isPublic = true, bool isTest = false)
    {
        var n = new GraphNode { Id = id, Type = NodeType.Method, Name = name };
        n.SetProperty("project", project);
        n.SetProperty("isPublic", isPublic);
        if (isTest) n.SetProperty("isTest", true);
        return n;
    }

    private static void Contains(CodeGraph g, string type, string member) =>
        g.AddEdge(new GraphEdge { FromId = type, ToId = member, Type = EdgeType.Contains });

    private static void Calls(CodeGraph g, string from, string to) =>
        g.AddEdge(new GraphEdge { FromId = from, ToId = to, Type = EdgeType.Calls });

    private static void SignatureRef(CodeGraph g, string method, string type)
    {
        var e = new GraphEdge { FromId = method, ToId = type, Type = EdgeType.References };
        e.Properties["signature"] = true;
        g.AddEdge(e);
    }

    /// <summary>
    /// Lib holds two public types. Used is called from App, so it and its Run
    /// stay; Unused is reached only from inside Lib. InternalOnly is the
    /// member-level case — it hangs off Used, which IS required, so it is never
    /// subsumed by its own type being reported.
    /// </summary>
    private static CodeGraph BuildGraph()
    {
        var g = new CodeGraph();

        g.AddNode(Type("type:Lib.Used", "Used", "Lib"));
        g.AddNode(Method("m:Lib.Used.Run()", "Run", "Lib"));
        Contains(g, "type:Lib.Used", "m:Lib.Used.Run()");
        g.AddNode(Method("m:Lib.Used.InternalOnly()", "InternalOnly", "Lib"));
        Contains(g, "type:Lib.Used", "m:Lib.Used.InternalOnly()");

        g.AddNode(Type("type:Lib.Unused", "Unused", "Lib"));
        g.AddNode(Method("m:Lib.Unused.Helper()", "Helper", "Lib"));
        Contains(g, "type:Lib.Unused", "m:Lib.Unused.Helper()");

        g.AddNode(Type("type:Lib.Internal", "Internal", "Lib", isPublic: false));

        g.AddNode(Type("type:App.Caller", "Caller", "App"));
        g.AddNode(Method("m:App.Caller.Go()", "Go", "App"));
        Contains(g, "type:App.Caller", "m:App.Caller.Go()");

        g.AddNode(Type("type:Tests.Suite", "Suite", "Lib.Tests"));
        g.AddNode(Method("m:Tests.Suite.T1()", "T1", "Lib.Tests", isTest: true));
        Contains(g, "type:Tests.Suite", "m:Tests.Suite.T1()");

        Calls(g, "m:App.Caller.Go()", "m:Lib.Used.Run()");           // real external use
        Calls(g, "m:Lib.Used.Run()", "m:Lib.Unused.Helper()");       // internal use only
        Calls(g, "m:Lib.Used.Run()", "m:Lib.Used.InternalOnly()");   // internal use only
        Calls(g, "m:Tests.Suite.T1()", "m:Lib.Used.InternalOnly()"); // test use: does not count

        return g;
    }

    [Fact]
    public void Reports_PublicTypesNothingOutsideTheAssemblyUses()
    {
        var found = new GraphQuery(BuildGraph()).FindExcessVisibility("Lib").ToList();

        Assert.Contains(found, r => r.Node.Id == "type:Lib.Unused");
        // A public method on a type that IS required, reached only from inside.
        Assert.Contains(found, r => r.Node.Id == "m:Lib.Used.InternalOnly()");
    }

    [Fact]
    public void Excludes_WhatIsUsedFromAnotherAssembly()
    {
        var found = new GraphQuery(BuildGraph()).FindExcessVisibility("Lib").Select(r => r.Node.Id).ToList();

        Assert.DoesNotContain("type:Lib.Used", found);
        Assert.DoesNotContain("m:Lib.Used.Run()", found);
    }

    [Fact]
    public void Excludes_NodesAlreadyInternal()
    {
        // Nothing uses it, but it makes no claim to be reachable — reporting it
        // would be noise with no available action.
        Assert.DoesNotContain("type:Lib.Internal",
            new GraphQuery(BuildGraph()).FindExcessVisibility("Lib").Select(r => r.Node.Id));
    }

    [Fact]
    public void TestProjects_DoNotCountAsConsumers_ByDefault()
    {
        // The whole reason the tool exists: a test reaching in is not a reason
        // to stay public when InternalsVisibleTo covers it.
        Assert.Contains("m:Lib.Used.InternalOnly()",
            new GraphQuery(BuildGraph()).FindExcessVisibility("Lib").Select(r => r.Node.Id));
    }

    [Fact]
    public void TestProjects_CountAsConsumers_WhenAsked()
    {
        Assert.DoesNotContain("m:Lib.Used.InternalOnly()",
            new GraphQuery(BuildGraph()).FindExcessVisibility("Lib", includeTests: true).Select(r => r.Node.Id));
    }

    [Fact]
    public void Reports_WhichProjectsDoConsumeIt()
    {
        var candidate = new GraphQuery(BuildGraph()).FindExcessVisibility("Lib")
            .Single(r => r.Node.Id == "m:Lib.Used.InternalOnly()");

        // "used only from inside" and "used by nobody" are different findings.
        Assert.Contains("Lib", candidate.ConsumedBy);
        Assert.Contains("Lib.Tests", candidate.ConsumedBy);
    }

    [Fact]
    public void Excludes_TypesPinnedPublicByAnExternallyUsedSignature()
    {
        // The case that decides whether this tool is safe to act on: nothing
        // CALLS Dto, but Used.Run returns it and App calls Run. C# requires Dto
        // stay public, so recommending internal would not compile.
        var g = BuildGraph();
        g.AddNode(Type("type:Lib.Dto", "Dto", "Lib"));
        SignatureRef(g, "m:Lib.Used.Run()", "type:Lib.Dto");

        Assert.DoesNotContain("type:Lib.Dto",
            new GraphQuery(g).FindExcessVisibility("Lib").Select(r => r.Node.Id));
    }

    [Fact]
    public void Reports_TypesInSignaturesOfMethodsNothingExternalUses()
    {
        // The counterweight: a signature reference only pins a type when the
        // method holding it is itself externally required. Unused.Helper is not,
        // so its return type is free to narrow along with it.
        var g = BuildGraph();
        g.AddNode(Type("type:Lib.InternalDto", "InternalDto", "Lib"));
        SignatureRef(g, "m:Lib.Unused.Helper()", "type:Lib.InternalDto");

        Assert.Contains("type:Lib.InternalDto",
            new GraphQuery(g).FindExcessVisibility("Lib").Select(r => r.Node.Id));
    }

    [Fact]
    public void Reports_InterdependentCandidates_AsASet()
    {
        // The sharp edge, pinned deliberately. Dto is required-public: Used.Run
        // returns it and App calls Run. But Dto being public does NOT pin its
        // members — a public method on a public class can be narrowed — so
        // Unwrap, which nothing outside Lib calls, is a fair candidate, and once
        // Unwrap narrows, Inner is free to narrow with it.
        //
        // So the result is a COORDINATED SET: apply all of it and it compiles,
        // apply Inner alone and it does not, because public Unwrap would still
        // be returning it. That is the aggression the tool advertises, and the
        // reason its output is a worklist rather than a patch.
        var g = BuildGraph();
        g.AddNode(Type("type:Lib.Dto", "Dto", "Lib"));
        g.AddNode(Method("m:Lib.Dto.Unwrap()", "Unwrap", "Lib"));
        Contains(g, "type:Lib.Dto", "m:Lib.Dto.Unwrap()");
        g.AddNode(Type("type:Lib.Inner", "Inner", "Lib"));

        SignatureRef(g, "m:Lib.Used.Run()", "type:Lib.Dto");
        SignatureRef(g, "m:Lib.Dto.Unwrap()", "type:Lib.Inner");

        var found = new GraphQuery(g).FindExcessVisibility("Lib").Select(r => r.Node.Id).ToList();

        Assert.DoesNotContain("type:Lib.Dto", found);   // pinned by an external signature
        Assert.Contains("m:Lib.Dto.Unwrap()", found);   // its members are not pinned with it
        Assert.Contains("type:Lib.Inner", found);       // and falls out once Unwrap does
    }

    [Fact]
    public void Subsumes_MembersOfATypeItAlreadyReports()
    {
        // Unused is reported, so listing its Helper as well turns one decision
        // into two lines of worklist. Narrowing the type covers the member.
        var found = new GraphQuery(BuildGraph()).FindExcessVisibility("Lib", includeMembers: true)
            .Select(r => r.Node.Id).ToList();

        Assert.Contains("type:Lib.Unused", found);
        Assert.DoesNotContain("m:Lib.Unused.Helper()", found);
    }

    [Fact]
    public void Excludes_PropertiesAndFields_UnlessAsked()
    {
        // Measured on this repo: 243 of 364 candidates were record properties.
        // A type's shape is not separately narrowable surface, and burying 35
        // real findings under them makes the tool unusable.
        var g = BuildGraph();
        var prop = new GraphNode { Id = "prop:Lib.Used.Name", Type = NodeType.Property, Name = "Name" };
        prop.SetProperty("project", "Lib");
        prop.SetProperty("isPublic", true);
        g.AddNode(prop);
        Contains(g, "type:Lib.Used", "prop:Lib.Used.Name");

        Assert.DoesNotContain("prop:Lib.Used.Name",
            new GraphQuery(g).FindExcessVisibility("Lib").Select(r => r.Node.Id));
        Assert.Contains("prop:Lib.Used.Name",
            new GraphQuery(g).FindExcessVisibility("Lib", includeMembers: true).Select(r => r.Node.Id));
    }

    [Fact]
    public void Scopes_ToTheRequestedProject()
    {
        var found = new GraphQuery(BuildGraph()).FindExcessVisibility("Lib").ToList();

        Assert.All(found, r => Assert.Equal("Lib", r.Node.GetProperty<string>("project")));
    }

    [Fact]
    public void Excludes_PublicMembersOfTypesThatAreNotPublic()
    {
        // A private nested record's synthesized constructor is declared public
        // and reaches nothing: there is no edit to make. Found by turning the
        // result into an edit plan and reading "private sealed record" at the
        // line the plan wanted to narrow.
        var g = BuildGraph();
        g.AddNode(Type("type:Lib.Hidden", "Hidden", "Lib", isPublic: false));
        g.AddNode(Method("m:Lib.Hidden..ctor()", ".ctor", "Lib"));
        Contains(g, "type:Lib.Hidden", "m:Lib.Hidden..ctor()");

        Assert.DoesNotContain("m:Lib.Hidden..ctor()",
            new GraphQuery(g).FindExcessVisibility("Lib").Select(r => r.Node.Id));
    }

    [Fact]
    public void Excludes_InterfaceMembers_WhenTheInterfaceItselfIsRequired()
    {
        // An interface member takes no modifier, so the only narrowing is the
        // interface's own. IPort is required (App calls Close through it), so
        // Open -- which nothing external calls -- must not be offered either.
        var g = BuildGraph();
        var port = Type("type:Lib.IPort", "IPort", "Lib");
        port.SetProperty("isInterface", true);
        g.AddNode(port);
        var open = Method("m:Lib.IPort.Open()", "Open", "Lib");
        open.SetProperty("isAbstract", true);
        g.AddNode(open);
        var close = Method("m:Lib.IPort.Close()", "Close", "Lib");
        close.SetProperty("isAbstract", true);
        g.AddNode(close);
        Contains(g, "type:Lib.IPort", "m:Lib.IPort.Open()");
        Contains(g, "type:Lib.IPort", "m:Lib.IPort.Close()");
        Calls(g, "m:App.Caller.Go()", "m:Lib.IPort.Close()");

        var found = new GraphQuery(g).FindExcessVisibility("Lib").Select(r => r.Node.Id).ToList();

        Assert.DoesNotContain("type:Lib.IPort", found);
        Assert.DoesNotContain("m:Lib.IPort.Open()", found);
    }

    [Fact]
    public void Excludes_InterfaceMembersWithDefaultBodies()
    {
        // A default-bodied interface member is not abstract, and it still takes
        // no modifier. Found as four ILuaHost members offered on this repo.
        var g = BuildGraph();
        var port = Type("type:Lib.IPort", "IPort", "Lib");
        port.SetProperty("isInterface", true);
        g.AddNode(port);
        g.AddNode(Method("m:Lib.IPort.Describe()", "Describe", "Lib"));
        Contains(g, "type:Lib.IPort", "m:Lib.IPort.Describe()");
        Calls(g, "m:App.Caller.Go()", "m:Lib.IPort.Describe()");

        Assert.DoesNotContain("m:Lib.IPort.Describe()",
            new GraphQuery(g).FindExcessVisibility("Lib").Select(r => r.Node.Id));
    }

    [Fact]
    public void Pins_TypesExposedByPropertiesOfRequiredTypes()
    {
        // Used stays public (App calls it). Its Shape property is not reported
        // -- properties never are by default -- so it stays public too, and the
        // compiler will refuse a public property of an internal type (CS0053).
        // Before this rule the audit offered Shape and the build rejected it.
        var g = BuildGraph();
        g.AddNode(Type("type:Lib.Shape", "Shape", "Lib"));
        var prop = new GraphNode { Id = "prop:Lib.Used.Shape", Type = NodeType.Property, Name = "Shape" };
        prop.SetProperty("project", "Lib");
        prop.SetProperty("isPublic", true);
        g.AddNode(prop);
        Contains(g, "type:Lib.Used", "prop:Lib.Used.Shape");
        g.AddEdge(new GraphEdge { FromId = "prop:Lib.Used.Shape", ToId = "type:Lib.Shape", Type = EdgeType.References });

        var found = new GraphQuery(g).FindExcessVisibility("Lib").Select(r => r.Node.Id).ToList();

        Assert.DoesNotContain("type:Lib.Shape", found);
        // The rule is about what STAYS public: a property of a type that is
        // itself narrowed pins nothing, so the same shape on Unused still reports.
        Assert.Contains("type:Lib.Unused", found);
    }

    [Fact]
    public void Pins_TypesExposedByInterfaceMembers()
    {
        // IPort is required through Close. Open is never called from outside,
        // but it cannot be narrowed (no modifier), so the Packet it returns
        // stays exposed and must not be offered (CS0050 otherwise).
        var g = BuildGraph();
        var port = Type("type:Lib.IPort", "IPort", "Lib");
        port.SetProperty("isInterface", true);
        g.AddNode(port);
        g.AddNode(Type("type:Lib.Packet", "Packet", "Lib"));
        var open = Method("m:Lib.IPort.Open()", "Open", "Lib");
        open.SetProperty("isAbstract", true);
        g.AddNode(open);
        var close = Method("m:Lib.IPort.Close()", "Close", "Lib");
        close.SetProperty("isAbstract", true);
        g.AddNode(close);
        Contains(g, "type:Lib.IPort", "m:Lib.IPort.Open()");
        Contains(g, "type:Lib.IPort", "m:Lib.IPort.Close()");
        Calls(g, "m:App.Caller.Go()", "m:Lib.IPort.Close()");
        SignatureRef(g, "m:Lib.IPort.Open()", "type:Lib.Packet");

        Assert.DoesNotContain("type:Lib.Packet",
            new GraphQuery(g).FindExcessVisibility("Lib").Select(r => r.Node.Id));
    }
}
