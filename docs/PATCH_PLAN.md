# Semantic Patch Plan Protocol

## Purpose

`unifiedscript.patch-plan/v0.3` represents one visual-editor gesture as an
ordered, atomic sequence of the same bounded semantic operations supported by
`unifiedscript.patch/v0.6`. It exists because a useful graph edit often needs
IDs created by an earlier edit or changes several overlapping source regions.
Submitting those stages as unrelated filesystem transactions would expose
partial programs and force the editor to predict random persistent IDs.

The plan coordinator never bypasses the normal patch applier. Each stage becomes
a one-operation semantic patch, runs the complete C# profile and semantic
pipeline, and advances only an in-memory request. Only the final source and
identity sidecar are eligible for a CLI filesystem transaction.

## Envelope

```json
{
  "schemaVersion": "unifiedscript.patch-plan/v0.3",
  "sourceCompilationId": "us_...",
  "semanticConfigurationId": "us_...",
  "steps": []
}
```

The source compilation and semantic configuration IDs are the external
optimistic-concurrency baseline. Every step has a unique, non-empty `stepId`
and a persistent `targetNodeId`.

Version v0.3 supports these step discriminators:

| `step` | Purpose | Structural result |
| --- | --- | --- |
| `replaceLiteral` | Replace one literal with a complete literal token | Target ID survives |
| `changeOperator` | Change a unary/binary operator inside its result-preserving family | Operator node ID survives |
| `reconnectArgument` | Exchange two pure, exactly typed positional arguments | Invocation and endpoint IDs survive |
| `insertStatement` | Insert a local-literal declaration or typed `if` skeleton | Local payload produces a handle; `if` does not |
| `deleteStatement` | Delete one proven-safe unused/effect-free statement | Removed subtree becomes tombstones |
| `introduceTemporary` | Move one stable expression into a single-evaluation local | Produces a local handle |
| `reconnectValue` | Replace a stable consumer with a produced local reference | Consumes a local handle |

Each step repeats the same old-value, type, endpoint, parent, and sibling
preconditions as its standalone patch operation. The plan protocol does not
weaken operation safety.

## Symbolic local handles

`introduceTemporary` and a local-literal `insertStatement` publish
their `stepId` as a symbolic local handle. A later `reconnectValue` supplies
that value as `sourceStepId`:

```json
{
  "step": "reconnectValue",
  "stepId": "connectScale",
  "targetNodeId": "us_...",
  "sourceStepId": "insertScale",
  "expectedContainingStatementId": "us_...",
  "expectedParentBlockId": "us_...",
  "expectedPreviousSiblingId": "us_...",
  "expectedNextSiblingId": null,
  "expectedTypeId": "us_..."
}
```

The source step must occur earlier, produce the same semantic type, and target
the same method. After its stage succeeds, the coordinator reconciles the new
sidecar, finds exactly one produced declaration, and records its actual random
`SemanticId`. The reconnect stage then uses that ID in a normal
`ReconnectValuePatchOperation`.

## Two-phase safety model

### Initial validation

Before executing the first stage, the coordinator:

1. analyzes the unchanged request;
2. checks the plan's source and configuration baselines;
3. resolves every original target to its required method and HIR category;
4. requires a persistent `.usids` sidecar on every target document;
5. applies every non-symbolic step independently to the original request in
   memory and rejects the plan if any standalone precondition fails;
6. validates symbolic reconnect statement, block, sibling, method, and type
   context against the original HIR.

This prevents an earlier stage from making a stale external precondition appear
valid. Initial validation results are discarded; they never advance identity
generation or source.

### Ordered execution

For each step, the coordinator reanalyzes the current in-memory request,
constructs a fresh one-operation patch against that stage's compilation ID,
executes the normal patch applier, adopts the newly reconciled sidecar, removes
transient identity pins, and proceeds. A failed stage returns no patched request
or analysis from any earlier successful stage.

## Internal rebasing rules

Internal rebasing recognizes only structural changes produced by earlier steps
in the same validated plan:

- `introduceTemporary` and `reconnectValue` recompute the target expression's
  current containing statement, parent block, and adjacent siblings;
- `deleteStatement` recomputes the target statement's current parent and
  siblings;
- `insertStatement` anchors its original gap. If both original siblings remain,
  it inserts immediately before the original next sibling after prior planned
  insertions. A start/end/empty-block gap similarly retains plan order;
- `reconnectArgument` locates both endpoint roots by persistent ID and derives
  their current positional indices;
- literal and operator steps reuse their original old-value/type preconditions,
  so their non-insertion target IDs cannot be repeated in one plan.

Only insertion steps may repeat a target block. This supports several ordered
new statements in one gap without giving repeated literal/operator edits
ambiguous original-versus-intermediate preconditions.

An `insertStatement` carrying an `if` payload executes through the same staged
transaction and gap rebasing, but does not publish a local handle. A
`reconnectValue` whose `sourceStepId` names that step is rejected during JSON
validation rather than failing after source mutation.

If an earlier step deletes a later target or removes every anchor needed to
reconstruct an insertion gap, execution fails atomically. Internal rebasing is
not a mechanism for accepting unrelated external source changes.

## CLI example

The checked-in composed sample inserts `scale`, reconnects a consumer to that
new declaration, changes `*` to `/`, and deletes `obsolete`:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-plan \
  samples/Patching/ComposedPlan.cs \
  --identity samples/Patching/ComposedPlan.cs.usids \
  --plan samples/Patching/ComposeScale.plan.json \
  --out artifacts/composed/ComposedPlan.cs \
  --out-identity artifacts/composed/ComposedPlan.cs.usids
```

The CLI requires non-in-place source and sidecar outputs and commits both with
the same backup-based transaction used by standalone patches. The input source
remains unchanged.

## Deliberate limits

Plan v0.3 composes all operations currently defined by patch v0.6, including
the graph-authored condition and explicit empty-block `if` insertion. It has no
branch-block handle, cannot populate either branch within the same plan, has no
cross-method or cross-document symbolic handle, and does not support a handle
to an arbitrary expression. Multi-document in-memory analysis remains
available below the CLI, while the CLI commit boundary is still one
source/identity pair.

There is no automatic reader migration for older patch-plan schemas; producers
should reanalyze and emit the current schema and baselines. The original
temporary-plus-reconnect and v0.2 composed step shapes remain representable in
v0.3.
