namespace TopLevelApp;

public sealed class Nav
{
    /// <summary>Read from the top-level statements, to exercise access attribution.</summary>
    public int Budget { get; set; }

    public IReadOnlyList<int> FindPath(int from, int to)
    {
        var path = new List<int>();
        for (var i = from; i <= to; i++) path.Add(Advance(i));
        return path;
    }

    public int Advance(int step) => step + 1;

    public int Accumulate(IEnumerable<int> steps)
    {
        var total = 0;
        foreach (var s in steps) total += s;
        return total;
    }

    public void Risky() => throw new InvalidOperationException("boom");

    /// <summary>Called from nowhere — the control for genuinely unreached code.</summary>
    public int NeverCalled() => -1;
}
