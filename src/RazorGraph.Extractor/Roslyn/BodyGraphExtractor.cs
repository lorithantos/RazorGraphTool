namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

/// <summary>
/// The graph *inside* one method: the compiler's control-flow graph (basic
/// blocks, branch edges, exception/lifetime regions) with every call site
/// anchored to its block and annotated with its syntactic guard depth.
///
/// Two lenses on purpose. Flow structure comes from Roslyn's CFG, because that
/// is what a transform must preserve. Nesting comes from syntax, because CFG
/// regions only model try/catch/finally and local lifetimes — an if inside two
/// foreach loops is branch edges, not regions, so "how deep is this buried"
/// (the christmas-tree question) is a syntax fact, not a CFG fact.
/// </summary>
public static class BodyGraphExtractor
{
    /// <summary>
    /// Maximum syntactic nesting depth of a method body: how many indentation
    /// steps of control flow a reader must hold to reach the deepest statement.
    /// An else-if chain does not add depth — visually it stays at one level.
    /// </summary>
    public static int NestingDepth(BaseMethodDeclarationSyntax decl)
    {
        SyntaxNode? body = (SyntaxNode?)decl.Body ?? decl.ExpressionBody;
        return body == null ? 0 : MaxDepth(body);

        static int MaxDepth(SyntaxNode node)
        {
            var max = 0;
            foreach (var child in node.ChildNodes())
            {
                var depth = MaxDepth(child) + (AddsNesting(child) ? 1 : 0);
                if (depth > max) max = depth;
            }
            return max;
        }
    }

    /// <summary>
    /// How many control-flow constructs stand between a node and its method
    /// declaration — the number of conditions/loops that must be entered for
    /// this site to execute, as a reader counts them.
    /// </summary>
    public static int GuardDepth(SyntaxNode site, BaseMethodDeclarationSyntax decl) =>
        site.Ancestors().TakeWhile(a => a != decl).Count(AddsNesting);

    internal static bool AddsNesting(SyntaxNode node) => node switch
    {
        // An if that is the whole body of an else keeps the else's indentation.
        IfStatementSyntax ifStatement => ifStatement.Parent is not ElseClauseSyntax,
        ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax or DoStatementSyntax
            or SwitchStatementSyntax or TryStatementSyntax or UsingStatementSyntax
            or LockStatementSyntax or FixedStatementSyntax => true,
        _ => false
    };

    /// <summary>
    /// Extract the body graph, or null when the method has no body to graph
    /// (abstract, extern, or no operation tree). idFor maps a call target to
    /// the id its Method node is registered under, so call sites here join the
    /// inter-method graph instead of forming a parallel namespace.
    /// </summary>
    public static BodyGraph? Extract(
        SemanticModel model, BaseMethodDeclarationSyntax decl, Func<IMethodSymbol, string> idFor)
    {
        var cfg = model.GetOperation(decl) switch
        {
            IMethodBodyOperation m => ControlFlowGraph.Create(m),
            IConstructorBodyOperation c => ControlFlowGraph.Create(c),
            _ => null
        };
        if (cfg == null) return null;

        var regionIds = new Dictionary<ControlFlowRegion, int>();
        var regions = new List<BodyRegion>();
        IndexRegion(cfg.Root, parentId: null, regionIds, regions);

        var blocks = cfg.Blocks.Select(block => new BodyBlock
        {
            Ordinal = block.Ordinal,
            Kind = block.Kind.ToString(),
            RegionId = regionIds[block.EnclosingRegion!],
            IsReachable = block.IsReachable,
            FallsTo = block.FallThroughSuccessor?.Destination?.Ordinal,
            BranchesTo = block.ConditionalSuccessor?.Destination?.Ordinal,
            BranchWhen = block.ConditionKind == ControlFlowConditionKind.None
                ? null
                : block.ConditionKind.ToString(),
            Calls = CallSites(block, decl, idFor).ToList()
        }).ToList();

        return new BodyGraph
        {
            MethodId = model.GetDeclaredSymbol(decl) is IMethodSymbol symbol ? idFor(symbol) : "(unknown)",
            NestingDepth = NestingDepth(decl),
            Regions = regions,
            Blocks = blocks
        };
    }

    private static void IndexRegion(
        ControlFlowRegion region, int? parentId,
        Dictionary<ControlFlowRegion, int> regionIds, List<BodyRegion> regions)
    {
        var id = regions.Count;
        regionIds[region] = id;
        regions.Add(new BodyRegion
        {
            Id = id,
            Kind = region.Kind.ToString(),
            ParentId = parentId,
            FirstBlock = region.FirstBlockOrdinal,
            LastBlock = region.LastBlockOrdinal
        });

        foreach (var nested in region.NestedRegions)
            IndexRegion(nested, id, regionIds, regions);
    }

    private static IEnumerable<BodyCallSite> CallSites(
        BasicBlock block, BaseMethodDeclarationSyntax decl, Func<IMethodSymbol, string> idFor)
    {
        var operations = block.BranchValue == null
            ? block.Operations
            : block.Operations.Add(block.BranchValue);

        foreach (var operation in operations)
        {
            foreach (var op in operation.DescendantsAndSelf())
            {
                var target = op switch
                {
                    IInvocationOperation invocation => invocation.TargetMethod,
                    IObjectCreationOperation creation => creation.Constructor,
                    _ => null
                };
                if (target == null) continue;

                yield return new BodyCallSite
                {
                    TargetId = idFor(target.OriginalDefinition),
                    Line = op.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    GuardDepth = GuardDepth(op.Syntax, decl)
                };
            }
        }
    }
}

/// <summary>One method's internals: flow blocks, structural regions, anchored call sites.</summary>
public sealed class BodyGraph
{
    public required string MethodId { get; init; }
    public int NestingDepth { get; init; }
    public required List<BodyRegion> Regions { get; init; }
    public required List<BodyBlock> Blocks { get; init; }
}

/// <summary>A CFG region: Root, LocalLifetime, or the try/catch/finally family.</summary>
public sealed class BodyRegion
{
    public int Id { get; init; }
    public required string Kind { get; init; }
    public int? ParentId { get; init; }
    public int FirstBlock { get; init; }
    public int LastBlock { get; init; }
}

public sealed class BodyBlock
{
    public int Ordinal { get; init; }
    public required string Kind { get; init; }
    public int RegionId { get; init; }
    public bool IsReachable { get; init; }
    public int? FallsTo { get; init; }
    public int? BranchesTo { get; init; }
    /// <summary>WhenTrue/WhenFalse when the block ends in a conditional branch.</summary>
    public string? BranchWhen { get; init; }
    public required List<BodyCallSite> Calls { get; init; }
}

public sealed class BodyCallSite
{
    public required string TargetId { get; init; }
    public int Line { get; init; }
    /// <summary>Conditions/loops that must be entered for this call to run (syntactic).</summary>
    public int GuardDepth { get; init; }
}
