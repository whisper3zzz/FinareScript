# M6.0 — Generic Authoring Surface

## Outcome

M6.0 removes the last sample-specific interpretation layer from the authoring
prototype. Declaration IR and Graph now project through a reusable .NET
contract before any browser or future UE UI renders them. Multiple Script
Assets, multiple Visual functions, structured branches, structured loops,
inline expressions, semantic edits, and live debug targets use the same
versioned Surface.

## Delivered

- new `UnifiedScript.Authoring` project with no browser, Roslyn, Runtime, or UE
  dependency;
- strict deterministic `unifiedscript.authoring-surface/v0.1` JSON;
- generic Declaration/Graph-to-Surface projection;
- multi-asset and multi-function hierarchy;
- exact region/action ownership for `if`, `while`, `for`, and other existing
  Graph region kinds;
- compact recursive expression summaries and declaration-reference IDs;
- typed literal and operator edit capabilities that produce Semantic Patch
  v0.19 operations;
- explicit optional debug-target and required source-span identities per action;
- `analyze` Surface artifact emission;
- authoring workspace v0.2 delivery after every committed reload;
- Surface-driven asset/function navigation, region rendering, inspector, AI
  outline, source selection, and live CoreCLR debugging;
- checked-in fallback Surface artifact;
- a two-asset/three-function `if`/`while`/`for` sample and automated
  ownership/round-trip/HTTP tests.

## Acceptance evidence

- the Workbench JavaScript contains no sample asset, function, state, or API
  names;
- every Surface action belongs to exactly one direct region;
- every Surface function joins Declaration IR and Graph by Semantic ID;
- literal/operator controls carry the complete typed patch precondition;
- browser verification loads the live workspace, renders the structured `if`
  region, exposes a generic operator editor, applies and reverses an operator
  patch, then pauses the real CoreCLR method on a Surface debug target;
- the full build, xUnit suite, CLI smoke tests, and repository validator pass.

## Deferred to M6.1

M6.0 deliberately does not bind Unreal Engine. UE reflection extraction,
UObject lifetime, world-scoped hosting, Slate integration, cook/packaging, and
the generated host component begin only after this engine-neutral contract is
stable.
