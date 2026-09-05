namespace SampleLib;

/// <summary>
/// Interface-dispatch coverage fixture. The test binds to IGreeter.Greet and
/// never names Greeter.Greet, so the implementation is reachable only through
/// the interface map. Kept apart from ICatalogStore so the depth asserts on
/// CatalogStore stay exact.
/// </summary>
public interface IGreeter
{
    string Greet(string name);
}

/// <summary>
/// Attributed on purpose: attributes are part of the declaration node, so a
/// node's line used to be the attribute's. DebuggerDisplay is inert -- it warns
/// about nothing and changes no extraction.
/// </summary>
[System.Diagnostics.DebuggerDisplay("greeter")]
public class Greeter : IGreeter
{
    public string Greet(string name) => Shape(name);

    /// <summary>One hop past the implementation, so depth keeps counting after the dispatch step.</summary>
    private static string Shape(string name) => $"hello {name}";

    /// <summary>An override: its accessibility is object.ToString's, not this type's to narrow.</summary>
    public override string ToString() => "greeter";
}
