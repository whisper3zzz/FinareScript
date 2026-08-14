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

## Verification

```bash
dotnet build UnifiedScript.slnx
dotnet test UnifiedScript.slnx --no-build
python3 tools/validate_project.py
```

