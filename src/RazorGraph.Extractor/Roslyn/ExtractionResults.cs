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
public sealed record CallSiteInfo(
    string FromId,
    string ToId,
    IReadOnlyList<string> GuardedBy,
    IReadOnlyList<string> FilteredBy,
    bool IsDelegate = false);

/// <summary>
/// One resource a method disposes implicitly, and whether the disposal is
/// await using — which resolves DisposeAsync rather than Dispose.
/// </summary>
public readonly record struct DisposedResource(ITypeSymbol Type, bool IsAsync);

public sealed class SymbolInfo
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

    public string? BaseType { get; init; }
    public List<string> ImplementedInterfaces { get; init; } = new();
    public List<string> InjectedServices { get; init; } = new();
    public List<PropertyInfo> Properties { get; init; } = new();
    public List<MethodInfo> Methods { get; init; } = new();

    /// <summary>Members promoted to their own graph nodes; see SymbolClassifier.ExtractMethodNodes.</summary>
    public List<MethodDetail> MethodNodes { get; init; } = new();

    /// <summary>Properties and fields promoted to their own graph nodes; see SymbolClassifier.ExtractMemberNodes.</summary>
    public List<MemberDetail> MemberNodes { get; init; } = new();
}

/// <summary>
/// A property or field as a graph node: identity, location, declared type, and
/// the in-solution types that declaration mentions — the raw material for the
/// References edges that make "who uses this type" answerable for DTOs.
/// </summary>
public sealed record MemberDetail
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
}

/// <summary>
/// One read or write of a property or field, from the node that performs it.
/// A named type rather than a tuple for the same reason as CallSiteInfo.
/// </summary>
public sealed record MemberAccessInfo(
    string FromId,
    string ToId,
    bool IsRead,
    bool IsWrite);

/// <summary>
/// A method as a graph node: identity, location, and the shape a reader needs to
/// decide whether to open the file.
/// </summary>
public sealed class MethodDetail
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
}

/// <summary>
/// One exception type escaping a method, with its base-type chain (self first,
/// stopping at System.Exception) resolved at extraction — the only moment both
/// the thrown and the caught side are live symbols, so assignability becomes a
/// set-membership test instead of a name heuristic. Conditional means every
/// local catch that would take it carries a when filter.
/// </summary>
public sealed record ThrownType(string Type, IReadOnlyList<string> AncestorChain, bool Conditional);

public sealed class PropertyInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public bool HasBindProperty { get; set; }
}

public sealed class MethodInfo
{
    public string Name { get; set; } = string.Empty;
    public string ReturnType { get; set; } = string.Empty;
    public List<string> Parameters { get; set; } = new();
    public bool IsAsync { get; set; }
    public string? HttpMethod { get; set; }
    public string? Route { get; set; }
}
