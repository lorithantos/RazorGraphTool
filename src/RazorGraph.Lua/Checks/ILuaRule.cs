namespace RazorGraph.Lua.Checks;

using RazorGraph.Core.Graph;
using RazorGraph.Lua.Hosts;

/// <summary>
/// Everything a rule is allowed to look at: the finished graph, the per-file
/// declarations behind it, and the host whose environment the code targets.
///
/// A rule gets the FINISHED graph deliberately. The interesting questions are
/// cross-file — what a module calls, what its floor is, what nothing serves —
/// and a per-file check could only ask the shallow ones.
/// </summary>
public sealed record LuaCheckContext(
    CodeGraph Graph,
    IReadOnlyList<LuaFileDeclarations> Declarations,
    ILuaHost Host);

/// <summary>
/// A check that can be run over a built Lua graph.
///
/// This is the second half of what a code tool owes its user. The graph answers
/// what is THERE; a rule answers whether it is right — and for a language a
/// model writes fluently but knows shallowly, the second question is the one
/// that bites. Lua's public corpus skews 5.3/5.4 while Lightroom runs 5.1, so
/// the most likely error in generated code is a construct that reads perfectly
/// and does not exist in the host.
///
/// EXTENDING: rules arrive through <see cref="ILuaHost.Rules"/>, so an
/// environment brings its own knowledge with it rather than the checker growing
/// a switch over host names. That is the same seam the module already uses for
/// module resolution, vendor classification and node annotation — a new host is
/// still one class, and it can now carry its rules too. Rules that belong to the
/// LANGUAGE rather than to any host live in this namespace and always run.
/// </summary>
public interface ILuaRule
{
    /// <summary>Stable id, e.g. <c>lua.dialect</c> or <c>lightroom.sdk-surface</c>.</summary>
    string Id { get; }

    /// <summary>One line for a listing: what this rule is looking for.</summary>
    string Title { get; }

    /// <summary>
    /// Findings, or empty. A rule that cannot decide must return nothing rather
    /// than guess: a finding stream is only worth reading if it is nearly all
    /// true, and one noisy rule discredits the rest.
    /// </summary>
    IEnumerable<LuaFinding> Check(LuaCheckContext context);
}
