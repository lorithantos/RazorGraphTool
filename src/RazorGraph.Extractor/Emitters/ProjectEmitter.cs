namespace RazorGraph.Extractor;

using Microsoft.CodeAnalysis;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor.Roslyn;

/// <summary>
/// Emits the project layer of a solution graph: one node per project and the
/// DependsOn edges between them.
/// </summary>
internal sealed class ProjectEmitter(CodeGraph graph)
{
    /// <summary>
    /// One node per project plus the reference edges between them. Cheap
    /// orientation for a solution graph: which assemblies exist and which way
    /// the dependencies point, without reading 900 nodes to infer it.
    /// </summary>
    internal void AddProjectNodes(
        IReadOnlyList<RoslynExtractor.LoadedProject> loaded, Solution? solution)
    {
        if (loaded.Count == 0) return;

        var nodeCounts = graph.Nodes
            .Select(n => n.GetProperty<string>("project"))
            .Where(p => p != null)
            .GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var project in loaded)
        {
            var node = new GraphNode
            {
                Id = ProjectId(project.Name),
                Type = NodeType.Project,
                Name = project.Name,
                FilePath = project.FilePath
            };

            node.SetProperty("assemblyName", project.Compilation.AssemblyName ?? project.Name);
            node.SetProperty("nodeCount", nodeCounts.TryGetValue(project.Name, out var c) ? c : 0);
            graph.AddNode(node);
        }

        if (solution == null) return;

        foreach (var project in loaded)
        {
            foreach (var reference in project.Project.ProjectReferences)
            {
                var target = solution.GetProject(reference.ProjectId);
                if (target == null || !graph.HasNode(ProjectId(target.Name))) continue;

                graph.AddEdge(new GraphEdge
                {
                    FromId = ProjectId(project.Name),
                    ToId = ProjectId(target.Name),
                    Type = EdgeType.DependsOn
                });
            }
        }
    }

    private static string ProjectId(string projectName) => $"proj:{projectName}";
}
