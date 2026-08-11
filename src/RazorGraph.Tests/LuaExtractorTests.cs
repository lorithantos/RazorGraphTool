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
