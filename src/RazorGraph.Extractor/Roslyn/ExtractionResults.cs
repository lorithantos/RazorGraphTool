namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using RazorGraph.Core.Graph;

/// <summary>
/// One resolved call site, caller to callee by method id, with the exception
/// guards enclosing the site. <paramref name="GuardedBy"/> holds types caught
/// firmly by enclosing trys ("*" for an untyped catch-all);
/// <paramref name="FilteredBy"/> holds types caught only behind a when filter.
/// <paramref name="IsDelegate"/> marks a method-group reference rather than an
/// invocation — a call the future makes, always unguarded. A named type
/// rather than a tuple so the two ids cannot be swapped silently at a call
/// site.
/// </summary>
/// <remarks>
/// Exactly one of <paramref name="FromId"/> and <paramref name="FromTypeFullName"/>
/// is set, the same split <see cref="ConstructionSiteInfo"/> makes and for the
/// same reason: a call in static initialization — a static field or property
/// initializer, or an explicit static constructor body — has no member to
/// attribute to, because a static constructor is not a graph node, so it is
/// attributed to the declaring TYPE by name, which only the emitter can turn
/// into an id.
/// </remarks>
internal sealed record CallSiteInfo(
    string? FromId,
    string ToId,
    IReadOnlyList<string> GuardedBy,
    IReadOnlyList<string> FilteredBy,
    bool IsDelegate = false,
    string? FromTypeFullName = null);

/// <summary>
/// One place a method builds an instance of an in-solution type: the member
/// holding the new-expression, the type's full name, and the line.
/// </summary>
/// <remarks>
/// The type's name rather than a node id, for the same reason MemberDetail
/// carries ReferencedTypeFullNames: the id prefix depends on how the type
/// classified (type:, vm:, pm:), so only the emitter — which owns the name→id
/// map — can turn a name into an id.
/// </remarks>
/// <remarks>
/// Exactly one of <paramref name="FromId"/> and <paramref name="FromTypeFullName"/>
/// is set. A construction in ordinary code is attributed to the MEMBER holding it;
/// one in static initialization has no member to attribute to — a static
/// constructor is not a graph node, and an implicit one has no syntax a reader
/// could open — so it is attributed to the declaring TYPE, whose name the emitter
/// resolves through the same map it uses for the far end.
/// </remarks>
internal sealed record ConstructionSiteInfo(
    string? FromId,
    string ToTypeFullName,
    int Line,
    string? FromTypeFullName = null);

/// <summary>
/// One resource a method disposes implicitly, and whether the disposal is
/// await using — which resolves DisposeAsync rather than Dispose.
/// </summary>
internal readonly record struct DisposedResource(ITypeSymbol Type, bool IsAsync);

/// <summary>
/// One interface a type implements, and the number of hops to the declaration
/// that brought it in: 1 when the interface is written in this type's own
/// declaration, more when it arrives through a base class or a base interface.
/// </summary>
/// <remarks>
/// The depth is the finding, not decoration. "Which interfaces does this class
/// itself declare" and "which interfaces does an instance of it satisfy" are
/// different questions, and <c>AllInterfaces</c> answers only the second — so a
/// flat list cannot tell a declared interface from an inherited one, and the
/// reader who wanted the first gets a fuller answer that is also a wrong one.
/// Carrying the distance keeps both askable, the same way Covers and Overrides
/// carry theirs.
/// </remarks>
internal sealed record InterfaceImplementation(string FullName, int Depth);

/// <summary>
/// What kind of thing a constructor parameter's type is. This is the fact the
/// flat string list this replaced could not carry: it named a <c>double</c> and
/// an interface in the same breath, and a reader could not tell a seam from a
/// number. Scope decides among Interface, Type and Framework; the TYPE alone
/// decides Value, wherever it is declared.
/// </summary>
/// <remarks>
/// An in-solution enum is Value, not Type, and that is deliberate: it is what
/// stops an enum from reading as a dependency. Neither an enum nor a struct
/// ever gets an InjectedInto edge -- the enum because the type walk emits no
/// node for it at all, the struct because it is Value and Value is never
/// resolved -- so on those the property and the edges legitimately disagree:
/// the entry is recorded, and nothing points at the type. That is a modelling
/// gap in the node vocabulary, not a classification error.
/// </remarks>
internal enum DependencyKind
{
    /// <summary>
    /// In scope and <c>TypeKind.Interface</c>. A substitution point: a seam with
    /// a candidate set of implementations.
    /// </summary>
    Interface,

