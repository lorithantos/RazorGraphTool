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

    /// <summary>
    /// One DISTINCT string value some member produces, deduplicated across the whole
    /// graph. Its Name is the value itself, so a name search over the graph is a
    /// search over literal text with no separate index.
    /// </summary>
    /// <remarks>
    /// It carries no filePath, line or project, for the same reason ExternalType does:
    /// a value used in five places has no single site. The sites are the incoming
    /// Mentions edges, each carrying its own line and provenance.
    ///
    /// Deliberately NOT the target of a Quotes edge. Quotes answers "which strings name
    /// a declaration", the rename-safety question, and it is only answerable because
    /// unmatched strings are dropped. These nodes are the strings themselves — every
    /// one of them, prose included — which is a different question and has to stay a
    /// different edge.
    /// </remarks>
    Literal,

    Middleware,
    Route,
    HtmlElement,
    TagHelperInvocation,
    JavaScriptFile,
    CssFile,

    /// <summary>
    /// A XAML file: a window, a page, a resource dictionary, a template set.
    /// </summary>
    /// <remarks>
    /// A real member rather than a foreign kind, for the reason stated above about
    /// Lua: this extractor owns the format, so a kind it emits itself is one it
    /// models. The view layer of a desktop app was previously absent from the
    /// graph entirely — measured on one app, 8 files and 248 bindings contributing
    /// nothing — which made every member reachable only from markup read as used
    /// by tests alone.
    /// </remarks>
    XamlView,

    Unknown
}
