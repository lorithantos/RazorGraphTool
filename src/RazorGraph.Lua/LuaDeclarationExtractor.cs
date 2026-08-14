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
/// <param name="BoundTo">
/// The variable the reference was assigned to, as in
/// <c>local LrDialogs = import 'LrDialogs'</c>. This is what makes a later
/// <c>LrDialogs.message(...)</c> resolvable: without the binding, a call through
/// an imported module is just an unknown name.
/// </param>
public sealed record LuaReference(string Mechanism, string? Target, int Line, string? BoundTo = null);

/// <summary>
/// A call site, attributed to the function containing it.
/// </summary>
/// <param name="Form">
/// How the callee was written, which decides what can resolve it:
/// <c>bare</c> (<c>f()</c>), <c>member</c> (<c>M.f()</c>), <c>method</c>
/// (<c>obj:f()</c>), or <c>chain</c> (<c>a.b.c()</c>, where the middle is not
/// tracked and only the root and final name survive).
/// </param>
/// <param name="Arguments">
/// The call's arguments, positionally, with a literal string's value where it has
/// one and null where it does not. Null entries are the point as much as the
/// values: a checker must be able to tell "this argument is the string 'caption'"
/// from "this argument is an expression nobody can evaluate here", and treat only
/// the first as something it may judge.
///
/// Empty for a call with no arguments, and for argument shapes that carry no
/// literal at all — a table constructor, <c>f{...}</c>, is a single argument that
/// is never a string.
/// </param>
public sealed record LuaCall(
    string? EnclosingFunction,
    int? EnclosingLine,
    string Callee,
    string Root,
    string? Member,
    string Form,
    int Line,
    IReadOnlyList<string?> Arguments);

/// <summary>
/// A construct this host's Lua rejects that a later Lua accepts.
///
/// Kept apart from an ordinary parse failure because they call for opposite
/// responses: a malformed file is broken everywhere, while this one is
/// well-formed Lua aimed at the wrong version — the single most likely mistake
/// in generated code, since the public corpus skews 5.3/5.4 and Lightroom runs
/// 5.1. Reporting it as "failed to parse" hides exactly the thing worth saying.
/// </summary>
/// <param name="AcceptedBy">The oldest later Lua that does accept it, e.g. "Lua 5.2".</param>
public sealed record LuaSyntaxRejection(int Line, string Message, string AcceptedBy);

/// <summary>
/// One field of the table a file returns at chunk level.
///
/// A manifest in Lua is just a file returning a table — Lightroom's Info.lua,
/// a rockspec, a WoW .toc equivalent — so this is a general fact about a file
/// rather than a Lightroom one. What the keys MEAN is host knowledge; that they
/// are there is not.
/// </summary>
/// <param name="Kind">number, string, boolean, table, function, or other.</param>
/// <param name="Value">The literal, when it is one. Null for tables and functions.</param>
public sealed record LuaManifestField(string Key, string Kind, string? Value, int Line);

/// <summary>What one parsed file yielded.</summary>
public sealed record LuaFileDeclarations(
    LuaSourceFile File,
    IReadOnlyList<LuaFunction> Functions,
    IReadOnlyList<LuaReference> References,
    IReadOnlyList<string> ParseErrors,
    IReadOnlyList<LuaCall> Calls,
    IReadOnlyList<LuaSyntaxRejection> DialectRejections,
    /// <summary>
    /// Null when the file does not return a table literal at all; empty when it
    /// returns one with no fields. The difference matters to a manifest check:
    /// "return {}" is a readable, empty manifest, and reporting it as unreadable
    /// is a false positive on a legal file.
    /// </summary>
    IReadOnlyList<LuaManifestField>? ReturnedFields,

    /// <summary>
    /// Every string assigned to a field named <c>file</c> anywhere inside the
    /// returned table.
    ///
    /// Manifests name their entry points that way — Lightroom's menu items are
    /// <c>{ title = "...", file = "X.lua" }</c> — and those names are the ROOTS
    /// of what the host will load. Collected generically here because finding a
    /// string in a table is parsing; deciding that it means "load this script"
    /// is host knowledge.
    /// </summary>
    IReadOnlyList<string> ReturnedFileReferences);

