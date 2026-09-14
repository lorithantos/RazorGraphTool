namespace SampleLib;

/// <summary>
/// The visibility-audit fixture for an OVERRIDE PROPERTY, and the property twin
/// of Greeter.ToString.
///
/// <para>
/// An override cannot be less visible than the declaration it overrides
/// (CS0507), so the audit must never offer one for narrowing. It decides that
/// by reading isOverride off the node -- which Method nodes carried and Property
/// nodes did not, so a public override property on a public type was offered as
/// an edit that cannot compile. Both types here are consumed by SampleWeb on
/// purpose: an unconsumed type is reported in its own right and its members are
/// folded into it, which would hide the property before the override guard ever
/// saw it.
/// </para>
/// </summary>
public class Beacon
{
    public virtual string Signal => "steady";
}

/// <summary>
/// Overrides <see cref="Beacon.Signal"/>: the member the audit must not offer.
/// Cadence is the control -- a plain property on the same type, offered before
/// and after, so a test can tell an override excluded by its flag from a member
/// hidden by its owner.
/// </summary>
public sealed class PulseBeacon : Beacon
{
    public override string Signal => "pulse";

    public string Cadence => "fast";
}
