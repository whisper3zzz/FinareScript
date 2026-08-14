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
  "entries": [
    {
      "id": "us_0123456789abcdef0123456789abcdef",
      "kind": "invocation",
      "owner": "Samples.DoorController.BeginPlay()",
      "fingerprint": "sha256:...",
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

On each successful parse, the identity service matches the new syntax tree to
the previous sidecar in this order:

1. Match the same owner, syntax kind, span, and structural fingerprint.
2. Within an owner, match a unique structural fingerprint whose surrounding
   context also matches.
3. Use Roslyn syntax-tree edit information to match moved or locally rewritten
   nodes, requiring compatible parent and role information.
4. Allocate a cryptographically random `SemanticId` for every unmatched new
   semantic node.
5. Move unmatched old entries to tombstones rather than reusing their IDs.

Structural fingerprints exclude trivia and absolute positions. They include the
node role, normalized tokens, child roles, and the owning declaration ID. Local
variable names and literal values are included because changing either normally
changes the authored semantic node; whitespace and comments are excluded.

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

M3 will implement this service before graph-to-source editing. Until then, M1
span-derived IDs remain an explicit bootstrap mechanism rather than a promise of
edit-stable identity.
