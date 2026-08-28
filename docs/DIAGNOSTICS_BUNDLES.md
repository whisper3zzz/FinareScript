# Privacy-safe diagnostics bundles

`diagnostics-bundle` turns a directory of UnifiedScript gate/report artifacts
into one deterministic support ZIP. Its default policy is deliberately strict:
the command collects known versioned reports, not every file that happens to be
near them.

```powershell
dotnet UnifiedScript.Cli.dll diagnostics-bundle Saved\UnifiedScript `
  --out unifiedscript-diagnostics.zip
```

The source checkout ships
`schemas/unifiedscript.diagnostics-policy.v0.1.json`; an installed SDK carries
the same policy in `Schemas` and beside its CLI. `--policy <file>` exists for a
release-owned policy override, not for silently broadening a customer support
upload.

## Safe-default boundary

The v0.1 policy only admits 29 exact report schemas such as CI lanes, release
and compatibility gates, runtime diagnostics, reliability/scale results,
performance budgets, compiled-chunk load and ReadyToRun experiment profiles,
binding coverage, onboarding, SDK install/package, and round-trip reports. It
excludes by construction:

- `.cs`, `.cpp`, `.h`, Blueprint/uasset, binary, log, dump, trace, and profiler
  capture files;
- HIR, Semantics, graph, Authoring Surface, Script Asset, identity, API
  Contract, reflection, deployment, and compiled program inputs;
- malformed JSON, third-party schemas, and unknown/future report revisions;
- filesystem reparse points, reports over 16 MiB, more than 256 reports, and a
  sanitized payload over 64 MiB.

Approved reports are parsed and rewritten into a diagnostics envelope. The
sanitizer replaces path/root/directory/endpoint/URL/name fields and explicit
source, environment, command-line, credential, identity, symbol, project,
asset, type, method, message, exception, and stack fields. Free-form strings
are redacted by default; only policy-declared status, version, configuration,
metric, digest, phase, and result fields retain their string values. Values in
that small safe set are still redacted when they look like absolute
user/machine paths, credentials, email addresses, or IP addresses. The ZIP
never stores the input filename or directory name: reports receive ordinal
names such as `reports/0001-report.json`.

This is data minimization, not encryption or a proof that arbitrary future
schemas contain no sensitive value. Review `manifest.json` and the small JSON
envelopes before sharing a bundle outside the team. Raw logs and crash dumps
must be handled through a separate, explicit support process; v0.1 has no flag
that adds them accidentally.

## Reproducibility and integrity

Input files are enumerated in ordinal order. Object properties are emitted in
ordinal order, report names are ordinal, line endings are LF, ZIP entry order is
fixed, and every entry timestamp is `2000-01-01 00:00:00`. The same policy and
input bytes therefore produce the same ZIP bytes and SHA-256.

`manifest.json` uses `unifiedscript.diagnostics-bundle/v0.1` and records:

- policy version and `safe-default` privacy mode;
- scanned/included counts and sanitized byte total;
- each ordinal report's original schema, sanitized size/SHA-256, and redaction
  count;
- aggregate skip counts for non-JSON, invalid/unapproved/oversized input,
  reparse points, and report-limit overflow.

The CLI prints the output path, archive SHA-256, report count, total redactions,
and privacy mode. It writes the completed archive through the same transactional
output mechanism as other UnifiedScript artifacts, so an interrupted build does
not leave a successful-looking partial ZIP.

## Recommended support workflow

1. Point the command at the narrowest artifact/run directory that reproduces
   the issue, not a repository or user profile root.
2. Keep the bundled policy from the exact SDK/release that produced the reports.
3. Inspect `manifest.json`; an unexpected `unapprovedSchema` count usually means
   the report came from a different release and should be upgraded or collected
   with that release's CLI.
4. Inspect every diagnostics envelope. Confirm redaction counts are plausible
   and no project identifiers remain.
5. Share the ZIP hash alongside the bundle so support can confirm it received
   the reviewed bytes.

Because source and raw logs are absent, the first bundle may not explain every
failure. Support should request a second, explicitly scoped artifact only after
the safe bundle shows which phase and report family needs deeper evidence.
