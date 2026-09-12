namespace RazorGraph.Extractor;

using RazorGraph.Core.Graph;
using RazorGraph.Extractor.Xaml;

/// <summary>
/// The XAML view layer: a node per view file, and a DataBinds edge from a view to
/// every member one of its bindings could name.
/// </summary>
/// <remarks>
/// Runs after the Roslyn layer, like every other per-project file layer, so the
/// members it points at are already nodes. It reads no workspace of its own — the
/// view files are text and the far ends are looked up in the graph.
/// </remarks>
internal sealed class XamlLayerEmitter(CodeGraph graph)
{
    /// <summary>
    /// Member name to the ids it could mean, built once across the whole graph.
    /// Null until the first view is seen, so a solution with no XAML pays nothing.
    /// </summary>
    private Dictionary<string, List<string>>? _candidatesByName;

    /// <summary>Declaring type full name plus member name, for code-behind members.</summary>
    private Dictionary<(string Type, string Member), List<string>>? _membersByType;

    internal void AddXamlLayer(string projectDir, string? idScope)
    {
        var views = XamlExtractor.ExtractViews(projectDir, idScope);
        if (views.Count == 0) return;

        foreach (var view in views) AddViewNode(view, idScope);
        foreach (var view in views) AddBindingEdges(view);
    }

    private void AddViewNode(XamlViewInfo view, string? idScope)
    {
        if (graph.HasNode(view.Id)) return;

        var node = new GraphNode
        {
            Id = view.Id,
            Type = NodeType.XamlView,
            Name = view.Name,
            FilePath = view.FilePath,
            LineStart = 1,
            LineEnd = view.LineCount
        };

        node.SetProperty("relativePath", view.RelativePath);
        node.SetProperty("lineCount", view.LineCount);
        if (idScope != null) node.SetProperty("project", idScope);

        // Absent means a file with no code-behind — a resource dictionary or a
        // theme — which is a fact about the view, not a missing value.
        if (view.CodeBehindType != null) node.SetProperty("codeBehind", view.CodeBehindType);

        // Stamped even at zero. A view with no bindings and a view this layer
        // failed to read are both silent otherwise, and they are not the same.
        node.SetProperty("bindingCount", view.Bindings.Count);

        // Which is why the unreadable one says so rather than going missing. A
        // file the extractor never parsed reports zero bindings exactly like a
        // file that has none, and the graph then answers "nothing binds this
        // member" on the strength of a file it could not read.
        if (view.ParseError != null) node.SetProperty("parseError", view.ParseError);

        graph.AddNode(node);
    }

