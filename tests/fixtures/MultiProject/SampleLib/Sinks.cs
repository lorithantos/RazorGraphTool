namespace SampleLib;

/// <summary>
/// The type-level implementation fixture, and the third in the family beside
/// the IGreeter one (interface dispatch) and the Doctrine one (class dispatch).
///
/// <para>
/// Those two pin the MEMBER relation: which body runs when a caller binds to a
/// declaration. This one pins the TYPE relation, which is the different
/// question a reader actually asks first -- "which classes implement this
/// interface" -- and which returned a well-formed zero until 2026-09-11.
/// </para>
///
/// <para>
/// Every shape here exists because it reaches the interface by a DIFFERENT
/// route, and a walk that handles one route and not the others produces a
/// partial answer that reads exactly like a complete one. Nothing in this file
/// is named by any other fixture, so the depths asserted on it stay exact.
/// </para>
/// </summary>
public interface ISink
{
    /// <summary>
    /// A PROPERTY on the interface, deliberately. Implementation ids were
    /// collected for methods only, and an interface property's implementation
    /// was invisible -- accessors are not MethodKind.Ordinary, so the member
    /// walk skipped them and nothing said it had.
    /// </summary>
    string Channel { get; }

    void Write(int value);
}

/// <summary>
/// A derived interface, so an implementer reaches <see cref="ISink"/> without
/// ever naming it. This is the route a "directly declared only" walk misses.
/// </summary>
public interface IAuditSink : ISink
{
    void Seal();
}

/// <summary>
/// A generic interface. Its node id carries angle brackets, which is what an
/// agent copies out of a JSON response when it asks who implements this.
/// </summary>
public interface ITally<T>
{
    T Total();
}

/// <summary>Implements <see cref="ISink"/> outright: the depth-1 case.</summary>
public abstract class SinkBase : ISink
{
    public abstract string Channel { get; }

    public abstract void Write(int value);
}

/// <summary>
/// Reaches <see cref="ISink"/> ONLY through its base class. Nothing in this
/// declaration names the interface, so a walk reading the declared list alone
/// reports it as implementing nothing.
/// </summary>
public sealed class RelaySink : SinkBase
{
    public override string Channel => "relay stream";

    public override void Write(int value)
    {
    }
}

/// <summary>
/// Reaches <see cref="ISink"/> through a base INTERFACE rather than a base
/// class -- the same omission by a second route, and the one that survives
/// when someone fixes only the first.
/// </summary>
public sealed class LedgerSink : IAuditSink
{
    public string Channel => "ledger stream";

    public void Write(int value)
    {
    }

    public void Seal()
    {
    }
}

/// <summary>
/// The control. <see cref="System.IComparable"/> is out of solution, so it has
/// no node to point at and must produce NO edge -- while the in-solution
/// interface on the same declaration must still produce one. A walk that emits
/// an edge per declared interface regardless of whether the far end exists
/// fails here and nowhere else.
/// </summary>
public sealed class ScratchSink : ISink, System.IComparable
{
    public string Channel => "scratch stream";

    public void Write(int value)
    {
    }

    public int CompareTo(object? obj) => 0;
}

/// <summary>
/// Explicit implementation. The members carry the interface name in their own,
/// so a walk matching implementations by simple name misses both of them while
/// the type still genuinely implements the interface.
/// </summary>
public sealed class HiddenSink : ISink
{
    string ISink.Channel => "hidden stream";

    void ISink.Write(int value)
    {
    }
}

/// <summary>Closes the generic interface, so the edge crosses a constructed type.</summary>
public sealed class CountTally : ITally<int>
{
    public int Total() => 0;
}
