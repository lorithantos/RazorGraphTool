namespace RazorGraph.Tests;

using RazorGraph.Lua;
using RazorGraph.Lua.Hosts;
using Xunit;

/// <summary>
/// The forms and spellings the corpus census said actually occur. Each test names
/// the count that justified it: these are not hypothetical constructs, they are
/// the ones a regex-based extractor would have missed in Penlight and Kong.
/// See note.razorgraph-lua-scoping.
/// </summary>
public class LuaExtractorTests
{
    private static LuaFileDeclarations Extract(string source, ILuaHost? host = null) =>
        new LuaDeclarationExtractor(host ?? new LuaRocksHost(Path.GetTempPath()))
            .Extract(new LuaSourceFile("/x/m.lua", "m.lua"), source);

    // ---- Function declaration forms ---------------------------------------
    // Four forms, no dominant one. Kong: local function 2355, assigned 1346,
    // member 662, method 846 — so missing any one of them loses a fifth.

    [Fact]
    public void Functions_AllFourDeclarationForms_AreFound()
    {
        var decl = Extract("""
            local M = {}
            function M.member() end
            function M:method() end
            local function localFn() end
            M.assigned = function() end
            return M
            """);

        Assert.Equal(
            new[] { "member", "method", "localFn", "assigned" },
            decl.Functions.Select(f => f.Name.Split('.').Last().Split(':').Last()));
        Assert.Equal(
            new[] { "member", "method", "localFunction", "assignedFunction" },
            decl.Functions.Select(f => f.Form));
    }

    [Fact]
    public void Functions_TableConstructorFields_AreFound()
    {
        // Found by hand-checking pl/class.lua: 12 of 14 without this. A table
        // field is not an assignment, so the assignment case never sees it, and
        // Kong's 305 setmetatable sites make it common rather than exotic.
        var decl = Extract("""
            local mt = setmetatable({}, {
              __call = function(f, ...) return f end,
              __index = function(t, k) return nil end,
            })
            """);

        Assert.Equal(new[] { "__call", "__index" }, decl.Functions.Select(f => f.Name));
        Assert.All(decl.Functions, f => Assert.Equal("tableFieldFunction", f.Form));
    }

    [Fact]
    public void Functions_MethodForm_IsMarked()
    {
        // Colon-defined functions take an implicit self; the distinction is worth
        // keeping even before anything consumes it.
        var decl = Extract("local M = {}\nfunction M:go() end");

        Assert.True(Assert.Single(decl.Functions).IsMethod);
    }

    // ---- Module reference spellings ---------------------------------------
    // The finding the parser choice was made for: paren-less require outnumbers
    // the parenthesised form roughly 2:1 in BOTH corpora (249/2497 vs 65/1370).

    [Fact]
    public void References_ParenlessAndParenthesisedAndLongString_AllFound()
    {
        var decl = Extract("""
            local a = require "mod.a"
            local b = require('mod.b')
            local c = require [[mod.c]]
            """);

        Assert.Equal(new[] { "mod.a", "mod.b", "mod.c" }, decl.References.Select(r => r.Target));
        Assert.All(decl.References, r => Assert.Equal("require", r.Mechanism));
    }

    [Fact]
    public void References_DynamicArgument_IsRecordedWithNullTarget()
    {
        // 46 such sites across the corpora. Null target becomes a reported
        // Unresolved rather than a guess -- the @Url.Action lesson in a new language.
        var decl = Extract("""
            local name = "mod." .. suffix
            local m = require(name)
            """);

        Assert.Null(Assert.Single(decl.References).Target);
    }

    [Fact]
    public void References_HostDecidesTheMechanism()
    {
        // Lightroom: import 191 vs require 22. A require-only extractor finds
        // almost nothing there, which is why the host names the functions.
        var decl = Extract("""
            local LrDialogs = import 'LrDialogs'
            local helper = require 'Helper'
            """, new LightroomHost(Path.GetTempPath()));

        Assert.Equal(new[] { "import", "require" }, decl.References.Select(r => r.Mechanism));
        Assert.Equal(new[] { "LrDialogs", "Helper" }, decl.References.Select(r => r.Target));
    }

    [Fact]
    public void References_ImportIsNotSeenWhenTheHostDoesNotUseIt()
    {
        // The same source under the LuaRocks host yields nothing: `import` is an
        // ordinary function call there, not a module reference.
        var decl = Extract("local d = import 'LrDialogs'");

        Assert.Empty(decl.References);
    }

    // ---- Dialect ----------------------------------------------------------

