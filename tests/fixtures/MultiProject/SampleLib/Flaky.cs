namespace SampleLib;

/// <summary>
/// The DI-shaped fixture: callers bind to the interface, the throw lives in
/// the implementation — ASP.NET's default architecture. Before interface
/// widening, every escape chain died at this boundary. Nothing else in the
/// fixtures calls these members directly; the point is the dispatch.
/// </summary>
public interface IFlaky
{
    int Risky();
}

public class FlakyService : IFlaky
{
    public int Risky() => Throwing.UnguardedThrow();
}
