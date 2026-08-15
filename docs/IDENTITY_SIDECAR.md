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

Graph-authored literal replacement attaches a Roslyn annotation to the proposed
syntax node and converts it into an explicit identity pin. Changing `1` to
`100` therefore preserves the target ID and every unrelated live ID while
updating fingerprints, anchors, source digest, and generation. Without a
sidecar, deterministic span-derived bootstrap identity remains available for
batch analysis.

Structural graph operations, declaration rename reconciliation, multi-file CLI
patch commits, bounded tombstone compaction, and `.usgraph` layout persistence
remain future work.
