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
3. Switch between Structure, Split, and Code.
4. Click `AI VIEW` to inspect the compact semantic outline and preview a real
   `unifiedscript.declaration-patch/v0.1` document.
5. Click `DEBUG RUN`; the real CoreCLR thread stops on the `Math.Clamp`
   statement breakpoint and exposes its live frame, IL offset, and locals.
6. Click `STEP OVER` to pause on `return scaled`, then `CONTINUE` to receive the
   canonical runtime result.

## Current integration boundary

The workbench loads `data/damage-policy.asset.json`,
`data/damage-policy.graph.json`, and `data/damage-policy.source.cs`: real
Declaration IR v0.1, Graph v0.12, and source artifacts generated from the same
`samples/Authoring/DamagePolicy.cs` compilation. Names, types, defaults,
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

Inspector edits and the AI suggestion are translated into typed `addState` or
`updateState` operations with real baseline IDs, target Semantic IDs, and full
old-value preconditions. The preview JSON is accepted by
`apply-declaration-patch`, whose backend transaction writes C#, Declaration IR,
and an optional `.usids` sidecar only after full reanalysis. This dependency-
free page does not itself submit mutations, so browser editing remains unsaved
until a host integration sends that patch to the backend. `debug-serve` does
connect the read/execute side: it validates artifact identities, materializes a
Script Asset receiver from state IDs and current values, and drives Run,
Continue, Step Over, pause location, locals, and result through the live
CoreCLR loopback transport. No execution timing or local-value fixture remains.
