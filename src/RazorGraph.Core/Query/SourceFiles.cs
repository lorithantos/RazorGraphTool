namespace RazorGraph.Core.Query;

using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using RazorGraph.Core.Graph;

/// <summary>
/// Which files under a folder are worth reading as text, decided once for every
/// reader: grep's disk section and the graph's TextFile nodes list the same files
/// by the same rule, so the two can never disagree about what a tree contains.
/// </summary>
/// <remarks>
/// Public because the extractor and the MCP server are different assemblies and
/// Core's internals are visible to the tests alone. The directory-name lists live
/// here for the same reason: the client-asset extractor's vendor rule and the
/// directory walk below used to keep separate copies of them, and a copy is a
/// list that can drift without anything noticing.
/// </remarks>
public static class SourceFiles
{
    /// <summary>Files larger than this are skipped rather than read; generated dumps are not what a text reader is for.</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    // The probe git itself uses to call a file binary: a NUL in the first 8000-odd bytes.
    private const int BinaryProbeBytes = 8 * 1024;

    /// <summary>
    /// Directory names that mark everything beneath them as third-party. Matched as
    /// whole path segments, never substrings: a substring rule ("\lib\") once admitted
    /// nopCommerce's lib_npm -- 102k LOC of moment locales and elfinder classified as
    /// first-party by one unmatched name.
    /// </summary>
    public static readonly FrozenSet<string> VendorDirNames = new[]
    {
        "lib", "lib_npm", "node_modules", "bower_components", "vendor"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directory names that hold build output, which is never source.</summary>
    public static readonly FrozenSet<string> BuildOutputDirNames = new[]
    {
        "bin", "obj"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // What a directory walk leaves out when there is no ignore file to honour: the
    // two lists above, plus git's own store.
    private static readonly FrozenSet<string> WalkSkippedDirNames =
        VendorDirNames.Concat(BuildOutputDirNames).Append(".git").ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <see cref="Probe"/> found a file to be.</summary>
    public enum Verdict
    {
        /// <summary>Readable, within <see cref="MaxFileBytes"/>, and no NUL in its first 8 KB.</summary>
        Text,
        /// <summary>A NUL in the first 8 KB.</summary>
        Binary,
        /// <summary>Larger than <see cref="MaxFileBytes"/>.</summary>
        TooLarge,
        /// <summary>Missing -- git still lists a file deleted from the working tree -- or unreadable.</summary>
        Unreadable
    }

    /// <summary>Whether a file is text worth reading, and when it is not, why.</summary>
    public static Verdict Probe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Verdict.Unreadable;
            if (info.Length > MaxFileBytes) return Verdict.TooLarge;

            using var stream = info.OpenRead();
            var buffer = new byte[BinaryProbeBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, read) < 0 ? Verdict.Text : Verdict.Binary;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Verdict.Unreadable;
        }
    }

    /// <summary>
    /// Relative paths of every candidate file under the folder, and what decided that.
    /// Git's own answer when the folder is in a working tree, so .gitignore is honoured
    /// exactly rather than approximately; a directory walk otherwise. Candidates are not
    /// yet probed: a binary or oversized file is listed here and turned away by
    /// <see cref="Probe"/>, so each caller can count what it skipped.
    /// </summary>
    public static (IReadOnlyList<string> Files, string IgnoreSource) List(string folder)
    {
        if (GraphOutput.FindRepositoryRoot(folder) is not null)
        {
            if (GitListFiles(folder, "--cached", "--others", "--exclude-standard") is { } listed)
                return (listed, "git ls-files --cached --others --exclude-standard");

            return (WalkFiles(folder),
                "directory walk (git repository, but git ls-files failed): skipped .git, vendor and build-output directories");
        }

        return (WalkFiles(folder), "directory walk (not a git repository): skipped .git, vendor and build-output directories");
    }

    /// <summary>
    /// The files under the folder that git tracks, relative to it and spelled the way
    /// <see cref="List"/> spells them. Tracked is null when the folder is not in a working
    /// tree, and also when it is but git could not answer; InGitTree tells the two apart,
    /// because only the first means nothing here is untracked.
    /// </summary>
    public static (bool InGitTree, IReadOnlySet<string>? Tracked) TrackedFiles(string folder)
    {
        if (GraphOutput.FindRepositoryRoot(folder) is null) return (false, null);

        return (true, GitListFiles(folder, "--cached")?.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The files git lists under the folder in the given modes, relative to it. Null when
    /// git could not answer, so the caller walks instead of reporting an empty tree.
    /// </summary>
    private static List<string>? GitListFiles(string folder, params string[] modes)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                // Run IN the folder: ls-files then lists only what is beneath it, relative to it.
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
            };

            // -z: NUL-separated and unquoted, so no path needs unescaping.
            foreach (var argument in (string[])["ls-files", .. modes, "-z"]) start.ArgumentList.Add(argument);

            using var process = Process.Start(start);
            if (process is null) return null;

            // Drain stderr concurrently: a full pipe on either stream blocks git forever.
            var stderr = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000) || process.ExitCode != 0) return null;
            _ = stderr.Result;

            return output
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                // An untracked nested repository is listed as "dir/"; it is not a file.
                .Where(p => !p.EndsWith('/'))
                .Select(p => p.Replace('/', Path.DirectorySeparatorChar))
                .ToList();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // git is not installed or not on PATH.
            return null;
        }
    }

    private static List<string> WalkFiles(string folder)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            // Junctions and symlinks can loop; hidden files are still text worth finding.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(folder);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            files.AddRange(Directory.EnumerateFiles(dir, "*", options).Select(f => Path.GetRelativePath(folder, f)));

            foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
            {
                if (!WalkSkippedDirNames.Contains(Path.GetFileName(sub))) pending.Push(sub);
            }
        }

        return files;
    }
}
