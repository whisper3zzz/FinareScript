# BlockBreaker Visual/C# round-trip proof

M8.4 proves that Visual and C# are two authoring projections of the same real
BlockBreaker gameplay program. The proof edits the migrated Player and Rifle
scripts in the full multi-source project, reopens the result as compiler-owned
Graphs, and then runs the same managed, Unreal, asset, and package gates for
both authoring directions.

## Representative gameplay edits

The pinned Visual baseline is comparison commit
`b1d3de399c18fbf645ab533822d5f4f786a54683`. One project-level semantic patch
changes two stable Graph nodes:

- `BlockBreakerPlayerScript.ShouldFire` changes the fire threshold from `0.5f`
  to `0.6f`.
- `BlockBreakerRifleScript.ResolveDamage` changes the damage multiplier from
  `1.0f` to `1.25f`.

The emitted C# is then edited as ordinary text without changing behavior. The
Player local is renamed from `thresholdReached` to `fireRequested` and `0.6f`
is written as `0.60f`; the Rifle local is renamed from `tunedDamage` to
`resolvedDamage` and `1.25f` is written as `1.250f`. Reanalysis reconciles each
`.usids` sidecar and reopens equivalent Graphs. The final comparison source is
commit `e62a46e3fb80a50232c6c23c117e98b58b5ee0c5`.

This is a whole-project operation rather than two isolated snippets.
`apply-project-patch` loads the actual `BlockBreaker.UnifiedScript.csproj`, all
of its sources and references, applies both node edits atomically, and emits
only changed sources plus reconciled identity sidecars into a contained output
directory.

## Equivalence gates

`verify-roundtrip` compiles the Visual-emitted and code-edited candidates and
requires all of the following for each selected method:

1. the same stable method identity;
2. the same bound semantic digest, excluding source spans, local spelling, and
   other presentation-only differences;
3. identical MethodDef IL, stack/local signatures, and exception regions;
4. identical results for the checked-in boundary cases;
5. equivalent Script Asset declarations and state contracts.

The BlockBreaker runner additionally creates two complete gameplay assemblies,
one from each candidate, and puts each through the same UE 5.8.1 map. It
requires identical 300-frame results, both Script Asset paths, and matching
`.uasset` hashes. With `-Package`, each candidate independently runs
Build/Cook/Stage/Pak/Archive and the packaged executable smoke test; the two
delivery reports must agree on every required gameplay result.

The full local acceptance run on 2026-08-28 passed:

| Gate | Visual candidate | C# candidate |
| --- | ---: | ---: |
| Frames | 300 | 300 |
| Shots / hits | 1 / 1 | 1 / 1 |
| Gameplay command completions | 2 | 2 |
| Fast / CallFrame calls | 3,631 / 3,631 | 3,631 / 3,631 |
| Legacy calls | 0 | 0 |
| Packaged files | 249 | 249 |
| Packaged bytes | 1,030,617,655 | 1,030,617,655 |
| Puerts or TypeScript package files | 0 | 0 |

Player and Rifle Script Asset SHA-256 values were byte-identical between the
two candidates. The machine-readable report uses schema
`unifiedscript.blockbreaker-round-trip-report/v0.1` and records the individual
semantic, IL, execution, asset, gameplay, and packaged-build verdicts instead
of collapsing them into a single smoke exit code.

## Run the proof

Managed analysis, Visual-to-C# emission, code edit, Graph reopen, and managed
equivalence:

```powershell
.\comparison\verify-blockbreaker-roundtrip.ps1
```

Add two live Unreal gameplay candidates:

```powershell
.\comparison\verify-blockbreaker-roundtrip.ps1 -RunUnreal
```

Add independent packaging and packaged smoke for both candidates:

```powershell
.\comparison\verify-blockbreaker-roundtrip.ps1 -RunUnreal -Package
```

Reports and intermediate candidates are written below
`artifacts/roundtrip/blockbreaker-*` and are intentionally ignored by Git. The
runner exports the pinned baseline, backs up the comparison worktree, and
restores it in `finally`; it refuses to start when the tracked Player/Rifle
source or identity inputs are already dirty.

## Conflict and merge contract

Visual/C# coexistence never resolves ambiguity by overwriting disk state:

- a stale Surface/draft revision is rejected with `UAW1005`;
- an external source or identity edit is rejected with `UAW1002`;
- declaration and method-body stages can share one named draft, preview as one
  exact diff, publish one source/identity/history/runtime generation, and undo
  as one entry;
- if an external edit appears after that combined preview, group commit keeps
  the external file, source identity, history, artifacts, and runtime unchanged;
  neither the declaration edit nor the body edit is partially published.

The HTTP Workbench regression exercises the stale request, successful combined
merge, exact undo, and post-preview external conflict against physical files.

## Boundary

M8.4 proves the product loop on representative, live gameplay and the complete
delivery path. It does not claim that every C# construct is visually editable.
M8.5 owns the remaining language-profile closure, diagnostics, and unsupported
construct inventory.
