namespace RazorGraph.Tests;

using Loretta.CodeAnalysis;
using Loretta.CodeAnalysis.Lua;
using Xunit;

/// <summary>
/// What we rely on Loretta to do, asserted directly against Loretta rather than
/// through our own code.
///
/// These are not tests of RazorGraph. They are the contract we are depending on
/// from a pinned 0.2.13, written down so a version bump — or another quirk like
/// the one below — fails HERE, loudly, in a file whose name says what happened,
/// instead of turning a check silently permissive.
///
/// That distinction is the whole point. The dialect rule stopped detecting goto
/// and its own test still passed, because the rule's test exercised our code and
/// nothing pinned the library behaviour underneath it. A parser dependency needs
/// a layer that says what the parser must do.
///
/// They also serve as the trigger for a parked decision: replacing Loretta with
/// our own parser is worth doing only if this file starts failing in ways a
/// workaround cannot fix. The condition is mechanical so nobody has to keep
/// re-deciding it.
/// </summary>
public class LorettaCharacterizationTests
{
    // NO WARM-UP HERE EITHER, deliberately.
    //
    // An earlier version warmed the parser in a static constructor, mirroring
    // what the extractor did on 0.2.13. That was necessary then and is a liability
    // now: on the nightly these facts hold cold, and a warm-up in the test would
    // hide a regression if this dependency were ever rolled back. The tests should
    // exercise the state the product actually runs in, which is now no state at all.

    private static IReadOnlyList<Diagnostic> Errors(string source, LuaSyntaxOptions options) =>
        LuaSyntaxTree.ParseText(source, new LuaParseOptions(options), "t.lua")
            .GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

    // ---- Dialect discrimination -------------------------------------------
    // The dialect rule is built entirely on these four facts. If any flips, the
    // rule is reporting something other than what it claims.

    [Fact]
    public void GotoFacts_TheDialectRuleDependsOn()
    {
        // ONE test, fixed order, because Loretta's keyword state is not merely
        // seeded by the first parse -- a parse under All mutates it again, and
        // three separate tests asserting these facts passed or failed depending
        // on which xunit ran first. Splitting them made the suite a coin toss;
        // sequencing them here makes the contract deterministic.
        //
        // The behaviour being pinned is the whole basis of lua.dialect.
        const string source = "do goto skip ::skip:: end";

        // 5.1 has no goto. This is what makes a finding possible at all.
        Assert.NotEmpty(Errors(source, LuaSyntaxOptions.Lua51));

        // 5.2 introduced it. This is what the finding names.
        Assert.Empty(Errors(source, LuaSyntaxOptions.Lua52));

        // CORRECTION, recorded because the wrong version was believed and
        // written down: once the parser is warmed, All accepts goto too. The
        // earlier reading -- "All is not a superset, because ::label:: is a
        // label in 5.2 and a type cast in Luau" -- was a plausible story built
        // on a symptom of the cold-start state bug, not on Luau. Only the
        // warm-up was load-bearing.
        //
        // The version ladder stays regardless, on its own merit: it names the
        // EARLIEST dialect that accepts the construct, so a finding says "valid
        // in Lua 5.2" instead of "valid in some later Lua". That is a better
        // message, and it is why the ladder is not being reverted now that the
        // reason it was introduced turns out to have been misdiagnosed.
        Assert.Empty(Errors(source, LuaSyntaxOptions.All));
    }

    [Fact]
    public void IntegerDivision_IsRejectedBy52_AndAcceptedBy53()
    {
        const string source = "local x = 7 // 2";

        Assert.NotEmpty(Errors(source, LuaSyntaxOptions.Lua51));
        Assert.NotEmpty(Errors(source, LuaSyntaxOptions.Lua52));
        Assert.Empty(Errors(source, LuaSyntaxOptions.Lua53));
    }

    // NOT CHARACTERISED: acceptNestingOfLongStrings.
    //
    // The extractor sets it, and the justification is real -- Adobe's
    // remote_control sample fails to parse without it. But the first attempt
    // here asserted it against `[[ outer [[ inner ]] ]]`, which is not the
    // construct: a long string ends at the first ]], so that sample is invalid
    // Lua under every dialect and the test was measuring my mistake rather than
    // Loretta's behaviour. Pinning it needs the construct identified from the
    // file that motivated it, and a wrong characterization is worse than none --
    // it would fail on a version bump for a reason nobody could act on.

