# Callable Extraction

Semantic Patch `unifiedscript.patch/v0.27` adds `extractCallable`: a bounded,
identity-aware transformation from one consecutive C# statement range to a
private `[Visual]` method. The same operation is available through the Unreal
Authoring Surface as **Extract Function**.

## Patch contract

The client submits the first statement as `targetNodeId`, the exact containing
method and parent block identities, the complete ordered statement range, the
outer previous/next sibling identities, and the requested method name. These
are optimistic concurrency preconditions. If source order or ownership changed
after the Surface was generated, the entire operation is rejected.

The client does not declare parameters, a result, staticness, or source text.
Those are derived from the current compiler model so browser, Slate, and future
clients cannot disagree about data flow.

## Data-flow rules

- Uses whose definitions are outside the range become typed input parameters,
  ordered by their source definitions. A callable is limited to 16 inputs.
- With no value needed after the range, the generated method returns `void`.
- One selected local used after the range becomes the method result; its
  original declaration identity stays on the caller-side declaration.
- One mutated external value used after the range becomes the result and is
  assigned back at the call site.
- Multiple live outputs are rejected. The author can narrow the selection or
  introduce one aggregate result before retrying.
- Every parameter and result must use a graph-safe, explicit C# type.

The generated method is private, receives `[Visual]`, and mirrors the caller's
static or instance shape. The candidate source must compile and every projected
Visual method must pass CFG validation before the Workbench can commit it.

## Identity ownership

Statements moved into the helper retain their semantic identities. The new
method, method body, parameters, and call site receive fresh identities. For a
caller-visible selected local, the local identity remains at the call site while
its initializer and descendant expression identities move into the helper.
Other members and state declarations in the containing type are pinned during
the type-level mutation. Source and `.usids` publish atomically with generated
artifacts and runtime reload, so Undo/Redo restores one coherent generation.

## Rejected boundaries

This version rejects async methods or selected `await`, directives, selected
`return`, and `break`/`continue` whose target is outside the selection. Nested
loop or switch control transfer is allowed only when its target is wholly inside
the selected range. Invalid or conflicting method names, unsupported value
definitions, unsafe types, stale IDs/gaps, missing identity sidecars, and more
than one live output also fail without writing source.

## Unreal workflow

Open a Visual function in the Authoring Surface, select one or more consecutive
ordinary statement nodes in one block, enter a new method name, and choose
**Extract Function**. Slate builds the preconditioned patch and submits it to the
existing Workbench transaction endpoint. On success the regenerated Surface and
matching components reload together; on failure the old graph and runtime
generation remain active and the diagnostic is shown in the editor.
