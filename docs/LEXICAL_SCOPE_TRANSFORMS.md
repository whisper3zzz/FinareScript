# Lexical Scope Collapse and Expansion

Semantic Patch `unifiedscript.patch/v0.25` adds two reversible structured
operations:

- `collapseStatements` wraps an exact consecutive statement range in a C#
  lexical block;
- `expandBlock` removes one nested lexical block and returns its exact ordered
  children to the parent.

This is the bounded first collapse/expand contract. It creates a real semantic
region rather than hiding canvas nodes or storing C# meaning in `.usgraph`.

## Preconditions

Collapse carries the parent block ID, every selected statement ID in source
order, and the previous/next siblings outside the range. At least two members
are required. Slate reconstructs this order from Surface regions, including
already nested lexical blocks, so an unordered graph selection cannot reorder
source.

Expand carries the nested block ID, parent block ID, every child statement ID,
and the block's outer siblings. Only a non-root `block` region can expand. An
empty block, changed child list, preprocessor directive, or comment attached to
a brace is rejected rather than discarded.

## Identity and semantics

Both operations require `.usids` and pin the complete containing method before
mutation. On collapse, the new block receives a fresh region ID while all
selected statements and nested expressions keep their IDs. On expansion, the
wrapper ID becomes a tombstone and its children keep theirs.

Indentation changes are trivia-aware and preserve comments and annotations.
The candidate then passes the full Visual Profile compilation and projected
CFG validation. This naturally rejects a collapse that would hide a local from
a later use and an expansion that would introduce an outer-scope name conflict.

## Unreal workflow

Select two or more consecutive statement nodes and press **Collapse Scope** in
the graph header. After the Workbench commits, My Script shows the new nested
BLOCK. Select that region and press **Expand Scope** in the inspector to reverse
the transformation. Each direction is one Workbench transaction and therefore
uses the existing Undo/Redo history and CoreCLR reload boundary.

Callable function/macro extraction needs a separate parameter, return, state,
effect, and exception-boundary contract; it is intentionally not approximated
by this lexical operation.