    // ---- Shapes a text scan would miss ------------------------------------
    // Each of these is why there is a parser here at all, with the corpus count
    // that justified it.

    [Fact]
    public void ParenlessAndLongStringCalls_AreModelledAsArguments()
    {
        // require "x" outnumbers require("x") roughly 2:1 in both corpora, so a
        // regex for require\( would miss the majority.
        var tree = LuaSyntaxTree.ParseText(
            "require 'a'\nrequire(\"b\")\nrequire [[c]]",
            new LuaParseOptions(LuaSyntaxOptions.Lua51), "t.lua");

        var calls = tree.GetRoot().DescendantNodes()
            .OfType<Loretta.CodeAnalysis.Lua.Syntax.FunctionCallExpressionSyntax>()
            .ToList();

        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public void MethodCalls_AreADistinctNodeTypeFromFunctionCalls()
    {
        // Handling only FunctionCallExpressionSyntax drops every obj:m(), which
        // in metatable-heavy Lua is most calls. Silent, and total.
        var tree = LuaSyntaxTree.ParseText(
            "obj:method()\nobj.field()",
            new LuaParseOptions(LuaSyntaxOptions.Lua51), "t.lua");

        var nodes = tree.GetRoot().DescendantNodes().ToList();

        Assert.Single(nodes.OfType<Loretta.CodeAnalysis.Lua.Syntax.MethodCallExpressionSyntax>());
        Assert.Single(nodes.OfType<Loretta.CodeAnalysis.Lua.Syntax.FunctionCallExpressionSyntax>());
    }

    // ---- Error recovery ----------------------------------------------------

    [Fact]
    public void AMalformedFile_StillYieldsTheDeclarationsAroundTheBreak()
    {
        // The expensive thing to rebuild, and the reason a bad file costs one
        // file rather than a whole run. Everything else in the grammar is small;
        // recovery is not.
        // Asserted through the extractor rather than over raw node types,
        // because that is the dependency: what must survive a break is our
        // declaration list, not a particular tree shape. Stating it in Loretta's
        // vocabulary made it fragile in a way that taught nothing -- the first
        // version parsed under a different dialect than the extractor uses and
        // failed while the extractor recovered the same file perfectly.
        var declarations = new RazorGraph.Lua.LuaDeclarationExtractor(
                new RazorGraph.Lua.Hosts.LuaRocksHost(Path.GetTempPath()))
            .Extract(
                new RazorGraph.Lua.Hosts.LuaSourceFile("/x/broken.lua", "broken.lua"),
                "local function good() end\nlocal x = = =\nlocal function alsoGood() end");

        Assert.NotEmpty(declarations.ParseErrors);
        Assert.Contains(declarations.Functions, f => f.Name == "good");
        Assert.Contains(declarations.Functions, f => f.Name == "alsoGood");
    }

    // ---- What is NOT pinned here, and why ----------------------------------
    //
    // The cold-start half of the keyword quirk. Loretta seeds keyword
    // recognition from the first parse in a process, so a 5.1-first process
    // never recognises goto afterwards. That is only observable OUT of process:
    // by the time any test runs, some parse has happened. Running the CLI over a
    // Lightroom tree containing goto is the check, and it is how this was found
    // in the first place -- the in-process test said everything was fine.
    //
    // Recorded rather than faked, because a test that cannot see the failure it
    // claims to guard is worse than an honest gap.
    //
    // The same applies to the wider hazard behind it. Reflection over the
    // assembly shows process-wide mutable statics -- LexerCache.s_keywordKindPool
    // among them -- and identical source under identical options has been
    // observed parsing differently depending on what parsed before. A probe that
    // asserted the divergence was written and then deleted: its own result
    // depended on test ordering, so it was flaky in exactly the way it claimed
    // to document, and a flaky test documenting flakiness teaches nobody.
    //
    // What guards this instead lives in the product, where it can act:
    // LuaDeclarationExtractor.DialectDiscriminationWorks parses a canonical
    // snippet under both dialects before the dialect rule reads anything, and
    // the rule reports that it could not run rather than reporting nothing.
    // Silence and "no problems found" have to be different sentences, because
    // this failure is permissive: a 5.1 parse taught to accept goto finds
    // nothing wrong.
}