    /// <summary>In scope and not an interface. A hard-wired dependency on a concrete type.</summary>
    Type,

    /// <summary>
    /// Out of scope and a reference type: ILogger&lt;T&gt;, IConfiguration,
    /// TimeProvider, a collection. A real dependency, declared outside this
    /// solution. Whether a container actually supplies it depends on
    /// registrations the extractor cannot see, so this says "declared
    /// elsewhere", not "injectable".
    /// </summary>
    Framework,

    /// <summary>
    /// A value type, an enum, or string, wherever it is declared. Required by the
    /// constructor, and not satisfiable by the container's default constructor
    /// injection: the container has nothing to hand it. A type carrying one is
    /// therefore either registered with a factory (services.AddSingleton(sp =>
    /// new Foo(42)) passes whatever it likes), constructed explicitly -- new, or
    /// ActivatorUtilities with arguments -- or not container-constructed at all.
    /// The kind asserts none of those; it says only that default activation
    /// cannot be how this parameter is filled.
    /// </summary>
    Value
}

/// <summary>
/// One parameter of a type's first public instance constructor, by type, with
/// what kind of thing that type is; see SymbolClassifier.ExtractConstructorDependencies.
/// </summary>
/// <param name="TypeFullName">
/// The parameter type's display string, constructed type arguments included, so
/// it is exactly the name AddInjectionEdges has always resolved edges by.
/// </param>
/// <param name="DefinitionFullName">
/// The open generic definition's display string when the parameter type is a
/// constructed generic -- <c>IRepository&lt;T&gt;</c> for a parameter typed
/// <c>IRepository&lt;CatalogItem&gt;</c> -- and null otherwise. The solution
/// declares the definition, never the construction, so this is the name its
/// node carries.
/// </param>
internal sealed record InjectedDependency(string TypeFullName, DependencyKind Kind, string? DefinitionFullName = null);

internal sealed class SymbolInfo
{
    public required string Id { get; init; }
    public required NodeType Type { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }

    /// <summary>Name of the project whose compilation declared this type.</summary>
    public string? Project { get; init; }

    public string? FilePath { get; init; }
    public int? LineStart { get; init; }
    public int? LineEnd { get; init; }

    /// <summary>First line of the XML doc comment, when the type has one.</summary>
    public int? DocsStart { get; init; }

    /// <summary>Last line of the XML doc comment, set exactly when DocsStart is.</summary>
    public int? DocsEnd { get; init; }

    /// <summary>Source file a generated type was compiled from (first #line directive); null for hand-written types.</summary>
    public string? GeneratedFrom { get; set; }

    /// <summary>
    /// Whether the type is reachable from outside its assembly — declared
    /// public, and nested only inside public types. A public type inside an
    /// internal one is not public in any way a consumer can use, and reporting
    /// it as such would make every visibility answer wrong at the edges.
    /// </summary>
    public bool IsPublic { get; init; }

    /// <summary>
    /// Whether the declaration is an interface. Interfaces are Class nodes unless DI
    /// registration promotes them to ServiceInterface, so without this flag thirty
    /// of an app's thirty-seven interfaces were indistinguishable from classes.
    /// </summary>
    public bool IsInterface { get; init; }

    /// <summary>
    /// Whether the declaration is a static type.
    /// </summary>
    /// <remarks>
    /// It is what makes a zero readable. A static class cannot be constructed by
    /// anyone, so an empty incoming set says nothing about it — measured on one
    /// stack, sixteen of seventeen zero-degree types were static classes, and the
    /// most-called type in the whole codebase was among them. Without the flag a
    /// reader has to grep declarations to tell "nothing builds this" from "nothing
    /// could", which is the text-search fallback the graph exists to remove.
    /// </remarks>
    public bool IsStatic { get; init; }

    public string? BaseType { get; init; }

    /// <summary>
    /// Every interface this type implements, and how far away each was declared;
    /// see SymbolClassifier.InterfaceImplementations.
    /// </summary>
    /// <remarks>
    /// Out-of-solution interfaces are KEPT here and dropped only when edges are
    /// emitted. The node listing IDisposable is a fact worth having; an edge to
    /// it would point at nothing.
    /// </remarks>
    public List<InterfaceImplementation> ImplementedInterfaces { get; init; } = new();

