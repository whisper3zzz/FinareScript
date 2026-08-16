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
7. `UnifiedScript.Semantics` must not expose Roslyn, CLR-hosting, or UE types.
8. Unknown calls are effectful by default; purity must be established explicitly.
9. `.usgraph` layout is non-semantic: layout reconciliation must never mutate
   source, HIR, bound semantics, or `.usids`.
10. Patch plans must validate external preconditions against the initial HIR;
    internal rebasing may account only for earlier stages in the same plan.

## Verification

```bash
dotnet build UnifiedScript.slnx
dotnet test UnifiedScript.slnx --no-build
python3 tools/validate_project.py
```
