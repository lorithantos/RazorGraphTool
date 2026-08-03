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

    Unknown
}