    /// <summary>
    /// The parameter types of the first public instance constructor, each
    /// classified; see SymbolClassifier.ExtractConstructorDependencies.
    /// </summary>
    /// <remarks>
    /// Framework and Value entries are KEPT. Dropping them would lose the fact
    /// that the constructor takes them; the kind is what marks them, and it is
    /// AddInjectionEdges that declines to resolve them.
    /// </remarks>
    public List<InjectedDependency> ConstructorDependencies { get; init; } = new();
    public List<PropertyInfo> Properties { get; init; } = new();
    public List<MethodInfo> Methods { get; init; } = new();

    /// <summary>Members promoted to their own graph nodes; see SymbolClassifier.ExtractMethodNodes.</summary>
    public List<MethodDetail> MethodNodes { get; init; } = new();

    /// <summary>Properties and fields promoted to their own graph nodes; see SymbolClassifier.ExtractMemberNodes.</summary>
    public List<MemberDetail> MemberNodes { get; init; } = new();

    /// <summary>Attributes written on the type itself; see SymbolClassifier.ExtractAttributes.</summary>
    public List<AttributeUsage> Attributes { get; init; } = new();
}

/// <summary>
/// One attribute as written at one site — not one attribute type. [Theory] with
/// twenty [InlineData] is twenty-one of these, and the line is what tells them
/// apart.
/// </summary>
/// <param name="FullName">
/// The attribute class's original definition, so a generic attribute
/// (RegisterDependency&lt;IFoo&gt;) names the same type as every other
/// instantiation of it rather than minting one per type argument — the trap
/// <see cref="SymbolIds.MethodId"/> already documents for methods.
/// </param>
/// <param name="Assembly">
/// Declaring assembly, which is what decides whether this resolves to a type the
/// solution declares or to an external one.
/// </param>
/// <param name="Target">
/// What the attribute actually landed on. Carried because it is not always the
/// obvious thing: [return: MarshalAs] sits on a method's declaration and applies
/// to its return value, and a record's primary-constructor parameter is a
/// parameter and a property at once.
/// </param>
/// <param name="UnresolvedReason">
/// Set only when the attribute class could not be bound. In compiling C# that
/// cannot happen — attribute arguments and types are resolved at compile time —
/// so its presence means the compilation had errors, which is a finding about the
/// build rather than about the code.
/// </param>
internal sealed record AttributeUsage(
    string FullName,
    string Name,
    string? Assembly,
    string Target,
    int? Line,
    string? UnresolvedReason = null)
{
    /// <summary>
    /// Positional arguments in declaration order, one slot per argument, never
    /// compacted — a slot that failed to evaluate holds null AND is named in
    /// <see cref="UnresolvedArgs"/>, because removing it would shift every later
    /// index. Values are already the shapes the serializer round-trips
    /// identically (string, int, long, double, bool, null, nested lists), read
    /// from TypedConstant rather than syntax so enum members and collection
    /// expressions arrive resolved instead of re-parsed.
    /// </summary>
    public List<object?>? Args { get; init; }

    /// <summary>Named arguments (Name = value). A map, because their order is not semantic.</summary>
    public Dictionary<string, object?>? Named { get; init; }

    /// <summary>
    /// Type arguments of a generic attribute (RegisterDependency&lt;IFoo&gt;), as
    /// display strings. On the usage rather than the node: the node is the open
    /// generic, one per attribute type, and the instantiation belongs to the
    /// site that wrote it.
    /// </summary>
    public List<string>? TypeArgs { get; init; }

    /// <summary>
    /// In-scope named types this usage names through typeof(...) arguments and
    /// generic type arguments, recursively (typeof(List&lt;MyDto&gt;) yields MyDto)
    /// — the raw material for Registers edges. Only in-scope types: a typeof
    /// pointing outside the solution gets no edge, but the fact is not lost,
    /// because it still renders into <see cref="Args"/>.
    /// </summary>
    public List<string>? RegisteredTypeFullNames { get; init; }

    /// <summary>
    /// The argument list exactly as written, present whenever there are
    /// arguments — uniform, so its absence means exactly "no arguments". One
    /// string per usage rather than per argument: most arguments are string
    /// literals whose parsed and written forms differ only by quotes.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Positional indices and named-argument names that failed to evaluate.
    /// Compile-time-constant rules mean this only happens when the compilation
    /// has errors, so presence here is a finding about the build. No sentinel in
    /// the value slots — any sentinel could be a real string.
    /// </summary>
    public List<string>? UnresolvedArgs { get; init; }
}

