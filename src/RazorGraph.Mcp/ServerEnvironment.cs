namespace RazorGraph.Mcp;

using System.Collections;
using System.Text.RegularExpressions;

// A COPY, not a shared dependency. Taken from JanetHome src\Janet.Core\ServerEnvironment.cs at
// commit abad43f, adapted for razorgraph: RAZORGRAPH_* where that file has JANET_*, and
// RAZORGRAPH_KEEP_ENVIRONMENT as the opt-out. It is copied because Janet's shared code reaches
// other repositories only through a local package feed, and this repository is public and must
// restore from a clean clone. The two solve one problem the same way: a fix to either belongs
// in both, and they should be kept in step.

/// <summary>What normalising the server's environment changed, by variable NAME only.</summary>
/// <remarks>
/// Names, never values: a launcher's environment can hold tokens and connection strings, and this
/// report is served by server_info and so ends up in transcripts.
/// </remarks>
/// <param name="Applied">False when the environment was left as inherited -- see SkippedBecause.</param>
/// <param name="SkippedBecause">Why it was left as inherited; null when it was applied.</param>
/// <param name="Dropped">Variables the launcher had that the clean environment does not: the leaks this exists to stop.</param>
/// <param name="Changed">Variables present in both with a different value -- typically PATH, which a developer prompt prepends to.</param>
/// <param name="KeptFromLauncher">RAZORGRAPH_* variables carried over on purpose: deliberate configuration, not leakage.</param>
internal sealed record EnvironmentReport(
    bool Applied,
    string? SkippedBecause,
    IReadOnlyList<string> Dropped,
    IReadOnlyList<string> Changed,
    IReadOnlyList<string> KeptFromLauncher);

/// <summary>
/// Gives the resident server the environment a fresh sign-in would give it, instead of whatever
/// the terminal that started it happened to hold.
/// </summary>
/// <remarks>
/// WHY: a resident server's environment is a property of the shell that launched it hours or days
/// earlier, and nothing records which one that was. razorgraph-mcp loads solutions through
/// MSBuildWorkspace, and MSBuild promotes environment variables to global properties -- so a
/// server started from a Visual Studio x64 Native Tools prompt carries Platform=x64 for its whole
/// life, and every graph it builds evaluates for a platform nobody asked for. Generated-code paths
/// follow the platform, so the same solution came out 8 to 848 nodes different per corpus from
/// such a server than from a clean start. Graphs now record the platform they were built for;
/// this stops the server inheriting one by accident. It fixes the class rather than the instance:
/// the server normalises ITSELF at startup, so MSBuild's out-of-proc build host and anything else
/// it starts inherit the clean environment too.
///
/// WHAT CLEAN MEANS: the machine and user environment from the registry (user over machine, and
/// PATH as machine then user, the way Windows composes it at sign-in), plus the logon and system
/// variables Windows sets per session and never stores there (SystemRoot, USERPROFILE, APPDATA and
/// kin), plus RAZORGRAPH_* -- the server's own configuration, set deliberately. Everything else the
/// launcher had is dropped. RAZORGRAPH_KEEP_ENVIRONMENT=1 opts out, for a session that needs a
/// launcher's environment on purpose.
///
/// Windows only: elsewhere there is no registry environment to rebuild from, and the server keeps
/// what it inherited and says so.
/// </remarks>
internal static class ServerEnvironment
{
    internal const string OptOutVariable = "RAZORGRAPH_KEEP_ENVIRONMENT";

    private const string OwnPrefix = "RAZORGRAPH_";

    /// <summary>
    /// Set by Windows for each logon or process rather than stored in the registry, so a clean
    /// environment takes them from the launcher. Values are identical in every process of one
    /// sign-in, so none of them is a leak.
    /// </summary>
    internal static readonly IReadOnlyList<string> LogonVariables =
    [
        "SystemRoot", "SystemDrive", "windir", "ComSpec", "OS",
        "COMPUTERNAME", "USERNAME", "USERDOMAIN", "USERDOMAIN_ROAMINGPROFILE", "LOGONSERVER", "SESSIONNAME",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "HOMESHARE", "APPDATA", "LOCALAPPDATA",
        "ALLUSERSPROFILE", "ProgramData", "PUBLIC",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION",
        "NUMBER_OF_PROCESSORS",
    ];

    private static readonly Regex Reference = new("%([^%]+)%", RegexOptions.Compiled);

    /// <summary>The report from this process's own normalisation, or null when it has not run.</summary>
    internal static EnvironmentReport? Last { get; private set; }

