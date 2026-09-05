namespace RazorGraph.Lua.Hosts;

/// <summary>
/// Picks the host from what is on disk. The choice is always reported by the
/// caller, never silent: a misdetected host uses the wrong reference function and
/// produces a graph that is empty for a structural reason no one can see.
/// </summary>
internal static class HostDetection
{
    internal sealed record Detection(ILuaHost Host, string Evidence);

    public static Detection Detect(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);

        // Lightroom first: an Info.lua is a far more specific signal than a
        // rockspec, and a tree could conceivably carry both.
        var info = Directory.EnumerateFiles(root, "Info.lua", SearchOption.AllDirectories).FirstOrDefault();
        if (info is not null)
            return new Detection(new LightroomHost(root), $"Info.lua at {Path.GetRelativePath(root, info)}");

        var rockspec = Directory.EnumerateFiles(root, "*.rockspec", SearchOption.AllDirectories)
            .OrderBy(f => f.Length)
            .FirstOrDefault();
        if (rockspec is not null)
            return new Detection(new LuaRocksHost(root, rockspec), $"rockspec {Path.GetRelativePath(root, rockspec)}");

        return new Detection(new LuaRocksHost(root), "no manifest found; plain Lua path conventions");
    }
}
