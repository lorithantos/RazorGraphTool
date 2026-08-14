namespace RazorGraph.Lua.Hosts;

using RazorGraph.Core.Graph;
using RazorGraph.Lua.Checks;

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
    /// Why this file is vendor code rather than the author's own, or null when it
    /// is first-party. Only the host knows: for Lightroom it is the sample plugins
    /// shipped inside an SDK folder, for another host it might be a bundled
    /// dependency directory.
    ///
    /// Same contract as the client-asset vendor rule this mirrors — vendor files
    /// are dropped by default, kept behind a flag, marked when kept, and the skip
    /// is always reported. Left in, they drown the author's own code: Lori's
    /// Lightroom tree is 9 of her files against 115 Adobe sample modules, so every
    /// aggregate answer was 93% someone else's.
    /// </summary>
    string? VendorReason(LuaSourceFile file) => null;

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

    /// <summary>
    /// Add host-specific facts about the external API a node actually CALLS, as
    /// opposed to what it imports.
    ///
    /// The distinction is the whole point. An import says a module was named; a
    /// call says a function was used, and only the second bounds what the host
    /// must provide. Lightroom's LrDevelopController has existed since SDK 6.0
    /// while carrying functions added as late as 15.3 — so a file that imports it
    /// and never calls it requires 6.0, and reporting otherwise sends someone
    /// hunting for a compatibility problem they do not have. That mistake was
    /// made against Lori's own plug-ins before calls were extracted.
    ///
    /// Called for each function node with external calls, and once per module
    /// with everything its functions call.
    /// </summary>
    void AnnotateExternalCalls(GraphNode node, IReadOnlyList<ExternalCall> calls) { }

    /// <summary>
    /// Checks this environment can make that no other can.
    ///
    /// The extension point for the second half of the job. Understanding what the
    /// code IS belongs to the extractor; knowing what it SHOULD be is host
    /// knowledge — which SDK functions exist, which version they need, which
    /// manifest keys are required — and it arrives with the host rather than
    /// through a switch in the checker.
    ///
    /// Optional, like the rest of this interface's second half: a host with no
    /// opinion stays a three-method implementation and still gets the language
    /// rules, which run for everyone.
    /// </summary>
    IEnumerable<ILuaRule> Rules => [];

    /// <summary>
    /// The host API function an <c>obj:method()</c> call reaches, when the method
    /// name alone identifies it. Null when it does not, which is the default.
    /// </summary>
    /// <remarks>
    /// Lua has no types, so the receiver of a method call is usually unidentifiable
    /// statically: <c>photoArray[name]:setRawMetadata(...)</c> is an index into a
    /// local table. Every one of Lightroom's metadata accessors is reached that
    /// way, so a resolver keyed only on imported module names sees none of them --
    /// they fall into "unresolved" with locals and dynamic dispatch.
    ///
    /// A host that knows its own surface can do better than nothing WITHOUT
    /// guessing, by answering only for names that admit one answer. It must refuse
    /// ambiguity rather than pick: a wrong attribution here becomes a wrong finding
    /// about code that is correct.
    /// </remarks>
    (string Module, string Function)? OwnerOfInstanceMethod(string methodName) => null;
}

/// <summary>
/// A call into an API that lives outside the graph — the host application's own
/// surface, or a library this unit does not contain.
/// </summary>
/// <param name="Module">The external module, as the source named it.</param>
/// <param name="Function">The function called on it, unqualified.</param>
/// <param name="Arguments">
/// The arguments as written, positionally: a literal string's value, or null where
/// the argument is an expression this cannot evaluate. A host that knows a
/// function's accepted values can check the ones it can see and must stay silent
/// about the rest — the nulls are what keep it honest about which is which.
///
/// Defaulted so a host or test that only cares about who-calls-what can build one
/// without inventing an argument list.
/// </param>
public sealed record ExternalCall(
    string Module,
    string Function,
    int Line,
    IReadOnlyList<string?>? Arguments = null);
