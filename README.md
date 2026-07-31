# RazorGraphTool

Builds a queryable code graph of an ASP.NET Core Razor Pages application, and serves it
to an AI agent over MCP.

Text search cannot answer "which overload does this call bind to", "who actually
implements this interface", or "what breaks if I change this signature" — and an agent
without a semantic model will guess at exactly those questions, plausibly enough to
survive review. This exposes the compiler's model instead: Roslyn for C# symbols and
call resolution, the Razor syntax tree for pages, layouts, partials and tag helpers, and
a scan of `wwwroot` plus inline `<script>` blocks for the client tier.

## Quick start

```bash
# One project
dotnet run --project src/RazorGraph.Cli -- build path/to/App.csproj -o graph.json

# A whole solution: the only way to get edges that cross a project boundary
dotnet run --project src/RazorGraph.Cli -- build-solution path/to/App.sln -o solution-graph.json

# Ask it things
dotnet run --project src/RazorGraph.Cli -- query graph.json --type RazorPage
dotnet run --project src/RazorGraph.Cli -- query graph.json --id "page:Pages/Index.cshtml" --context
dotnet run --project src/RazorGraph.Cli -- query graph.json --id "pm:App.IndexModel" --trace --depth 3
dotnet run --project src/RazorGraph.Cli -- query solution-graph.json --uncovered --project App

# Relevance-scored subgraph for feeding to a model
dotnet run --project src/RazorGraph.Cli -- research graph.json --focus "page:Pages/Index.cshtml"
```

`build` and `build-solution` are slow — they run a real MSBuild + Roslyn compile.
`load`/`save` of the JSON is fast, so build once and query many times.

## Use it from Claude Code

`.mcp.json` in the repo root registers the server:

```bash
dotnet build src/RazorGraph.Mcp -c Debug   # .mcp.json points at the Debug exe
```

Restart the session and 18 tools appear: `build_graph`, `build_solution`, `load_graph`,
`save_graph`, `list_graphs`, `drop_graph`, `graph_summary`, `find_nodes`, `get_node`,
`render_tree`, `page_context`, `trace_data_flow`, `find_path`, `covering_tests`,
`covered_methods`, `uncovered_methods`, `find_server_to_js_mismatches`, `research`.

The server holds **several graphs at once** in a keyed registry. Every tool takes an
optional `graphId` and falls back to the most recently added, so a solution graph and a
single-project graph of the same code can be compared without rebuilding either.

Results are compact JSON — the consumer is a model, not a terminal — and searches return
a `{ returned, totalMatches, truncated }` envelope. Check `truncated` before concluding
you have seen everything.

> A running server holds its own DLLs open, so rebuilding while it is attached fails with
> MSB3021. Stop the session first. Extractor changes only reach the tools after a
> rebuild *and* a session restart.

## What ends up in the graph

**Nodes** — `Project`, `RazorPage`, `PageModel`, `ApiController`, `ControllerAction`,
`PartialView`, `ViewComponent`, `Layout`, `Service`, `ServiceInterface`,
`ServiceImplementation`, `ViewModel`, `Class`, `Method`, `Property`, `Field`,
`ViewDataKey`, `Middleware`, `Route`, `HtmlElement`, `TagHelperInvocation`,
`JavaScriptFile`, `CssFile`.

**Edges** — structural (`Contains`, `Inherits`, `Implements`, `DependsOn`), rendering
(`PageServedBy`, `UsesLayout`, `RendersPartial`, `RendersComponent`, `DefinesSection`,
`ReturnsView`), data flow (`HasModel`, `BindsTo`, `Reads`, `Writes`, `Calls`,
`InjectedInto`, `ViewDataReadBy`, `ViewDataWrittenBy`), routing (`MapsToRoute`,
`HandlesHttpMethod`, `UrlGeneratedBy`), and `Covers`.

Traversal takes a direction. This is not a convenience: several edge types are authored
pointing the opposite way from the question asked of them — `InjectedInto` runs
service → consumer — so an outgoing-only walk answers "what does this page depend on"
with silence rather than an error. Containment is followed without spending depth,
because call edges hang off `Method` nodes and a class-level trace that cannot descend
for free would report nothing.

### Two things worth knowing

**Coverage is reachability, not runtime coverage.** `Covers` edges are emitted from a
test method to production code its call chain reaches, to depth 3, carrying the depth on
the edge. A "covered" method is one some test *can* reach — not one a test asserted on.
Edges are only emitted across a project boundary, so `build_solution` is required;
`uncovered_methods` excludes bodiless interface and abstract declarations, which never
bind a call and would otherwise swamp the report.

**Solution graphs scope ids by project.** A solution build produces
`page:<Project>/<relPath>` and `js:<Project>/<relPath>`, because two projects can both
contain `Pages/Index.cshtml`. Single-project builds keep the unscoped `page:<relPath>`
form, so previously saved graphs and their ids still work.

**Vendor client assets are classified, then dropped by default.** Detection matches
whole path segments (`lib`, `lib_npm`, `node_modules`, `bower_components`, `vendor`),
npm `@scope` directories, package manifests shipped inside `wwwroot`, directories whose
children match the root `package.json` dependencies, and `.min.` files. Dropped files
are always reported — on stderr and in the build tools' `skippedVendorAssets` — never
silently. Pass `--include-vendor` (CLI) or `includeVendor` (MCP) to graph them anyway,
e.g. when the bug lives inside a shipped bundle; included vendor nodes carry
`vendor: true` and a `vendorReason`.

## Layout

| Path | Contents |
|---|---|
| `src/RazorGraph.Core` | Graph model, traversal, and the query surface |
| `src/RazorGraph.Extractor` | Roslyn, Razor, and client-asset extraction; `GraphBuilder` |
| `src/RazorGraph.Cli` | `build`, `build-solution`, `query`, `research` |
| `src/RazorGraph.Mcp` | MCP stdio server over the graph |
| `src/RazorGraph.Tests` | Unit and integration tests |
| `tests/fixtures` | `SampleApp` (single project) and `MultiProject` (solution) |

## Tests

```bash
dotnet test src/RazorGraph.Tests/RazorGraph.Tests.csproj
```

The integration tests compile the fixture apps with the real toolchain, so the first run
is slower than the rest.
