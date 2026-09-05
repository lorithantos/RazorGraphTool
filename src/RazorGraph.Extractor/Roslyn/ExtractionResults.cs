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
internal sealed record CallSiteInfo(
    string FromId,
    string ToId,
    IReadOnlyList<string> GuardedBy,
    IReadOnlyList<string> FilteredBy,
    bool IsDelegate = false);

/// <summary>
/// One resource a method disposes implicitly, and whether the disposal is
/// await using — which resolves DisposeAsync rather than Dispose.
/// </summary>
internal readonly record struct DisposedResource(ITypeSymbol Type, bool IsAsync);

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

    public string? BaseType { get; init; }
    public List<string> ImplementedInterfaces { get; init; } = new();
    public List<string> InjectedServices { get; init; } = new();
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

    public string? FilePath { get; init; }
    public int? LineStart { get; init; }
    public bool IsPublic { get; init; }
    public bool IsStatic { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsConst { get; init; }
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

    /// <summary>Exception types this HTTP boundary absorbs firmly; see ExceptionFlow.BoundaryCatchSets.</summary>
    public List<string> BoundaryCatches { get; init; } = new();

    /// <summary>Exception types this HTTP boundary absorbs only behind a filter (or runtime decision).</summary>
    public List<string> BoundaryCatchesFiltered { get; init; } = new();

    public string? FilePath { get; init; }
    public int? LineStart { get; init; }

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
