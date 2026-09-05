namespace SampleWeb;

using SampleLib;

/// <summary>
/// Cross-project consumer of SampleLib's C# 14 extension members — the case
/// this fixture solution exists for, and the shape the defect was found in: a
/// loader whose call sites all lived in other projects reported no consumers at
/// all, so the visibility audit called the type unused.
///
/// <para>
/// Deliberately not a page handler and not an entry point, so the escape
/// sweep's asserted count is untouched.
/// </para>
/// </summary>
public static class PriceUse
{
    public static bool FreeAcrossTheBoundary(Price price) => price.IsFree;

    public static Price DoubledAcrossTheBoundary(Price price) => price.Doubled();

    public static Price MadeAcrossTheBoundary(int cents) => Price.FromCents(cents);
}
