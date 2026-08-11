namespace RazorGraph.Lua.Hosts;

using RazorGraph.Core.Graph;
using RazorGraph.Lua.Checks;

/// <summary>
/// What the code asks of the Lightroom SDK, checked against the catalogued
/// surface.
///
/// Host knowledge, so it arrives with the host: only LightroomHost knows there
/// is an SDK, that its modules are Lr-prefixed, or that a version was recorded
/// for each function.
///
/// Every finding here is bounded by the catalogue's own coverage, and says so
/// rather than overclaiming. The catalogue holds 3.0, 8.0 and 15.3 while Adobe
/// has shipped more, and 30 of 844 documented functions carry no version at all
/// — so "not catalogued" is a statement about US, and it is reported as a note.
/// The one thing worth an error is a module Adobe REMOVED, because that is a
/// call that fails on a version the plug-in claims to support.
/// </summary>
public sealed class LightroomSdkRule : ILuaRule
{
    public string Id => "lightroom.sdk-surface";

    public string Title => "Calls into the Lightroom SDK, against the catalogued surface";

    public IEnumerable<LuaFinding> Check(LuaCheckContext context)
    {
        var catalog = LightroomHost.Catalog;

        foreach (var module in context.Graph.Nodes.Where(n => n.ForeignType == LuaGraphBuilder.ModuleKind))
        {
            var file = module.GetProperty<string>("relativePath") ?? module.Name;

            // Retired: the module was catalogued once and is absent from the
            // newest catalogued SDK. A live compatibility problem rather than
            // trivia, and the only claim here strong enough to be an error.
            foreach (var retired in module.GetProperty<List<string>>("sdkModulesRetired") ?? [])
            {
                yield return new LuaFinding(
                    Id, LuaSeverity.Error, file, 0,
                    $"imports a module the SDK no longer ships: {retired}",
                    $"catalogued versions: {string.Join(", ", catalog.CatalogedVersions)}");
            }

            // Called but not catalogued. A note, not a warning: the likeliest
            // cause is that the function is newer than anything we hold, and
            // calling it an error would send someone hunting a typo that is not
            // there. Named so a reader can check it against Adobe's own docs.
            var uncatalogued = module.GetProperty<List<string>>("sdkCallsNotInCatalogue") ?? [];
            if (uncatalogued.Count > 0)
            {
                yield return new LuaFinding(
                    Id, LuaSeverity.Note, file, 0,
                    $"calls {uncatalogued.Count} SDK function(s) this catalogue does not carry",
                    string.Join(", ", uncatalogued.Take(8)));
            }

            // Imported and never used. Not a defect -- an unused import costs
            // nothing at runtime -- but it is what inflated a version floor and
            // produced a wrong compatibility answer, so it is worth seeing when
            // it is the ONLY thing holding the floor up.
            var drivers = module.GetProperty<List<string>>("minimumSdkVersionDrivenBy") ?? [];
            var floor = module.GetProperty<string>("minimumSdkVersion");
            if (floor is not null && drivers.Count > 0 && drivers.All(d => d.Contains("imported", StringComparison.Ordinal)))
            {
                yield return new LuaFinding(
                    Id, LuaSeverity.Note, file, 0,
                    $"needs SDK {floor} only because of an import nothing calls",
                    string.Join(", ", drivers));
            }
        }
    }
}
