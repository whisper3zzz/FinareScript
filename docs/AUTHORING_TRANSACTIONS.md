# Authoring Transactions and History

The loopback Workbench is the single commit authority for visual, C#, and AI
authoring. Workspace protocol `unifiedscript.authoring-workspace/v0.5` exposes
a bounded server-owned history, while journal
`unifiedscript.authoring-history/v0.1` persists that history inside the same
source, identity, artifact, and CoreCLR publication boundary.

## Project-backed sessions and runtime chunks

`debug-serve <source.cs> --project <project.csproj>` evaluates the actual MSBuild
project: Compile items (including linked/generated inputs), references, implicit
usings, configuration, framework and language settings. `--configuration` and
`--framework` select the project configuration; `--no-build --no-restore` reuse
prepared project outputs. Manual `--assembly`, `--source` and `--reference`
overrides are rejected in project mode. File-only sessions remain supported.

This is a source-oriented session with whole-project compilation. Only the
selected source is editable; other Compile items are read-only inputs. An edit
to those inputs is rejected with `UAW1017`, including during preview. Open a
different primary source in its own session to edit it. Avoid simultaneous
writers to the same project outputs. Adjacent `.cs.usids` files are consumed by
both project authoring and ordinary CLI project emission so rebuilds retain the
identities saved by Graph. The primary source's sidecar is persisted on commit.

`--chunk-out` adds the runtime `.uschunk` to the existing artifact transaction.
Source/Graph apply, Undo and Redo publish the chunk from the same uninstrumented
program as the DLL/PDB and Declaration IR; they cannot leave the preferred chunk
at an older behavior while publishing a newer DLL. `--publish-initial` publishes
the initial snapshot after acquiring the loopback listener. It does not rewrite
unchanged compilation inputs.

`tools/start-finare-authoring.ps1` uses the configured project and chunk
stage path and requires normal build outputs to exist. It does not rebuild or
replace shared native-host dependencies during Editor use. Native World/component
reload remains a separate consumer of the published artifacts, not an HTTP
commit side effect.

### Import external edits without restarting

`POST /api/authoring/refresh` with `{}` and the ordinary transaction headers
reevaluates the selected MSBuild project and reads its current Compile items,
settings, prepared references and adjacent identities. File-only sessions reread
their known input files. This does not restore packages or build dependencies;
use the normal build workflow when those outputs need updating.

Refresh compiles the complete candidate and publishes matching artifacts without
rewriting the external C# inputs. It rereads inputs before publication to reject
a concurrent save. Invalid sources, changed inputs or publication failure leave
the old loaded debug program, revision and chunk active and preserve disk edits
(`UAW1018`). A no-op leaves the revision and generation unchanged. Active debug
execution, grouped drafts and stale explicit revisions retain their existing
conflict rules.

A primary-source-only import becomes an ordinary Undo entry. A change to project
configuration, source membership or other compilation inputs starts a new Undo
context because old primary-source snapshots cannot restore those external
inputs. When persisted history is nonempty, the previous journal is retained
under `<history-path>.retired/` in the same publication transaction. Revision
ordering and committed operation-ID replay protection survive this transition.
External text uses conservative sidecar reconciliation: unchanged function IDs
can survive, but an edited literal need not retain its old node ID as it would
with an explicit Graph patch.

Native **Refresh C#** fetches the current workspace revision before importing
disk inputs, so a stale tab can recover without bypassing the server's conflict
checks. Unsaved native C# buffers block refresh. Successful native Build & Reload
also refreshes an open Script Asset, including Graph-only tabs that have not yet
opened the C# snapshot. The diagnostic **Refresh Surface** quick fix remains a
read-only GET; neither action retries an earlier rejected source mutation.

## Request contract

Mutation clients send these headers:

- `X-Finare-Baseline-Revision`: the workspace revision rendered by the
  client;
- `X-Finare-Transaction-Id`: a unique operation ID, at most 128
  characters;
- `X-Finare-Transaction-Label`: an author-facing description, at most
  256 characters.
