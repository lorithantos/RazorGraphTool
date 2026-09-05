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

public class Greeter : IGreeter
{
    public string Greet(string name) => Shape(name);

    /// <summary>One hop past the implementation, so depth keeps counting after the dispatch step.</summary>
    private static string Shape(string name) => $"hello {name}";
}
