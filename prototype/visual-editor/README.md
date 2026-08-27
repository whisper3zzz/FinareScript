# Behavior Workbench M6.0

This dependency-free frontend demonstrates the intended UnifiedScript
authoring experience. It is a structured semantic workbench, not a
Blueprint-style one-node-per-expression graph.

## Run the live single-asset workflow

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  debug-serve samples/Authoring/DamagePolicy.cs \
  --root prototype/visual-editor \
  --port 4173
```

Open <http://127.0.0.1:4173>.

## Run the multi-asset/control-flow workflow

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  debug-serve samples/Authoring/MultiAssetControlFlow.cs \
  --root prototype/visual-editor \
  --port 4173
```

The asset picker now exposes two Script Assets. `CombatRules` contains an `if`
function and a `while` function; `InventoryRules` contains a `for` function.
Changing asset or function replaces the declaration contract, behavior
hierarchy, source selection, inspector, AI outline, debug method, arguments,
and receiver without any sample-specific frontend adapter.

## Things to try

1. Switch Script Assets and Visual functions from the top/left navigation.
2. Inspect compact action cards. Literals, identifiers, binary expressions,
   member access, and pure calls stay inline instead of becoming tiny nodes.
3. Inspect `if` and loop regions. Their lanes come directly from Graph region
   ownership plus graph pins/edges through Authoring Surface v0.4.
4. Select a card with an `EDIT` badge. The inspector renders every literal or
   operator capability declared by the Surface and submits Semantic Patch
   v0.19 with complete typed preconditions.
5. Select a state declaration, change its name/type/default/exposure, and
   commit Declaration Patch v0.1. State is never declared in Blueprint.
6. Add a state with `+`; text, Surface, AI, and a future UE reflection adapter
   receive one declaration and one semantic identity.
7. Switch among Behavior, Dual, and Source.
8. Open AI Lens to see coordinate-free declarations/actions/identities and
   preview a real typed declaration patch.
9. Enable action breakpoints and run, or run without one and request Pause. The
   same CoreCLR method stops on explicit semantic targets. Inspect any active
   Visual frame and its detached locals, evaluate a bounded read-only watch,
   then continue or step in, over, and out.

## Data flow

The live page loads `unifiedscript.authoring-workspace/v0.5`, validates the
published Visual C# v0.3/opaque-code boundary, and shows source-only method
counts and call-node ownership. One committed
workspace revision contains canonical source, Declaration IR v0.1, Graph
v0.13, Authoring Surface v0.4, transaction history, and a CoreCLR generation. After a declaration or
behavior write, the server prepares and validates a full candidate, reloads the
same CoreCLR transport, transactionally commits C# plus identity sidecar, and
returns a completely regenerated workspace.

Undo and Redo use the same server-owned transaction boundary and are also
available through Ctrl/Cmd+Z, Shift+Ctrl/Cmd+Z, and Ctrl/Cmd+Y. Each request
carries the rendered baseline revision plus a unique operation ID. History
navigation restores the exact source and stable identities, republishes all
artifacts, and reloads a new CoreCLR generation.

The browser does not infer action promotion, reconstruct expressions from Graph
edges, discover region ownership, guess state references, or choose patch
preconditions. `UnifiedScript.Authoring` does this once in .NET and publishes a
strict engine-neutral Surface. The frontend only renders that Surface and
translates an advertised capability to its existing patch operation.

The checked-in `data/damage-policy.*` files provide a read-only fallback. They
include source, Declaration IR, Graph, and Surface generated from the same
sample and compilation identities.

## Interface direction

Dark contract/runtime surfaces surround a light engineering canvas. Orange
indicates structured control or a write transaction, green indicates
synchronization and entry/exit, blue indicates state/data, and violet indicates
calls. A continuous semantic spine and nested regions make execution order
legible without imitating source lines or Blueprint wire density.

This frontend is still plain HTML/CSS/JavaScript because its purpose is to
prove the information model and interactions. A future UE Slate implementation
should consume the same Surface, Declaration Patch, Semantic Patch, and debug
contracts instead of porting browser-specific interpretation logic.
