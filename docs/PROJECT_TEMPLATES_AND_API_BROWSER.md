# Project templates and generated API browser

Finare ships a versioned project-template catalog and a searchable API/
diagnostic browser. Both are project-owned surfaces: templates create ordinary
source/configuration files, while the browser is regenerated from the exact
Unreal API Contract, node catalog, and coverage report used by bindings and the
editor palette.

## Template catalog

`Templates/UnrealProject/finare.project-templates.json` in the SDK uses
`unifiedscript.project-template-catalog/v0.1`. It currently declares three
templates:

| ID | Starting point | Product boundary |
| --- | --- | --- |
| `minimal` | lifecycle, exposed state, one Visual method | no game-specific API or replication |
| `visual-gameplay` | structured health/shield/movement policy | no network authority or project Actor bindings |
| `data-plane` | World-scoped command buffer and batch trace | no project-specific semantics or portable benchmark claim |

Create a project from one catalog entry:

```powershell
powershell -ExecutionPolicy Bypass `
  -File "$env:FINARE_SDK_ROOT\Tools\new-finare-project.ps1" `
  -Project $env:FINARE_PROJECT `
  -EngineRoot $env:FINARE_UE_ROOT `
  -Template visual-gameplay `
  -ScriptName PlayerPolicy
```

The scaffold report is `unifiedscript.project-scaffold-report/v0.2`. It records
the selected template, catalog SHA-256, capabilities, non-claims, every created
file, and the onboarding outputs. Unknown IDs, duplicate catalog entries,
unsafe template paths, unresolved placeholders, and existing outputs fail
before project integration.

Source acceptance runs every catalog entry through the public scaffold command:

```powershell
powershell -ExecutionPolicy Bypass `
  -File tools/test-finare-project-templates.ps1 `
  -EngineRoot $env:FINARE_UE_ROOT
```

The runner creates isolated minimal UE project shells and verifies every
rendered source/configuration, managed solution, IDE workspace, task/debugger
contract, and manifest without running the expensive Unreal onboarding loop.
The public scaffold's normal mode runs Build/Onboard first, so templates that
use generated Unreal APIs receive their typed bindings before the managed
project is compiled; it then verifies Declaration IR and Authoring Surface
output. `-SkipOnboarding` deliberately defers those generated artifacts. The
acceptance report schema is
`unifiedscript.project-template-acceptance/v0.1`.

## One effective Contract, five generated consumers

Project onboarding now emits the browser as part of the existing transactional
binding generation:

```text
managed source use -> dependency requests
                            +
Unreal reflection + reviewed base Contract
        -> safe development Contract overlay
        -> typed managed bindings
        -> native thunk table
        -> visual node catalog
        -> binding coverage report
        -> API/diagnostic browser
```

The browser uses `unifiedscript.unreal-api-browser/v0.1`. Its `sources` object
names all three input schema versions and contains a SHA-256 over their canonical
content. Every browsable node retains the generated member ID, Contract path,
binding disposition, category, tooltip, typed pins/defaults, World/thread/
latency/deprecation constraints, availability, and stable coverage diagnostic
codes. Struct make/break nodes are explicitly marked `visualOnly`; the UI does
not invent a runtime route.

`ue-bindings` writes it in the same all-or-nothing output transaction:

```powershell
finare ue-bindings Generated/Game.reflection.json `
  --contract Contracts/Game.unreal-api-contract.json `
  --out Managed/Game.Finare/Generated/Unreal.bindings.generated.cs `
  --native-out Plugins/Finare/Source/Game/Generated/Game.thunks.cpp `
  --nodes-out Generated/Game.api-nodes.json `
  --browser-out Generated/Game.api-browser.json `
  --report Generated/Game.binding-coverage.json `
  --fail-on-rejected
```

New adopted projects first run `ue-surface-expand` inside project-local Setup.
It emits the versioned source request set, development Contract, and
development-surface decision report. Only a completely safe result becomes the
effective `--contract` input shown above; an ambiguous, missing, manual, or
unsupported request keeps the reviewed base Contract unchanged and emits an
exact source-linked diagnostic. This preserves one identity across all five
generated consumers without requiring a Contract edit before first Play.

`setup-unreal-project.ps1` regenerates and byte-compares the browser during its
determinism phase. New scaffold configurations declare `bindings.browserOutput`;
older v0.1 configurations receive a deterministic sibling of `api-nodes.json`.
After changing a shared managed assembly, use normal Apply onboarding so the
deployed assembly, Demo assembly, and `.uschunk` dependency identities advance
together. `-ValidateOnly` deliberately checks the existing deployed baseline;
it is not a substitute for rebuilding that baseline.

## Browse from the Workbench

`start-finare-authoring.ps1` validates the generated browser and passes it
to the local authoring server. The server exposes a read-only
`GET /api/unreal/browser` endpoint. The Workbench **API + DIAGNOSTICS** drawer
supports:

- full-text search across types, members, paths, pins, routes, and diagnostics;
- route filters for direct thunk, CallFrame, data plane, reflection, rejected,
  and visual-only nodes;
- exact pin types/defaults and World, thread, latent, and deprecation rules;
- stable diagnostic-code lookup back to affected Contract paths.

The endpoint serves only the validated generated document. It cannot mutate the
Contract or bindings, and non-GET methods are rejected. Contract changes still
go through normal source review and onboarding regeneration.

## Current boundary

The catalog is a reviewed starting point, not a game generator. It does not
create maps, game-specific assets, replicated authority policy, or native game
classes. The browser describes the effective project Contract: the reviewed
base plus a fully accepted local development overlay when enabled. Unreferenced
APIs and requests with open decisions are intentionally absent. Runtime diagnostic instances remain in the
debug panel and privacy-safe diagnostics bundles—the browser catalogs stable API
generation/coverage diagnostics rather than retaining gameplay failure data.