    [Fact]
    public void Parse_GotoAndLabels_AreAccepted()
    {
        // Kong uses these 148 times. A Lua 5.1 grammar fails the stress corpus
        // outright, which is what ruled 5.1 out as the target dialect.
        var decl = Extract("""
            for i = 1, 10 do
              if i == 5 then goto continue end
              ::continue::
            end
            """);

        Assert.Empty(decl.ParseErrors);
    }

    [Fact]
    public void Parse_ErrorsAreCollectedNotThrown()
    {
        // One unparseable file in a 1,309-file corpus must not abort the run.
        // Kong ships exactly one: spec/fixtures/invalid-module.lua.
        var decl = Extract("local = = =");

        Assert.NotEmpty(decl.ParseErrors);
    }

    // ---- Resolution: three outcomes, not two ------------------------------

    [Fact]
    public void Resolve_InitLua_CollapsesToTheDirectoryModule()
    {
        // kong/cache/init.lua IS kong.cache, not kong.cache.init. Kong uses this
        // idiom 70 times.
        using var tree = new TempTree();
        tree.Write("kong/cache/init.lua", "return {}");
        var host = new LuaRocksHost(tree.Root);

        var file = host.Discover(tree.Root).Single();
        Assert.Equal("kong.cache", host.ModuleNameFor(file));

        var resolution = Assert.IsType<ModuleResolution.InGraph>(host.Resolve("kong.cache", file));
        Assert.EndsWith("init.lua", resolution.FilePath);
    }

    [Fact]
    public void Resolve_MissingModule_IsExternalNotUnresolved()
    {
        // require "string" is correct code. Calling the standard library
        // unresolved would bury the genuinely dynamic requires in noise.
        using var tree = new TempTree();
        tree.Write("m.lua", "return {}");
        var host = new LuaRocksHost(tree.Root);

        var resolution = host.Resolve("string", host.Discover(tree.Root).Single());

        Assert.Equal("string", Assert.IsType<ModuleResolution.External>(resolution).Name);
    }

    [Fact]
    public void Resolve_DynamicReference_IsUnresolvedWithAReason()
    {
        using var tree = new TempTree();
        tree.Write("m.lua", "return {}");
        var host = new LuaRocksHost(tree.Root);

        var resolution = host.Resolve(null, host.Discover(tree.Root).Single());

        Assert.Contains("not a literal string", Assert.IsType<ModuleResolution.Unresolved>(resolution).Reason);
    }

    [Fact]
    public void Resolve_LightroomSdkModule_IsExternal()
    {
        // LrDialogs is not a missing module, it is someone else's. Collapsing
        // this into Unresolved reports 191 phantom failures on a healthy plugin.
        using var tree = new TempTree();
        tree.Write("p.lrdevplugin/Info.lua", "return {}");
        var host = new LightroomHost(tree.Root);

        var resolution = host.Resolve("LrDialogs", host.Discover(tree.Root).Single());

        Assert.Equal("LrDialogs", Assert.IsType<ModuleResolution.External>(resolution).Name);
    }

    // ---- Host detection and graph shape -----------------------------------

    [Fact]
    public void Detect_InfoLua_SelectsLightroomOverRockspec()
    {
        using var tree = new TempTree();
        tree.Write("p.lrdevplugin/Info.lua", "return {}");

        var detection = HostDetection.Detect(tree.Root);

        Assert.Equal("lightroom", detection.Host.Name);
        Assert.Contains("Info.lua", detection.Evidence);
    }

    [Fact]
    public void Build_ModuleIdsAreUniquePerFile()
    {
        // LR-Lua carries two SDK copies whose sample plugins share names, so
        // stem-based ids merged 33 of 75 files into other modules' nodes.
        using var tree = new TempTree();
        tree.Write("sdk3/p.lrdevplugin/Info.lua", "return {}");
        tree.Write("sdk8/p.lrdevplugin/Info.lua", "return {}");

        var (graph, report) = new LuaGraphBuilder().Build(tree.Root);

        Assert.Equal(2, report.Modules);
        Assert.Equal(2, graph.Nodes.Count(n => n.ForeignType == LuaGraphBuilder.ModuleKind));
        Assert.Empty(report.ParseFailures);
    }

