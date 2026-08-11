namespace RazorGraph.Lua.Hosts;

using System.Text.RegularExpressions;

/// <summary>
/// Plain Lua and LuaRocks packages: <c>require</c>, resolved against a rockspec
/// where one exists and against path convention otherwise.
///
/// The rockspec is Lua's nearest thing to a .csproj — Kong's carries 605 explicit
/// ["kong.cache"] = "kong/cache/init.lua" mappings, which is authoritative and
/// removes all guessing. It is not exhaustive though: those 605 cover 605 of
/// Kong's 1,309 .lua files, so spec/ and bin/ still need the convention rule.
/// </summary>
public sealed partial class LuaRocksHost : ILuaHost
{
    private readonly string _root;
    private readonly Dictionary<string, string> _rockspecModules;

    /// <summary>Module name to relative path, from the rockspec. Empty when there was none.</summary>
    public IReadOnlyDictionary<string, string> RockspecModules => _rockspecModules;

    public LuaRocksHost(string root, string? rockspecPath = null)
    {
        _root = Path.GetFullPath(root);
        _rockspecModules = rockspecPath is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : ParseRockspecModules(rockspecPath);
    }

    public string Name => _rockspecModules.Count > 0 ? "luarocks" : "lua";

    // LuaJIT 2.1 rather than 5.1: Kong uses goto/::label:: 148 times, which 5.1
    // does not have, and a 5.1-only parse fails on the stress corpus outright.
    public LuaDialect Dialect => LuaDialect.LuaJit21;

    public ModuleReferenceSupport ReferenceSupport => ModuleReferenceSupport.Static;

    public IReadOnlySet<string> ReferenceFunctions { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "require" };

    public IEnumerable<LuaSourceFile> Discover(string rootPath) =>
        Directory.EnumerateFiles(rootPath, "*.lua", SearchOption.AllDirectories)
            .Select(f => new LuaSourceFile(f, Relative(f)))
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal);

    public string ModuleNameFor(LuaSourceFile file)
    {
        // A rockspec entry is authoritative when one names this file.
        foreach (var (module, path) in _rockspecModules)
        {
            if (string.Equals(Normalize(path), Normalize(file.RelativePath), StringComparison.OrdinalIgnoreCase))
                return module;
        }

        // Convention: strip .lua, and collapse a trailing /init to the directory
        // itself — kong/cache/init.lua IS kong.cache, not kong.cache.init.
        var rel = Normalize(file.RelativePath);
        if (rel.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) rel = rel[..^4];
        if (rel.EndsWith("/init", StringComparison.OrdinalIgnoreCase)) rel = rel[..^5];
        return rel.Replace('/', '.');
    }

    public ModuleResolution Resolve(string? reference, LuaSourceFile from)
    {
        if (reference is null)
            return new ModuleResolution.Unresolved("dynamic require: argument is not a literal string");

        if (_rockspecModules.TryGetValue(reference, out var declared))
        {
            var full = Path.GetFullPath(Path.Combine(_root, declared.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(full)) return new ModuleResolution.InGraph(full);

            // The rockspec named it and it is not there. Distinct from "not found":
            // the manifest and the tree disagree, which is a fact worth reporting.
            return new ModuleResolution.Unresolved($"rockspec maps '{reference}' to '{declared}', which does not exist");
        }

        // package.path convention: foo.bar -> foo/bar.lua, else foo/bar/init.lua.
        var asPath = reference.Replace('.', Path.DirectorySeparatorChar);
        foreach (var candidate in new[]
        {
            Path.Combine(_root, asPath + ".lua"),
            Path.Combine(_root, asPath, "init.lua")
        })
        {
            if (File.Exists(candidate)) return new ModuleResolution.InGraph(Path.GetFullPath(candidate));
        }

        // Standard library and anything installed outside the tree. Not a failure:
        // require "string" is correct code, and calling it unresolved would bury
        // the genuinely dynamic requires in noise.
        return new ModuleResolution.External(reference);
    }

    private string Relative(string full) =>
        Path.GetRelativePath(_root, full);

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Pull the build.modules table out of a rockspec. Read with a regex rather
    /// than by evaluating the rockspec: a rockspec is executable Lua, and running
    /// third-party code to find out what files it declares is not a trade worth
    /// making. The declaration table is flat and literal in practice.
    /// </summary>
    private static Dictionary<string, string> ParseRockspecModules(string rockspecPath)
    {
        var modules = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(rockspecPath)) return modules;

        foreach (Match m in ModuleEntry().Matches(File.ReadAllText(rockspecPath)))
        {
            modules[m.Groups["name"].Value] = m.Groups["path"].Value;
        }
        return modules;
    }

    // ["kong.cache"] = "kong/cache/init.lua"
    [GeneratedRegex("""\[\s*"(?<name>[^"]+)"\s*\]\s*=\s*"(?<path>[^"]+\.lua)"|\[\s*'(?<name>[^']+)'\s*\]\s*=\s*'(?<path>[^']+\.lua)'""")]
    private static partial Regex ModuleEntry();
}
