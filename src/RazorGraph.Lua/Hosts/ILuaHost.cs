namespace RazorGraph.Lua.Hosts;

using RazorGraph.Core.Graph;

/// <summary>
/// Which Lua dialect a host runs. Our own enum rather than the parser's, so the
/// choice of parser stays replaceable: this is a property of the host, not of
/// whatever library happens to be parsing today.
/// </summary>
public enum LuaDialect
{
    /// <summary>Permissive superset — accepts every dialect's syntax.</summary>
    Any,
    Lua51,
    Lua52,
    Lua53,
    Lua54,
    LuaJit21,
    GMod,
    Luau
}

/// <summary>
/// Whether a host has a statically determinable module graph at all.
/// </summary>
public enum ModuleReferenceSupport
{
    /// <summary>Module references appear in source and can be resolved.</summary>
    Static,

    /// <summary>
    /// The host has no module-reference mechanism. A WoW addon is the case: the
    /// .toc lists load ORDER, there is no require, and coupling happens through a
    /// shared global namespace. A graph of such a host correctly has no module
    /// edges — and reads exactly like a broken extractor, so it must say so.
    /// </summary>
    None
}

/// <summary>
/// What resolving one module reference produced. Three outcomes, not two: the
/// difference between "someone else's module" and "a module I could not find" is
/// the difference between a healthy Lightroom plugin and one reporting 191
/// failures.
/// </summary>
public abstract record ModuleResolution
{
    private ModuleResolution() { }

    /// <summary>Resolved to a file in this unit — emit an edge.</summary>
    public sealed record InGraph(string FilePath) : ModuleResolution;

    /// <summary>
    /// Resolved, but outside the graph: an SDK or host API module such as
    /// LrDialogs, vim.api, ngx. Recorded, not edged, and NOT a failure.
    /// </summary>
    public sealed record External(string Name) : ModuleResolution;

    /// <summary>
    /// Could not be resolved, with the reason preserved. Kong's dynamic
    /// require(expr) and Roblox's instance-tree require(script.Parent.X) are both
    /// unresolvable and have nothing else in common; one bucket would hide that.
    /// </summary>
    public sealed record Unresolved(string Reason) : ModuleResolution;
}

/// <summary>One Lua source file belonging to a unit.</summary>
/// <param name="LoadOrder">
/// Position where the host defines one (a WoW .toc, a Factorio stage), otherwise
/// null. Present on the file rather than implied by enumeration order, because a
/// host that has load order needs it to survive into the graph.
/// </param>
public sealed record LuaSourceFile(string FullPath, string RelativePath, int? LoadOrder = null);

/// <summary>
/// A Lua host environment: what the unit of deployment is, how module references
/// are written, and how they resolve.
///
/// This is the primary abstraction rather than a dialect flag, because Lua is
/// predominantly an EMBEDDED language — the standalone library is the minority
/// case. Shaping this around `require` would quietly forbid `import` (Lightroom),
/// `include` (Garry's Mod), .toc load order (WoW) and instance paths (Roblox);
/// the interface was validated against all of those on paper before it was
/// written. See note.razorgraph-lua-scoping.
/// </summary>
public interface ILuaHost
{
    /// <summary>Reported in the build summary — a misdetected host produces a confidently empty graph.</summary>
    string Name { get; }

    LuaDialect Dialect { get; }

    ModuleReferenceSupport ReferenceSupport { get; }

    /// <summary>
    /// Function names that introduce a module reference: require, import,
    /// include, AddCSLuaFile. Empty when <see cref="ReferenceSupport"/> is None.
    ///
    /// Naming the FUNCTIONS rather than parsing the references keeps every
    /// syntactic form — parenthesised, paren-less, long-string — in one place in
    /// the extractor, and leaves the host deciding only what counts as a
    /// reference.
    /// </summary>
    IReadOnlySet<string> ReferenceFunctions { get; }

    /// <summary>Source files in this unit, in load order where the host defines one.</summary>
    IEnumerable<LuaSourceFile> Discover(string rootPath);

    /// <summary>The module name a file is known by, used for node ids and reference targets.</summary>
    string ModuleNameFor(LuaSourceFile file);

    /// <summary>
    /// Resolve one reference. <paramref name="reference"/> is null when the
    /// argument was not a literal string — a dynamic require — which the host
    /// still classifies, because only it knows whether that is merely
    /// unresolvable or meaningful in its environment.
    /// </summary>
    ModuleResolution Resolve(string? reference, LuaSourceFile from);

    /// <summary>
    /// Add host-specific facts to a module node once its references are known.
    /// Optional: the default does nothing, so a host with nothing to add stays a
    /// three-method implementation.
    ///
    /// This is where an environment says what only it can — which host-API
    /// modules a file uses, what minimum host version that implies, which
    /// references it does not recognise. The builder cannot know any of it
    /// without becoming host-specific itself.
    /// </summary>
    /// <param name="externalNames">
    /// Reference targets this host resolved as <see cref="ModuleResolution.External"/>.
    /// </param>
    void Annotate(GraphNode module, IReadOnlyList<string> externalNames) { }
}
