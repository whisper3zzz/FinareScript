# Contributing

Start with [architecture](docs/ARCHITECTURE.md) for module boundaries and the
[testing guide](docs/TESTING.md) for focused commands. The
[capability summary](docs/CAPABILITIES.md) describes current support; the
[roadmap](docs/ROADMAP.md) owns unfinished product outcomes.

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

Managed tests are grouped by behavior using xUnit categories; Python tools,
native UE Automation, consumer workflows and release gates retain separate
execution scopes. Follow [TESTING.md](docs/TESTING.md) to select a suite and
interpret its result. Preserve test names, fixtures and assertions when
reorganizing coverage; fix failures rather than silently excluding cases.

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

## Documentation ownership

| Document | Owns |
| --- | --- |
| `README.md` | Project introduction, first steps and navigation |
| `docs/DOCUMENTATION.md` | Consumer documentation shipped in the SDK |
| `docs/CAPABILITIES.md` | Current supported behaviors and limits |
| `docs/ROADMAP.md` | Active outcome, remaining work and completion conditions |
| `docs/TESTING.md` | Test selection, execution and evidence boundaries |
| Technical documents and active plans | Contracts, design decisions and relevant detailed evidence |
| `docs/history/` and `docs/ROADMAP_HISTORY.md` | Dated implementation and acceptance history |

Keep consumer links portable within the packaged catalog. Repository-only
development instructions belong in this guide or the root README. A document
move must preserve useful examples and repair relative links. Historical logs
must retain their original revision and scope; moving them does not make them
new validation results. Do not require exact prose in tests when document
existence, links or executable contracts express the actual requirement.
