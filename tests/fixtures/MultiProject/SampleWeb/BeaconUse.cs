namespace SampleWeb;

using SampleLib;

/// <summary>
/// Cross-project consumer of the Beacon types, so the visibility audit treats
/// both types as required and looks at their members one by one rather than
/// reporting the types themselves. Neither property is named here: that is what
/// leaves them for the member-level rules to judge.
///
/// <para>
/// Deliberately not a page handler and not an entry point, so the escape
/// sweep's asserted count is untouched.
/// </para>
/// </summary>
public static class BeaconUse
{
    public static Beacon SteadyAcrossTheBoundary() => new Beacon();

    public static PulseBeacon PulsingAcrossTheBoundary() => new PulseBeacon();
}