    /// <summary>
    /// One edge per (view, member) pair a binding could name.
    /// </summary>
    /// <remarks>
    /// Grouped by pair rather than per occurrence, like call edges: a member bound
    /// in four places is one relationship, and the count keeps the other three
    /// from being silently dropped. The fan-out is carried separately, because
    /// "this view binds Amount in four places" and "Amount could mean four
    /// different members" are different facts and a reader acts on them
    /// differently.
    /// </remarks>
    private void AddBindingEdges(XamlViewInfo view)
    {
        var unresolved = new List<string>();
        var missingOnDataType = new List<string>();

        foreach (var group in view.Bindings.GroupBy(b => (b.Member, b.ContextType)))
        {
            var (member, contextType) = group.Key;
            var targets = Candidates(member, view.CodeBehindType, contextType);

            // A binding that names NOTHING is the finding, not the leftover. It is
            // the failure mode XAML has no compiler for: a renamed or mistyped
            // property leaves a binding that resolves to nothing, and WPF reports
            // it by writing a line to the debug output and rendering an empty
            // control. Dropping it here would discard the most valuable thing this
            // layer can see.
            //
            // Recorded on the view rather than raised as a defect, because this
            // layer cannot prove a typo: a DataContext that is not a view-model
            // type is outside the candidate bound and produces the same zero. So
            // the graph reports the name that resolved to nothing and leaves the
            // adjudicating to a reader who can open the file.
            // TWO DIFFERENT FACTS, and collapsing them would waste the stronger
            // one. A binding under a template that DECLARES its DataType and names
            // a member that type does not have is a defect: the author said what
            // the context is, and the member is not on it. A binding with no
            // declared context that matched nothing is a maybe — the DataContext
            // may simply be a type outside the candidate bound.
            if (targets.Count == 0)
            {
                if (contextType != null) missingOnDataType.Add($"{contextType}.{member}");
                else unresolved.Add(member);
                continue;
            }

            var line = group.Min(b => b.Line);
            var sites = group.Count();

            foreach (var targetId in targets)
            {
                var edge = new GraphEdge
                {
                    FromId = view.Id,
                    ToId = targetId,
                    Type = EdgeType.DataBinds
                };

                edge.Properties["path"] = group.First().Expression;
                edge.Properties["line"] = line;
                edge.Properties["candidates"] = targets.Count;
                if (sites > 1) edge.Properties["sites"] = sites;
                graph.AddEdge(edge);
            }
        }

        if (graph.GetNode(view.Id) is not { } node) return;

        // Ordered, so a rebuilt graph equals a replayed one exactly.
        if (unresolved.Count > 0)
            node.SetProperty(
                "unresolvedBindings",
                unresolved.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList());

        if (missingOnDataType.Count > 0)
            node.SetProperty(
                "missingOnDataType",
                missingOnDataType.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Every member a binding name could mean: members of view-model types, plus
    /// members of this view's own code-behind class.
    /// </summary>
    /// <remarks>
    /// Bounded by a RELATION rather than by the name alone, which is the whole
    /// difference between a conservative answer and a useless one. Matching a bare
    /// name across the solution would make <c>{Binding Amount}</c> reach every
    /// Amount in the codebase; restricting to the types that can actually be a
    /// DataContext keeps the fan-out to candidates a reader can adjudicate.
    /// <para>
    /// It is still a candidate set and not an answer, and it is the same shape as
    /// interface dispatch: which one the runtime picks depends on the visual tree
    /// at the moment the binding evaluates, so the graph names them all and
    /// declines to choose.
    /// </para>
    /// </remarks>
    private List<string> Candidates(string member, string? codeBehindType, string? contextType)
    {
        BuildIndexes();

        // A declared DataType REPLACES the candidate set rather than widening it.
        // It is the author's own statement of what the context is, so falling back
        // to the name heuristic when it misses would convert a defect into a wrong
        // edge pointing at some unrelated type that happens to share the name —
        // and the wrong edge is the failure worth avoiding, because it reads as
        // confirmation.
        if (contextType != null)
            return _membersByType!.TryGetValue((contextType, member), out var onContext)
                ? onContext.Distinct(StringComparer.Ordinal).ToList()
                : [];

        var found = new List<string>();
        if (_candidatesByName!.TryGetValue(member, out var byName)) found.AddRange(byName);

        if (codeBehindType != null
            && _membersByType!.TryGetValue((codeBehindType, member), out var onCodeBehind))
            found.AddRange(onCodeBehind);

        return found.Distinct(StringComparer.Ordinal).ToList();
    }

    private void BuildIndexes()
    {
        if (_candidatesByName != null) return;

        var viewModelTypes = graph.Nodes
            .Where(n => n.Type == NodeType.ViewModel)
            .Select(n => n.GetProperty<string>("fullName"))
            .Where(n => n != null)
            .ToHashSet(StringComparer.Ordinal)!;

        var members = graph.Nodes
            .Where(n => n.Type is NodeType.Property or NodeType.Field or NodeType.Method)
            .Select(n => (Node: n, DeclaringType: n.GetProperty<string>("declaringType")))
            .Where(m => m.DeclaringType != null)
            .ToList();

        _candidatesByName = members
            .Where(m => viewModelTypes.Contains(m.DeclaringType!))
            .GroupBy(m => m.Node.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Node.Id).ToList(), StringComparer.Ordinal);

        _membersByType = members
            .GroupBy(m => (m.DeclaringType!, m.Node.Name))
            .ToDictionary(g => g.Key, g => g.Select(m => m.Node.Id).ToList());
    }
}
