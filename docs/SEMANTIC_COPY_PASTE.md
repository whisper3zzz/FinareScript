# Semantic Statement Copy/Paste

Semantic Patch `unifiedscript.patch/v0.24` introduces
`duplicateStatement`. It is the first clipboard operation that changes C#,
persistent identity, generated artifacts, and the running CoreCLR generation
through one Workbench transaction.

## Contract

The operation identifies one source statement, its exact parent block, its
current previous/next siblings, and one exact destination gap. The initial
scope pastes immediately after a selected non-terminal statement in the same
explicit function block or switch-section body. A local declaration also
carries `newLocalName`; all other statements require that field to be null.

The source and destination preconditions are evaluated against the analyzed
HIR, never canvas coordinates or transient `UEdGraphNode` objects. A stale
Surface therefore fails with `UVS3013` instead of applying at a guessed place.

## Identity and safety

The source applier requires a `.usids` sidecar and pins every existing node in
the containing method before the syntax mutation. It inserts a clean syntax
copy without those annotations. Consequently:

- the source statement, its nested expressions/regions, and unrelated method
  nodes retain their exact semantic IDs;
- every node in the copied subtree receives a fresh semantic ID;
- leading comments move with the copied statement;
- copied top-level locals receive a different conflict-free C# identifier;
- return, break, continue, preprocessor directives, insertion after a terminal,
  changed sibling gaps, and invalid/reducibility-breaking results are rejected.

The full C# compilation and projected CFG validation remain the final gates.
`UVS3031` reports copy-specific structural failures.

## Slate workflow

In the UnifiedScript GraphEditor:

1. select one deletable, non-terminal statement and press Ctrl/Cmd+C;
2. optionally select another non-terminal statement in the same block;
3. press Ctrl/Cmd+V to insert after that statement, or clear selection to paste
   after the copied source;
4. use Ctrl/Cmd+Z or Ctrl/Cmd+Y to navigate the same server-owned transaction
   history used by the Undo/Redo buttons.

The clipboard stores only the compiler-owned source identity and function
identity. Each paste reconstructs a fresh v0.24 operation from the latest
Surface, so repeated local copies receive `nameCopy`, `nameCopy2`, and so on.

Multi-selection, cross-region/cross-function clipboard transport, persisted
clipboard state, and collapse/expand transforms remain later M8.1 work.
