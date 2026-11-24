namespace RazorGraph.Core.Graph;

/// <summary>
/// Resolving a node id that arrives wearing escape sequences. One copy, used by
/// every surface that takes an id from a caller: the MCP tools and the CLI's
/// query command. It lived in RazorGraph.Mcp until the CLI needed it too, and a
/// second copy would have been a second set of rules to drift.
/// </summary>
public static class NodeIdEscapes
{
    /// <summary>
    /// Every escape sequence a node id can arrive wearing, paired with the
    /// character it stands for. Named, decimal and hex spellings, because an id
    /// is escaped by whatever layer it passed through and they do not agree.
    ///
    /// The JSON spellings (backslash-u-003C, six literal characters, for the
    /// less-than sign) are the form RazorGraph itself produced until
    /// ToolResponses moved to the relaxed encoder: System.Text.Json's default
    /// encoder writes &lt; &gt; &amp; that way, and a client that shows a tool
    /// result as text rather than parsing it hands the escape to the agent
    /// verbatim. Ids copied from older output, and from clients that re-escape,
    /// still arrive wearing them, so the decode stays. Only those
    /// three: the encoder escapes quotes and plus as well, but no id has needed
    /// them, and a decoder that grows past what was observed starts inventing.
    ///
    /// The ampersand is LAST and the order is load-bearing: decoding it first
    /// turns <c>&amp;amp;lt;</c> into <c>&amp;lt;</c> and then into <c>&lt;</c>,
    /// inventing a bracket the caller never wrote. Decoded last, the same input
    /// lands on <c>&amp;lt;</c> — one pass, left to right, which is what an
    /// escaper's output means.
    /// </summary>
    private static readonly (char Character, string[] Spellings)[] EscapeSpellings =
    {
        ('<', ["&lt;", "&#60;", "&#x3C;", "\\u003C"]),
        ('>', ["&gt;", "&#62;", "&#x3E;", "\\u003E"]),
        ('"', ["&quot;", "&#34;", "&#x22;"]),
        ('\'', ["&apos;", "&#39;", "&#x27;"]),
        ('&', ["&amp;", "&#38;", "&#x26;", "\\u0026"]),
    };

    /// <summary>
    /// Undo HTML escaping of a node id, and nothing else.
    ///
    /// Deliberately not <c>WebUtility.HtmlDecode</c>: that table turns
    /// <c>&amp;nbsp;</c> into U+00A0 and tolerates entities missing their
    /// semicolon, so it can rewrite an id in ways no escaper could have produced
    /// from a legal signature. Only the five characters an escaper actually
    /// touches are decoded here.
    ///
    /// Whitespace is never touched — not trimmed, not collapsed. An id's spacing
    /// is significant: <c>Func&lt;TSource, TItem&gt;</c> carries a space after
    /// its comma while the enclosing parameter list carries none, so a tidy-up
    /// pass would turn a resolvable id into one that matches nothing.
    /// </summary>
    public static string Decode(string id)
    {
        var decoded = id;
        foreach (var (character, spellings) in EscapeSpellings)
            foreach (var spelling in spellings)
                decoded = decoded.Replace(spelling, character.ToString(), StringComparison.OrdinalIgnoreCase);
        return decoded;
    }

    /// <summary>
    /// Resolve a node id against the graph, accepting the HTML-escaped spelling
    /// an id comes back wearing once it has crossed a markdown or HTML layer.
    ///
    /// A generic signature is the whole problem. The id
    /// <c>m:…Paginate(IReadOnlyList&lt;TSource&gt;,int,int,Func&lt;TSource, TItem&gt;)</c>
    /// reaches a caller with <c>&amp;lt;</c> and <c>&amp;gt;</c> where its angle
    /// brackets were; an exact lookup missed it and reported a bare "Node not
    /// found", which reads as "no such thing" rather than "you are one decode
    /// away". That near miss dressed as an absence cost four wasted calls before
    /// anyone suspected the brackets.
    /// </summary>
    /// <remarks>
    /// The id is tried AS GIVEN first and the decoded form is only a fallback,
    /// never the reverse: an id that genuinely contains the characters
    /// <c>&amp;amp;</c> exists, and it must keep beating an escaped id that
    /// merely decodes onto something else.
    /// </remarks>
    public static bool TryResolve(CodeGraph graph, string id, out string resolved)
    {
        if (graph.HasNode(id)) { resolved = id; return true; }

        var decoded = Decode(id);
        if (decoded != id && graph.HasNode(decoded)) { resolved = decoded; return true; }

        resolved = id;
        return false;
    }
}
