namespace RazorGraph.Mcp;

using System.Security.Cryptography;
using System.Text;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Serialization;

/// <summary>
/// The server's own copy of every graph it builds, so a session that restarts can
/// load_graph what the last one paid to compile instead of compiling it again.
/// </summary>
/// <remarks>
/// Saved when a build succeeds, never at shutdown: rotation stops the server with
/// Stop-Process -Force, which runs no shutdown hook at all, so a save-on-exit fires
/// only when nothing went wrong. A build is the one moment a graph's content is
/// decided -- nothing mutates a held graph afterwards -- so saving there is complete.
///
/// It lives in the per-user application data folder rather than anywhere near a
/// repository. This is the tool's own internal bookkeeping and optimisation, so it
/// belongs in the tool's own space; a working tree is the user's, and the rule in
/// <see cref="GraphOutput"/> keeps the tool's state out of it.
/// </remarks>
internal static class GraphCache
{
    /// <summary>What a build response reports about its save: exactly one of the two is set.</summary>
    internal sealed record Outcome(string? SavedTo, string? SaveError);

    /// <summary>
    /// %LOCALAPPDATA%\RazorGraph\graphs, or null when the platform has no such folder.
    /// </summary>
    /// <remarks>
    /// Null rather than a relative path: Path.Combine over an empty folder would
    /// quietly put the cache in the working directory, which is the one place it
    /// must never land.
    /// </remarks>
    internal static string? DefaultDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "RazorGraph", "graphs");
        }
    }

    /// <summary>
    /// <c>&lt;graphId&gt;-&lt;hash8&gt;.json</c>: the id so a person can find it, the hash of
    /// the source so two sources filed under one id do not overwrite each other, and
    /// nothing else, so the next build of the same source overwrites the same file.
    /// </summary>
    internal static string FileName(string graphId, string source) =>
        $"{Sanitise(graphId)}-{SourceHash(source)}.json";

    /// <summary>
    /// Saves the entry's graph into <paramref name="directory"/> and says where, or
    /// says why not. Never throws: the build this follows has already succeeded, and a
    /// cache that cannot be written is not a reason to report it as failed.
    /// </summary>
    internal static Outcome Save(GraphStore.GraphEntry entry, string? directory)
    {
        string? temp = null;
        try
        {
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("there is no per-user application data folder to cache graphs in");

            var full = Path.GetFullPath(directory);
            Directory.CreateDirectory(full);
            var target = Path.Combine(full, FileName(entry.Id, entry.Source));

            // Written beside the target and moved over it, so a server killed mid-write
            // leaves a stray temp file rather than a half graph under the real name.
            temp = Path.Combine(full, $"{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, GraphSerializer.ToJson(entry.Graph));
            File.Move(temp, target, overwrite: true);
            temp = null;

            return new Outcome(target, null);
        }
        catch (Exception ex)
        {
            // Every exception, deliberately: whatever the save hit, the graph is built
            // and registered, and the response says what went wrong in one line.
            if (temp != null)
            {
                try { File.Delete(temp); } catch { /* the error being reported is the save's, not this */ }
            }
            return new Outcome(null, OneLine($"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>
    /// First 8 hex characters of a SHA-256 of the full source path. Upper-cased first
    /// on Windows, where C:\Repos and c:\repos are the same file and must be the same
    /// cache entry.
    /// </summary>
    internal static string SourceHash(string source)
    {
        var normalised = Path.GetFullPath(source);
        if (OperatingSystem.IsWindows()) normalised = normalised.ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// The id with anything that is not a letter, digit, '-', '_' or '.' replaced, so
    /// one id names one file on every file system rather than per platform.
    /// </summary>
    private static string Sanitise(string graphId)
    {
        var chars = graphId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        return chars.Length == 0 ? "graph" : new string(chars);
    }

    private static string OneLine(string text) =>
        string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
