namespace SampleApp.Infrastructure;

/// <summary>
/// Fixture stand-in for a codebase-specific registration attribute: the generic
/// type argument names the service this class is registered as. Declared inside
/// the fixture so the graph must resolve DecoratedBy to the type's own node
/// rather than minting an external one.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RegisterServiceAttribute<TService> : Attribute
{
    public RegisterServiceAttribute() { }

    public RegisterServiceAttribute(string[]? tags) => Tags = tags;

    public string[]? Tags { get; }
}