- `X-Finare-Group-Id`: the active grouped draft ID for stage, commit,
  and cancel requests.
- `X-Finare-Preview-Token`: the SHA-256 identity of the exact prepared
  candidate accepted in the Diff dialog.

The revision and IDs remain optional for older clients. Updated browser and
Slate clients always send them after receiving a live workspace. The server
rejects a stale explicit revision with `UAW1005` and a previously committed
operation ID with `UAW1006`, before modifying source or runtime state.

The write endpoints are:

- `POST /api/authoring/declaration-patch`;
- `POST /api/authoring/semantic-patch`;
- `POST /api/authoring/semantic-patch-plan`;
- `POST /api/authoring/source-text`;
- `POST /api/authoring/refresh`;
- `POST /api/authoring/ai-semantic-proposal`;
- `POST /api/authoring/group/begin`;
- `POST /api/authoring/group/commit`;
- `POST /api/authoring/group/cancel`;
- `POST /api/authoring/undo`;
- `POST /api/authoring/redo`.

Undo and redo accept `{}`. They use the same revision and operation headers as
ordinary edits. `UAW1007` reports an empty side of the history; `UAW1008`
reports invalid transaction metadata.

## Prepared diff and commit binding

Native Slate preflights declaration, semantic, plan, Undo, Redo, and grouped
commit requests through matching `/api/authoring/preview/...` endpoints. A
preview runs steps 1-4 of the commit path under the same session gate, returns
the versioned stable-ID semantic Graph diff plus exact changed C#
`beforeText`/`afterText`, and performs no persistence or runtime reload. Native
Slate and the browser Workbench both require this review for declaration and
behavior edits. Group stages preview the current draft; grouped commit previews
the draft against the published workspace.

The preview response includes a SHA-256 token over revisions, transaction/group
metadata, complete candidate source, reconciled identity sidecars, Declaration
IR, and Graph. AI proposal previews additionally bind the deterministic
`proposalId`, so a different valid proposal cannot reuse an accepted diff. The
server keeps that immutable prepared snapshot in a bounded
cache. An accepted native request sends its token as
`X-Finare-Preview-Token` and commits that exact candidate; `UAW1012`
rejects missing/expired tokens or transaction-context drift. Older patch
clients may omit the header, but the restricted C# source endpoint requires it
and rejects direct commits. The native Unreal editor never bypasses its preview
for a source-changing operation. Structured failures include diagnostic/quick-fix
arrays and a rollback receipt. See
[AUTHORING_DIAGNOSTICS.md](AUTHORING_DIAGNOSTICS.md).
The standalone review/merge schemas and conservative function-level merge
boundary are documented in
[SEMANTIC_DIFF_AND_MERGE.md](SEMANTIC_DIFF_AND_MERGE.md).
The AI envelope, offline pack command, and proposal-specific endpoints are
documented in [AI_SEMANTIC_PROPOSALS.md](AI_SEMANTIC_PROPOSALS.md).

`unifiedscript.csharp-source-text-patch/v0.1` is intentionally a whole-buffer
transport, not an unrestricted file API. It identifies exactly one source that
is already part of the workspace, carries the SHA-256 digest of the rendered
baseline, and limits candidate text to 512 Ki characters under the server's
1 MiB request cap. The Workbench also compares the request revision, validates
that the source has not changed on disk, compiles the candidate under the same
references/settings, and rejects no-op or invalid programs. Only the immutable
candidate retained by preview-token lookup can enter the commit path.

## User-grouped transactions

`group/begin` reserves one unique transaction ID and label at the current
committed revision. While the group is active, declaration, semantic, plan, and
source-text
requests carry its group ID and unique stage operation IDs. Each accepted stage
fully analyzes a new in-memory draft and returns its regenerated Surface, but
does not write C# or `.usids`, emit artifacts, reload CoreCLR, change components,
or append history.

