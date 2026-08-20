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

## Honest boundary

The existing compiler owns method-body HIR/Graph, semantic patching, CoreCLR
emission, reload-safe breakpoints, and cooperative live debugging. This UI is
not yet connected to those services. More importantly, type-level script state,
defaults, and UE exposure metadata are not yet first-class in the shared HIR.
The prototype is intended to validate that Declaration IR and the non-spaghetti
editing model before implementing that backend slice.