/// <summary>
/// Parses Lua and yields declarations. The ONLY file in the codebase that knows
/// Loretta types exist — everything downstream sees the records above, so
/// replacing the parser is a rewrite of this file rather than of the project.
/// </summary>
public sealed class LuaDeclarationExtractor(ILuaHost host)
{
    // NO PARSER WARM-UP, as of 0.2.14-nightly.26.
    //
    // 0.2.13 needed one. Its lexer cached tokens without accounting for the
    // syntax options in force, so parsing a 5.1 file first left goto and
    // ::label:: unrecognised for the rest of the process even where a later
    // parse set acceptGoto: the option read True and the parse failed anyway.
    // That silently disabled the goto half of the dialect rule, and a throwaway
    // 5.4 parse up front defused it.
    //
    // Upstream issue #152 fixed it on 2025-07-24, after the last stable release,
    // which is why this project pins a nightly. Verified cold and out of process
    // on the tree that exposed the original: goto and integer division are both
    // caught with no warm-up, and a file that previously fell through as an
    // ordinary parse failure is now correctly attributed to Lua 5.2.

    /// <summary>
    /// Whether the parser can currently tell this dialect from a later one —
    /// checked, not assumed.
    ///
    /// Kept after the upstream fix, because it is not specific to that bug. The
    /// failure mode of a parser that cannot separate dialects is SILENCE: a 5.1
    /// parse that accepts goto reports no error, so the rule finds nothing and
    /// "found nothing" reads exactly like "clean". Any future cache or state
    /// defect in this dependency lands the same way, and two parses of six
    /// tokens is a cheap price for the difference between an absent finding and
    /// an absent capability.
    ///
    /// So the dialect rule measures its instrument before trusting it. Two
    /// parses of a canonical snippet: the host's dialect must reject goto and a
    /// later one must accept it. If that fails, discrimination is not working in
    /// this process and the rule says so rather than reporting nothing and
    /// letting silence read as a pass.
    ///
    /// Cheap — two parses of six tokens, once per build.
    /// </summary>
    public static bool DialectDiscriminationWorks(LuaDialect hostDialect)
    {
        const string probe = "do goto skip ::skip:: end";

        static bool Rejects(string source, LuaSyntaxOptions options) =>
            LuaSyntaxTree.ParseText(source, new LuaParseOptions(options), "dialect-probe.lua")
                .GetDiagnostics()
                .Any(d => d.Severity == DiagnosticSeverity.Error);

        // Only meaningful for a dialect that predates goto; anything later has
        // nothing to discriminate against here and is left alone.
        if (hostDialect != LuaDialect.Lua51) return true;

        return Rejects(probe, SyntaxOptionsFor(hostDialect))
            && !Rejects(probe, LuaSyntaxOptions.Lua52);
    }

