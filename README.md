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

# Lean navigation graph: skip test projects (no Covers edges — often ~80% of the total).
# Coverage queries refuse a test-less graph rather than reporting everything uncovered.
dotnet run --project src/RazorGraph.Cli -- build-solution path/to/App.sln --no-tests -o nav-graph.json

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

`.mcp.json` in the repo root points the session at an HTTP server on port 7718. Publish a
copy and start it once; every session on the machine then shares it, and it survives being
killed and rebuilt while a session stays open:

```bash
dotnet publish src/RazorGraph.Mcp -c Release -o .mcp-bin
.mcp-bin/RazorGraph.Mcp.exe --http --auth-token-command "<command that prints the token>"
                                            # port 7718 unless --port says otherwise; see below
```

(`RazorGraph.Mcp.exe` with no flags speaks stdio instead, for clients that need it.)

The HTTP transport refuses any request whose Host is not `127.0.0.1`, `localhost` or `[::1]` (400), any request carrying a non-loopback `Origin`, `null` included (403), and any POST that is not `application/json` (415), on every endpoint including `/health`, so a browser page cannot reach it by DNS rebinding or a cross-site POST.

It also checks a bearer token, set by `--auth require|report|off`. `require`, the default, answers 401 to any request without `Authorization: Bearer <token>`; `report` lets every request through and counts what each carried, the step to take before requiring; `off` checks nothing. The server keeps no token of its own: `--auth-token-command` names a command, run without a shell (its first word found through PATH), whose trimmed stdout is the token, for example `--auth-token-command "janet mcp-token --print"`. It runs at startup and again at most every 30 seconds when a request's token does not match, so a rotated token is picked up; a non-zero exit or a 10-second timeout leaves the server with no token and the reason on `/health`. Under `--auth require` the server will not start without it. `/health` is never checked, and reports the mode, whether a token is configured and the counts, never the token. Claude Code clients supply the header through `headersHelper` on the server's entry in `.mcp.json`. The guard and the check come from the `Janet.Mcp.Security` package, built from [Janet.Shared](https://github.com/lorithantos/Janet.Shared) and, for now, restored from the local feed `..\.packages` that `nuget.config` names.

The server **does not keep the environment of the shell that started it**. On Windows it
rebuilds the one a fresh sign-in would give it — machine and user variables from the
registry, the per-logon ones, and any `RAZORGRAPH_*` — and drops the rest before anything
else runs. MSBuild turns environment variables into global properties, so a server started
from a Visual Studio x64 prompt would otherwise build every graph for `Platform=x64`. This
applies over stdio too, so an MCP client's `env` block is dropped like a terminal's: a
build that needs a particular platform passes `platform` to `build_graph` or
`build_solution` instead. Set
`RAZORGRAPH_KEEP_ENVIRONMENT=1` to keep the inherited environment on purpose. `server_info`
reports what this did under `environment` (`applied`, `skippedBecause`, `dropped`,
`changed`, `keptFromLauncher`), by variable name only — never values.

Restart the session and 25 tools appear: `server_info`, `build_graph`, `build_solution`, `build_lua`,
`load_graph`, `save_graph`, `list_graphs`, `drop_graph`, `graph_summary`, `find_nodes`, `get_node`,
`render_tree`, `page_context`, `trace_data_flow`, `find_path`, `covering_tests`,
`covered_methods`, `uncovered_methods`, `deep_methods`, `exception_escapes`, `excess_visibility`,
`method_body_graph`, `method_body_diff`, `find_server_to_js_mismatches`, `research`.

The server holds **several graphs at once** in a keyed registry. Every tool takes an
optional `graphId` and falls back to the most recently added, so a solution graph and a
single-project graph of the same code can be compared without rebuilding either.

Results are compact JSON — the consumer is a model, not a terminal — and searches return
a `{ returned, totalMatches, truncated }` envelope. Check `truncated` before concluding
you have seen everything.

**`get_node` is narrow by default**, because context is the resource that runs out.
It returns the node, its outgoing edges as rows, and its incoming edges *summarised by
type with counts*. That summary is usually the answer: "130 tests reach this" is what a
caller used to pay for 130 rows to learn. Expansion is opt-in — `edges` picks a side
(`none`, `outgoing`, `incoming`, `all`), `edgeType` narrows it to one type, `limit` caps
the rows. Every result carries an `availableFields` block naming the expansions *that
node* can take, so the counts tell you whether to look further and the menu tells you
what to ask for. An unknown edge type is refused, naming the types the node has, rather
than answered with an empty list that reads as "nothing points at this".

The same applies to the node's own payload. Scalar properties come back inline — they
are what orients a reader, and they are cheap. List-valued ones (a type's `methods`, a
method's `throws`) come back as counts, and `fields` expands one by name or `all`. The
split is measured: across a 2,098-node graph the scalars total 245KB, about 117 bytes a
node, while the lists are 37KB concentrated on 229 nodes and own every fat payload. On
the worst node in that graph — a test class whose method-name list duplicates the
`Contains` edges the same node already reports — collapsing the lists takes the answer
from 4,543 characters to 1,456.

`total` and `byType` always describe the whole side. `edgeType` narrows the rows only,
and a filtered expansion reports `filteredTo` and `matching` beside the unfiltered
total — a narrowing argument must never change what the unasked-for half of an answer
claims about itself.

**A node says what to read, not just where to look.** Every node carries `lineStart`,
`lineEnd`, and `docsStart` — the first line of its XML documentation comment, absent when
it has none. So `docsStart ?? lineStart` through `lineEnd` is the declaration and its
documentation and nothing else, and a caller opens the exact chunk instead of a line
number and a guess about how far down to go. `docsEnd` is the comment's last line, so
`docsStart` through `docsEnd` is the documentation alone; it is stored rather than left
as `lineStart - 1` because an attribute list often sits in between. Absent `docsStart` is
a fact rather than a gap: that declaration is undocumented.

One edge to know: attributes are part of the declaration node while the doc comment is
leading trivia in front of it, and `lineStart` deliberately points at the declaration
rather than at the `[`. So `docsStart` clears the attribute list on a documented member,
while an attributed *undocumented* member has its attributes sitting just above
`lineStart`. Methods, properties and fields carried only `lineStart` before 2026-09-05;
a graph saved earlier has no `lineEnd` on them and no doc line anywhere, and an absent
value there means "not known", not "undocumented". Format 1.x graphs saved since then
spell the doc line `lineDocs`; format 2.0 renamed it `docsStart` and still reads the old
key, so a 1.x graph loads whole and can be diffed against a 2.0 one.

**A span is only true of the source as it stood at build time**, so every node result
says when that was. `builtAt` is on the response, `fileWrittenSince` is on the node when
its file has been written since, and `list_graphs` reports `builtAt` beside `loadedAt` —
the second only says when a graph entered this server, so one loaded from a saved file
a minute ago can have been built days before.

When a graph carries no build stamp at all the response says so in `freshnessCaveat`,
because `fileWrittenSince` is emitted only when true and its absence would otherwise
carry two meanings: "this file is unchanged", which is evidence, and "nothing was
computed", which is silence. A reader cannot tell those apart from a node, so the graph
states which kind of absence it is, once, per response.

**A graph also says what it was built for.** `hostArchitecture` is the CPU of the machine
that extracted it, `platform` is the MSBuild `Platform` the workspace loaded with, and
`platformFrom` says why: `requested` (the build passed `platform` / `--platform`),
`environment` (inherited from the process's `Platform` variable), or `projectDefault`
(neither, and `platform` is absent). Generated-code paths follow it (`obj\x64\Debug` against
`obj\Debug`), so two graphs of one source can differ on hundreds of nodes for that reason
alone. Solution graphs also put `platformTarget` on each `Project` node, which is what the
compiler targeted. A graph saved before format 3.4 has none of these, which means "cannot
tell", not `projectDefault`.

> The server runs from the published `.mcp-bin` copy, not live build output, so
> ordinary `dotnet build` keeps working while a session is attached. Code changes reach
> the tools after a re-publish and a restart of the HTTP server; sessions reconnect on
> their own.

## What ends up in the graph

**Nodes** — `Project`, `RazorPage`, `PageModel`, `ApiController`, `ControllerAction`,
`PartialView`, `ViewComponent`, `Layout`, `Service`, `ServiceInterface`,
`ServiceImplementation`, `ViewModel`, `Class`, `Method`, `Property`, `Field`,
`ViewDataKey`, `Middleware`, `Route`, `HtmlElement`, `TagHelperInvocation`,
`JavaScriptFile`, `CssFile`, `TextFile`.

**Every other text file in the source tree is a `TextFile` node carrying its lines** —
`.json`, `.md`, `.ps1`, `.bicep`, `.csproj`, `.yml` and the rest, one node per file, id
`text:` plus its path relative to the solution's or project's directory. The files are the
ones `grep`'s disk section reads, listed by the same code: `git ls-files` in a working
tree, a walk skipping vendor and build output elsewhere, binaries and files over 2 MB
turned away and named in the build summary. C#, Razor and XAML files, and any file that
is already a `JavaScriptFile` or `CssFile`, never become one; nor does text under a vendor
directory or a minified name. The lines travel with every saved graph and the `research`
export, so the graph holds only what the repository already publishes: in a git tree an
untracked file stays out, and anywhere a file with a secret-style name (`.env*`, `.envrc`,
`*secret*`, `*credential*`, `*.pem`, `*.key`, `*.publishsettings`, `local.settings.json`,
`.netrc`, `.pgpass`, `.git-credentials`, SSH private keys) stays out, each named in the build
summary. Both are still searched by `grep`'s disk section, which reads them without storing
them. In a git tree where `git ls-files` fails, no file is graphed as text. So `grep`'s graph section answers for those files too, with
`matchedOn: "text"` and the line — except a file written since the build, whose lines may
no longer be true: the disk section answers for it, and `staleTextFiles` says how many.
`get_node` returns `lines` as a count unless `fields=lines` asks. `excludeText`
(`--no-text`) skips them, and the build summary says it did. Lua graphs have none.

**Edges** — structural (`Contains`, `Inherits`, `Implements`, `Overrides`, `DependsOn`, `References`
— a page referencing a script or stylesheet), rendering
(`PageServedBy`, `UsesLayout`, `RendersPartial`, `RendersComponent`, `DefinesSection`,
`ReturnsView`), data flow (`HasModel`, `BindsTo`, `Reads`, `Writes`, `Calls`,
`InjectedInto`, `ViewDataReadBy`, `ViewDataWrittenBy`, `DomSelectedBy`), routing
(`MapsToRoute`, `HandlesHttpMethod`, `UrlGeneratedBy`), `Covers`, `Escapes`, and
`Extends` — an extension method pointing at the in-solution type it extends.

`DataBinds` runs from a XAML view to every member one of its bindings could name,
carrying the path as written, the line, and how many candidates the name resolved
to. Conservative in the same direction as interface dispatch, and for the same
reason: a binding resolves against the `DataContext` of whatever element it sits on
at runtime — walked up the tree by `RelativeSource`, rebound mid-tree, or supplied
by the item a `DataTemplate` is applied to — so naming one candidate would be a
plausible answer that is sometimes wrong. Candidates are bounded by a relation
rather than by the name alone (view-model types, plus the view's own `x:Class`),
because matching a bare name across a solution is conservative in the useless
direction. **A binding that names nothing is recorded on the view** as
`unresolvedBindings`: that is the failure XAML has no compiler for, where a
renamed property leaves an expression that resolves to nothing and reports itself
by drawing an empty control.

Where a `DataTemplate` carries `DataType`, the author has stated what the context
is, so bindings inside it resolve against **that type alone** — a declared relation
rather than a name heuristic, and the difference between a candidate set and an
answer. A binding that leaves the template deliberately (`RelativeSource`,
`ElementName`, an explicit `Source`) is not scoped to it, because there the
declared type is the wrong answer rather than a better one. A name the declared
type does not have is reported separately as `missingOnDataType`: with no declared
context an unmatched name may only mean the `DataContext` is outside the candidate
bound, but with one it is a defect.

`Constructs` runs from the member holding a `new X()` to the type X, carrying the
line. It is what answers *which implementation did the composition root pick* — an
interface's implementers are the candidates, and only construction says which one
production builds. A new-expression already produced a `Calls` edge to the
constructor, which looks like it makes this redundant and does not: that edge needs
a `.ctor` node at the far end, and a type whose constructor the compiler supplied
has none, so the plainest types — the ones a root news up and passes as an
interface — had no incoming edge of any kind and read as dead. Not emitted for
`typeof(X)` (that is `Registers`: consulting a type is not building one) or for
`new T()` on a type parameter, which resolves to no constructor, so an edge would
name a type the call site does not build.

Static initialization is the one shape whose edge starts at a **type** rather than
a member. `private static readonly IFoo Default = new Foo();` runs in the static
constructor, which is not a graph node — an implicit one has no syntax a reader
could open, and minting one would put every static constructor in the solution
into `uncovered_methods` as a confident untested claim, since nothing models
"first access runs it". So the declaring type is the near end; responses carry the
node kind, so the two shapes are told apart by reading rather than by guessing.

`Implements` runs at both granularities, the way `Contains` does. Type → interface
answers *which classes implement this*, carrying `depth`: 1 when the interface is
written in the type's own declaration, more when it arrives through a base class or
a base interface, so "what does this class itself declare" stays askable. Member →
member answers *which body runs* when a caller binds to a declaration, and that is
the one coverage and escape propagation widen across. Asking an interface **type**
for its implementers returned a well-formed zero until 2026-09-11, which is
indistinguishable from nobody implementing it — the answer was there, one node away
on the interface's method, and a reader who trusted the zero concluded the graph did
not index implementation at all.

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
test, so work done there is exercised even though no test calls it. **Both dispatch
mechanisms are widened**: a test that calls `I.M` reaches every in-solution
implementation of `I.M`, and a test that calls an abstract or virtual declaration
reaches every in-solution override of it, because the override is what runs.
Method-level `Implements` and `Overrides` edges carry the two joins, and `Overrides`
spans the whole chain, so a test reaching an abstract root also reaches a leaf two
levels below it. Not followed: property accessor bodies (source-generated setters
included), stored-and-forwarded delegates, and reflection — an empty `covering_tests`
answer names these in its `caveat` rather than reading as proof.

> Class overrides were not widened until 2026-09-05, and the cost was not a smaller
> answer but a wrong one: every override in a solution reported zero covering tests, so
> a doctrine or strategy layer — overrides all the way down — read as entirely untested
> while being the most exercised code in the repository. On gamehub one such method went
> from 0 to 130, and `uncovered_methods` was naming every override in the solution.
> Rebuild any graph saved before that date before trusting a coverage answer about an
> override.

**Coverage answers are paged, and the histogram is usually the answer.** A well-covered
method has hundreds of tests, so `covering_tests`, `covered_methods` and
`uncovered_methods` return a page with `totalMatches`, and `nextOffset` when there is
more — absent when there is not, so a caller never compares two numbers to learn it
finished. The first two also return `depthCounts`, the whole set's distribution by depth,
which is what tells you whether to page at all: on gamehub one method's 130 tests are 3
at depth 3 and 27 at depth 4, with 78 of them out at depths 9 and 10 — real exercise
versus blast radius, in one line. `depth` narrows to one band, and an unknown depth is
refused naming the ones present.

Offset rather than an opaque cursor, because a built graph does not change under the
reader. That needs a **total** order, so the coverage queries sort by depth *and* id;
sorting by depth alone left ties to edge enumeration order, and an offset into a
partially ordered result silently repeats and skips rows.

A "covered" method
is one some test *can* reach — not one a test asserted on. Edges are only emitted across
a project boundary, so `build_solution` is required; `uncovered_methods` excludes
bodiless interface and abstract declarations, which never bind a call and would
otherwise swamp the report. Coverage queries **refuse a graph containing no test
methods** — single-project builds and `--no-tests` / `excludeTests` builds — because an
empty answer would read as "everything is uncovered". `--no-tests` skips test projects
before compiling them, which on a well-tested solution drops most of the edge volume;
the skipped projects are always reported, on stderr and in the build summary's
`skippedTestProjects`.

**Extension methods are part of the type they extend, in both syntaxes.** A
reduced extension call (`money.Add(b)`) binds to the same node as the static
declaration — the `this` parameter unreduces before the id forms, so call edges,
coverage, and escape chains all flow through extensions. Extension-method nodes
carry an `extendsType` property and an `Extends` edge to the extended type when
it is in-solution, so "the working surface of `Money`" is the type's incoming
`Extends` edges plus its own members.

C# 14 `extension` blocks are modeled the same way and mint the same ids. A
member declared in a block is a node of the **static class holding the block**,
identified through the lowered static implementation the compiler emits — so
`Doubled()` inside `extension(Price price)` and `Doubled(this Price)` beside it
both become `m:PriceExtensions.Doubled(SampleLib.Price)`, and moving a codebase
between the syntaxes does not churn a single node id. The receiver leads the
parameter list for an instance member and is absent for a static one, and
extension *properties* carry it parenthesised
(`prop:PriceExtensions.IsFree(SampleLib.Price)`) because two blocks in one class
may declare the same member name on different receiver types. The block itself
is never a node: it has no name, and it is syntax rather than something a reader
navigates to.

> Until 2026-09-05 an extension block was dropped whole. The block is an
> `ExtensionBlockDeclarationSyntax`, which derives from `TypeDeclarationSyntax`,
> so the type walk reached it and the compiler-generated filter discarded it on
> its empty name — taking every member with it. The containing class still
> advertised the lowered implementation names in its `methods` property, so the
> type looked present while none of its members was a node or an edge endpoint,
> and `excess_visibility` reported a heavily-used loader as consumed by nobody.
> A graph built before that date is wrong in that specific way rather than
> merely incomplete; rebuild it.

**Properties and fields are nodes, statics included.** Every declared property and
field — any accessibility, instance or static — is its own node (`prop:` / `field:`
ids) contained by its type, carrying the declared type and `isStatic` / `isConst` /
`isReadOnly` / `isPublic`. Reads and writes are edges from the code that performs
them: methods and constructors; accessor and expression bodies attribute to the
*property* node (a computed property reading its inputs is real data flow, and
accessors are not Method nodes); member initializers attribute to the instance
constructors, static initializers to nothing — static ctors are not nodes. Compound
assignment and `++` yield both a Reads and a Writes edge; an `out` argument writes,
a `ref` argument does both. A member's declared type becomes a `References` edge to
every in-solution type it mentions — through `List<>` and array wrappers — so "who
uses this DTO" is the type's incoming References from the members typed by it, even
when the type appears in no call. Compiler plumbing stays out: backing fields,
record `EqualityContract`, and Razor codegen's `__` members are not nodes, and
`nameof` is a name, not an access.

**Methods include constructors, and disposal is a call.** Explicit instance
constructors are Method nodes — constructors run real code, and xUnit's primary setup
idiom is the test-class ctor. An implicit default ctor appears only when field
initializers run in it; static ctors stay out. A `using` / `await using` counts as a
Calls edge to the resource's `Dispose`/`DisposeAsync`. Methods whose bodies nest carry
a `bodyDepth` property stamped at build time, which feeds the deep-nesting report
(`deep_methods` / `query --deep`).

**A string that names a declaration is an edge to it.** `Quotes` edges run from the
code that produces a string to the node whose name it matches, carrying the value,
the line, and a `provenance`: `nameof` survives a rename, `literal` and
`interpolated` do not, and `attributeArgument` breaks a framework binding instead of
the build. That distinction is the reason this is done over syntax — `nameof(X)` and
`"X"` compile to identical instructions, so an IL reader cannot separate them.
`quoted_symbols` reports the breakable ones crossing a project boundary, which is the
coupling no per-compilation analyzer can even ask about: compiling the quoting
project, the quoted names are not in scope. Matching is by name and only when that
name is **unique** in the solution, so the report both under-reports (a string naming
something called `Dispose` is dropped) and over-reports (a string that merely equals
a name matches it). Rows are candidates to read.

**A node says what a person can change about it.** Type nodes carry `isInterface` —
interfaces are `Class` nodes unless DI registration promotes them to
`ServiceInterface`, so without the flag most of a codebase's interfaces are
indistinguishable from classes. They carry `isStatic`, which is what makes a
zero readable: a static class cannot be constructed by anyone, so an empty
incoming set is a fact about C# rather than about the codebase — on one stack,
sixteen of seventeen zero-degree types were static classes, and the most-called
type in the whole codebase was among them. They also carry `interfaces`, every interface the
type implements including the out-of-solution ones that get no edge, because
`baseType` alone reported `object` for a class with a full interface list — true,
and the least informative true thing the node could say about its own shape. Method nodes carry `isOverride` and
`isPrimaryConstructor`, because neither has a modifier of its own: an override must
match the base declaration, and a primary constructor *is* the type header. These
are what let `excess_visibility` offer only edits that exist. A node's `line` is its
declaration's, not its first attribute's — attributes are part of the declaration
node rather than leading trivia, so a graph built before this pointed one or more
lines high on every attributed declaration.

**Escape analysis is static reachability over in-solution code.**
`exception_escapes` (CLI: `query --escapes`) reports throwing operations that can reach
an application entry point — declared `static Main`, Razor page handlers, controller
actions, `(object, EventArgs)` event handlers, `async void` methods, overrides of
framework virtuals, implementations of framework-declared interfaces (`IMiddleware`,
`IValueConverter` — the invisible-registration pattern; core BCL plumbing interfaces
excluded), and callbacks registered with out-of-solution code — with no catch stopping
them. Everything is precomputed at build time: methods carry `throws` and
`entryPointKind` properties, `Calls` edges carry the catch guards of their sites
(`guardedBy`, with `filteredBy` kept apart because a `when` filter may decline at
runtime — it reports `conditional`, never handled), and a worklist sweep emits
`Escapes` edges carrying the exception type, hop depth, and one representative path.
Delegates are followed one hop: a method-group reference becomes an unguarded `Calls`
edge (`viaDelegate` — a try around the registration site does not catch the later
invocation), and a method group or lambda handed to a framework marks its target (or
the lambda's container) `callback`. Interface dispatch widens to in-solution
implementations — callers bind to the interface, the throw lives in the
implementation, and the DI-default architecture of ASP.NET depends on that join
(method-level `Implements` edges carry it) — while class virtual overrides are not
widened, even though the `Overrides` edges coverage uses are now in the graph. That
asymmetry is deliberate for now and worth knowing: widening coverage removes false
"untested" claims, while widening escapes manufactures new escape paths, so the two
carry opposite risks and the second wants its own evidence. Middleware is its own entry kind (`IMiddleware` or convention
`*Middleware.Invoke[Async](HttpContext, …)`), and middleware that catches around
`next` is an **exception boundary**: its catch set is recorded
(`boundaryCatches`/`boundaryCatchesFiltered`, `IExceptionHandler` as a conditional
catch-all), and escapes into HTTP entry points carry a disposition —
`interceptedBy` a boundary means a shaped response by design (a ValidationException
becoming a 400), no interception means a raw 500. Status-code *returns* (404,
ProblemDetails) are not exceptions and never appear. Blind spots come back as data
in the tool's `caveats`: BCL and out-of-solution throwers are invisible,
stored-and-forwarded delegates and local functions are not followed, pipeline order
and Map branches are not modeled, and top-level-statement `Main` and minimal-API
lambda endpoints are not entry points.

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
