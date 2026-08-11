# Checks — the other half of the job

The graph answers **what is there**. A rule answers **whether it is right**.

That second half matters more than usual for Lua, and the reason is measurable
rather than aesthetic. Across Adobe's 115 sample plug-ins, **86 of 738 catalogued
SDK functions are ever called** — the working vocabulary is about a tenth of the
surface. A model writing this code has weak priors and no signal that they are
weak: Lua is a small slice of public code, what exists skews 5.3/5.4, and
Lightroom embeds **5.1**. So the likeliest error in generated Lua is a construct
that reads perfectly and does not exist in the host.

## What runs

| Rule | Owner | Claims |
|---|---|---|
| `lua.dialect` | the language | A construct this host's Lua rejects that a later Lua accepts |
| `lightroom.sdk-surface` | `LightroomHost` | Retired modules, calls the catalogue does not carry, floors held up by an import nothing calls |

Language rules run for every host. A host's own rules arrive with it.

## Extending

Rules plug in through `ILuaHost.Rules`, which is the seam this module already
uses for module resolution, vendor classification and node annotation:

```csharp
public sealed class MyHost : ILuaHost
{
    public IEnumerable<ILuaRule> Rules => [new MyRule()];
    // ... the three required members
}
```

`Rules` is a default interface member, so a host with no opinion never mentions
it and still gets the language rules. **There is no registry and no switch over
host names** — an environment brings its knowledge with it, which is the only
arrangement where a third party can add a host without touching this project.

A rule receives the finished graph, the per-file declarations, and the host. It
gets the *finished* graph deliberately: the questions worth asking are
cross-file, and a per-file check could only ask the shallow ones.

## Two rules for writing rules

**Return nothing rather than guess.** A finding stream is worth reading only if
it is nearly all true, and one noisy rule discredits every other. Severity is a
claim about certainty, not importance:

- `Error` — true by construction. The parser rejected it; the module is gone.
- `Warning` — probably wrong, resting on a catalogue that may be incomplete.
- `Note` — worth knowing, claims nothing.

**Say what you observed, not just what you concluded.** Every finding carries
`Evidence` separately from `Message`, so a reader can check the claim instead of
believing it.

The catalogue's own limits are the reason for most of the `Note`s here. It holds
SDK 3.0, 8.0 and 15.3 while Adobe has shipped more, and 30 of 844 documented
functions carry no version at all — so *"this catalogue does not carry that
function"* is a statement about us, and it is phrased as one.

## Findings ride the build

Rules run during `Build`, and findings land in `LuaBuildReport.Findings`, so they
reach the CLI and the MCP envelope without either learning about individual
rules. A check nobody runs is not a check.

## Why this project pins a Loretta nightly

`0.2.13` — the newest stable, from March 2025 — cached lexer tokens without
accounting for the syntax options in force. Parse a Lua 5.1 file first and
`goto` / `::label::` stayed unrecognised for the rest of that process, even where
a later parse set `acceptGoto`: the option read `True` and the parse failed
anyway.

That silently disabled the `goto` half of `lua.dialect` — the single likeliest
mistake in generated 5.1 code, and the case the rule exists for — **while its
test passed**. In a full suite another class parsed a goto-accepting dialect
first, so the rule worked; run alone, the same test failed. Green in the suite,
wrong in the product.

It is upstream issue **#152**, fixed 2025-07-24, and there has been no stable
release since. Hence `0.2.14-nightly.26`. Verified cold and out of process: both
`goto` and integer division are caught with no workaround, and a file that used
to fall through as an ordinary parse failure is now correctly attributed to
Lua 5.2.

Three things came out of the episode and none of them were the version bump:

- **The rule measures its instrument.** `DialectDiscriminationWorks` parses a
  canonical snippet under both dialects before the rule reads anything, and the
  rule reports that it *could not run* rather than reporting nothing. That guard
  outlives this bug, because the failure mode of any parser that cannot separate
  dialects is silence — and silence otherwise reads as success.
- **A test that passes only in company is not evidence.** Both order-dependent
  bugs in this codebase — this one and the MSBuildLocator race — were found by
  *using* the tool, never by running the suite.
- **A confident mechanism can be wrong.** The first explanation here was that
  `LuaSyntaxOptions.All` is "a union of conflicting syntaxes" rejecting the goto
  idiom because `::label::` is a type cast in Luau. Plausible, written into a
  commit message, and false — it was a symptom of the cache bug. Recorded because
  it stood for a whole session.

## What the corpus caught

Running these against Adobe's own samples immediately produced a false positive
worth keeping as a lesson:

```lua
local logger = import 'LrLogger'( 'FlickrAPI' )   -- the import result is CALLED
logger:trace(...)                                  -- so this is a method on the object
```

`logger` holds what the module returned, not the module, so `logger:trace()` was
being reported as a missing `LrLogger.trace`. Three of Adobe's own plug-ins
tripped it. The vendor corpus is not just something to exclude from a user's
graph — it is the reference implementation to test rules against.