    public LuaFileDeclarations Extract(LuaSourceFile file, string source)
    {
        var options = new LuaParseOptions(SyntaxOptionsFor(host.Dialect));
        var tree = LuaSyntaxTree.ParseText(source, options, file.FullPath);
        var root = tree.GetRoot();

        // Diagnostics are collected, not thrown. One unparseable file in a
        // 1,309-file corpus must not abort the run: the graph is worth having
        // minus that file, and the failure is worth reporting rather than hiding.
        var diagnostics = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        var errors = diagnostics
            .Select(d => $"{d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage()}")
            .Take(5)
            .ToList();

        var dialectRejections = DialectRejections(source, file, diagnostics);

        var functions = new List<LuaFunction>();
        var references = new List<LuaReference>();
        var calls = new List<LuaCall>();

        // Which syntax node produced each function, so a call can be attributed
        // to the function containing it. DescendantNodes walks parents before
        // children, so every enclosing declaration is already recorded by the
        // time a call inside it is reached.
        var declaredAt = new Dictionary<SyntaxNode, LuaFunction>();

        void Declare(SyntaxNode at, LuaFunction fn)
        {
            functions.Add(fn);
            declaredAt[at] = fn;
        }

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                // function M.f() / function M:f() / function f()
                case FunctionDeclarationStatementSyntax fn:
                    Declare(fn, new LuaFunction(
                        NameOf(fn.Name), LineOf(fn), EndLineOf(fn),
                        FormOf(fn.Name), fn.Name is MethodFunctionNameSyntax));
                    break;

                // local function f()
                case LocalFunctionDeclarationStatementSyntax local:
                    Declare(local, new LuaFunction(
                        local.Name.Name, LineOf(local), EndLineOf(local), "localFunction", IsMethod: false));
                    break;

                // M.f = function() ... end — the form a naive "function <name>("
                // scan misses entirely, and the second-largest bucket in Kong.
                case AssignmentStatementSyntax assign:
                    foreach (var (at, fn) in AssignedFunctions(assign)) Declare(at, fn);
                    break;

                // { __call = function() end } — a field, not an assignment, so the
                // assignment case above never sees it. Found by hand-checking
                // pl/class.lua against ground truth: 12 of 14 without this, and
                // Kong's 305 setmetatable sites make it common in metatable-heavy
                // code rather than a curiosity.
                case IdentifierKeyedTableFieldSyntax { Value: AnonymousFunctionExpressionSyntax anonField } field:
                    Declare(anonField, new LuaFunction(
                        field.Identifier.Text, LineOf(anonField), EndLineOf(anonField),
                        "tableFieldFunction", IsMethod: false));
                    break;

                case FunctionCallExpressionSyntax call when ReferenceName(call) is { } mechanism:
                    references.Add(new LuaReference(
                        mechanism, LiteralArgument(call.Argument), LineOf(call), BoundVariable(call)));
                    break;

                // f() and M.f(). Everything the host does not claim as a module
                // reference is an ordinary call.
                case FunctionCallExpressionSyntax call:
                    if (CalleeOf(call.Expression) is var (callRoot, callMember, callForm))
                        calls.Add(CallAt(call, callRoot, callMember, callForm, declaredAt));
                    break;

                // obj:f(). A DISTINCT node type in the parser, not a
                // FunctionCallExpressionSyntax with a colon — so a scan that
                // handles only the case above silently drops every method call,
                // which in metatable-heavy Lua is most of them.
                case MethodCallExpressionSyntax method:
                    // A receiver that is not a simple name -- photoArray[name]:f(),
                    // getPhotos()[1]:f() -- still records the call, with an empty
                    // root meaning "the receiver is not a name this can follow".
                    //
                    // Requiring a resolvable root dropped these on the floor
                    // entirely: not resolved, not unresolved, not counted. In
                    // Lightroom plug-ins that is most of the interesting surface,
                    // because photos arrive from catalog:getTargetPhotos() and are
                    // then indexed out of a table -- so every metadata call was
                    // invisible, and the call census quietly understated itself.
                    calls.Add(CallAt(
                        method, RootOf(method.Expression) ?? string.Empty,
                        method.Identifier.Text, "method", declaredAt));
                    break;
            }
        }

        var returnedTable = ReturnedTable(root);

        return new LuaFileDeclarations(
            file, functions, references, errors, calls, dialectRejections,
            FieldsOf(returnedTable), FileReferencesIn(returnedTable));
    }

    /// <summary>
    /// Strings assigned to a field named <c>file</c>, at any depth inside the
    /// returned table. Depth matters: menu items are a list of tables, so the
    /// interesting names are two levels below the key a reader would name.
    /// </summary>
    private static IReadOnlyList<string> FileReferencesIn(TableConstructorExpressionSyntax? table)
    {
        if (table is null) return [];

        // Two spellings, because manifests use both. A menu item names its
        // script with `file = "X.lua"`; a provider list is a bare array of
        // names — Adobe's custommetadatasample writes
        // LrMetadataTagsetFactory = { 'CustomMetadataTagset.lua', ... }.
        //
        // Collecting only the keyed form reported three of Adobe's own files as
        // loaded by nothing, which for a rule that says "delete this" is the
        // worst possible direction to be wrong in. Any string ending in .lua is
        // taken as a reference: a false root only costs silence, a false orphan
        // costs someone their code.
        var keyed = table.DescendantNodes()
            .OfType<IdentifierKeyedTableFieldSyntax>()
            .Where(field => string.Equals(field.Identifier.Text, "file", StringComparison.OrdinalIgnoreCase))
            .Select(field => (field.Value as LiteralExpressionSyntax)?.Token.Value as string);

        var anyLuaName = table.DescendantNodes()
            .OfType<LiteralExpressionSyntax>()
            .Select(literal => literal.Token.Value as string)
            .Where(value => value is not null && value.EndsWith(".lua", StringComparison.OrdinalIgnoreCase));

        return keyed.Concat(anyLuaName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The fields of a table returned at CHUNK level — <c>return { a = 1 }</c> at
    /// the end of the file.
    ///
    /// Chunk level only: a return inside a function is that function's result,
    /// not the file's, and treating one as a manifest would read a table from
    /// whichever function happened to be scanned. <c>return M</c> yields nothing,
    /// which is correct — the fields were assigned elsewhere and this pass does
    /// not track them.
    /// </summary>
    private static TableConstructorExpressionSyntax? ReturnedTable(SyntaxNode root)
    {
        var chunkReturn = root.DescendantNodes()
            .OfType<ReturnStatementSyntax>()
            .FirstOrDefault(r => !r.Ancestors().Any(a =>
                a is FunctionDeclarationStatementSyntax
                  or LocalFunctionDeclarationStatementSyntax
                  or AnonymousFunctionExpressionSyntax));

        return chunkReturn?.Expressions.FirstOrDefault() as TableConstructorExpressionSyntax;
    }

    private static IReadOnlyList<LuaManifestField>? FieldsOf(TableConstructorExpressionSyntax? table)
    {
        // Null, not empty: "returns nothing to read" and "returns an empty
        // table" are different facts, and only the first is unreadable.
        if (table is null) return null;

        var fields = new List<LuaManifestField>();
        foreach (var field in table.Fields.OfType<IdentifierKeyedTableFieldSyntax>())
        {
            var (kind, value) = ValueOf(field.Value);
            fields.Add(new LuaManifestField(field.Identifier.Text, kind, value, LineOf(field)));
        }

        return fields;
    }

    /// <summary>
    /// A field value classified far enough to check a declared type against,
    /// without evaluating anything.
    /// </summary>
    private static (string Kind, string? Value) ValueOf(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax { Token.Value: string s } => ("string", s),
        LiteralExpressionSyntax { Token.Value: bool b } => ("boolean", b ? "true" : "false"),
        LiteralExpressionSyntax literal when literal.Token.Value is not null
            => ("number", Convert.ToString(literal.Token.Value, System.Globalization.CultureInfo.InvariantCulture)),
        TableConstructorExpressionSyntax => ("table", null),
        AnonymousFunctionExpressionSyntax => ("function", null),
        _ => ("other", null)
    };

    /// <summary>
    /// Which of this file's parse errors are the HOST's Lua refusing valid
    /// later-Lua, rather than the file being broken.
    ///
    /// Decided by re-parsing against SPECIFIC later versions, oldest first, and
    /// naming the first that accepts the file: <c>goto</c> is 5.2, integer
    /// division and bitwise operators are 5.3, and all of them are dead in
    /// Lightroom's 5.1. "Valid in Lua 5.2" is a different sentence from "failed
    /// to parse", and it is the one a reader can act on.
    ///
    /// A ladder rather than one permissive parse, because naming the earliest
    /// accepting version is what makes the finding actionable: "valid in Lua
    /// 5.2" tells a reader which construct they reached for, where "valid in
    /// some later Lua" only tells them they are wrong.
    ///
    /// It was introduced for a second reason that turned out to be a
    /// misdiagnosis — LuaSyntaxOptions.All appeared not to be a superset, and
    /// once the parser warm-up below was in place, All accepted the same code.
    /// The ladder is kept on the first reason alone. See
    /// LorettaCharacterizationTests.
    ///
    /// Only runs when the first parse already failed, so a clean file costs
    /// nothing.
    /// </summary>
    private IReadOnlyList<LuaSyntaxRejection> DialectRejections(
        string source, LuaSourceFile file, IReadOnlyList<Diagnostic> diagnostics)
    {
        if (diagnostics.Count == 0) return [];

        foreach (var (dialect, name) in LaterDialects)
        {
            if (dialect == host.Dialect) continue;

            var candidate = LuaSyntaxTree.ParseText(
                source, new LuaParseOptions(SyntaxOptionsFor(dialect)), file.FullPath);

            if (candidate.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) continue;

            return diagnostics
                .Select(d => new LuaSyntaxRejection(
                    d.Location.GetLineSpan().StartLinePosition.Line + 1, d.GetMessage(), name))
                .Take(10)
                .ToList();
        }

        // No later version takes it either: the file is malformed, and saying
        // "wrong Lua version" would send someone looking for a setting.
        return [];
    }

    /// <summary>
    /// Versions to test a rejected file against, oldest first, so the report
    /// names the EARLIEST Lua that would accept it rather than merely some Lua.
    /// LuaJIT sits last: it is a 5.1 superset rather than a later standard, so
    /// attributing a construct to it is only right when no standard version has it.
    /// </summary>
    private static readonly (LuaDialect Dialect, string Name)[] LaterDialects =
    [
        (LuaDialect.Lua52, "Lua 5.2"),
        (LuaDialect.Lua53, "Lua 5.3"),
        (LuaDialect.Lua54, "Lua 5.4"),
        (LuaDialect.LuaJit21, "LuaJIT 2.1")
    ];

    /// <summary>
    /// One call site, attributed to the innermost function declaration around it.
    /// A call at file scope has no enclosing function, which is normal in Lua —
    /// module bodies run on load — and is recorded as null rather than invented.
    /// </summary>
    private static LuaCall CallAt(
        SyntaxNode site, string root, string? member, string form,
        Dictionary<SyntaxNode, LuaFunction> declaredAt)
    {
        LuaFunction? enclosing = null;
        foreach (var ancestor in site.Ancestors())
        {
            if (declaredAt.TryGetValue(ancestor, out var found)) { enclosing = found; break; }
        }

        // An empty root is a receiver this cannot name, so the callee is the method
        // alone. Writing ":f" instead would invent a spelling no source contains,
        // and it is the callee that later gets matched against declared functions.
        var separator = form == "method" ? ":" : ".";
        var callee = member is null
            ? root
            : root.Length == 0 ? member : $"{root}{separator}{member}";

        // Both call spellings carry their arguments, but on DIFFERENT node types --
        // the same split that makes obj:f() a separate case above. Reading only
        // FunctionCallExpressionSyntax would leave every method call argument-less,
        // and method calls are where the metadata keys are.
        var argument = site switch
        {
            FunctionCallExpressionSyntax call => call.Argument,
            MethodCallExpressionSyntax method => method.Argument,
            _ => null
        };

        return new LuaCall(
            enclosing?.Name, enclosing?.LineStart, callee, root, member, form, LineOf(site),
            LiteralArguments(argument));
    }

    /// <summary>
    /// A call's arguments positionally: the value of each literal string, null for
    /// anything else.
    /// </summary>
    /// <remarks>
    /// The single-argument sibling of this, <see cref="LiteralArgument"/>, answers
    /// "what module is being imported". This answers "what was passed", which is a
    /// different question: position matters, and an argument that is not a literal
    /// must come back as null rather than being skipped. Dropping it would shift
    /// every argument after it left, and a checker reading argument 1 would find
    /// argument 2.
    /// </remarks>
    private static IReadOnlyList<string?> LiteralArguments(FunctionArgumentSyntax? argument) => argument switch
    {
        // f "x" and f [[x]] — one argument, always a string.
        StringFunctionArgumentSyntax s => [s.Expression.Token.Value as string],

        ExpressionListFunctionArgumentSyntax list =>
            [.. list.Expressions.Select(e => (e as LiteralExpressionSyntax)?.Token.Value as string)],

        // f{...} — a table constructor. One argument, never a literal string.
        _ => []
    };

    /// <summary>
    /// The callee split into the name it starts from and the member being
    /// called. <c>a.b.c()</c> keeps only the root and the final name, and says so
    /// with the form <c>chain</c>: the middle is not tracked, so resolution must
    /// not treat it as if <c>c</c> hung off <c>a</c>.
    /// </summary>
    private static (string Root, string? Member, string Form)? CalleeOf(PrefixExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => (id.Name, null, "bare"),
        MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax owner } member
            => (owner.Name, member.MemberName.Text, "member"),
        MemberAccessExpressionSyntax member when RootOf(member.Expression) is { } root
            => (root, member.MemberName.Text, "chain"),
        _ => null
    };

    /// <summary>The leftmost identifier of a prefix expression, or null when it does not start from one.</summary>
    private static string? RootOf(PrefixExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Name,
        MemberAccessExpressionSyntax member => RootOf(member.Expression),
        MethodCallExpressionSyntax method => RootOf(method.Expression),
        FunctionCallExpressionSyntax call => RootOf(call.Expression),
        _ => null
    };

    /// <summary>
    /// The variable a module reference was assigned to — the <c>LrDialogs</c> of
    /// <c>local LrDialogs = import 'LrDialogs'</c>.
    ///
    /// This is what makes a later <c>LrDialogs.message(...)</c> resolvable to the
    /// module it came from. Without it a call through an imported module is just
    /// an unknown name, and the only question that could be answered is which
    /// modules were imported — which is not the same as which were USED.
    ///
    /// Paired positionally, the way Lua's multiple assignment works.
    /// </summary>
    private static string? BoundVariable(SyntaxNode call)
    {
        var child = call;

        foreach (var ancestor in call.Ancestors())
        {
            switch (ancestor)
            {
                // local logger = import 'LrLogger'( 'name' )
                //
                // The import is being CALLED, so the variable holds what it
                // returned -- a logger object -- not the module. Binding it as
                // the module attributes logger:trace() to LrLogger.trace, a
                // module function that does not exist: three of Adobe's own
                // samples reported exactly that before this case existed. The
                // import itself is still recorded; only the binding is refused.
                case FunctionCallExpressionSyntax invoked when ReferenceEquals(invoked.Expression, child):
                case MethodCallExpressionSyntax method when ReferenceEquals(method.Expression, child):
                    return null;

                case EqualsValuesClauseSyntax equals when equals.Parent is LocalVariableDeclarationStatementSyntax local:
                {
                    var index = IndexOf(equals.Values, child);
                    return index >= 0 && index < local.Names.Count ? local.Names[index].ToString().Trim() : null;
                }

                case AssignmentStatementSyntax assign when assign.EqualsValues is { } values:
                {
                    var index = IndexOf(values.Values, child);
                    return index >= 0 && index < assign.Variables.Count ? assign.Variables[index].ToString().Trim() : null;
                }

                // Anything else means the reference is nested in an expression
                // rather than bound to a name: require("x").field, or a call
                // argument. There is no variable to report.
                case StatementSyntax:
                    return null;
            }

            child = ancestor;
        }

        return null;
    }

    private static int IndexOf<T>(SeparatedSyntaxList<T> list, SyntaxNode node) where T : SyntaxNode
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], node)) return i;
        }
        return -1;
    }

    /// <summary>
    /// Function-valued assignments: <c>M.f = function() end</c> and
    /// <c>local f = function() end</c>. Paired positionally, which is how Lua's
    /// multiple assignment works.
    /// </summary>
    private static IEnumerable<(SyntaxNode At, LuaFunction Function)> AssignedFunctions(AssignmentStatementSyntax assign)
    {
        var targets = assign.Variables.ToList();
        var values = assign.EqualsValues?.Values.ToList() ?? [];

        for (var i = 0; i < Math.Min(targets.Count, values.Count); i++)
        {
            if (values[i] is not AnonymousFunctionExpressionSyntax anon) continue;
            yield return (anon, new LuaFunction(
                targets[i].ToString().Trim(), LineOf(anon), EndLineOf(anon), "assignedFunction", IsMethod: false));
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
