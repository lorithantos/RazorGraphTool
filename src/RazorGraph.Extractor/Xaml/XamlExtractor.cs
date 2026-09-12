namespace RazorGraph.Extractor.Xaml;

using System.Xml;
using System.Xml.Linq;

/// <summary>
/// One binding expression in a view: the member name it reaches for, and the type
/// its DataContext is DECLARED to be, when the markup says so.
/// </summary>
/// <remarks>
/// ContextType is the author's own statement, not a guess. A DataTemplate that
/// carries DataType names the type it renders, so a binding inside it resolves
/// against that type exactly — which turns a candidate set into an answer, and
/// turns a name that type does not have into a defect rather than a maybe.
/// Null when no enclosing template declares one, or when the binding explicitly
/// leaves the template context.
/// </remarks>
internal sealed record XamlBinding(string Member, string Expression, int Line, string? ContextType);

/// <summary>
/// A XAML file as the graph sees it: identity, where it is, the code-behind type
/// it declares, and every binding it contains.
/// </summary>
internal sealed record XamlViewInfo(
    string Id,
    string Name,
    string FilePath,
    string RelativePath,
    string? CodeBehindType,
    int LineCount,
    IReadOnlyList<XamlBinding> Bindings);

/// <summary>
/// Reads the XAML files of a project. Text in, facts out — no correlation to C#
/// happens here, because matching a name to a declaration needs the solution-wide
/// node set and this runs per project.
/// </summary>
internal static class XamlExtractor
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// View ids are project-relative and scoped by project name on a solution
    /// build, for the reason asset and page ids are: two projects may each hold
    /// Views/MainWindow.xaml, and unscoped they would collide into one node.
    /// </summary>
    internal static string ViewId(string? idScope, string relativePath) =>
        idScope is null ? $"xaml:{relativePath}" : $"xaml:{idScope}/{relativePath}";

    /// <summary>
    /// Every XAML file under a project directory.
    /// </summary>
    /// <remarks>
    /// Directory globbing rather than the .csproj item list, matching how .cshtml
    /// and client assets are already found. The obj/bin filter is not optional
    /// here: the WPF SDK copies XAML into obj\ on the way to BAML, so an
    /// unfiltered glob reports every view twice and attributes the copy to a path
    /// no reader can open.
    /// </remarks>
    internal static List<XamlViewInfo> ExtractViews(string projectDir, string? idScope = null)
    {
        var views = new List<XamlViewInfo>();
        if (!Directory.Exists(projectDir)) return views;

        var sep = Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(projectDir, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase)) continue;
            if (file.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase)) continue;

            if (ReadView(file, projectDir, idScope) is { } view) views.Add(view);
        }

        return views;
    }

    /// <summary>
    /// One file, or null when it cannot be read or parsed.
    /// </summary>
    /// <remarks>
    /// A malformed view is skipped rather than thrown, deliberately: a build spans
    /// every file in a solution, and one unparseable view taking the whole build
    /// down would cost every other answer in it. The cost of skipping is that its
    /// bindings are absent, which is exactly the state before this layer existed.
    /// </remarks>
    private static XamlViewInfo? ReadView(string file, string projectDir, string? idScope)
    {
        XDocument doc;
        string text;
        try
        {
            text = File.ReadAllText(file);
            doc = XDocument.Parse(text, LoadOptions.SetLineInfo);
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var relative = Path.GetRelativePath(projectDir, file).Replace('\\', '/');

        return new XamlViewInfo(
            Id: ViewId(idScope, relative),
            Name: Path.GetFileName(file),
            FilePath: file,
            RelativePath: relative,
            CodeBehindType: CodeBehindType(doc),
            LineCount: text.Count(c => c == '\n') + 1,
            Bindings: Bindings(doc).ToList());
    }

    /// <summary>
    /// The type declared by x:Class, or null for a file with no code-behind — a
    /// resource dictionary, a theme, a template set.
    /// </summary>
    private static string? CodeBehindType(XDocument doc)
    {
        var declared = doc.Root?.Attribute(XamlNamespace + "Class")?.Value;
        return string.IsNullOrWhiteSpace(declared) ? null : declared;
    }

    /// <summary>Every attribute value in the document that is a binding naming a member.</summary>
    private static IEnumerable<XamlBinding> Bindings(XDocument doc)
    {
        foreach (var element in doc.Descendants())
            foreach (var attribute in element.Attributes())
            {
                if (BoundMember(attribute.Value) is not { } member) continue;

                var line = attribute is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;
                yield return new XamlBinding(
                    member,
                    attribute.Value.Trim(),
                    line,
                    LeavesTemplateContext(attribute.Value) ? null : DeclaredContextType(attribute));
            }
    }

    /// <summary>
    /// Whether a binding deliberately resolves somewhere other than the DataContext
    /// it sits in.
    /// </summary>
    /// <remarks>
    /// RelativeSource walks up the visual tree, ElementName jumps to a named
    /// element, and an explicit Source leaves the tree altogether — so for these
    /// the enclosing template's DataType is the wrong answer, not a better one.
    /// Treating them as scoped resolves a binding against a type it never touches
    /// and reports candidates:1, which is a wrong answer wearing the appearance of
    /// an exact one.
    /// <para>
    /// Two clauses and not three, which a mutation established rather than taste:
    /// a RelativeSource test was there first and never fired, because the only
    /// spelling that reaches this parser is the attribute form
    /// <c>RelativeSource={...}</c>, and that already contains <c>Source=</c>.
    /// A clause that cannot fire is a claim about what is load-bearing that
    /// happens to be false.
    /// </para>
    /// <para>
    /// The <c>=</c> in <c>Source=</c> is doing work: it separates the SETTING from
    /// a path that merely happens to be called Source, so <c>{Binding Source}</c>
    /// and <c>{Binding ImageSource}</c> stay ordinary bindings.
    /// </para>
    /// </remarks>
    private static bool LeavesTemplateContext(string expression) =>
        expression.Contains("Source=", StringComparison.Ordinal)
        || expression.Contains("ElementName", StringComparison.Ordinal);

    /// <summary>
    /// The full type name declared by the nearest enclosing template, or null.
    /// </summary>
    private static string? DeclaredContextType(XAttribute attribute)
    {
        for (var element = attribute.Parent; element != null; element = element.Parent)
        {
            // HierarchicalDataTemplate as well as DataTemplate, and both may be
            // written in any namespace prefix the document chose.
            if (!element.Name.LocalName.EndsWith("DataTemplate", StringComparison.Ordinal)) continue;

            // STOP at the nearest template, declared type or not. A template
            // establishes a NEW DataContext — the item it renders — so an inner
            // one with no DataType means the context is UNKNOWN, not that the
            // enclosing one's applies. Walking past it attributed every row
            // binding to the page view model: measured on one app, that produced
            // 35 confident "member does not exist" claims, all of them wrong, all
            // of them row members reported against the page they sit on.
            return element.Attribute("DataType")?.Value is { } declared
                ? ResolveTypeName(declared, element)
                : null;
        }

        return null;
    }

    /// <summary>
    /// A XAML type reference to a CLR full name: <c>{x:Type vm:Thing}</c> or the
    /// bare <c>vm:Thing</c>, with the prefix resolved through the xmlns
    /// declarations in scope at that element.
    /// </summary>
    /// <remarks>
    /// Null unless the prefix resolves to a clr-namespace declaration. A type in
    /// the default presentation namespace is a framework type with no node to
    /// point at, and guessing a namespace for an unprefixed name would mint a full
    /// name nothing declares.
    /// </remarks>
    private static string? ResolveTypeName(string declared, XElement scope)
    {
        var text = declared.Trim();

        if (text.StartsWith('{'))
        {
            var inner = text.Trim('{', '}').Trim();
            var space = inner.IndexOf(' ');
            if (space < 0) return null;

            text = inner[(space + 1)..].Trim();
        }

        var colon = text.IndexOf(':');
        if (colon <= 0) return null;

        var resolved = scope.GetNamespaceOfPrefix(text[..colon])?.NamespaceName;
        if (resolved is null || !resolved.StartsWith("clr-namespace:", StringComparison.Ordinal)) return null;

        var clrNamespace = resolved["clr-namespace:".Length..];
        var assembly = clrNamespace.IndexOf(';');
        if (assembly >= 0) clrNamespace = clrNamespace[..assembly];

        var local = text[(colon + 1)..].Trim();
        return clrNamespace.Length == 0 ? local : $"{clrNamespace}.{local}";
    }

    /// <summary>
    /// The member name a binding expression reaches for on its DataContext, or
    /// null when the expression names none.
    /// </summary>
    /// <remarks>
    /// The FIRST segment of the path, because that is the one that lives on the
    /// DataContext. In <c>{Binding Account.Name}</c> the view model supplies
    /// Account, and Name belongs to whatever type that is; resolving further would
    /// mean walking types this parser has no model of, so it is left undone rather
    /// than guessed. A later segment matched against the wrong type is a wrong
    /// edge, and a wrong edge is worse than a missing one.
    /// <para>
    /// A leading <c>DataContext.</c> is stepped over. It appears when a binding
    /// walks up the visual tree by RelativeSource, where that framework property
    /// is the route taken and the segment after it is the member actually wanted.
    /// </para>
    /// <para>
    /// Null for <c>{Binding}</c> with no path, which binds the context object
    /// itself and names no member.
    /// </para>
    /// </remarks>
    internal static string? BoundMember(string? value)
    {
        if (value is null) return null;

        var text = value.Trim();
        if (!text.StartsWith("{Binding", StringComparison.Ordinal)) return null;

        var body = text[8..].TrimEnd('}').Trim();
        if (body.Length == 0) return null;

        if (PathArgument(body) is not { } path) return null;

        foreach (var segment in path.Split('.'))
        {
            if (segment.Equals("DataContext", StringComparison.Ordinal)) continue;
            return IsIdentifier(segment) ? segment : null;
        }

        return null;
    }

    /// <summary>
    /// The path out of a binding argument list: the first positional argument, or
    /// the one written Path=. Splitting respects nesting, because a RelativeSource
    /// argument carries braces and commas of its own.
    /// </summary>
    private static string? PathArgument(string body)
    {
        foreach (var argument in SplitTopLevel(body))
        {
            var trimmed = argument.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith("Path=", StringComparison.Ordinal)) return trimmed[5..].Trim();

            // A positional argument is the path; anything else named is a setting.
            if (!trimmed.Contains('=', StringComparison.Ordinal)) return trimmed;
        }

        return null;
    }

    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        var start = 0;

        for (var i = 0; i < body.Length; i++)
        {
            switch (body[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return body[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return body[start..];
    }

    /// <summary>
    /// A plain C# identifier. Indexers and attached properties are excluded by it:
    /// <c>Items[0]</c> and <c>(Grid.Row)</c> name no member this layer can
    /// resolve, and admitting them would hang edges on names nothing declares.
    /// </summary>
    private static bool IsIdentifier(string segment) =>
        segment.Length > 0
        && (char.IsLetter(segment[0]) || segment[0] == '_')
        && segment.All(c => char.IsLetterOrDigit(c) || c == '_');
}
