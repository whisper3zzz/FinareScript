# Region-aware control-flow reconnection

Semantic Patch v0.22 introduces `moveStatement` as the first safe control-wire
authoring operation. A control drag is not stored as an arbitrary graph edge:
the UE editor translates the gesture into a deterministic statement-order
transform, the C# source remains canonical, and the regenerated Graph owns the
committed control edges.

## Contract

The operation carries:

- the moved statement ID;
- its exact source block, previous sibling, and next sibling;
- the exact destination block and destination sibling gap.

All IDs are semantic preconditions, not presentation coordinates. A stale
source order rejects atomically and asks the client to refresh. The current
bounded implementation accepts only moves inside one structured block. A drag
across an `if`, loop, or `switch` region is rejected with `UVS3028` and directs
the author to an explicit structured transform.

## Safety gates

Before publishing source, the patch applier verifies that:

- both source and destination sibling preconditions still match;
- the move is not a no-op;
- `return`, `break`, and `continue` remain terminal in their block;
- no local reference moves before its declaration;
- a `.usids` sidecar is present so the moved statement and its descendants
  retain their semantic identities;
- the patched source passes normal C# analysis and reprojects to a valid,
  reducible Graph CFG.

The Workbench performs the source, identity, generated artifact, and CoreCLR
reload transaction. The Slate graph does not optimistically replace the old
wire: it submits `moveStatement`, keeps the compiler-owned canvas unchanged
until commit, and then renders the regenerated Surface. A rejection therefore
cannot leave a half-mutated graph.

## Current editor gesture

Dragging a sequential `then` output onto a statement's occupied `in` pin moves
the target statement immediately after the source statement when both resolve
to the same editable block. Entry, branch, and switch-case routing pins are not
treated as reorder handles.

This is the first M8.1 control-flow slice. Cross-region branch/loop/switch
moves, visual switch-section construction and reordering, and multi-operation
undo/redo remain explicit follow-up work rather than being approximated as raw
edge mutation.
