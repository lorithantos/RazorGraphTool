namespace RazorGraph.Core.Graph;

/// <summary>
/// Which way a traversal follows edges.
///
/// Direction is not a convenience: several edge types are authored pointing the
/// opposite way from the question people ask of them. InjectedInto runs
/// service -> consumer, so "what does this page depend on" is an Incoming walk,
/// and an outgoing-only traverser answers it with silence rather than an error.
/// </summary>
public enum TraversalDirection
{
    /// <summary>Follow edges from the node outward (the authored direction).</summary>
    Outgoing,

    /// <summary>Follow edges that point at the node (who references me).</summary>
    Incoming,

    /// <summary>Treat the graph as undirected — both adjacency lists.</summary>
    Both
}
