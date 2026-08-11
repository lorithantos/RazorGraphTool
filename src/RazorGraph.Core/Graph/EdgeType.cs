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

    // Extension surface. Emitted from an extension method to the in-solution
    // type it extends: the method is part of that type's working surface even
    // though containment says it lives on a static class elsewhere.
    Extends,

    Unknown
}
