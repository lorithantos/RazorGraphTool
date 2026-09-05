namespace RazorGraph.Lua.Hosts;

using RazorGraph.Lua.Checks;

/// <summary>
/// Literal arguments that are not among the values the SDK documents for them.
///
/// The gap this closes: a signature says setRawMetadata takes a string key, so
/// nothing catches "capshun". It is a plausible name, confidently spelled, and it
/// fails only at run time inside Lightroom -- the worst place to find out and the
/// hardest to reach from a build.
///
/// Severity follows <see cref="LightroomSdkRule"/> rather than inventing its own
/// scale. That rule makes a REMOVED module an Error and an uncatalogued function a
/// Note, because the second is a statement about our coverage rather than about
/// the code. An undocumented argument value is the first kind: where the reference
/// enumerates a parameter it enumerates it completely, so a value outside the set
/// is wrong rather than merely unknown to us.
///
/// What keeps that claim honest is everything the check stays silent about, all
/// decided in <c>LightroomHost.AnnotateArgumentValues</c>: a parameter the docs do
/// not enumerate, and an argument that is not a literal. Neither produces a
/// finding, because neither is knowable from here.
/// </summary>
internal sealed class LightroomArgumentRule : ILuaRule
{
    public string Id => "lightroom.sdk-argument";

    public string Title => "Literal arguments, against the values the SDK documents";

    public IEnumerable<LuaFinding> Check(LuaCheckContext context)
    {
        foreach (var module in context.Graph.Nodes.Where(n => n.ForeignType == LuaGraphBuilder.ModuleKind))
        {
            var file = module.GetProperty<string>("relativePath") ?? module.Name;

            // A value the reference does not list for this parameter. One finding
            // per site rather than a count: the whole use of this is seeing which
            // call, on which line, with what spelling.
            foreach (var bad in module.GetProperty<List<string>>("sdkArgumentsNotDocumented") ?? [])
            {
                yield return new LuaFinding(
                    Id, LuaSeverity.Error, file, 0,
                    $"argument value the SDK does not document: {bad}",
                    "the reference enumerates this parameter, so a value outside it is not accepted");
            }

            // Real, but younger than the plug-in says it supports. A Warning, not
            // an Error: the code is correct on a newer Lightroom, and the fix is a
            // manifest change rather than a code one.
            foreach (var late in module.GetProperty<List<string>>("sdkArgumentsNewerThanFloor") ?? [])
            {
                var floor = module.GetProperty<string>("minimumSdkVersion");

                yield return new LuaFinding(
                    Id, LuaSeverity.Warning, file, 0,
                    $"argument value newer than the declared minimum: {late}",
                    floor is null ? "no LrSdkMinimumVersion is declared" : $"declared minimum is {floor}");
            }
        }
    }
}
