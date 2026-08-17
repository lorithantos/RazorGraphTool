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
    /// <summary>
    /// A type the compilation referenced but does not contain: an attribute from xUnit or
    /// ASP.NET, and later any out-of-solution base type or interface. Carries the declaring
    /// assembly and no file position, because there is no source to point at.
    /// </summary>
    /// <remarks>
    /// A real node kind rather than the Unknown+foreign-kind pair the Lua extractor uses. Lua
    /// is deliberately a foreign-vocabulary producer; this extractor owns the format, so
    /// emitting foreign kinds from it would mark every C# graph as holding data its own writer
    /// did not model -- untrue, and frequent enough to make the warning worthless.
    ///
    /// A type declared inside the solution never becomes one of these: it already has its own
    /// node, and reusing that is what lets one node answer both what an attribute decorates and
    /// who calls it.
    /// </remarks>
    ExternalType,

    /// <summary>
    /// One parameter of a method, emitted ONLY when it carries an attribute -- the runtime
    /// supplying an argument ([FromBody], [FromServices]) is a fact about the method's contract
    /// that no call site witnesses.
    /// </summary>
    /// <remarks>
    /// The restriction is the point: a node for every parameter would cost methods x arity for
    /// a signal measured at nothing to a few dozen per solution. Because a parameter node exists
    /// BECAUSE it is decorated, an absent one means undecorated, never unmodelled. Widening this
    /// later is additive; narrowing it would not be.
    /// </remarks>
    Parameter,

    Middleware,
    Route,
    HtmlElement,
    TagHelperInvocation,
    JavaScriptFile,
    CssFile,
    Unknown
}
