namespace RazorGraph.Lua.Checks;

using RazorGraph.Lua.Hosts;

/// <summary>
/// Code written for a later Lua than the host runs.
///
/// The highest-value check available for generated Lua, and the cheapest: the
/// public corpus skews 5.3/5.4 while Lightroom embeds 5.1, so <c>goto</c>,
/// integer division, bitwise operators and <c>string.pack</c> are all things a
/// model reaches for by default and none of them exist in the host. They read
/// perfectly. They just do not run.
///
/// Decidable, which is why it goes first: the parser either accepts the
/// construct at the host's version or it does not, and no catalogue, corpus or
/// convention is involved. A finding here is true by construction.
///
/// A rule of the LANGUAGE rather than of any host, so it lives here and always
/// runs — a host chooses its dialect, not whether the dialect is enforced.
/// </summary>
internal sealed class DialectRule : ILuaRule
{
    public string Id => "lua.dialect";

    public string Title => "Constructs the host's Lua version rejects";

    public IEnumerable<LuaFinding> Check(LuaCheckContext context)
    {
        var dialect = DialectName(context.Host.Dialect);

        // Measure the instrument before reading it. The parser carries
        // process-wide mutable state, and when dialect discrimination is broken
        // it fails SILENTLY and in the permissive direction: nothing is
        // reported, which is indistinguishable from clean code. Saying "this
        // check could not run" is the difference between an absent finding and
        // an absent capability.
        if (!LuaDeclarationExtractor.DialectDiscriminationWorks(context.Host.Dialect))
        {
            yield return new LuaFinding(
                Id, LuaSeverity.Note, context.Declarations.Count > 0 ? context.Declarations[0].File.RelativePath : "(build)", 0,
                $"dialect checking did not run: the parser could not distinguish {dialect} from a later Lua in this process",
                "Loretta keeps process-global parser state; results here would be unsound rather than merely absent");
            yield break;
        }

        foreach (var declaration in context.Declarations)
        {
            if (declaration.DialectRejections.Count == 0) continue;

            // ONE finding per file, at the first rejection. A single unsupported
            // construct cascades: one `goto` produced ten diagnostics, and ten
            // lines describing one mistake is how a report teaches people to skim
            // past it. The count rides along so nothing is hidden.
            var first = declaration.DialectRejections.OrderBy(r => r.Line).First();
            var extra = declaration.DialectRejections.Count > 1
                ? $" (+{declaration.DialectRejections.Count - 1} further diagnostic(s) from the same file)"
                : "";

            yield return new LuaFinding(
                Id,
                LuaSeverity.Error,
                declaration.File.RelativePath,
                first.Line,
                $"valid in {first.AcceptedBy}, rejected by {dialect} which '{context.Host.Name}' runs",
                $"{first.Message}{extra}");
        }
    }

    private static string DialectName(LuaDialect dialect) => dialect switch
    {
        LuaDialect.Lua51 => "Lua 5.1",
        LuaDialect.Lua52 => "Lua 5.2",
        LuaDialect.Lua53 => "Lua 5.3",
        LuaDialect.Lua54 => "Lua 5.4",
        LuaDialect.LuaJit21 => "LuaJIT 2.1",
        LuaDialect.GMod => "Garry's Mod Lua",
        LuaDialect.Luau => "Luau",
        _ => dialect.ToString()
    };
}
