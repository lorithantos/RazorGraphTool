namespace SampleLib;

/// <summary>
/// The construction fixture: a composition root and the types it builds.
///
/// <para>
/// The shape that motivated the edge is <see cref="PlainPart"/> — no explicit
/// constructor, so the compiler supplies one, so no <c>.ctor</c> node exists, so
/// the Calls edge a new-expression already produced had nothing to land on and
/// was dropped. A type built by the running program had no incoming edge of any
/// kind and read as dead.
/// </para>
///
/// <para>
/// Each member of <see cref="PartFactory"/> is one construction shape. Three of
/// them must produce NO edge, and those are the assertions worth having: a walk
/// that mints an edge for typeof or for an unconstructed type parameter is not
/// more complete, it is wrong in a way that reads as thorough.
/// </para>
/// </summary>
public sealed class PlainPart
{
    public int Value => 1;
}

/// <summary>
/// A primary constructor with a defaulted parameter. Different syntax from a
/// compiler-supplied constructor and it reaches the same hole, so it is pinned
/// separately rather than assumed to follow.
/// </summary>
public sealed class PrimaryPart(int size = 4)
{
    public int Size => size;
}

/// <summary>Generic, so the edge has to find the DEFINITION and not the constructed form.</summary>
public sealed class Holder<T>
{
    public T? Item { get; set; }
}

/// <summary>Reached only through the interface, which is the point of the pair.</summary>
public interface IPart
{
    int Value { get; }
}

/// <summary>The implementation a composition root picks out of several candidates.</summary>
public sealed class SeededPart : IPart
{
    public int Value => 2;
}

/// <summary>
/// The composition root. Nothing outside names the concrete types it builds, so
/// construction is the only relation that can say which implementation runs.
/// </summary>
public sealed class PartFactory
{
    // A field initializer runs inside the constructor, not inside any method, so
    // the method walk never sees it. This IS the composition-root idiom.
    private readonly PlainPart _seed = new PlainPart();

    public PlainPart Seed() => _seed;

    /// <summary>Construction behind an interface return: the candidate list cannot say this.</summary>
    public IPart Pick() => new SeededPart();

    public PrimaryPart Sized() => new PrimaryPart(3);

    /// <summary>An object initialiser hangs off the creation node, so this is ONE site, not two.</summary>
    public Holder<int> Held() => new Holder<int> { Item = 5 };

    /// <summary>A lambda is not a scope of its own, so this is attributed here.</summary>
    public System.Func<IPart> Later() => () => new SeededPart();

    /// <summary>Names a type without building one. Must mint no Constructs edge.</summary>
    public System.Type Named() => typeof(PlainPart);

    /// <summary>
    /// No constructor symbol resolves here, so there is nothing to point at. An
    /// edge to the constraint would name a type this call site does not build.
    /// </summary>
    public static T Fresh<T>() where T : new() => new T();
}
