# Visual Editor Experience Prototype

This is a dependency-free interaction prototype for the intended UnifiedScript
authoring experience. It is deliberately not a Blueprint-style one-node-per-
expression graph.

Run it with the real CoreCLR debug bridge:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  debug-serve samples/Authoring/DamagePolicy.cs \
  --root prototype/visual-editor \
  --port 4173
```

Then open <http://127.0.0.1:4173>.

Try these interactions:

1. Select script-state declarations in the left panel and change type, default,
   or engine exposure in the inspector.
2. Add `ArmorRatio` with the `+` button. The prototype updates the declaration,
   canonical-text preview, and AI outline together.
3. Switch between Behavior, Dual, and Source. Dual temporarily gives the
   inspector width to the side-by-side semantic/source comparison.
4. Click `AI LENS` to inspect the compact semantic outline and preview a real
   `unifiedscript.declaration-patch/v0.1` document.
5. Click `RUN LIVE`; the real CoreCLR thread stops on the `Math.Clamp`
   statement breakpoint and exposes its live frame, IL offset, and locals.
6. Click `STEP OVER` to pause on `return scaled`, then `CONTINUE` to receive the
   canonical runtime result.
7. Change a declaration and click `COMMIT PATCH` (or press Ctrl/Cmd+S). The host
   writes canonical C# plus `.usids`, regenerates the artifacts, and reloads the
   same CoreCLR transport.
8. Select the `Math.Clamp` card, edit `Clamp minimum`, and click
   `SAVE EXPRESSION`. This submits Semantic Patch v0.19 by literal Semantic ID.
9. In `AI LENS`, preview the declaration patch and click
   `APPLY TO SOURCE + CLR` to apply that exact typed document.

## Interface direction

The workbench uses a semantic drafting-table model rather than Blueprint
chrome. Dark surfaces own the asset contract, semantic inspector, authoring
commands, and runtime state. The behavior itself sits on a light engineering
canvas with one continuous semantic spine. Orange means control flow or a
write transaction, green means synchronization or entry/return, and blue means
data. This makes the same behavior readable at a glance without turning every
operator or source line into a box.

## Live integration

The workbench first loads `/api/authoring/workspace`, whose Declaration IR v0.1,
Graph v0.12, source, revision, and CoreCLR generation come from one committed
`samples/Authoring/DamagePolicy.cs` compilation. The checked-in
`data/damage-policy.*` artifacts remain a read-only fallback. Names, types, defaults,
exposure flags, card identities, source lines, control regions, and inline
expressions therefore come from the compiler pipeline rather than embedded UI
mock data. If any artifact or cross-artifact identity check fails, the
prototype visibly falls back to its embedded demonstration workspace.

The Graph contains 17 nodes. The UI intentionally promotes only six semantic
actions/regions and reconstructs identifiers, literals, binary expressions,
member access, and invocation arguments inline. State-reference tokens carry
Declaration IR IDs. The `Math.Clamp` card owns the Graph assignment-expression
ID, `debugTargetId` identifies the enclosing probeable HIR expression
statement, and the nested invocation retains its own third expression ID.

Inspector edits and the AI suggestion are translated into typed `addState`,
`updateState`, or `removeState` operations with real baseline IDs, target
Semantic IDs, and full old-value preconditions. The same JSON accepted by the
CLI is submitted to `/api/authoring/declaration-patch`; promoted behavior inputs
use `/api/authoring/semantic-patch`. `debug-serve` prepares an immutable
candidate, reloads CoreCLR, transactionally writes C# plus `.usids`, and returns
the regenerated Declaration IR/Graph/source generation. It refuses a write if
an external editor changed either file. The same host validates artifact
identities, materializes Script Asset receivers, and drives Run, Continue, Step
Over, pause location, locals, and result through the live CoreCLR transport. No
execution timing, local-value fixture, or browser-only mutation remains.
