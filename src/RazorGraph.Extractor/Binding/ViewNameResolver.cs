namespace RazorGraph.Extractor.Binding;

/// <summary>
/// Resolves a view or partial NAME to the templates that could serve it, in the
/// order ASP.NET would search.
///
/// This replaces a substring match over the whole relative path that took the
/// first hit. That rule looked correct on a ten-template project and is wrong on
/// a real one: measured against OrchardCore's 1,576 templates, the name "Menu"
/// matched 71 paths and first-wins chose a _ViewImports.cshtml, "User" matched
/// 195 and chose an unrelated audit-trail template. Worse, the input list has no
/// guaranteed order, so which wrong template it picked could differ between runs
/// of identical source.
///
/// Two deliberate departures from the old behaviour:
///
/// 1. Matching is on PATH SEGMENTS. "Menu" may match a file named Menu.cshtml,
///    never a directory called AdminMenu or a file called MenuWidget.
/// 2. It returns EVERY candidate in precedence order rather than one winner.
///    A name can legitimately be served by several templates — an area override,
///    a theme — and picking one silently asserts a resolution that depends on
///    runtime configuration. Callers that need a single edge take the first;
///    callers that want to report ambiguity now can.
/// </summary>
public static class ViewNameResolver
{
    /// <summary>Shared folders, in the order ASP.NET consults them.</summary>
    private static readonly string[] SharedFolders = ["Views/Shared", "Pages/Shared", "Views"];

    /// <summary>
    /// Candidate templates for <paramref name="name"/>, most specific first.
    /// Empty means nothing in the compiled set can serve it — which is a finding,
    /// not a lookup failure to paper over.
    /// </summary>
    /// <param name="name">The name as authored: "_Card", "Shared/_Card", "~/Views/Shared/_Card.cshtml".</param>
    /// <param name="fromRelativePath">Relative path of the file doing the referencing, for sibling and ancestor lookup.</param>
    /// <param name="candidates">Relative paths of every template available.</param>
    public static IReadOnlyList<string> Resolve(string name, string? fromRelativePath, IEnumerable<string> candidates)
    {
        var all = candidates.Select(Normalize).ToList();
        var wanted = Normalize(name);
        var ordered = new List<string>();

        // 1. An explicit path is not a name to search for: it either exists or it
        //    does not, and falling back to a search would invent a match the
        //    author did not ask for.
        if (IsExplicitPath(wanted))
        {
            var target = wanted.TrimStart('~', '/');
            AddIfPresent(ordered, all, p => p.Equals(target, StringComparison.OrdinalIgnoreCase)
                                            || p.EndsWith('/' + target, StringComparison.OrdinalIgnoreCase));
            return ordered;
        }

        var stem = StripExtension(wanted);

        // 2. Beside the referencing file, then up its directory chain — a partial
        //    next to its only caller is the commonest layout of all.
        foreach (var dir in AncestorDirectories(Normalize(fromRelativePath ?? string.Empty)))
        {
            AddIfPresent(ordered, all, p => PathIs(p, dir, stem));
            foreach (var shared in SharedFolders)
            {
                AddIfPresent(ordered, all, p => PathIs(p, Combine(dir, shared), stem));
            }
        }

        // 3. The conventional shared roots, wherever they sit in the tree. A
        //    solution with many projects has many Views/Shared, so this can
        //    legitimately yield several candidates.
        foreach (var shared in SharedFolders)
        {
            AddIfPresent(ordered, all, p => p.EndsWith($"/{shared}/{stem}.cshtml", StringComparison.OrdinalIgnoreCase)
                                            || p.Equals($"{shared}/{stem}.cshtml", StringComparison.OrdinalIgnoreCase));
        }

        // 4. Anywhere, by exact file name. Last because it is the weakest claim,
        //    and included so a project with an unconventional layout still
        //    resolves rather than reporting a phantom missing template.
        AddIfPresent(ordered, all, p => FileName(p).Equals($"{stem}.cshtml", StringComparison.OrdinalIgnoreCase));

        return ordered;
    }

    /// <summary>The single best candidate, or null. For callers that emit one edge.</summary>
    public static string? ResolveOne(string name, string? fromRelativePath, IEnumerable<string> candidates) =>
        Resolve(name, fromRelativePath, candidates).FirstOrDefault();

    /// <summary>
    /// Candidates for a view rendered by a controller action, most specific first.
    ///
    /// `Views/{Controller}/{name}.cshtml` is tried before anything else, and that
    /// ordering is load-bearing rather than a nicety: OrchardCore has 34 templates
    /// named Edit, and the controller folder is the only thing separating
    /// MenuController.Edit from NodeController.Edit. Without it they collapse to
    /// one arbitrary winner.
    /// </summary>
    /// <param name="controller">Controller name without its suffix — "Menu", not "MenuController".</param>
    public static IReadOnlyList<string> ResolveForController(
        string name, string controller, IEnumerable<string> candidates)
    {
        var all = candidates.Select(Normalize).ToList();
        var wanted = Normalize(name);

        // An explicit path ignores the controller entirely, as it does everywhere else.
        if (IsExplicitPath(wanted)) return Resolve(name, null, all);

        var stem = StripExtension(wanted);
        var ordered = new List<string>();

        // Views/{Controller}/{name}.cshtml, wherever that Views folder sits.
        AddIfPresent(ordered, all, p => p.EndsWith($"/Views/{controller}/{stem}.cshtml", StringComparison.OrdinalIgnoreCase)
                                        || p.Equals($"Views/{controller}/{stem}.cshtml", StringComparison.OrdinalIgnoreCase));

        // Then the ordinary shared search, which the base overload already knows.
        foreach (var shared in Resolve(name, $"Views/{controller}/_.cshtml", all))
        {
            if (!ordered.Contains(shared, StringComparer.OrdinalIgnoreCase)) ordered.Add(shared);
        }

        return ordered;
    }

    private static bool IsExplicitPath(string name) =>
        name.StartsWith('~') || name.StartsWith('/') || name.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is exactly dir/stem.cshtml.</summary>
    private static bool PathIs(string path, string dir, string stem)
    {
        var expected = Combine(dir, $"{stem}.cshtml");
        return path.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The referencing file's directory and each ancestor, nearest first.</summary>
    private static IEnumerable<string> AncestorDirectories(string relativePath)
    {
        var dir = DirectoryOf(relativePath);
        while (true)
        {
            yield return dir;
            if (dir.Length == 0) yield break;
            dir = DirectoryOf(dir);
        }
    }

    private static void AddIfPresent(List<string> ordered, List<string> all, Func<string, bool> match)
    {
        // Ordinal ordering makes the result stable: the candidate list arrives in
        // whatever order the extractor produced, which is not deterministic.
        foreach (var p in all.Where(match).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (!ordered.Contains(p, StringComparer.OrdinalIgnoreCase)) ordered.Add(p);
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string DirectoryOf(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? string.Empty : path[..i];
    }

    private static string FileName(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? path : path[(i + 1)..];
    }

    private static string StripExtension(string name) =>
        name.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) ? name[..^7] : name;

    private static string Combine(string dir, string rest) =>
        dir.Length == 0 ? rest : $"{dir}/{rest}";
}
