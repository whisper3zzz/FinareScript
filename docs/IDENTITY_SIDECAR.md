# Persistent Semantic Identity

## Why a sidecar is required

The M1 bootstrap derives statement and expression IDs from the logical source
path, owning method, syntax kind, and character span. This is deterministic, but
an unrelated insertion before a node changes its span and therefore its ID.
Interactive text/graph editing needs identity to survive formatting, movement,
and most local rewrites.

Identity must not be stored as comments in C# source. Inline markers pollute user
code, interact badly with formatters, and are easy for generators to remove.
UnifiedScript will instead keep a source-controlled sidecar next to each visual
source file.

## File contract

For `DoorController.cs`, the identity file is `DoorController.cs.usids`. The
initial schema is:

```json
{
  "schemaVersion": "unifiedscript.identity/v0.1",
  "sourcePath": "samples/DoorController.cs",
  "sourceDigest": "sha256:...",
  "generation": 4,
  "entries": [
    {
      "id": "us_0123456789abcdef0123456789abcdef",
      "kind": "invocation",
      "owner": "Samples.DoorController.BeginPlay()",
      "role": "expression",
      "fingerprint": "sha256:...",
      "shapeFingerprint": "sha256:...",
      "parentFingerprint": "sha256:...",
      "anchor": { "start": 412, "length": 13 },
      "contextBefore": "sha256:...",
      "contextAfter": "sha256:...",
      "generation": 4
    }
  ],
  "tombstones": []
}
```

The sidecar stores identity and reconciliation evidence only. Graph positions,
collapsed regions, colors, and comments belong in a separate `.usgraph` layout
sidecar so layout conflicts cannot change program identity.

## Reconciliation algorithm

On each successful analysis, the identity service matches the new HIR to the
previous sidecar in this order:

1. Apply explicit editor pins, rejecting missing, duplicate, or tombstoned IDs.
2. Match owner, kind, role, anchor, and complete reconciliation evidence.
3. Score equal structural fingerprints using parent shape, normalized token
   context, and anchor evidence; require one best candidate.
4. Match a unique shallow shape within an owner/kind/role group.
5. Allocate a cryptographically random `SemanticId` for every unmatched new
   semantic node.
6. Move unmatched old entries to tombstones rather than reusing their IDs.

Structural fingerprints exclude trivia and absolute positions. They include HIR
shape plus child structure. Shallow shape fingerprints include execution-
relevant attributes such as operators, names, types, argument counts, and
literal text. Parent fingerprints and four normalized Roslyn tokens on each
side add local evidence without making comments or whitespace significant.

Ambiguous matches are never guessed silently. The identity service allocates new
IDs and reports a reconciliation warning containing the competing candidates.
The editor may then offer an explicit “preserve identity” action.

## Transaction boundary

A text edit or graph edit is committed as one transaction:

1. parse and validate the proposed source;
2. reconcile or allocate semantic IDs in memory;
3. lower to HIR and validate graph invariants;
4. write source, `.usids`, and `.usgraph` through temporary files;
5. atomically replace all changed files, or replace none of them.

The sidecar is never advanced after a failed parse/profile validation. This
prevents invalid intermediate editor text from consuming or reassigning IDs.

## Merge behavior

Sidecars are sorted by `id`, use deterministic JSON formatting, and contain no
timestamps. Two branches may safely add entries with independently generated
IDs. If both branches change the same entry incompatibly, source merge is
resolved first and reconciliation is rerun against both candidate histories.
Tombstoned IDs are retained for a bounded number of repository generations and
must never be assigned to a different node.

## Invariants

- One live semantic node has exactly one ID.
- One live ID identifies at most one semantic node in a compilation.
- Formatting and comment-only edits preserve every ID.
- Unrelated insertions preserve IDs outside the inserted subtree.
- Graph and text projections carry the same IDs.
- Failed edits do not mutate persistent identity.
- Deleted IDs are tombstoned, not recycled.

## Implemented slice

The sidecar is now implemented as an opt-in input on each C# source document.
It covers parameters, blocks, statements, and expressions; declarations retain
their deterministic symbol IDs. Unchanged source produces a byte-identical
sidecar. Formatting and comment changes reconcile persistent IDs, deleted nodes
become tombstones, and ambiguous evidence emits `UVS3101` while allocating a
fresh ID rather than guessing. Invalid explicit pins emit `UVS3102` and stop
analysis.

Graph-authored replacements attach Roslyn annotations to proposed syntax nodes
and convert them into explicit identity pins. Changing `1` to `100` therefore
preserves the literal ID; changing an operator preserves the unary/binary node
ID; exchanging two invocation arguments preserves the invocation plus both
moved argument-root IDs. A statement insertion pins its parent block and every
pre-existing direct sibling while the new local declaration and literal receive
freshly allocated IDs. A safe deletion pins the parent block and surviving
siblings; every entry from the removed statement subtree becomes a tombstone
and is never reassigned. Temporary introduction pins every pre-existing
expression in the changed statement: the extracted expression subtree moves to
the new initializer without changing IDs, while its ancestors remain on the
consumer side with their existing IDs. Only the new declaration and replacement
identifier allocate new IDs. Unrelated live IDs remain stable while
fingerprints, anchors, source digest, and generation update. Without a sidecar,
deterministic span-derived bootstrap identity remains available for batch
analysis.

Value reconnection deliberately does not pin the removed consumer subtree. Its
IDs move to tombstones, while the replacement identifier receives a fresh ID.
The containing statement, surviving expression ancestors, direct siblings,
parent block, and source local declaration remain pinned or structurally
reconciled. Full semantic reanalysis must bind the new identifier's
`referencedSymbolId` to the requested source declaration before either source or
sidecar can commit.

Multi-stage patch plans require a sidecar before their first step. After each
successful in-memory stage, the coordinator adopts the newly reconciled
sidecar as the next stage's identity baseline. This makes the random ID of a
new temporary or inserted local declaration stable immediately and allows
`sourceStepId` handles to resolve it in subsequent reconnections. Intermediate
sidecars are never written. A late failure returns no proposed request, so
neither the initial sidecar nor source is advanced on disk; a successful plan
writes only the final source and final sidecar through one filesystem
transaction. Sidecar generation and tombstone generation reflect the ordered
internal reconciliation stages.

Graph-authored branch insertion pins every existing sibling expression before
adding the new `if`, condition subtree, and two explicit blocks. Those authored
nodes receive fresh identities while layout synchronization leaves their Graph
nodes explicitly unplaced. Patch-plan v0.7 captures those fresh block IDs after
the producer stage and exposes them symbolically as `then`/`else` targets. Each
branch-body stage adopts the preceding sidecar before resolving the block, so
ordered insertions remain stable even though the editor never sees or predicts
the generated IDs. Branch population can now also insert an assignment to an
existing outer local; that assignment receives normal reconciled statement and
expression identities without creating a new local handle. Broader
expression/control-flow synthesis, arbitrary cross-expression rewiring,
declaration rename reconciliation, multi-file CLI patch commits, bounded
tombstone compaction, and automatic `.usgraph` placement remain future work.

Nested-block insertion additionally pins every unaffected live syntax node in
the edited document when that node is not contained by the replaced subtree.
This includes ancestor syntax that contains the target Block: replacing an
inner `then`/`else` Block does not semantically replace its enclosing `if`.
Those ancestors keep their IDs through arbitrarily nested plan stages. This is
also required when newly inserted syntax has the same shape and value as an
exterior node; the exterior ID remains attached to its original semantic
location, while nodes truly inside the replacement continue to use the
operation-specific subtree pins.