/// <summary>
/// A property or field as a graph node: identity, location, declared type, and
/// the in-solution types that declaration mentions — the raw material for the
/// References edges that make "who uses this type" answerable for DTOs.
/// </summary>
internal sealed record MemberDetail
{
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>NodeType.Property or NodeType.Field.</summary>
    public required NodeType Kind { get; init; }

    /// <summary>Display string of the declared type, e.g. List&lt;Order&gt;.</summary>
    public required string MemberType { get; init; }

    /// <summary>In-solution named types the declared type mentions, recursively.</summary>
    public List<string> ReferencedTypeFullNames { get; init; } = new();

    /// <summary>
    /// Ids of in-solution interface PROPERTIES this member implements; the
    /// member-level twin of MethodDetail.ImplementsIds.
    /// </summary>
    /// <remarks>
    /// Missing until 2026-09-11, and invisible rather than merely absent: an
    /// interface whose members are all properties had implementers that no
    /// query could reach, because InSolutionImplementedMembers filters to
    /// MethodKind.Ordinary and a property accessor is not one.
    /// </remarks>
    public List<string> ImplementsIds { get; init; } = new();

    public string? FilePath { get; init; }
    public int? LineStart { get; init; }
    public int? LineEnd { get; init; }

    /// <summary>First line of the XML doc comment, when the member has one.</summary>
    public int? DocsStart { get; init; }

    /// <summary>Last line of the XML doc comment, set exactly when DocsStart is.</summary>
    public int? DocsEnd { get; init; }
    public bool IsPublic { get; init; }
    public bool IsStatic { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsConst { get; init; }
    /// <summary>Overrides a base property, so its accessibility is the base declaration's to choose; the member twin of MethodDetail.IsOverride.</summary>
    public bool IsOverride { get; init; }
    public bool HasBindProperty { get; init; }

    /// <summary>Attributes written on this member.</summary>
    public List<AttributeUsage> Attributes { get; init; } = new();
}

/// <summary>
/// One read or write of a property or field, from the node that performs it.
/// A named type rather than a tuple for the same reason as CallSiteInfo.
/// </summary>
internal sealed record MemberAccessInfo(
    string FromId,
    string ToId,
    bool IsRead,
    bool IsWrite);

/// <summary>
/// Every read and write of an in-solution property or field (see
/// MemberAccessScanner), with the call and construction sites, the quoted names
/// (see QuotedNameScanner) and the callback targets (see
/// CallSiteScanner.CallbackTargets) the same walk collects; see
/// RoslynExtractor.ExtractUsageSites for why it is one walk.
/// </summary>
internal sealed record UsageSites(
    List<CallSiteInfo> Calls,
    List<ConstructionSiteInfo> Constructions,
    List<MemberAccessInfo> MemberAccesses,
    List<QuotedName> QuotedNames,
    List<string> CallbackTargets);

/// <summary>
/// One string a member produces, before anything has decided whether it names
/// something. ToId is absent on purpose: matching needs the solution-wide name
/// index, which exists only once every declaration is a node.
/// </summary>
internal sealed record QuotedName(
    string FromId,
    string Value,
    string Provenance,
    int Line);

/// <summary>
/// A method as a graph node: identity, location, and the shape a reader needs to
/// decide whether to open the file.
/// </summary>
internal sealed class MethodDetail
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Signature { get; init; }
    public string ReturnType { get; init; } = string.Empty;
    public bool IsAsync { get; init; }
    public bool IsPublic { get; init; }
    public bool IsStatic { get; init; }

    /// <summary>Carries a [Fact]/[Test]/[TestMethod]-style attribute.</summary>
    public bool IsTest { get; init; }

    /// <summary>
    /// A setup/teardown hook on a test class — [SetUp]-style attribute or an
    /// xUnit IAsyncLifetime/IDisposable member. The framework calls it, no test
    /// does, so coverage traversal must seed from it alongside the tests.
    /// </summary>
    public bool IsTestLifecycle { get; init; }

    /// <summary>Declared without a body — an interface member or an abstract method.</summary>
    public bool IsAbstract { get; init; }

    /// <summary>Overrides a base member, so its accessibility is the base declaration's to choose.</summary>
    public bool IsOverride { get; init; }

