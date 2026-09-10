# Versioning and upgrades

Finare publishes one machine-readable compatibility contract at
`Schemas/compatibility-policy.v0.1.json` in the SDK and at
`schemas/finare.compatibility-policy.v0.1.json` in a source checkout.
It is the release-owned answer to three separate questions:

1. which schema, generated-code, chunk, and Unreal ABI revisions are current;
2. which older persistent documents this release can read and rewrite; and
3. which derived or binary outputs must instead be regenerated or rebuilt.

JSON remains the reviewable authoring, contract, import/export, and diagnostic
format. It is not parsed every frame by packaged gameplay. Shipping execution
uses the deterministic `.uschunk` program image, CoreCLR PE/IL, generated native
thunks, and versioned shared/batched ABI tables. A future faster program image
therefore evolves the compiled chunk without replacing the readable source
contract or introducing a second language VM.

The SDK root also carries `release-manifest.json`, copied byte-for-byte from
`release/finare.release.json`. It binds the product version and channel
to the plugin version, archive name, exact verified engine/platform, release
notes, curated documentation catalog, and acceptance gates. SDK packaging
rejects a caller-supplied version or source plugin descriptor that disagrees
with that release manifest.

## Compatibility actions

Each family declares exactly one upgrade strategy:

| Action | Meaning | Examples |
| --- | --- | --- |
| `preserve` | Current persistent data is retained byte-for-byte. An unknown version is blocked. | Script Asset, identity sidecar, graph layout, deployment declaration |
| `rewrite` | The current release can deserialize an explicitly listed old version, normalize it, and write the current version deterministically. | Unreal reflection v0.1-v0.3 to v0.4; API Contract v0.1 to v0.2 |
| `regenerate` | The file is derived. Delete/replace it through its declared producer using the named source of truth. | HIR, Semantics, graph, Authoring Surface, visual API nodes, coverage, generated C#/C++ |
| `rebuild` | The file is an immutable delivery or ABI artifact. Recompile/repackage a matched managed/native set. | `.uschunk`, bootstrap/call-frame/event/shared-data/gameplay-data ABI set |
| `manual` | No safe automated route is declared. Stop and follow a release-specific guide. | Reserved for future incompatible persistent formats |

Generated C# and C++ bindings are one artifact family. They must always be
regenerated together from the same reflection manifest and API Contract; a
release never patches generated source text in place. ABI component revisions
are likewise a matched set. Compatibility is not inferred merely because one
side still compiles.

## Inspect an upgrade

The bundled CLI scans recognized JSON documents, `.usgraph`, `.usids`, and
`.uschunk` files. Unrelated JSON schemas are ignored. Reports contain relative
paths and no timestamps or source content, so the same input produces the same
report and does not leak machine paths.

```powershell
dotnet Finare.Cli.dll upgrade-plan . `
  --out upgrade-plan.json `
  --fail-on-blocked
```

Use `--policy <file>` to test against a specific release policy. Without it,
the CLI loads the policy shipped beside the executable. `--fail-on-blocked`
returns data-error status when a recognized persistent family has an unreadable
version, after still writing the plan.

The plan classifies every recognized file as `current`, `rewrite`,
`regenerate`, `rebuild`, or `blocked`. An old derived schema does not need to be
read to be classified: its declared source of truth is sufficient to request
regeneration. An unknown reflection or API Contract revision is blocked because
guessing its persistent semantics could lose project intent.

## Apply direct migrations

`upgrade-apply` writes to a separate directory and never edits inputs:

```powershell
dotnet Finare.Cli.dll upgrade-apply ProjectContracts `
  --out UpgradedContracts
```

The first implementation directly rewrites the two families for which the
release already has verified read-old/write-current serializers:

- Unreal reflection v0.1, v0.2, or v0.3 to canonical v0.4;
- Unreal API Contract v0.1 to canonical v0.2.

All migrated files and `upgrade-report.json` are committed as one filesystem
transaction. If the scan also contains a `regenerate`, `rebuild`, or `blocked`
action, no migrated artifact is written; only the report is produced and the
command returns data-error status. This prevents a partially upgraded project
from looking complete. Re-run the declared generators/builds, then plan again
until every item is current or directly rewritable.

## Release rules

- A current persistent schema cannot silently change semantics; change its
  version and add a fixture for the previous version.
- Direct migrations must be deterministic, idempotent, tested from checked
  legacy input, and preserve the original input.
- Derived outputs do not receive bespoke migration code when their authoritative
  inputs can reproduce them.
- Compiled chunks and ABI tables are never mutated. Rebuild them with one
  release so their managed/native hashes and versions stay matched.
- Removing a previously readable version requires a release note and an
  intermediate supported upgrade path.
- Upgrade reports contain versions, actions, relative paths, and reasons only;
  they do not embed project source, environment variables, user names, or
  absolute paths.
