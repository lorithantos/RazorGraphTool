# Binding — named binding points

Resolving a **name** to the thing that serves it, where the coupling is by string
rather than by symbol and therefore invisible to the compiler.

This is a separate concern from `Razor/` (parsing one file), `Roslyn/` (the
semantic model) and `Emitters/` (writing nodes and edges). It grew out of one
mis-behaving helper on the Razor emitter, and it is a module because the concept
turned out to be much larger than that helper:

| Framework | The name | Resolved against |
|---|---|---|
| ASP.NET MVC | `return View("Foo")` | `Views/{Controller}/Foo`, then `Views/Shared/Foo` |
| OrchardCore | shape name plus alternates | Razor / Liquid / code bindings, theme precedence |
| Umbraco | document-type alias | a view of the same name; `[Alias]Controller` hijacking |

## Why it exists

Three properties make this worth modelling rather than resolving inline:

1. **Resolution is one-to-many and precedence-ordered.** A name can be served by
   several templates — an area, a theme, a Razor Class Library. Choosing one
   asserts a resolution that depends on runtime configuration, so the resolver
   returns every candidate in order and declines to pick a winner.
2. **Failure is a runtime error, not a compile error.** OrchardCore's own docs:
   an unresolved binding "throws an `InvalidOperationException` that names the
   missing shape type and active theme. There is no implicit catch-all HTML
   rendering." A name with no candidate is therefore a finding, and the most
   valuable thing this module produces.
3. **A rename breaks exactly one side, silently.** The same hazard as
   `ViewDataReadBy` and `DomSelectedBy`, which the graph already models — this is
   that pattern one layer up, and for OrchardCore it is the primary structure of
   the application.

## Shape of the module

- `ViewNameResolver` — the search-path rules. Pure, no graph, no Roslyn, so the
  naming grammars are unit-testable on their own.
- `ShapeNameGrammar` — the OrchardCore name-to-file reordering.
- `ViewCallScanner` — what the semantic model says a call renders, and which
  methods are actions.
- `PlacementReader` — the config half of the same question.
- The emitter turns names into `NamedBinding` nodes with `Produces` and `BoundBy`
  edges, in a post-pass once every project is in the graph.

## Not every mention demands a template

The finding — a name nothing serves — is only worth reading if it is nearly all
true. Three mentions cannot fail that way, and each is recorded on the node while
staying out of the report:

| Mention | Why a miss is survivable |
|---|---|
| An alternate (`Metadata.Alternates.Add`, or `alternates` in placement) | Alternates are *tried*; a missing one falls back to the base shape. That is the override mechanism working |
| A `placement.json` key | Placing a shape another module owns is ordinary — Contents arranges parts it does not define |
| A shape named in test code | A fixture renders nothing. OrchardCore's solution produced exactly one of these, `ContentZone` from `DisplayDriverTestHelper`, and it was 1 of the 4 reported |

The mirror of that: a **wrapper** and a **substituted shape** (`"shape": "X"`) are
rendered as shapes in their own right, so a missing template for either does
throw, and both are required.

Because these facts arrive in no fixed order — a driver names a shape, a
placement rule half a solution away hides it — nothing is judged until every
mention is in. Binding is a sweep at the end, not a decision at first sight.

## placement.json is code for this purpose

Config that can stop a page rendering is not configuration in the sense the graph
can ignore. `placement.json` adds names (`alternates`, `wrappers`, `shape`) and
can drop a shape entirely with `"place": "-"`. The schema comes from OrchardCore's
own `PlacementFile` type rather than from its documentation, so the two cannot
drift.

Two details are load-bearing:

- **These files carry comments.** `OrchardCore.Contents` ships a `/* */` banner
  and a block of `//`-commented example entries. A strict JSON parser fails on the
  framework's own file, so the reader skips comments and allows trailing commas,
  as the runtime does.
- **Only an UNCONDITIONAL hide retires a finding.** A hide behind a content type
  or display type still leaves every other render live. Positions are read with
  `Utf8JsonReader` for the same care: Contents names a shape in its banner eight
  lines above the real entry, and a finding that points at a comment sends a
  reader to the wrong place.

## A helper renders its caller's view

`return View(model)` inside a *private* method takes the view name from route data
— it is the **invoking action's** name, not the helper's. The helper's own name is
the one answer guaranteed to be wrong, and claiming it reported templates missing
that never existed.

What the helper does have is callers, and the graph already holds them as `Calls`
edges. The edge therefore runs from each calling action to the view it thereby
renders, carrying `via` — which is also the direction the question gets asked in.
The caller's *routing* name is used, not its method name, because
`[ActionName(nameof(Login))] LoginPOST` renders `Login.cshtml`.

One hop only. A helper reached through another helper stays unresolved rather
than followed: the chain fans out to actions that never reach the call, and a
wrong edge is worse than a missing one.

## Two rules that were learned the hard way

**Match path SEGMENTS, never substrings.** The rule this replaced was a
case-insensitive `Contains` over the whole relative path taking the first hit.
Within the single project `OrchardCore.Users`, the partial name `User` matched 100
of its 167 templates and the first-wins pick was an unrelated dashboard view.

**Take candidates from the GRAPH, not the filesystem.** Enumerating `*.cshtml`
under a project directory is wrong in both directions: it cannot see views in a
referenced Razor Class Library, which ASP.NET does resolve against, while it
freely matches unrelated files inside the project. The graph already carries
`Project` nodes, `DependsOn` edges and per-node project attribution — scoping to
the referencing project and its references prunes the noise at source. A graph
tool resolving by filesystem scan is the inversion DESIGN-NOTES section 4 exists
to prevent.
