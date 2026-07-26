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

    Unknown
}
