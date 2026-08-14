using System.Diagnostics;

namespace RazorGraph.Core.Graph;

/// <summary>
/// Where generated graphs go, and whether git is about to pick one up.
/// </summary>
/// <remarks>
/// In Core rather than in either front end because the CLI and the MCP server both write
/// graphs, and a rule about not polluting someone's repository is worth exactly nothing if
/// only one of them follows it.
/// </remarks>
public static class GraphOutput
{
    /// <summary>
    /// The directory every default output goes in, relative to the working directory.
    /// </summary>
    /// <remarks>
    /// Defaults used to be bare file names -- graph.json, solution-graph.json, lua-graph.json,
    /// research.json -- so the default behaviour of an analysis command was to drop a
    /// multi-megabyte derived artifact in the root of whatever repository was being analysed,
    /// where the host's .gitignore says nothing about it and `git add -A` sweeps it in. One
    /// measured case was 1.8 MB.
    ///
    /// The name is deliberately not a word anyone would pick for their own content, which is
    /// what lets the matching ignore rule be a single unambiguous line. The broad patterns it
    /// replaced (*.graph.json, graphs/, .graph/) needed a warning attached, because they could
    /// not tell a generated graph from a directory of charts someone authored.
    /// </remarks>
    public const string Directory = "Janet.Plumbline";

    /// <summary>A default output path inside <see cref="Directory"/>.</summary>
    public static string Default(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>Creates the directory the output is about to be written into, and nothing else.</summary>
    /// <remarks>
    /// This tool writes the file it was asked for and no other. An earlier cut of this dropped a
    /// self-ignoring <c>.gitignore</c> into its own output directory, which would have hidden
    /// the artifact in any repository for free -- and was rejected, correctly: creating files
    /// nobody asked for inside someone's working tree is bad manners even when the file is
    /// helpful, and a tool that edits your repository as a side effect of answering a question
    /// has to be trusted about more than the answer. <see cref="GitAdvice"/> says the same thing
    /// in words instead, and leaves the decision where it belongs.
    /// </remarks>
    public static void Prepare(string outputPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (string.IsNullOrEmpty(directory)) return;

        System.IO.Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Advice to surface after writing a graph, or <see langword="null"/> when there is nothing
    /// to say.
    /// </summary>
    /// <remarks>
    /// Addressed to the agent reading the tool's output, because it is the agent that will
    /// relay it. Silent when the path is outside any repository, and silent when git already
    /// ignores it: a reminder that fires every time is one that gets filtered out mentally, and
    /// the one that mattered goes with it.
    ///
    /// Whether a path is ignored is asked of git rather than answered here. Reimplementing
    /// gitignore precedence -- negations, directory rules, nested files, excludesFile, the
    /// repository's own info/exclude -- would be a second opinion that disagrees with the real
    /// one occasionally and silently, which is worse than not checking.
    /// </remarks>
    public static string? GitAdvice(string outputPath)
    {
        var full = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory)) return null;

        var root = FindRepositoryRoot(directory);
        if (root is null) return null;

        if (IsIgnoredByGit(full, root) == true) return null;

        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        var pattern = Path.GetRelativePath(root, directory).Replace('\\', '/').TrimEnd('/') + "/";

        return
            $"NOTE FOR THE AGENT: this graph was written to '{relative}', inside the git repository " +
            $"at '{root}', and git does not ignore it. A code graph is derived data -- regenerable " +
            "from source, stale as soon as anything is edited, and kept forever once committed. " +
            "Unless the user has already dealt with this, remind them to add a rule for " +
            $"'{pattern}' to their .gitignore. If git already tracks that path, .gitignore will not " +
            "help on its own -- it needs `git rm --cached` first.";
    }

    /// <summary>
    /// Asks git whether it ignores a path. Null when git could not answer.
    /// </summary>
    /// <remarks>
    /// A failure to run git returns null rather than true, so the caller advises. The asymmetry
    /// is deliberate: a redundant reminder costs a line of output, while a wrongly suppressed
    /// one costs a committed artifact that nobody notices until it is in history for good.
    /// </remarks>
    private static bool? IsIgnoredByGit(string fullPath, string repositoryRoot)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                // -q: exit code carries the answer, so nothing has to be parsed.
                ArgumentList = { "check-ignore", "-q", fullPath },
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null) return null;

            // Bounded: git answers this in milliseconds, and an analysis tool must not hang on
            // an advisory check. A timeout is treated as "could not answer", so we still advise.
            if (!process.WaitForExit(3000)) return null;

            return process.ExitCode switch
            {
                0 => true,   // ignored
                1 => false,  // not ignored
                _ => null,   // not a repository, git too old, anything else: do not claim to know
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // git is not installed or not on PATH.
            return null;
        }
    }

    /// <summary>Walks up looking for a working tree. Returns null when there is no repository.</summary>
    /// <remarks>
    /// '.git' is tested with Exists rather than as a directory: in a worktree or a submodule it
    /// is a FILE pointing elsewhere, and treating that as "no repository" would silence the
    /// advice exactly where a stray artifact is least likely to be noticed.
    /// </remarks>
    private static string? FindRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);

        while (current is not null)
        {
            var marker = Path.Combine(current.FullName, ".git");
            if (System.IO.Directory.Exists(marker) || File.Exists(marker)) return current.FullName;

            current = current.Parent;
        }

        return null;
    }
}