`group/commit` requires the group ID as both group and transaction identity. It
rebases the final draft onto exactly one new committed workspace revision, then
runs the normal reload/publication path once. All stages therefore appear as
one named Undo/Redo entry. `group/cancel` discards the draft and returns the
unchanged committed Surface and runtime generation. Empty commits, nested
groups, stale draft revisions, mismatched group IDs, duplicate stage IDs,
ordinary writes, history navigation, and debug start while a group is active
are rejected explicitly.

Workspace responses expose optional `group` state with ID, label, committed
baseline revision, and staged operation count. A staged response may carry a
provisional revision greater than the committed revision; commit advances the
persisted revision only once, and cancel returns to the unchanged committed
revision.

## Commit order

Source/Graph edits and history navigation run under the authoring/debug session gate:

1. reject edits while a debug execution is active;
2. validate the client revision and operation identity;
3. reject source or identity files changed outside the Workbench and snapshot
   the current validated `.usgraph` baseline;
4. prepare and fully analyze the candidate snapshot;
5. reload the candidate through the same CoreCLR transport;
6. synchronize stable-ID layout and transactionally publish source, `.usids`,
   `.usgraph`, Declaration IR, Surface, DLL, PDB, optional compiled chunk, and the prospective history
   journal;
7. commit the in-memory workspace and history entry, then expose the new
   revision and runtime generation.

Publication failure reloads the previous snapshot and leaves history unchanged.
The client never mutates compiler-owned nodes or wires optimistically.
Disk refresh uses the same gate but keeps the previous loaded debug program
alive until candidate artifacts commit; publication failure does not require
recompiling the old source against potentially changed dependencies.

## History semantics

The workspace retains up to 100 accepted edit transactions. Each entry owns the
exact before and after snapshots, including stabilized identity sidecars.
Undo restores the before snapshot; redo restores the after snapshot. Both
operations publish a new monotonically increasing workspace revision and a new
CoreCLR generation, even when their source content matches an older revision.

Committing a new edit after undo clears the redo branch. Transaction IDs remain
reserved in the persisted journal, so an ambiguous retry cannot apply twice
after either a request timeout or host restart. Workspace responses expose
`canUndo`, `canRedo`, both depths, the next transaction IDs/descriptions, and
the last committed operation ID.

`debug-serve` writes the journal to `--history`; without an explicit path it
uses `<input.cs>.ushistory.json`. The Unreal launchers place it beside staged
authoring artifacts. A restart restores the monotonically increasing revision,
both history stacks, transaction labels, and the complete operation-ID replay
guard. Historical snapshots retain every source, reconciled sidecar, and
identity pin, but are reanalyzed lazily only when Undo/Redo targets them.

Restoration requires the exact source set and text, the current non-empty
`.usids`, semantic configuration, snapshot digest, source-compilation ID, and
continuous Undo/Redo chains. A stale, corrupt, or incompatible journal is left
untouched and reported as `history.persistenceState = rejected`; the workspace
starts a fresh revision-1 history instead of applying an unsafe snapshot. The
states `empty`, `restored`, `persisted`, and `disabled` distinguish the other
startup/publication paths in browser and Slate clients.

`--layout-out` opts the shareable `.usgraph` sidecar into the same transaction.
Layout remains directly authorable by the native Graph canvas before preview,
so the server snapshots its latest validated bytes rather than treating normal
node movement as an external source edit. The preview token binds that exact
SHA-256 baseline. Any subsequent layout write rejects commit with `UAW1015`;
successful publication writes only a semantically changed synchronized layout,
preserving byte-exact JSON when the Graph edit does not alter layout ownership.

Atomic semantic patch plans already group dependent source operations. Semantic
Patch v0.24 statement duplication is also one server-owned transaction, so its
source, identity, generated artifacts, runtime reload, Undo, and Redo cannot
split. Patch v0.25 applies lexical Collapse Scope / Expand Scope through the
same boundary. Patch v0.26 applies multi-statement, cross-region copy/paste with
exact source/destination gaps and binding checks. Patch v0.27 extracts one
consecutive range into a typed private Visual method, including its C# and
identity migration, within the same transaction. Workspace v0.4 then closes
M8.1 user-defined grouping with server-owned begin/stage/commit/cancel drafts.
