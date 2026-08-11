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
    Middleware,
    Route,
    HtmlElement,
    TagHelperInvocation,
    JavaScriptFile,
    CssFile,
    Unknown
}
