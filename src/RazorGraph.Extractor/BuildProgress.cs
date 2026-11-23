namespace RazorGraph.Extractor;

using System.Diagnostics;

/// <summary>
/// One step of a build, as a front end hears about it: the phase that just
/// started, where it sits in the build's plan, and -- for the phases that walk
/// the solution a project at a time -- which project out of how many.
/// </summary>
/// <remarks>
/// Public because GraphBuilder is, and this is what its Progress property
/// carries to the CLI and the MCP server, which are other assemblies. It is the
/// only part of the seam that has to cross: the clock that produces it stays
/// internal.
/// </remarks>
public sealed record BuildProgress(
    string Phase,
    int PhaseNumber,
    int PhaseCount,
    TimeSpan Elapsed,
    int Project = 0,
    int ProjectCount = 0,
    string? ProjectName = null)
{
    /// <summary>
    /// The line a front end shows. One spelling for both, so the CLI and the MCP
    /// server cannot describe the same moment of the same build differently.
    /// </summary>
    public override string ToString() => ProjectCount > 0
        ? $"phase {PhaseNumber} of {PhaseCount}: {Phase} -- project {Project} of {ProjectCount} ({ProjectName})"
        : $"phase {PhaseNumber} of {PhaseCount}: {Phase}";
}

/// <summary>How long one phase of a finished build took.</summary>
/// <remarks>Public for the reason BuildProgress is: GraphBuilder.PhaseTimings hands it to the front ends.</remarks>
public sealed record BuildPhaseTiming(string Name, double Seconds);

/// <summary>
/// The phase names, in the order a build runs them. Constants rather than
/// literals at each call site so the plan and the calls that walk it cannot
/// drift apart by a typo.
/// </summary>
internal static class BuildPhases
{
    public const string LoadSolution = "load solution";
    public const string LoadProject = "load project";
    public const string Compile = "compile";
    public const string CheckErrors = "check compile errors";
    public const string Symbols = "symbols";
    public const string Calls = "calls, member access, quoted names and literals";
    public const string Escapes = "exception escapes";
    public const string Razor = "razor, client assets and xaml";
    public const string Links = "cross-project links";
    public const string Coverage = "coverage";
    public const string TextFiles = "text files";

    /// <summary>build_solution and build-solution: every project, compiled together.</summary>
    public static readonly IReadOnlyList<string> Solution =
    [
        LoadSolution, Compile, CheckErrors, Symbols, Calls, Escapes, Razor, Links, Coverage, TextFiles
    ];

    /// <summary>build_graph and build: one project, from a .csproj or from inside a solution.</summary>
    public static readonly IReadOnlyList<string> Project =
    [
        LoadProject, Compile, CheckErrors, Symbols, Calls, Escapes, Razor, Links, TextFiles
    ];
}

/// <summary>
/// Times a build phase by phase and tells a listener as each one starts. The
/// one progress seam: GraphBuilder and RoslynExtractor call it, and whatever
/// front end is listening hears it through GraphBuilder.Progress.
/// </summary>
/// <remarks>
/// A phase ends when the next begins, or at Finish. Reports go out synchronously
/// on the build's own thread, in order; a listener that wants them elsewhere
/// does its own hand-off. A phase the plan does not name is a programming error
/// and throws, because a number like "phase 0 of 11" would be a lie told quietly.
/// </remarks>
internal sealed class BuildClock(IReadOnlyList<string> plan, IProgress<BuildProgress>? listener)
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Stopwatch _phase = new();
    private readonly List<BuildPhaseTiming> _timings = new();
    private string? _current;
    private int _currentNumber;

    public IReadOnlyList<BuildPhaseTiming> Timings => _timings;

    public void Begin(string phase)
    {
        var index = IndexOf(phase);
        Close();
        _current = phase;
        _currentNumber = index + 1;
        _phase.Restart();
        listener?.Report(new BuildProgress(phase, _currentNumber, plan.Count, _total.Elapsed));
    }

    /// <summary>Project <paramref name="number"/> of <paramref name="count"/>, inside the current phase.</summary>
    public void Project(int number, int count, string name)
    {
        if (_current is null) throw new InvalidOperationException("Project progress reported outside any phase.");
        listener?.Report(new BuildProgress(_current, _currentNumber, plan.Count, _total.Elapsed, number, count, name));
    }

    /// <summary>Close the last phase. Idempotent, so a caller cannot double-count one.</summary>
    public void Finish() => Close();

    private void Close()
    {
        if (_current is null) return;
        _timings.Add(new BuildPhaseTiming(_current, Math.Round(_phase.Elapsed.TotalSeconds, 3)));
        _current = null;
    }

    private int IndexOf(string phase)
    {
        for (var i = 0; i < plan.Count; i++)
            if (plan[i] == phase) return i;
        throw new InvalidOperationException($"Phase '{phase}' is not in this build's plan.");
    }
}
