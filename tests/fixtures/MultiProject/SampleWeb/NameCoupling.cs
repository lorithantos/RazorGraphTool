using System.ComponentModel;

namespace SampleWeb;

/// <summary>
/// Fixture for Quotes edges. Every method here names a SampleLib declaration,
/// or deliberately names nothing, and each does it in a different form -- the
/// forms are what the provenance property has to tell apart.
/// </summary>
public class NameCoupling
{
    /// <summary>A typed literal naming another project's type: the breakable form.</summary>
    public string TypedName() => "CatalogStore";

    /// <summary>The same name, in the form a rename carries along.</summary>
    public string SafeName() => nameof(SampleLib.CatalogStore);

    /// <summary>
    /// An interpolated segment that is exactly a name. Segments are whole runs
    /// of text, so this matches and "call Normalize now" would not.
    /// </summary>
    public string Interpolated(string prefix) => $"{prefix}Normalize";

    /// <summary>Names no declaration anywhere in the solution, so it is not an edge.</summary>
    public string Prose() => "nothing in this sentence is declared";

    /// <summary>
    /// Names something several declarations share, so the graph cannot say which
    /// is meant and says nothing. Usually the author meant a framework member
    /// with no node at all, as here.
    /// </summary>
    public string AmbiguousName() => "Dispose";

    /// <summary>An attribute argument: read by a framework, not by this assembly.</summary>
    [Description("PriceBook")]
    public string Attributed() => "ok";
}

/// <summary>
/// Exists to give the solution a SECOND Dispose. One declaration of a name is a
/// claim the graph can support; two make the name say nothing, which is what
/// AmbiguousName above is testing.
/// </summary>
public sealed class Scratch : System.IDisposable
{
    public void Dispose() { }
}
