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
source or destination order rejects atomically and asks the client to refresh.
The bounded implementation accepts ordinary variable declarations and
expression statements across explicit function, `then`, `else`, loop-body, and
switch-section bodies. It never encodes a naked jump: the transform removes one
source statement, inserts it after the selected destination statement, then
lets normal compilation and Graph projection regenerate the control edges.

Moving an entire branch, loop, or switch, moving `return`/`break`/`continue`, or
crossing a function boundary is rejected with `UVS3028` and directs the author
to the corresponding explicit structured transform.

## Safety gates

Before publishing source, the patch applier verifies that:

- source and destination blocks belong to the same Visual method and both
  sibling preconditions still match;
- the move is not a no-op;
- `return`, `break`, and `continue` remain terminal in their block;
- no local reference moves before its declaration;
- a `.usids` sidecar is present so the moved statement and its descendants
  retain their semantic identities; cross-region moves pin every identity in
  the containing method because both nested containers may change spans;
- the patched C# passes scope, definite-assignment, and semantic-profile
  analysis, then reprojects to a valid, reducible Graph CFG.

The Workbench performs the source, identity, generated artifact, and CoreCLR
reload transaction. The Slate graph does not optimistically replace the old
wire: it submits `moveStatement`, keeps the compiler-owned canvas unchanged
until commit, and then renders the regenerated Surface. A rejection therefore
cannot leave a half-mutated graph.

## Current editor gesture

Dragging a node's single-path control output onto a statement's occupied
single-path control input moves the target statement immediately after the
source. The two statements may be in the same block or in different explicit
structured bodies. Entry,
branch, and switch-case routing pins are not treated as reorder handles, and a
structured or terminal target is rejected before patch submission.

This completes M8.1's bounded control-reconnection slice. Structural node
rewrites remain explicit operations, while unified multi-operation undo/redo is
the next M8.1 transaction slice.
