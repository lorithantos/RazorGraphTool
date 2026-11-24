namespace SampleLib;

/// <summary>Injected as <c>IAuditTrail?</c>: an optional seam, still a seam.</summary>
public interface IAuditTrail { }

/// <summary>A generic seam, injected below with the annotation inside and outside.</summary>
public interface IShelf<T> { }

/// <summary>
/// Every constructor parameter carries a nullable reference annotation, in each
/// place one can sit. The project has nullable enabled, so the parameter type's
/// display string keeps the '?', and no node's full name ever has one: matched
/// as written, none of the three found its node and the type read as injected
/// with nothing.
/// </summary>
public sealed class OptionalAuditor
{
    public OptionalAuditor(IAuditTrail? audit, IShelf<PlainPart?> parts, IShelf<SeededPart>? spares) { }
}
