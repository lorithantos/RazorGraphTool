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

# Deep-nesting report: methods whose bodies nest 3+ levels, deepest first
dotnet run --project src/RazorGraph.Cli -- query graph.json --deep 3

# Exception escapes: throws that can reach an entry point with no catch stopping them
dotnet run --project src/RazorGraph.Cli -- query solution-graph.json --escapes --project App

# Inside one method: control-flow blocks, regions, call sites with guard depths
dotnet run --project src/RazorGraph.Cli -- body path/to/App.csproj --method "m:App.Services.OrderService.Place(App.Models.Order)"

# Prove a refactor kept the flow: exit 0 equivalent, 1 different, 2 error
dotnet run --project src/RazorGraph.Cli -- body-diff path/to/App.csproj --method "m:App.OrderService.Place(App.Models.Order)" --against "m:App.OrderService.PlaceOld(App.Models.Order)"

# Method ids carry the full parameter-type list; a parameterless method is "m:Type.Name()"

# Relevance-scored subgraph for feeding to a model
dotnet run --project src/RazorGraph.Cli -- research graph.json --focus "page:Pages/Index.cshtml"
```

`build` and `build-solution` are slow — they run a real MSBuild + Roslyn compile.
`load`/`save` of the JSON is fast, so build once and query many times.

## Use it from Claude Code

`.mcp.json` in the repo root registers the server:

```bash
dotnet publish src/RazorGraph.Mcp -c Release -o .mcp-bin   # .mcp.json launches this published copy
```

Restart the session and 22 tools appear: `build_graph`, `build_solution`, `load_graph`,
`save_graph`, `list_graphs`, `drop_graph`, `graph_summary`, `find_nodes`, `get_node`,
`render_tree`, `page_context`, `trace_data_flow`, `find_path`, `covering_tests`,
`covered_methods`, `uncovered_methods`, `deep_methods`, `exception_escapes`,
`method_body_graph`, `method_body_diff`, `find_server_to_js_mismatches`, `research`.

The server holds **several graphs at once** in a keyed registry. Every tool takes an
optional `graphId` and falls back to the most recently added, so a solution graph and a
single-project graph of the same code can be compared without rebuilding either.

Results are compact JSON — the consumer is a model, not a terminal — and searches return
a `{ returned, totalMatches, truncated }` envelope. Check `truncated` before concluding
you have seen everything.

> The server launches from the published `.mcp-bin` copy, not live build output, so
> ordinary `dotnet build` keeps working while a session is attached. The published copy
> is itself held open while any session uses it: re-publishing needs those sessions
> closed first, and code changes only reach the tools after a re-publish *and* a session
> restart.

## What ends up in the graph

**Nodes** — `Project`, `RazorPage`, `PageModel`, `ApiController`, `ControllerAction`,
`PartialView`, `ViewComponent`, `Layout`, `Service`, `ServiceInterface`,
`ServiceImplementation`, `ViewModel`, `Class`, `Method`, `Property`, `Field`,
`ViewDataKey`, `Middleware`, `Route`, `HtmlElement`, `TagHelperInvocation`,
`JavaScriptFile`, `CssFile`.

**Edges** — structural (`Contains`, `Inherits`, `Implements`, `DependsOn`, `References`
— a page referencing a script or stylesheet), rendering
(`PageServedBy`, `UsesLayout`, `RendersPartial`, `RendersComponent`, `DefinesSection`,
`ReturnsView`), data flow (`HasModel`, `BindsTo`, `Reads`, `Writes`, `Calls`,
`InjectedInto`, `ViewDataReadBy`, `ViewDataWrittenBy`, `DomSelectedBy`), routing
(`MapsToRoute`, `HandlesHttpMethod`, `UrlGeneratedBy`), `Covers`, and `Escapes`.

`DomSelectedBy` is the selector contract: element ids a page composition (page +
layout + partials) renders that a script reaches with literal `getElementById` /
`querySelector` / jQuery selectors. Selector ids no referencing page renders are
annotated `unboundSelectorIds` on the script node — the rename-that-broke-one-side
defect — with deliberate silences: dynamic selector call sites, Razor-computed `id`
attributes in scope, or an unreferenced script all suppress the accusation. JS
comments are stripped before scanning, so documentation cannot create contracts.

Traversal takes a direction. This is not a convenience: several edge types are authored
pointing the opposite way from the question asked of them — `InjectedInto` runs
service → consumer — so an outgoing-only walk answers "what does this page depend on"
with silence rather than an error. Containment is followed without spending depth,
because call edges hang off `Method` nodes and a class-level trace that cannot descend
for free would report nothing.

### Worth knowing

**Coverage is reachability, not runtime coverage.** `Covers` edges are emitted from a
test method to production code its call chain reaches — the full call closure, not a
fixed horizon, carrying on each edge the depth it was reached at, so a consumer who
wants direct exercise alone filters on depth. Traversal seeds from the test and from its
class's lifecycle hooks and constructor alike: the framework runs those around every
test, so work done there is exercised even though no test calls it. A "covered" method
is one some test *can* reach — not one a test asserted on. Edges are only emitted across
a project boundary, so `build_solution` is required; `uncovered_methods` excludes
bodiless interface and abstract declarations, which never bind a call and would
otherwise swamp the report.

**Methods include constructors, and disposal is a call.** Explicit instance
constructors are Method nodes — constructors run real code, and xUnit's primary setup
idiom is the test-class ctor. An implicit default ctor appears only when field
initializers run in it; static ctors stay out. A `using` / `await using` counts as a
Calls edge to the resource's `Dispose`/`DisposeAsync`. Methods whose bodies nest carry
a `bodyDepth` property stamped at build time, which feeds the deep-nesting report
(`deep_methods` / `query --deep`).

**Escape analysis is static reachability over in-solution code.**
`exception_escapes` (CLI: `query --escapes`) reports throwing operations that can reach
an application entry point — declared `static Main`, Razor page handlers, controller
actions, `(object, EventArgs)` event handlers, `async void` methods, overrides of
framework virtuals — with no catch stopping them. Everything is precomputed at build
time: methods carry `throws` and `entryPointKind` properties, `Calls` edges carry the
catch guards of their sites (`guardedBy`, with `filteredBy` kept apart because a `when`
filter may decline at runtime — it reports `conditional`, never handled), and a
worklist sweep emits `Escapes` edges carrying the exception type, hop depth, and one
representative path. Blind spots come back as data in the tool's `caveats`: BCL and
out-of-solution throwers are invisible, dispatch is static, delegates/lambdas/local
functions are not followed, and top-level-statement `Main` is not an entry point.

**Method bodies have their own graph.** `method_body_graph` (CLI: `body`) compiles the
project and returns the graph *inside* one method: control-flow basic blocks with
branch edges, structural regions (try/finally, lifetimes), and every call site anchored
to its block with line and guard depth. `method_body_diff` (CLI: `body-diff`) proves
two bodies flow-equivalent — bisimulation over the blocks, comparing operations, calls,
canonicalized branch conditions, and exception-region context — or reports precisely
why not. Conservative by design: a renamed local reports different; a semantically
wrong change is never blessed. Compare against another method in the same compilation
or against a body-graph JSON saved earlier.

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
| `src/RazorGraph.Cli` | `build`, `build-solution`, `query`, `body`, `body-diff`, `research` |
| `src/RazorGraph.Mcp` | MCP stdio server over the graph |
| `src/RazorGraph.Tests` | Unit and integration tests |
| `tests/fixtures` | `SampleApp` (single project) and `MultiProject` (solution) |

## Tests

```bash
dotnet test src/RazorGraph.Tests/RazorGraph.Tests.csproj
```

The integration tests compile the fixture apps with the real toolchain, so the first run
is slower than the rest.
