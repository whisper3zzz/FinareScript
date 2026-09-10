# Contributing

## Invariants

1. `[Visual]` syntax must be either losslessly representable or rejected with a
   stable diagnostic code.
2. Text and graph projections share semantic IDs and HIR; neither decompiles the
   other's generated output.
3. Compiler artifacts must be deterministic for identical source, path, and
   options.
4. New HIR nodes require graph projection and serialization tests.
5. Core and graph projects stay independent of Unreal Engine.
6. UE integration must arrive through adapters after the core model stabilizes.
7. `Finare.Semantics` must not expose Roslyn, CLR-hosting, or UE types.
8. Unknown calls are effectful by default; purity must be established explicitly.
9. `.usgraph` layout is non-semantic: layout reconciliation must never mutate
   source, HIR, bound semantics, or `.usids`.
10. Patch plans must validate external preconditions against the initial HIR;
    internal rebasing may account only for earlier stages in the same plan.

## Verification

Use the risk-based rules in [AGENTS.md](AGENTS.md): validate the behavior that
changed. Documentation normally needs a content/link review, a local logic fix
needs relevant tests, and cross-language or lifecycle changes need the affected
native integration checks. Broaden coverage when there is a concrete risk,
not simply because a file belongs to a large module.

The [Engineering Harness](docs/HARNESS.md) is optional orchestration. Use its
plan for impact discovery or a profile when that combined coverage is useful;
direct focused commands remain valid. No plan file or profile run is required
for every change. The Roadmap remains the product ordering authority.

```powershell
# Optional: inspect possible impact, then choose relevant verification.
python -B tools/harness.py plan --paths <changed files>
# Only when the full managed and generation baseline is relevant:
python -B tools/harness.py run --profile precommit
```

The profile name does not authorize or create a Git commit. A green quick
profile cannot prove native, package, or performance behavior. Formal release
and milestone acceptance retain their relevant requirements; routine edits do
not automatically trigger full acceptance. Do not rerun compilation or UE
solely because a final documentation update changed a report fingerprint.

Underlying commands remain available when their coverage is needed:

```bash
dotnet build Finare.slnx
dotnet test Finare.slnx --no-build
python3 tools/validate_project.py
```
