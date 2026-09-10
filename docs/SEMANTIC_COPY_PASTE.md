# Semantic Statement Copy/Paste

Semantic Patch `unifiedscript.patch/v0.24` introduced the bounded
`duplicateStatement` operation. Patch v0.26 added `duplicateStatements` for a
consecutive range that may be pasted into another structured region of the same
method; the current `unifiedscript.patch/v0.28` reader retains both contracts. Both
paths change C#, persistent identity, generated artifacts, and the running
CoreCLR generation through one Workbench transaction.

## Range contract

`duplicateStatements` carries:

- the ordered source statement IDs, their exact source block, and the outer
  previous/next sibling IDs;
- an exact destination block and previous/next sibling gap;
- one old-name/new-name mapping for every direct copied local declaration.

The source selection must be consecutive in one explicit block. The destination
may be any ordinary non-terminal statement in an explicit function, branch,
loop, or switch-section body in the same method. Preconditions are evaluated
against analyzed HIR, never canvas coordinates or transient `UEdGraphNode`
objects. A stale Surface therefore fails with `UVS3013` rather than applying at
a guessed location.

## Identity, binding, and control-flow safety

The source applier requires a `.usids` sidecar and pins every existing node in
the containing method before mutation. It inserts clean syntax copies without
those annotations. Consequently:

- the source range, nested expressions/regions, and unrelated method nodes keep
  their exact semantic IDs;
- every copied subtree receives fresh semantic IDs;
- comments and formatting move with the copied statements;
- direct copied locals receive distinct conflict-free names, and every copied
  reference bound to those locals is renamed with them;
- definitions created inside copied `for`, `foreach`, or pattern scopes remain
  internal to their cloned subtree;
- references to parameters remain valid, while references to external locals,
  loop variables, or pattern variables must still be dominated at the target
  gap or the paste is rejected with `UVS3027`;
- return, break, continue, directives, unreachable/terminal destinations,
  changed gaps, and invalid or reducibility-breaking results are rejected.

The full C# compilation and projected CFG validation remain the final gates.
`UVS3031` reports copy-specific structural failures.

## Slate workflow

In the Finare GraphEditor:

1. select one or more consecutive deletable, non-terminal statements in one
   source block and press Ctrl/Cmd+C;
2. select one ordinary destination statement anywhere in the same function;
3. press Ctrl/Cmd+V to insert after it, or clear selection to paste after the
   copied range;
4. use Ctrl/Cmd+Z or Ctrl/Cmd+Y to navigate the same persisted server-owned
   transaction history used by the Undo/Redo buttons.

The editor clipboard stores only ordered compiler-owned source identities and
the function identity. Each paste reconstructs a fresh v0.27 operation from the
latest Surface, so repeated local copies receive `nameCopy`, `nameCopy2`, and so
on.

Cross-function clipboard transport and persisted clipboard contents remain out
of scope. Moving behavior across functions belongs to callable extraction
because it requires parameter, return, effect, and visibility contracts.
