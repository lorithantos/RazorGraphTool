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

/// <summary>Built only by a static field initializer, so nothing but that walk can see it.</summary>
public sealed class StaticPart : IPart
{
    public int Value => 3;
}

/// <summary>Built only inside an explicit static constructor body.</summary>
public sealed class FallbackPart
{
    public int Value => 4;
}

/// <summary>
/// A STATIC composition root, which the instance walk cannot see at all.
///
/// <para>
/// Neither member below is a method, and the static constructor that actually
/// runs them is not a graph node — an implicit one has no syntax to be a node of,
/// and an explicit one is deliberately not minted. So both constructions are
/// attributed to this TYPE, which is the one shape of Constructs edge whose near
/// end is not a member, and the only honest place left to put it.
/// </para>
///
/// <para>
/// `private static readonly IFoo Default = new Foo();` is a common enough
/// composition-root idiom that missing it left real types reading as dead — a
/// live one was found in RazorGraph's own Lua checker the hour this shipped.
/// </para>
/// </summary>
public static class PartRegistry
{
    /// <summary>A static field initializer: runs in the static ctor, has no member of its own.</summary>
    public static readonly IPart Default = new StaticPart();

    /// <summary>
    /// A static field initializer that INVOKES rather than constructs — the
    /// Calls twin of <see cref="Default"/>. The method it calls has no other
    /// caller, so without an edge sourced at the type it reads as dead.
    /// </summary>
    public static readonly int Seed = ComputeSeed();

    /// <summary>
    /// An EXPLICIT static constructor. It IS walked — it is a method declaration
    /// like any other — and its edges were still dropped, because the node they
    /// would start from does not exist.
    /// </summary>
    static PartRegistry()
    {
        Fallback = new FallbackPart();
        Warm();
    }

    public static FallbackPart Fallback { get; }

    /// <summary>Called only from a static field initializer.</summary>
    private static int ComputeSeed() => 7;

    /// <summary>Called only from the explicit static constructor body.</summary>
    private static void Warm() { }
}

/// <summary>
/// A type a test only ever BUILDS, whose static field initializer calls a method
/// nothing else calls -- gamehub's GuardRetreatWorld.DrawnBoard shape. It declares
/// no constructor and no instance initializer, so there is no constructor node to
/// call: the Constructs edge is the only step from a test onto the type, and the
/// type-sourced Calls edge the only step from the type to DrawBoard.
/// </summary>
public sealed class DrawnWorld
{
    private static readonly int Board = DrawBoard();

    public int Size => Board;

    /// <summary>Called only from the static field initializer.</summary>
    private static int DrawBoard() => 3;
}

/// <summary>
/// An interface a test can touch with no implementation running: a static member
/// needs no instance, so nothing is built and nothing dispatches. Its one
/// implementer is <see cref="StampedWorld"/>.
/// </summary>
public interface IWorldStamp
{
    static int Version() => 1;
}

/// <summary>
/// Implements <see cref="IWorldStamp"/>, and its static field initializer calls
/// a method nothing else calls. Nothing builds it and nothing names it, so the
/// only route from a test to that initializer is the type-level Implements edge
/// off the interface -- which says what this type declares, not that anything
/// ran it. Touching an interface does not run an implementer's static
/// constructor, so a test reaching <see cref="IWorldStamp"/> must not cover
/// <see cref="Engrave"/>.
/// </summary>
public sealed class StampedWorld : IWorldStamp
{
    private static readonly int Stamp = Engrave();

    public int Size => Stamp;

    /// <summary>Called only from the static field initializer.</summary>
    private static int Engrave() => 4;
}

/// <summary>Built only inside a block-bodied property getter.</summary>
public sealed class GetterPart
{
    public int Value => 5;
}

/// <summary>Built only by an expression-bodied property.</summary>
public sealed class ArrowPart
{
    public int Value => 6;
}

/// <summary>Built only inside an indexer's getter.</summary>
public sealed class SlotPart
{
    public int Value => 7;
}

/// <summary>
/// Accessor bodies as a composition root. An accessor is not a method
/// declaration, so the method walk never reached one: every method below was
/// called only from a property or an indexer and read as uncalled, and every
/// type it builds read as never built. The edges start at the PROPERTY, because
/// accessors are not graph nodes.
/// </summary>
public sealed class PartShelf
{
    private GetterPart? _cached;

    /// <summary>A block-bodied getter that calls, under a guard, and constructs.</summary>
    public GetterPart Cached
    {
        get
        {
            try
            {
                Restock();
            }
            catch (InvalidOperationException)
            {
            }
            return _cached ??= new GetterPart();
        }
    }

    /// <summary>An expression-bodied property that calls and constructs.</summary>
    public ArrowPart Fresh => Label(new ArrowPart());

    /// <summary>An indexer whose getter calls and constructs.</summary>
    public SlotPart this[int slot]
    {
        get
        {
            Check(slot);
            return new SlotPart();
        }
    }

    /// <summary>Called only from <see cref="Cached"/>'s getter.</summary>
    private static void Restock() { }

    /// <summary>Called only from <see cref="Fresh"/>'s expression body.</summary>
    private static ArrowPart Label(ArrowPart part) => part;

    /// <summary>Called only from the indexer's getter.</summary>
    private static void Check(int slot) { }
}

/// <summary>Built only inside <see cref="PartRack"/>'s int indexer.</summary>
public sealed class NumberedPart
{
    public int Value => 8;
}

/// <summary>Built only inside <see cref="PartRack"/>'s string indexer.</summary>
public sealed class NamedPart
{
    public int Value => 9;
}

/// <summary>
/// Two indexers on one type, overloaded on the parameter type. Every indexer
/// is named this[] whatever its parameters, so an id built from the name alone
/// gives both the same node. Each getter calls and builds something the other
/// does not, so a merge shows as both getters' edges leaving one node, and a
/// drop as one indexer with no node of its own.
/// </summary>
public sealed class PartRack
{
    public NumberedPart this[int slot]
    {
        get
        {
            BySlot(slot);
            return new NumberedPart();
        }
    }

    public NamedPart this[string name]
    {
        get
        {
            ByName(name);
            return new NamedPart();
        }
    }

    /// <summary>Called only from the int indexer's getter.</summary>
    private static void BySlot(int slot) { }

    /// <summary>Called only from the string indexer's getter.</summary>
    private static void ByName(string name) { }
}

/// <summary>Reads each of <see cref="PartRack"/>'s indexers from its own method.</summary>
public static class PartRackReader
{
    public static int First(PartRack rack) => rack[0].Value;

    public static int Primary(PartRack rack) => rack["primary"].Value;
}