    /// <summary>
    /// The clean environment, from the launcher's (for logon variables and RAZORGRAPH_*), the
    /// machine's and the user's. Pure, so the tests drive it with dictionaries.
    /// </summary>
    /// <param name="launcher">The environment this process inherited.</param>
    /// <param name="machine">The machine environment from the registry.</param>
    /// <param name="user">The user environment from the registry.</param>
    /// <param name="signIn">
    /// HKCU\Volatile Environment: what Windows set for THIS sign-in (USERNAME, USERPROFILE,
    /// APPDATA and kin). It beats the launcher's copy of the same names, because a launcher's copy
    /// can be wrong and then propagates: the first janet-mcp rotation onto this code ran as
    /// USERNAME=SYSTEM, and the server it started -- launched BY it -- inherited SYSTEM as a
    /// "logon value". The registry is the source a launcher cannot corrupt. Null or empty off
    /// Windows and in tests that do not need it.
    /// </param>
    internal static IReadOnlyDictionary<string, string> Build(IDictionary launcher, IDictionary machine, IDictionary user, IDictionary? signIn = null)
    {
        Dictionary<string, string> clean = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> current = Flatten(launcher);
        Dictionary<string, string> machineValues = Flatten(machine);
        Dictionary<string, string> userValues = Flatten(user);

        foreach ((string name, string value) in machineValues.Concat(userValues))
        {
            if (!name.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                clean[name] = value;
            }
        }

        // Machine then user, as Windows composes it at sign-in. Not "user over machine": PATH is
        // the one variable the two are concatenated for rather than one replacing the other.
        string path = string.Join(';', new[]
        {
            machineValues.GetValueOrDefault("Path"),
            userValues.GetValueOrDefault("Path"),
        }.Where(p => !string.IsNullOrEmpty(p)));

        if (path.Length > 0)
        {
            clean["Path"] = path;
        }

        // AFTER the registry, because Windows sets these per logon OVER whatever the registry
        // says: the machine environment carries USERNAME=SYSTEM, and a sign-in replaces it with
        // the real user. Applying the registry last made a rotated janet-mcp believe it was
        // SYSTEM -- found 2026-10-08 on its first rotation, from its own 'changed' list.
        foreach (string name in LogonVariables)
        {
            if (current.TryGetValue(name, out string? value))
            {
                clean[name] = value;
            }
        }

        // And the sign-in's own record over both: see the signIn parameter.
        foreach ((string name, string value) in Flatten(signIn ?? new Hashtable()))
        {
            clean[name] = value;
        }

        foreach ((string name, string value) in current)
        {
            if (name.StartsWith(OwnPrefix, StringComparison.OrdinalIgnoreCase))
            {
                clean[name] = value;
            }
        }

        // Registry values may be REG_EXPAND_SZ and arrive unexpanded (%USERPROFILE%\.dotnet\tools).
        // Two passes, so a value referring to another expandable value resolves too. A reference
        // to a name that is not defined is left as written, which is what Windows does.
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (string name in clean.Keys.ToList())
            {
                clean[name] = Reference.Replace(clean[name], m =>
                    clean.TryGetValue(m.Groups[1].Value, out string? value) && !value.Contains('%' + m.Groups[1].Value + '%', StringComparison.OrdinalIgnoreCase)
                        ? value
                        : m.Value);
            }
        }

        return clean;
    }

    /// <summary>What replacing the launcher's environment with the clean one changes, by name.</summary>
    internal static EnvironmentReport Compare(IDictionary launcher, IReadOnlyDictionary<string, string> clean)
    {
        Dictionary<string, string> current = Flatten(launcher);

        return new EnvironmentReport(
            true,
            null,
            [.. current.Keys.Where(name => !clean.ContainsKey(name)).Order(StringComparer.OrdinalIgnoreCase)],
            [.. current.Where(kv => clean.TryGetValue(kv.Key, out string? value) && !string.Equals(value, kv.Value, StringComparison.Ordinal))
                       .Select(kv => kv.Key).Order(StringComparer.OrdinalIgnoreCase)],
            [.. current.Keys.Where(name => name.StartsWith(OwnPrefix, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// Replaces this process's environment with the clean one. Called once, first thing, by the
    /// server's entry point; everything it starts afterwards inherits the result. Never from a
    /// test: it rewrites the environment every test in the process shares.
    /// </summary>
    /// <param name="resident">
    /// True for the HTTP server, which outlives the shell that started it and is shared by every
    /// session. False for stdio, where the MCP client owns the process for exactly its own life,
    /// and anything in its config's env block -- DOTNET_ROOT, say -- was set there on purpose. A
    /// stdio server keeps what it was given, and the report says so. janet-mcp does not make this
    /// distinction yet; that is a gap there, not a choice to copy.
    /// </param>
    internal static EnvironmentReport Apply(bool resident = true)
    {
        IDictionary launcher = Environment.GetEnvironmentVariables();

        if (!resident)
        {
            return Last = new EnvironmentReport(false, "stdio: the MCP client owns this process and its environment", [], [], []);
        }

        if (launcher[OptOutVariable] is string optOut && (optOut == "1" || optOut.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            return Last = new EnvironmentReport(false, $"{OptOutVariable} is set", [], [], []);
        }

        if (!OperatingSystem.IsWindows())
        {
            return Last = new EnvironmentReport(false, "not Windows: there is no registry environment to rebuild from", [], [], []);
        }

        IReadOnlyDictionary<string, string> clean = Build(
            launcher,
            Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariables(EnvironmentVariableTarget.User),
            SignInEnvironment());

        EnvironmentReport report = Compare(launcher, clean);

        foreach (string name in report.Dropped)
        {
            Environment.SetEnvironmentVariable(name, null);
        }

        foreach ((string name, string value) in clean)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        return Last = report;
    }

    /// <summary>
    /// The string values under HKCU\Volatile Environment, which Windows writes at each sign-in.
    /// Empty when the key cannot be read; the launcher's values then stand, as before.
    /// </summary>
    private static Hashtable SignInEnvironment()
    {
        Hashtable values = new(StringComparer.OrdinalIgnoreCase);

        if (!OperatingSystem.IsWindows())
        {
            return values;
        }

        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Volatile Environment");

            foreach (string name in key?.GetValueNames() ?? [])
            {
                if (name.Length > 0 && key!.GetValue(name) is string value)
                {
                    values[name] = value;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable: fall back to the launcher's values rather than fail the server's start.
        }

        return values;
    }

    private static Dictionary<string, string> Flatten(IDictionary variables)
    {
        Dictionary<string, string> flat = new(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in variables)
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                flat[name] = value;
            }
        }

        return flat;
    }
}
