using System.Diagnostics;
using System.Text;

namespace RazorGraph.Mcp;

/// <summary>
/// Splits a command line given as one option value into an executable and its arguments, without
/// a shell.
/// </summary>
/// <remarks>
/// The rules are deliberately few, so a command line means the same thing on every machine:
/// whitespace outside double quotes separates tokens; a double-quoted span keeps its whitespace and
/// loses its quotes, and may abut unquoted text (<c>--x="a b"</c> is the one token <c>--x=a b</c>);
/// <c>\"</c> is a literal double quote, inside quotes or out; every other backslash is literal, so a
/// Windows path needs no doubling; single quotes are ordinary characters, so
/// <c>pwsh -Command "'abc'"</c> hands pwsh <c>'abc'</c>. An unterminated quote is refused rather
/// than guessed at.
/// </remarks>
internal static class CommandLine
{
    /// <summary>The tokens of <paramref name="commandLine"/>, in order.</summary>
    /// <exception cref="FormatException">A double quote is opened and never closed.</exception>
    internal static IReadOnlyList<string> Split(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);

        List<string> tokens = [];
        StringBuilder current = new();
        bool inToken = false;
        bool quoted = false;

        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];

            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                current.Append('"');
                inToken = true;
                i++;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                inToken = true;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }

                continue;
            }

            current.Append(c);
            inToken = true;
        }

        if (quoted)
        {
            throw new FormatException("The command line opens a double quote and never closes it.");
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}

/// <summary>Finds an executable the way a shell would, through PATH, so the error can say so.</summary>
internal static class ExecutableResolver
{
    /// <summary>
    /// The full path of <paramref name="name"/>. A name carrying a directory is taken as given; a
    /// bare name is looked for in each PATH directory in order, on Windows with each PATHEXT
    /// extension (so <c>janet</c> finds <c>janet.exe</c> in <c>~\.dotnet\tools</c>).
    /// </summary>
    /// <param name="name">The first token of the command line.</param>
    /// <param name="path">The PATH value to search.</param>
    /// <param name="pathExt">The PATHEXT value; ignored off Windows.</param>
    /// <exception cref="FileNotFoundException">Nothing by that name exists where it was looked for.</exception>
    internal static string Resolve(string name, string? path, string? pathExt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("The executable name is empty.", nameof(name));
        }

        bool hasDirectory = Path.IsPathRooted(name)
            || name.Contains(Path.DirectorySeparatorChar)
            || name.Contains(Path.AltDirectorySeparatorChar);

        if (hasDirectory)
        {
            foreach (string candidate in Candidates(Path.GetFullPath(name), pathExt))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new FileNotFoundException($"The executable '{name}' does not exist.", name);
        }

        string[] directories = (path ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string directory in directories)
        {
            foreach (string candidate in Candidates(Path.Combine(directory.Trim('"'), name), pathExt))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException(
            $"The executable '{name}' was not found on PATH ({directories.Length} directories searched). " +
            "Give its full path, or put its directory on PATH for the server's environment.",
            name);
    }

    private static IEnumerable<string> Candidates(string basePath, string? pathExt)
    {
        if (!OperatingSystem.IsWindows())
        {
            yield return basePath;
            yield break;
        }

        // A name that already carries an extension (pwsh.exe) is tried as written first.
        if (Path.HasExtension(basePath))
        {
            yield return basePath;
        }

        string extensions = string.IsNullOrWhiteSpace(pathExt) ? ".COM;.EXE;.BAT;.CMD" : pathExt;
        foreach (string extension in extensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return basePath + extension;
        }
    }
}

/// <summary>What a finished command printed and returned.</summary>
internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs one executable, no shell, with a timeout that kills it.</summary>
internal static class CommandRunner
{
    /// <summary>
    /// How long to wait for the output pipes to close once the process has exited or been killed.
    /// A process the command started and left running can hold them open.
    /// </summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <paramref name="executablePath"/> with <paramref name="arguments"/> passed one by one
    /// (no shell parses them), stdin closed, and stdout and stderr captured.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// It ran past <paramref name="timeout"/>. It and every process it started are killed first; the
    /// message carries what it wrote to stderr by then.
    /// </exception>
    internal static CommandResult Run(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        ProcessStartInfo startInfo = new(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        string name = Path.GetFileName(executablePath);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"'{name}' did not start.");

        // A command that prompts reads end-of-file and fails at once, rather than waiting out the
        // timeout for input nobody will type.
        process.StandardInput.Close();

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the wait giving up and the kill; it is gone either way.
            }

            stderr.Wait(DrainTimeout);
            throw new TimeoutException(
                $"'{name}' did not finish within {timeout.TotalSeconds:0.###} s and was killed." +
                Describe(stderr));
        }

        if (!Task.WaitAll([stdout, stderr], DrainTimeout))
        {
            throw new TimeoutException(
                $"'{name}' exited, but a process it started still holds its output open." + Describe(stderr));
        }

        return new CommandResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>The stderr text for an error message, trimmed and capped, or nothing when there is none.</summary>
    internal static string Describe(string? stderr)
    {
        string text = (stderr ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return " It wrote nothing to stderr.";
        }

        const int Cap = 1000;
        return " stderr: " + (text.Length > Cap ? text[..Cap] + "..." : text);
    }

    private static string Describe(Task<string> stderr) =>
        Describe(stderr.IsCompletedSuccessfully ? stderr.Result : null);
}
