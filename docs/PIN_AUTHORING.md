# Typed Pin Authoring

## Purpose

M8.2 adds source-backed Pin Defaults and **Promote to Local** to the native
Unreal GraphEditor workspace. These controls edit the C# program that owns the
graph; they never mutate transient `UEdGraphPin::DefaultValue` state or create a
second graph-only executable representation.

Select a node and use **PIN AUTHORING** in Details. Every connected data input
shows its semantic port name, type, current compiler-owned source, a C# literal
editor, **Reset**, and a suggested conflict-free local name.

## Typed value operation

`unifiedscript.patch/v0.28` adds `setDataInputValue`:

```json
{
  "operation": "setDataInputValue",
  "targetNodeId": "us_consumer",
  "targetPort": "right",
  "expectedEdgeId": "us_edge",
  "expectedSourceNodeId": "us_old_value",
  "expectedSourcePort": "value",
  "expectedTypeId": "us_int_type",
  "newValue": {
    "expression": "literal",
    "valueText": "42",
    "expectedTypeId": "us_int_type"
  }
}
```

The exact edge, both endpoints, both ports, and the semantic type are
preconditions. The Roslyn patcher resolves the current Graph edge again, lowers
the new value through the closed graph-expression algebra, checks the runtime
literal type, applies only the selected input, recompiles, rebinds, and
regenerates the Surface. A stale graph, `false` on an `int` pin, an unsuffixed
`double` on a `float` pin, or an unsupported value rejects before publication.

The editor accepts one bounded C# scalar literal. Signed numeric input is
encoded as the existing typed unary expression. **Reset** emits a typed
`default` expression; the source patcher materializes the canonical value for
Boolean, character, integer, floating-point, string, and enum graph-safe types.

## Promote to Local

**Promote to Local** emits the existing `introduceTemporary` operation against
the expression currently feeding the selected pin. The request includes the
containing statement, owning block, previous/next statement identities, chosen
name, display type, and semantic type ID. The source patcher then:

1. verifies that the expression and statement still occupy the same source
   location;
2. rejects name conflicts and non-graph-safe types;
3. proves that moving evaluation is stable and effect-free;
4. inserts the typed declaration immediately before the statement; and
5. replaces only the selected source expression with the new local reference.

The first safe surface supports expressions inside explicit local initializers
and return statements. Inputs in loop conditions or other contexts whose
evaluation frequency/order could change are rejected rather than silently
changing gameplay behavior.

## Transaction and UI behavior

Set, Reset, and Promote use the same Workbench endpoint, baseline revision,
transaction ID, optional user-defined group, artifact rebuild, Undo/Redo, and
live component reload path as every other semantic edit. The old node and wire
remain visible until the Workbench accepts the patch and publishes a new
compiler-owned Surface.

Literal values are already folded into Blueprint-style consumer pin labels
(for example, `Value = 100`). The Details editor exposes the exact C# spelling
and source identity without forcing literal/identifier leaf nodes back onto the
main canvas.

## Deliberate boundary

This tranche authors values and locals; it does not make transient Unreal pins
authoritative. The following M8.2 canvas tranche now supplies layout-only
Comment Boxes and typed Reroutes plus personal viewport/minimap state without
weakening that rule. See [CANVAS_AUTHORING.md](CANVAS_AUTHORING.md).
