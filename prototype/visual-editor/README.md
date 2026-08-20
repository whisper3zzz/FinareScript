# Visual Editor Experience Prototype

This is a dependency-free interaction prototype for the intended UnifiedScript
authoring experience. It is deliberately not a Blueprint-style one-node-per-
expression graph.

Run it locally:

```bash
cd prototype/visual-editor
python3 -m http.server 4173
```

Then open <http://127.0.0.1:4173>.

Try these interactions:

1. Select script-state declarations in the left panel and change type, default,
   or engine exposure in the inspector.
2. Add `ArmorRatio` with the `+` button. The prototype updates the declaration,
   canonical-text preview, and AI outline together.
3. Switch between Structure, Split, and Code.
4. Click `AI VIEW` to inspect the compact semantic outline and preview a
   semantic patch.
5. Click `DEBUG RUN`; execution stops on the `Math.Clamp` statement breakpoint
   and exposes a small live-locals strip.

## Current integration boundary

The declaration list and inspector load
`data/damage-policy.asset.json`, a real `unifiedscript.script-asset/v0.1`
artifact generated from `samples/Authoring/DamagePolicy.cs`. Names, types,
defaults, exposure flags, and Semantic IDs therefore come from the compiler
pipeline rather than embedded UI mock data. If the artifact cannot be fetched,
the prototype visibly falls back to its embedded demonstration declarations.

Editing still mutates browser state only. Declaration semantic patches and
transactional C# write-back are the next M5.5 slice. The structure cards and
debug animation also remain experience fixtures until the real Graph artifact
and CoreCLR debug protocol are connected.
