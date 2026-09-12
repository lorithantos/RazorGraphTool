namespace RazorGraph.Extractor.Xaml;

using System.Xml;
using System.Xml.Linq;

/// <summary>One binding expression in a view, and the member name it reaches for.</summary>
internal sealed record XamlBinding(string Member, string Expression, int Line);

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
                yield return new XamlBinding(member, attribute.Value.Trim(), line);
            }
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
