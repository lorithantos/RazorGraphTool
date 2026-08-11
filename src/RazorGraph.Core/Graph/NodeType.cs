namespace RazorGraph.Core.Graph;

public enum NodeType
{
    Project,
    RazorPage,
    PageModel,
    ApiController,
    ControllerAction,
    PartialView,
    ViewComponent,
    Layout,
    Service,
    ServiceInterface,
    ServiceImplementation,
    ViewModel,
    Class,
    Method,
    Property,
    Field,
    ViewDataKey,

    /// <summary>
    /// A name that code and templates couple through, where resolution happens by
    /// convention at runtime rather than by symbol at compile time: an MVC view
    /// name, an OrchardCore shape. Producers point at it, candidate bindings hang
    /// off it, and a name with no binding is a runtime failure waiting to happen.
    /// </summary>
    NamedBinding,

    /// <summary>
    /// A configuration file that takes part in the structure the graph models,
    /// rather than one that merely configures a service: an OrchardCore
    /// placement.json decides which shapes render, under which extra names, and
    /// which are dropped entirely. Config that can break rendering is code for
    /// this tool's purposes, and a name it introduces must resolve like any other.
    /// The framework-specific meaning rides on a 'kind' property, the same way
    /// NamedBinding carries orchardCoreShape.
    /// </summary>
    ConfigurationFile,
    Middleware,
    Route,
    HtmlElement,
    TagHelperInvocation,
    JavaScriptFile,
    CssFile,
    Unknown
}
