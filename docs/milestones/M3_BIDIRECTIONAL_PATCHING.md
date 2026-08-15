# M3 Bidirectional Source Projection

## Goal

Prove that a visual edit can become a localized C# source edit without creating
a second language implementation or bypassing the existing compiler checks.

## Implemented vertical slice

- `unifiedscript.patch/v0.1` polymorphic JSON protocol
- `replaceLiteral` operation targeted by HIR/Graph `SemanticId`
- expected-old-literal precondition
- content-sensitive source compilation baseline
- semantic configuration baseline covering assembly, references, and effects
- Graph schema v0.2 exposure of both baseline IDs
- multi-document in-memory patch application
- complete Roslyn/profile/semantic reanalysis before commit
- localized Roslyn replacement with leading/trailing trivia preservation
- single-document `apply-patch` CLI with non-in-place, atomic output writing
- deterministic sample patch and round-trip tests

## Failure contract

The applier returns no patched documents when any operation fails. Stable
diagnostic codes distinguish:

- `UVS3001`: source compilation baseline mismatch
- `UVS3002`: semantic configuration mismatch
- `UVS3003`: target node or source syntax not found
- `UVS3004`: target is not the operation's required HIR kind
- `UVS3005`: expected old value no longer matches
- `UVS3006`: replacement is not one complete C# literal

The proposed source is also rejected if normal C# compilation, Visual Profile
validation, semantic binding, or graph-safe signature validation fails after the
edit. CLI failures never write the output source.

## Proven scenarios

The test and sample suite proves that:

1. patch JSON retains its derived operation type deterministically;
2. duplicate operation targets and unknown schemas are rejected;
3. single-file compilation identity changes when source content changes;
4. source and semantic-configuration mismatches reject stale graph edits;
5. changed old values and wrong target kinds are reported explicitly;
6. expression-shaped replacements cannot masquerade as literal edits;
7. semantically invalid literal changes reject the whole transaction;
8. comments and formatting survive a successful replacement;
9. a multi-file request changes only the target document;
10. reanalysis projects the new literal back into Graph.

## Deliberate limits

This is a value-edit slice, not general graph-to-source generation. It does not
yet preserve a node ID when its source span changes, apply multi-file patches
through the CLI, edit operators or connections, insert/delete statements, or
persist graph layout. Those operations wait for `.usids` identity
reconciliation and a structural patch algebra.

## Next slice

Implement the identity sidecar reader/writer and reconciliation engine. The
first acceptance test should change `1` to `100`, reformat unrelated code, and
prove that the explicitly edited node plus every unrelated node retains its
identity across source-to-graph-to-source analysis.
