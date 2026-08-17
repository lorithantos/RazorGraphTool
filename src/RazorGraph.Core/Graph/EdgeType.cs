namespace RazorGraph.Core.Graph;

public enum EdgeType
{
    // Structural
    Contains,
    Inherits,
    Implements,

    // Razor rendering
    PageServedBy,
    UsesLayout,
    RendersPartial,
    RendersComponent,
    DefinesSection,
    HasModel,

    // Data flow
    InjectedInto,
    ReturnsView,
    BindsTo,
    Reads,
    Writes,
    Calls,

    // Routing
    MapsToRoute,
    HandlesHttpMethod,

    // ViewData
    ViewDataWrittenBy,
    ViewDataReadBy,

    // Server-rendered element ids reached by a script's literal selectors,
    // carrying the shared ids. Same family as ViewDataReadBy: state crossing
    // the server/client boundary by name, where a rename breaks one side only.
    DomSelectedBy,

    // Cross-layer
    UrlGeneratedBy,

    // Dependencies
    References,
    DependsOn,

    // Testing. Emitted from a test method to production code it exercises,
    // carrying the call depth at which it was reached, so consumers can ask for
    // direct exercise (depth 1) or the full blast radius.
    Covers,

    // Exception flow. Emitted from a throwing method to an application entry
    // point its exception can reach with no catch stopping it, carrying the
    // exception type, the hop depth, one representative path, and whether the
    // only handling en route was a filtered catch (conditional).
    Escapes,

    // Named binding points. Produces runs from the code that names a binding to
    // the name itself; BoundBy from the name to each template or handler that
    // could serve it, carrying rank and the kind of binding. Kept as two edges
    // rather than one code->template edge because resolution is one-to-many and
    // precedence-ordered: with themes and tenants in play, which candidate wins
    // is not statically decidable, so the graph shows all of them and declines
    // to pick.
    Produces,
    BoundBy,

    // Annotation. Emitted from a decorated node -- type, method, property, field, parameter or
    // project -- to the attribute's type, carrying the arguments as written, the line, and which
    // target the attribute actually hit. One edge per USAGE, not per attribute type: [Theory]
    // with twenty [InlineData] is twenty-one edges, and without the line they would be
    // indistinguishable from one another. The target matters because a record's primary
    // constructor parameter is a parameter and a property at once, and an attribute on it
    // reaches only one of them.
    DecoratedBy,

    // Registration. Emitted from a decorated node to a type an attribute names -- typeof(...)
    // or a generic attribute's type argument -- because the framework will construct or consult
    // that type on account of the annotation, with no call site anywhere.
    //
    // Deliberately NOT folded into References. That edge answers exactly one question today,
    // "which member declarations mention this type", and its value is that an empty incoming
    // set proves nothing declares one. Mixing registrations in would end that proof while every
    // existing traversal silently absorbed the new edges. Kept apart, it also earns its place:
    // a type reachable only this way has no caller and would otherwise read as dead.
    Registers,

    // Extension surface. Emitted from an extension method to the in-solution
    // type it extends: the method is part of that type's working surface even
    // though containment says it lives on a static class elsewhere.
    Extends,

    Unknown
}
