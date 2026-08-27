# Authoring Diagnostics, Diff Preview, and Rollback

## Purpose

M8.2 closes the native Unreal authoring safety loop. A source-backed edit is no
longer sent directly from Slate to the commit endpoint. The Workbench prepares
the real candidate snapshot first, Unreal displays its exact C# before/after
text, and the accepted request carries a SHA-256 preview token binding the
confirmed candidate to the later commit.

This remains one program model. The preview is produced by the same
`CSharpAuthoringWorkspace.Prepare(...)` path used by the transaction; it is not
a client-side rendering of patch JSON and it does not create another editable
graph or source buffer.

## Native workflow

1. Slate creates a declaration patch, semantic patch, patch plan, Undo, Redo,
   or grouped commit with its baseline revision and operation identity.
2. It posts the unchanged payload and headers to the corresponding
   `/api/authoring/preview/...` endpoint.
3. The server validates the current workspace/group, external-file state,
   typed preconditions, compiler contract, and candidate analysis without
   writing source, identity, artifacts, history, or a runtime generation.
4. A modal Unreal diff shows exact **Before** and **After** C# for every changed
   logical source plus any successful-candidate warnings.
5. Cancel closes the preview with a zero-write status. Apply resends the same
   payload, operation identity, baseline/group identity, and returned
   `X-UnifiedScript-Preview-Token` to the ordinary transaction endpoint.
6. The server consumes the cached immutable candidate—including its reconciled
   semantic identities and generated Graph—from that token. `UAW1012` rejects a
   missing/expired token or any baseline, group, or transaction-metadata drift.

Group begin/cancel do not mutate published source and therefore do not show a
diff. Each grouped stage previews the next in-memory draft and says that the
runtime is unchanged. Group commit previews the complete accumulated draft
against the published workspace before creating one history entry and one new
runtime generation.

## Preview response v0.1

`unifiedscript.authoring-preview/v0.1` contains:

- baseline and candidate revisions;
- exact transaction ID and description;
- whether acceptance publishes a runtime generation or only stages a draft;
- a lowercase SHA-256 preview token over transaction context, complete candidate
  source/identity sidecars, Declaration IR, and Graph;
- changed logical source paths with exact `beforeText` and `afterText`;
- structured compiler/contract diagnostics with severity, code, message,
  source span, and optional semantic ID.

The native parser is strict: unknown schema, invalid revisions/tokens,
duplicate paths, unchanged file entries, malformed spans, or unsupported
diagnostic severities reject the preview instead of silently applying it.

## Failure panel and safe quick fixes

Workbench authoring errors retain the protocol code/message and now expose
their diagnostic array directly in the Authoring Surface. Each diagnostic has
**Open Source** and **Copy** actions. Server-selected recovery actions remain
bounded and non-destructive:

- **Open Source** navigates to the first exact compiler/contract span;
- **Refresh Surface** performs a read-only workspace GET after stale revision,
  duplicate-operation, group-state, or preview-token conflicts.

There is deliberately no automatic “fix code and retry” action. A lost response
or stale transaction can represent an already committed operation, so changing
its identity and replaying it would not be a generally safe quick fix.

## Explicit rollback receipt

Every `AuthoringProtocolException` response includes a rollback receipt with
the server's current revision and runtime generation:

- `unchanged`: rejection occurred before any source/artifact/history/runtime
  write;
- `restored`: candidate runtime activation or persistence began, failed, and
  the previous source/artifact/history/runtime program was restored as the
  reported current generation;
- `failed`: restoration itself failed and authoring must stop for inspection.

The native panel distinguishes a confirmed `unchanged/restored` receipt from a
missing receipt or `failed` state. It never infers rollback merely from an HTTP
status or unchanged canvas. Source/artifact persistence continues to use the
existing temporary-file/backup transaction, while the runtime host reloads the
previous immutable request if persistence fails.

## Verification

The managed HTTP integration test proves:

- a normal preview returns exact changed source and performs zero writes;
- a mismatched preview token is rejected with `UAW1012` and an `unchanged`
  receipt;
- a statement insertion that allocates fresh semantic identities commits the
  exact cached preview candidate rather than preparing a second identity set;
- grouped stage previews do not publish and grouped commit previews include the
  complete accumulated source delta;
- external multi-file conflicts return a locatable diagnostic, safe source
  action, and unchanged revision/generation receipt.

The native Automation test covers endpoint derivation plus strict preview,
diagnostic, quick-fix, and rollback parsing. The UE 5.8 Editor build validates
the modal diff and diagnostic-panel Slate integration.
