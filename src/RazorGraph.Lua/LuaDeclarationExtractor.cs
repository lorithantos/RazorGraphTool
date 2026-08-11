namespace RazorGraph.Lua;

using global::Loretta.CodeAnalysis;
using global::Loretta.CodeAnalysis.Lua;
using global::Loretta.CodeAnalysis.Lua.Syntax;
using RazorGraph.Lua.Hosts;

/// <summary>A function found in a Lua file.</summary>
/// <param name="Form">Which of the four declaration spellings produced it — kept because the census showed no dominant one, so a skew here is the tell that a form is being missed.</param>
public sealed record LuaFunction(string Name, int LineStart, int LineEnd, string Form, bool IsMethod);

/// <summary>A module reference site, before the host has resolved it.</summary>
/// <param name="Target">The literal string argument, or null for a dynamic reference.</param>
public sealed record LuaReference(string Mechanism, string? Target, int Line);

/// <summary>What one parsed file yielded.</summary>
public sealed record LuaFileDeclarations(
    LuaSourceFile File,
    IReadOnlyList<LuaFunction> Functions,
    IReadOnlyList<LuaReference> References,
    IReadOnlyList<string> ParseErrors);

/// <summary>
/// Parses Lua and yields declarations. The ONLY file in the codebase that knows
/// Loretta types exist — everything downstream sees the records above, so
/// replacing the parser is a rewrite of this file rather than of the project.
/// </summary>
public sealed class LuaDeclarationExtractor(ILuaHost host)
{
    public LuaFileDeclarations Extract(LuaSourceFile file, string source)
    {
        var options = new LuaParseOptions(SyntaxOptionsFor(host.Dialect));
        var tree = LuaSyntaxTree.ParseText(source, options, file.FullPath);
        var root = tree.GetRoot();

        // Diagnostics are collected, not thrown. One unparseable file in a
        // 1,309-file corpus must not abort the run: the graph is worth having
        // minus that file, and the failure is worth reporting rather than hiding.
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")
            .Take(5)
            .ToList();

        var functions = new List<LuaFunction>();
        var references = new List<LuaReference>();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                // function M.f() / function M:f() / function f()
                case FunctionDeclarationStatementSyntax fn:
                    functions.Add(new LuaFunction(
                        NameOf(fn.Name), LineOf(fn), EndLineOf(fn),
                        FormOf(fn.Name), fn.Name is MethodFunctionNameSyntax));
                    break;

                // local function f()
                case LocalFunctionDeclarationStatementSyntax local:
                    functions.Add(new LuaFunction(
                        local.Name.Name, LineOf(local), EndLineOf(local), "localFunction", IsMethod: false));
                    break;

                // M.f = function() ... end — the form a naive "function <name>("
                // scan misses entirely, and the second-largest bucket in Kong.
                case AssignmentStatementSyntax assign:
                    functions.AddRange(AssignedFunctions(assign));
                    break;

                // { __call = function() end } — a field, not an assignment, so the
                // assignment case above never sees it. Found by hand-checking
                // pl/class.lua against ground truth: 12 of 14 without this, and
                // Kong's 305 setmetatable sites make it common in metatable-heavy
                // code rather than a curiosity.
                case IdentifierKeyedTableFieldSyntax { Value: AnonymousFunctionExpressionSyntax anonField } field:
                    functions.Add(new LuaFunction(
                        field.Identifier.Text, LineOf(anonField), EndLineOf(anonField),
                        "tableFieldFunction", IsMethod: false));
                    break;

                case FunctionCallExpressionSyntax call when ReferenceName(call) is { } mechanism:
                    references.Add(new LuaReference(mechanism, LiteralArgument(call.Argument), LineOf(call)));
                    break;
            }
        }

        return new LuaFileDeclarations(file, functions, references, errors);
    }

    /// <summary>
    /// Function-valued assignments: <c>M.f = function() end</c> and
    /// <c>local f = function() end</c>. Paired positionally, which is how Lua's
    /// multiple assignment works.
    /// </summary>
    private static IEnumerable<LuaFunction> AssignedFunctions(AssignmentStatementSyntax assign)
    {
        var targets = assign.Variables.ToList();
        var values = assign.EqualsValues?.Values.ToList() ?? [];

        for (var i = 0; i < Math.Min(targets.Count, values.Count); i++)
        {
            if (values[i] is not AnonymousFunctionExpressionSyntax anon) continue;
            yield return new LuaFunction(
                targets[i].ToString().Trim(), LineOf(anon), EndLineOf(anon), "assignedFunction", IsMethod: false);
        }
    }

    /// <summary>
    /// The host decides what counts as a module reference, so require, import,
    /// include and AddCSLuaFile all arrive here the same way.
    /// </summary>
    private string? ReferenceName(FunctionCallExpressionSyntax call)
    {
        var name = call.Expression switch
        {
            IdentifierNameSyntax n => n.Name,
            _ => null
        };
        return name is not null && host.ReferenceFunctions.Contains(name) ? name : null;
    }

    /// <summary>
    /// The literal string argument, in any of its three spellings, or null when
    /// the argument is an expression.
    ///
    /// This is the method the whole parser choice was made for. The census found
    /// paren-less <c>require "x"</c> outnumbering <c>require("x")</c> roughly 2:1
    /// in both corpora; Loretta models it as a distinct argument node, so both —
    /// and long-string <c>require [[x]]</c> — collapse to one switch instead of
    /// the regex that would have silently missed the majority.
    /// </summary>
    private static string? LiteralArgument(FunctionArgumentSyntax argument) => argument switch
    {
        // require "x"  and  require [[x]]
        StringFunctionArgumentSyntax s => s.Expression.Token.Value as string,

        // require("x") — literal only; an expression stays null and becomes a
        // reported Unresolved rather than a guess.
        ExpressionListFunctionArgumentSyntax list when list.Expressions.Count == 1
            => (list.Expressions[0] as LiteralExpressionSyntax)?.Token.Value as string,

        _ => null
    };

    private static string NameOf(FunctionNameSyntax name) => name.ToString().Trim();

    private static string FormOf(FunctionNameSyntax name) => name switch
    {
        MethodFunctionNameSyntax => "method",
        MemberFunctionNameSyntax => "member",
        SimpleFunctionNameSyntax => "function",
        _ => "function"
    };

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int EndLineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

    /// <summary>
    /// Map our dialect onto the parser's presets. The one place the two
    /// vocabularies meet, so a different parser changes this method and nothing
    /// else. Any is deliberately permissive: for a proof of concept, parsing a
    /// construct from the wrong dialect beats refusing the file.
    /// </summary>
    private static LuaSyntaxOptions SyntaxOptionsFor(LuaDialect dialect) => dialect switch
    {
        // Nested long strings -- [[ ... [[ ... ]] ... ]] -- were DEPRECATED in Lua
        // 5.1 and only removed in 5.2, so a real 5.1 interpreter accepts them.
        // Loretta's preset rejects them, which is stricter than the language and
        // stricter than the host: Lightroom runs 5.1.5, and Adobe's own
        // remote_control sample uses the construct and works. Left alone, that
        // file fails to parse and silently contributes nothing to the graph.
        LuaDialect.Lua51 => LuaSyntaxOptions.Lua51.With(acceptNestingOfLongStrings: true),
        LuaDialect.Lua52 => LuaSyntaxOptions.Lua52,
        LuaDialect.Lua53 => LuaSyntaxOptions.Lua53,
        LuaDialect.Lua54 => LuaSyntaxOptions.Lua54,
        LuaDialect.LuaJit21 => LuaSyntaxOptions.LuaJIT21,
        LuaDialect.GMod => LuaSyntaxOptions.GMod,
        LuaDialect.Luau => LuaSyntaxOptions.Luau,
        _ => LuaSyntaxOptions.All
    };
}
