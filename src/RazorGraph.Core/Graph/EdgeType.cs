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
    ReturnsPartial,
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

    // Cross-layer
    UrlGeneratedBy,
    ServiceSharedBy,
    PartialRenderedBy,

    // Dependencies
    References,
    DependsOn,

    // Testing. Emitted from a test method to production code it exercises,
    // carrying the call depth at which it was reached, so consumers can ask for
    // direct exercise (depth 1) or the full blast radius.
    Covers,

    Unknown
}
