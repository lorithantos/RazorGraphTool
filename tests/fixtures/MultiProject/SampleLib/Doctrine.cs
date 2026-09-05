namespace SampleLib;

/// <summary>
/// The class-dispatch fixture, and the twin of the IGreeter one.
///
/// <para>
/// A caller holds the abstract base and calls <c>Advance</c>; the body that
/// runs is an override in a derived class. Nothing calls the override by its
/// own name, so it is reachable ONLY across the override relation — which is
/// exactly the shape that reported zero covering tests until 2026-09-05, and
/// exactly the shape a doctrine or strategy layer is made of.
/// </para>
///
/// <para>
/// Two levels deep on purpose. An override can itself be overridden, so a test
/// reaching the abstract root has to reach the leaf that actually runs; a walk
/// that stopped at one hop would call the middle covered and the leaf untested.
/// </para>
/// </summary>
public abstract class Doctrine
{
    /// <summary>The declaration every caller binds to.</summary>
    public abstract int Advance(int steps);
}

/// <summary>The middle of the chain: overrides the root, and is overridden itself.</summary>
public class PatrolDoctrine : Doctrine
{
    public override int Advance(int steps) => steps + 1;
}

/// <summary>The leaf. Nothing names it but the runtime.</summary>
public sealed class EscortDoctrine : PatrolDoctrine
{
    public override int Advance(int steps) => steps + 2;
}

/// <summary>
/// Holds the base type and calls through it, so the call edge lands on
/// Doctrine.Advance and never on either override.
/// </summary>
public static class DoctrineRunner
{
    public static int Run(Doctrine doctrine, int steps) => doctrine.Advance(steps);
}