    [Fact]
    public void Build_EmitsCoreKindsForFunctionsAndForeignForModules()
    {
        // The hybrid vocabulary: core where honest, foreign only where new.
        using var tree = new TempTree();
        tree.Write("a.lua", "local M = {}\nfunction M.go() end\nreturn M");
        tree.Write("b.lua", "local a = require 'a'\nreturn {}");

        var (graph, _) = new LuaGraphBuilder().Build(tree.Root);

        var fn = Assert.Single(graph.Nodes, n => n.Type == Core.Graph.NodeType.Method);
        Assert.Null(fn.ForeignType);
        Assert.Contains(graph.Edges, e => e.Type == Core.Graph.EdgeType.Contains && e.ForeignType is null);

        var requires = Assert.Single(graph.Edges, e => e.ForeignType == LuaGraphBuilder.RequiresKind);
        Assert.Equal("require", requires.GetProperty<string>("mechanism"));
    }

    [Fact]
    public void Build_ExternalReferencesAreRecordedOnTheModule()
    {
        using var tree = new TempTree();
        tree.Write("a.lua", "local s = require 'string'\nreturn {}");

        var (graph, report) = new LuaGraphBuilder().Build(tree.Root);

        Assert.Equal(1, report.ExternalReferences);
        Assert.Empty(report.UnresolvedReferences);
        Assert.Contains("string", graph.GetNode("mod:a")!.GetProperty<List<string>>("externalReferences")!);
    }

    // ---- Vendor code -------------------------------------------------------
    // Unzipping SDKs beside a plug-in project buried 9 authored files under 115
    // Adobe sample modules, so every aggregate answer was 93% someone else's.

    private static TempTree LightroomTreeWithSamples()
    {
        var tree = new TempTree();
        tree.Write("mine.lrdevplugin/Info.lua", "return {}");
        tree.Write("mine.lrdevplugin/Mine.lua", "local d = import 'LrDialogs'\nreturn {}");
        tree.Write("Lightroom SDK 15.3/Sample Plugins/demo.lrdevplugin/Demo.lua", "return {}");
        return tree;
    }

    [Fact]
    public void Vendor_SamplePluginsAreDroppedByDefaultAndReported()
    {
        using var tree = LightroomTreeWithSamples();

        var (graph, report) = new LuaGraphBuilder().Build(tree.Root);

        Assert.Equal(2, report.Modules);
        Assert.DoesNotContain(graph.Nodes, n => n.Name.Contains("Demo"));

        // Reported, never silent: dropping most of a tree without saying so reads
        // as "that is all the code there is".
        var skipped = Assert.Single(report.SkippedVendorFiles);
        Assert.Contains("Lightroom SDK 15.3", skipped);
    }

    [Fact]
    public void Vendor_IncludedOnRequestAndMarkedWhenIncluded()
    {
        using var tree = LightroomTreeWithSamples();

        var (graph, report) = new LuaGraphBuilder { IncludeVendor = true }.Build(tree.Root);

        Assert.Equal(3, report.Modules);
        Assert.Empty(report.SkippedVendorFiles);

        var sample = Assert.Single(graph.Nodes, n => n.GetProperty<bool>("vendor"));
        Assert.Contains("Adobe sample plugin", sample.GetProperty<string>("vendorReason"));
    }

    [Fact]
    public void Vendor_AuthorsOwnFilesAreNeverMarked()
    {
        using var tree = LightroomTreeWithSamples();

        var (graph, _) = new LuaGraphBuilder { IncludeVendor = true }.Build(tree.Root);

        var mine = graph.Nodes.Single(n => n.Name.EndsWith("Mine"));
        Assert.False(mine.GetProperty<bool>("vendor"));
        Assert.Null(mine.GetProperty<string>("vendorReason"));
    }

    [Fact]
    public void Vendor_IsHostSpecific_LuaRocksMarksNothing()
    {
        // A rockspec tree has no vendor rule, so the default interface method
        // applies and nothing is dropped.
        using var tree = new TempTree();
        tree.Write("a.lua", "return {}");

        var (_, report) = new LuaGraphBuilder().Build(tree.Root);

        Assert.Empty(report.SkippedVendorFiles);
    }

    // ---- Calls -------------------------------------------------------------
    // What a file USES, as against what it names. The distinction is the whole
    // point: an import bounds nothing, a call bounds the host version.

    [Fact]
    public void Calls_MethodCallsAreADistinctNodeType_AndAreNotDropped()
    {
        // obj:m() parses to MethodCallExpressionSyntax, NOT to a function call
        // with a colon -- so a scan handling only the latter silently loses every
        // method call, which in metatable-heavy Lua is most of them.
        var decl = Extract("""
            local M = {}
            function M.run(obj)
              obj:save()
              helper()
              other.thing()
            end
            return M
            """);

        Assert.Equal(
            new[] { "obj:save", "helper", "other.thing" },
            decl.Calls.Select(c => c.Callee));
        Assert.Equal(
            new[] { "method", "bare", "member" },
            decl.Calls.Select(c => c.Form));
    }