    /// <summary>
    /// A primary constructor -- a positional record's or a class's -- is declared by
    /// the type header, so it has the type's accessibility and the type's line.
    /// </summary>
    public bool IsPrimaryConstructor { get; init; }

    /// <summary>
    /// Maximum syntactic nesting depth of the body — the christmas-tree metric.
    /// 0 for expression-bodied, abstract, and implicitly declared members.
    /// </summary>
    public int NestingDepth { get; init; }

    /// <summary>Exception types that can leave this method locally; see ExceptionFlow.ExtractThrows.</summary>
    public List<ThrownType> Throws { get; init; } = new();

    /// <summary>Entry-point classification, or null for an ordinary method; see MethodRoles.ClassifyEntryPoint.</summary>
    public string? EntryPointKind { get; init; }

    /// <summary>
    /// In-solution named types this method's signature mentions — return type
    /// and parameters, recursively through generic arguments.
    ///
    /// These are the types C# pins to at least this method's accessibility even
    /// when no call site names them. Without them a type used only as a return
    /// shape looks unreferenced, and both "what breaks if I change this type"
    /// and any visibility audit answer confidently wrong.
    /// </summary>
    public List<string> SignatureTypeFullNames { get; init; } = new();

    /// <summary>
    /// For an extension method, the full name of the type it extends — the
    /// this parameter's type. An extension is part of the extended type's
    /// working surface even though it is declared elsewhere, and the graph
    /// says so with an Extends edge.
    /// </summary>
    public string? ExtendsTypeFullName { get; init; }

    /// <summary>Ids of in-solution interface methods this method implements; see SymbolClassifier.InSolutionImplementedMembers.</summary>
    public List<string> ImplementsIds { get; init; } = new();

    /// <summary>
    /// Ids of the in-solution declarations this method overrides, nearest
    /// first; see SymbolClassifier.InSolutionOverriddenMembers. The class
    /// twin of <see cref="ImplementsIds"/>.
    /// </summary>
    public List<string> OverridesIds { get; init; } = new();

    /// <summary>Exception types this HTTP boundary absorbs firmly; see ExceptionFlow.BoundaryCatchSets.</summary>
    public List<string> BoundaryCatches { get; init; } = new();

    /// <summary>Exception types this HTTP boundary absorbs only behind a filter (or runtime decision).</summary>
    public List<string> BoundaryCatchesFiltered { get; init; } = new();

    public string? FilePath { get; init; }
    public int? LineStart { get; init; }
    public int? LineEnd { get; init; }

    /// <summary>First line of the XML doc comment, when the method has one.</summary>
    public int? DocsStart { get; init; }

    /// <summary>Last line of the XML doc comment, set exactly when DocsStart is.</summary>
    public int? DocsEnd { get; init; }

    /// <summary>Attributes written on this method, including its return value.</summary>
    public List<AttributeUsage> Attributes { get; init; } = new();

    /// <summary>This method's decorated parameters; see ParameterDetail for why only those.</summary>
    public List<ParameterDetail> Parameters { get; init; } = new();
}

/// <summary>
/// A DECORATED parameter as a graph node. Only decorated parameters are
/// extracted — a param: node exists because it is decorated, so an absent node
/// means "undecorated", never "unmodelled". Widening that later is additive;
/// narrowing it is not. Ordinal rides along as data, from the unreduced
/// original definition so an extension method's this parameter keeps its slot.
/// </summary>
internal sealed record ParameterDetail(
    string Id,
    string Name,
    int Ordinal,
    string ParameterType,
    int? Line)
{
    /// <summary>Attributes written on this parameter — at least one, by construction.</summary>
    public List<AttributeUsage> Attributes { get; init; } = new();
}

/// <summary>
/// One exception type escaping a method, with its base-type chain (self first,
/// stopping at System.Exception) resolved at extraction — the only moment both
/// the thrown and the caught side are live symbols, so assignability becomes a
/// set-membership test instead of a name heuristic. Conditional means every
/// local catch that would take it carries a when filter.
/// </summary>
internal sealed record ThrownType(string Type, IReadOnlyList<string> AncestorChain, bool Conditional);

internal sealed class PropertyInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public bool HasBindProperty { get; set; }
}

internal sealed class MethodInfo
{
    public string Name { get; set; } = string.Empty;
    public string ReturnType { get; set; } = string.Empty;
    public List<string> Parameters { get; set; } = new();
    public bool IsAsync { get; set; }
    public string? HttpMethod { get; set; }
    public string? Route { get; set; }
}
