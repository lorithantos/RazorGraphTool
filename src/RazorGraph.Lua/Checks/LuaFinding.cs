namespace RazorGraph.Lua.Checks;

/// <summary>How much a finding is claiming.</summary>
public enum LuaSeverity
{
    /// <summary>
    /// True by construction — the code cannot work as written. A construct the
    /// host's Lua rejects, a version floor above what the plug-in declares.
    /// </summary>
    Error,

    /// <summary>
    /// Probably wrong, and decidable enough to say so, but resting on a
    /// catalogue or a corpus that may be incomplete rather than on the language.
    /// </summary>
    Warning,

    /// <summary>
    /// Worth knowing, claims nothing. Where a rule's own coverage is the reason
    /// it cannot say more, it says THAT instead of guessing.
    /// </summary>
    Note
}

/// <summary>
/// One thing a rule has to say about the code.
///
/// Shaped so every surface can render it without knowing which rule produced it
/// — the same reason unbound shapes are a list of strings rather than a bag of
/// rule-specific structures. A new rule reaches the CLI, the MCP envelope and a
/// saved graph without any of them changing.
/// </summary>
/// <param name="RuleId">Stable id, e.g. <c>lua.dialect</c>. Prefixed by whoever owns the rule.</param>
/// <param name="Evidence">
/// What the rule actually observed, kept separate from the message so a reader
/// can check the claim rather than believe it.
/// </param>
public sealed record LuaFinding(
    string RuleId,
    LuaSeverity Severity,
    string File,
    int Line,
    string Message,
    string? Evidence = null)
{
    /// <summary>One line, for a report that has to fit on a terminal.</summary>
    public override string ToString()
    {
        var where = Line > 0 ? $"{File}:{Line}" : File;
        var evidence = Evidence is { Length: > 0 } ? $" — {Evidence}" : "";
        return $"{Severity.ToString().ToLowerInvariant()}: {where}: {Message} [{RuleId}]{evidence}";
    }
}
