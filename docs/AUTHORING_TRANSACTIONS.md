# Authoring Transactions and History

The loopback Workbench is the single commit authority for visual, C#, and AI
authoring. Workspace protocol `unifiedscript.authoring-workspace/v0.3` adds a
bounded server-owned history to the existing source, identity, artifact, and
CoreCLR publication boundary.

## Request contract

Mutation clients send these headers:

- `X-UnifiedScript-Baseline-Revision`: the workspace revision rendered by the
  client;
- `X-UnifiedScript-Transaction-Id`: a unique operation ID, at most 128
  characters;
- `X-UnifiedScript-Transaction-Label`: an author-facing description, at most
  256 characters.

The revision and IDs remain optional for older clients. Updated browser and
Slate clients always send them after receiving a live workspace. The server
rejects a stale explicit revision with `UAW1005` and a previously committed
operation ID with `UAW1006`, before modifying source or runtime state.

The write endpoints are:

- `POST /api/authoring/declaration-patch`;
- `POST /api/authoring/semantic-patch`;
- `POST /api/authoring/semantic-patch-plan`;
- `POST /api/authoring/undo`;
- `POST /api/authoring/redo`.

Undo and redo accept `{}`. They use the same revision and operation headers as
ordinary edits. `UAW1007` reports an empty side of the history; `UAW1008`
reports invalid transaction metadata.

## Commit order

Every endpoint runs under the authoring/debug session gate:

1. reject edits while a debug execution is active;
2. validate the client revision and operation identity;
3. reject source or identity files changed outside the Workbench;
4. prepare and fully analyze the candidate snapshot;
5. reload the candidate through the same CoreCLR transport;
6. transactionally publish source, `.usids`, Declaration IR, Surface, DLL, and
   PDB outputs;
7. commit the in-memory workspace and history entry, then expose the new
   revision and runtime generation.

Publication failure reloads the previous snapshot and leaves history unchanged.
The client never mutates compiler-owned nodes or wires optimistically.

## History semantics

The workspace retains up to 100 accepted edit transactions. Each entry owns the
exact before and after snapshots, including stabilized identity sidecars.
Undo restores the before snapshot; redo restores the after snapshot. Both
operations publish a new monotonically increasing workspace revision and a new
CoreCLR generation, even when their source content matches an older revision.

Committing a new edit after undo clears the redo branch. Transaction IDs remain
reserved for the lifetime of the Workbench process, so an ambiguous retry cannot
apply twice. Workspace responses expose `canUndo`, `canRedo`, both depths, the
next transaction IDs/descriptions, and the last committed operation ID.

History is currently session-local and is reset when the authoring host exits.
Atomic semantic patch plans already group dependent source operations. Semantic
Patch v0.24 statement duplication is also one server-owned transaction, so its
source, identity, generated artifacts, runtime reload, Undo, and Redo cannot
split. Persisted history, user-defined transaction grouping, collapse/expand
transforms, and broader multi-selection/cross-region clipboard transactions
remain later M8.1 work.
