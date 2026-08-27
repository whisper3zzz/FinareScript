# Native Canvas Authoring

## Purpose

M8.2 completes the first Blueprint-grade canvas workflow in the native Unreal
Authoring Surface. Semantic nodes and wires still come from C# and compiler
artifacts; the editor now owns the non-executable organization around them:
comments, wire routing, viewport navigation, selection framing, and familiar
keyboard gestures.

There is no second executable graph. A canvas edit either changes the
engine-neutral `.usgraph` layout sidecar or the local editor preference file.
It cannot silently change C#, HIR, Graph JSON, `.usids`, or a running World.

## Comment boxes

Use **Add Comment** or press `C` while the graph has focus. If semantic nodes are
selected, the new comment frames them with padding and records their stable node
IDs. Without a selection it is created at the current paste location. Unreal's
native comment node supplies move, resize, rename, selection, and grouped-node
movement behavior.

The sidecar stores a layout-local comment ID, title, bounds, collapsed state,
and sorted semantic membership. Renaming, moving, resizing, or deleting a
comment is detected by the canvas fingerprint and atomically saved. Comment IDs
never enter source or compiler identity.

## Typed wire reroutes

Double-click a visible wire segment to insert a native knot. The knot copies the
exact pin category and semantic type of that segment and retains the
compiler-owned edge ID. Multiple knots preserve an ordered, contiguous control
point list in `.usgraph`.

Deleting a knot reconnects its preceding and following segments before Unreal
removes the node. Later point indices are compacted, so saving and reopening the
surface cannot reorder the route. The visible wire count continues to report
semantic edges rather than inflated presentation segments.

Reroutes are intentionally layout-only. Dragging a compiler-owned endpoint still
uses the typed Workbench reconnection transaction; a knot cannot bypass source
preconditions or manufacture a new executable edge.

## Viewport and minimap

The bottom-right minimap renders node/comment bounds, routed wire segments, and
the current viewport. Click or drag inside it to center the main graph without
changing the current zoom. The **Minimap** checkbox controls its visibility.

Each absolute Surface path plus function semantic ID gets an independent
viewport entry in `GEditorPerProjectIni`. Panning and zooming are sampled only
when the view changes, restored when the function is reopened, and flushed on
normal editor persistence. Minimap visibility is also a per-project user
preference.

Viewport state is deliberately absent from `.usgraph` v0.1: it is personal
session/navigation state and must not create source-control conflicts. Comments
and reroutes remain in `.usgraph` because they are shareable canvas structure.

## Common Unreal interactions

- `Ctrl+Space`: focus Palette search.
- `C`: create a comment at the selection or paste location.
- `F`: frame the selection; with no selection, frame the whole function.
- `Home`: frame the whole function.
- **Focus Selection** and **Focus All** expose the same operations without a
  keyboard gesture.
- Standard GraphEditor pan, wheel zoom, marquee selection, node movement,
  comment manipulation, and Delete handling remain native Unreal behavior.

## Validation boundary

The focused native Automation test loads the checked-in
`samples/Layout/DamageRules.usgraph`, verifies comment membership and ordered
route points, round-trips a transient canvas through the native layout store,
checks reroute deletion/reindexing, and verifies the wire-double-click delegate.
The ordinary UE 5.8 Editor build is still required because the minimap and
GraphEditor interaction are native Slate code.