    [Fact]
    public void Calls_AreAttributedToTheEnclosingFunction()
    {
        var decl = Extract("""
            local function inner() helper() end
            local M = {}
            function M.outer() other() end
            return M
            """);

        Assert.Equal(
            new[] { "inner", "M.outer" },
            decl.Calls.Select(c => c.EnclosingFunction));
    }

    [Fact]
    public void Calls_AtFileScope_HaveNoEnclosingFunction()
    {
        // Module bodies run on load, so a call out here is real and belongs to
        // the module rather than to some invented owner.
        var decl = Extract("setup()");

        Assert.Null(Assert.Single(decl.Calls).EnclosingFunction);
    }

    [Fact]
    public void References_RecordTheVariableTheyAreBoundTo()
    {
        // Without this a call through an imported module is an unknown name, and
        // the only answerable question is which modules were imported -- not
        // which were used.
        var decl = Extract(
            "local d = import 'LrDialogs'\nreturn {}",
            new LightroomHost(Path.GetTempPath()));

        Assert.Equal("d", Assert.Single(decl.References).BoundTo);
    }

    [Fact]
    public void Calls_ThroughARequiredModule_BecomeCallEdges()
    {
        using var tree = new TempTree();
        tree.Write("util.lua", "local M = {}\nfunction M.trim(s) return s end\nreturn M");
        tree.Write("main.lua", "local util = require 'util'\nlocal function go() util.trim('x') end\nreturn go");

        var (graph, report) = new LuaGraphBuilder().Build(tree.Root);

        // The declaration says M.trim and the call says util.trim: neither knows
        // the other's spelling, and the final name is what they share.
        var edge = Assert.Single(graph.Edges, e => e.Type == Core.Graph.EdgeType.Calls);
        Assert.Equal("util.trim", edge.Properties["callee"]);
        Assert.Contains("trim", edge.ToId);
        Assert.Equal(1, report.Calls.InGraph);
    }

    [Fact]
    public void Calls_ToTheStandardLibrary_AreNotReportedAsGaps()
    {
        // A graph claiming thousands of unresolved calls because ipairs is not a
        // node would be describing the language, not the code.
        using var tree = new TempTree();
        tree.Write("a.lua", "local function f(t) for _ in ipairs(t) do end return string.format('%d', 1) end\nreturn f");

        var (_, report) = new LuaGraphBuilder().Build(tree.Root);

        Assert.Equal(2, report.Calls.Stdlib);
        Assert.Equal(0, report.Calls.Unresolved);
    }

    [Fact]
    public void Lightroom_MinimumVersion_SeparatesImportedFromCalled()
    {
        // The defect this pass exists for. LrDevelopController has shipped since
        // SDK 6.0; importing it requires 6.0 and says nothing more. Reporting a
        // higher floor from the import list sent someone hunting a compatibility
        // problem that was not there.
        using var tree = new TempTree();
        tree.Write("p.lrdevplugin/Info.lua", "return {}");
        tree.Write("p.lrdevplugin/Uses.lua", """
            local LrDialogs = import 'LrDialogs'
            local LrDevelopController = import 'LrDevelopController'
            local function go() LrDialogs.message('hi') end
            return go
            """);

        var (graph, _) = new LuaGraphBuilder().Build(tree.Root);
        var module = graph.Nodes.Single(n => n.Name.EndsWith("Uses"));

        // The floor is 6.0, and the ONLY thing holding it there is an import.
        Assert.Equal("6.0", module.GetProperty<string>("minimumSdkVersion"));

        var driver = Assert.Single(module.GetProperty<List<string>>("minimumSdkVersionDrivenBy")!);
        Assert.Equal("LrDevelopController (6.0, imported)", driver);

        // The call was captured -- it simply does not drive anything, because
        // LrDialogs has existed since 1.3. Only what sets the floor is listed, so
        // the explanation stays as short as the answer.
        Assert.Contains("LrDialogs.message", module.GetProperty<List<string>>("sdkCalls")!);
    }

    /// <summary>A throwaway directory tree, removed on dispose.</summary>
    private sealed class TempTree : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("razorgraph-lua").FullName;

        public void Write(string relativePath, string content)
        {
            var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }
}
