# UnifiedScript

UnifiedScript is an experimental dual-modal gameplay authoring system. A
restricted C# profile and a structured visual graph are intended to be two
lossless projections of one program model. The first execution backend will use
Roslyn and CoreCLR; Unreal Engine integration is intentionally deferred until
the language and projection model are proven independently.

## Current vertical slice

The repository currently implements:

- deterministic semantic IDs shared as compact JSON strings across compiler artifacts;
- source ranges and structured diagnostics;
- a Roslyn validator for Visual C# profile v0.1;
- C# syntax lowering into a polymorphic high-level IR (HIR);
- a Roslyn compilation host and language-neutral bound semantic model;
- resolved types, declaration references, overloads, and conservative call effects;
- deterministic, effect-aware HIR-to-graph projection with control and data edges;
- versioned HIR, semantics, and graph JSON emission through a small CLI;
- xUnit coverage for validation, lowering, identity, graph projection, and
  deterministic output.

The v0.1 profile accepts `[Visual]` methods containing local variables,
expressions, calls, assignments, `if/else`, `return`, casts, and `await`.
Unsupported syntax is rejected explicitly instead of being approximated.

## Architecture

```text
C# source
  -> Roslyn full-fidelity syntax tree
  -> Visual Profile validation
  -> Unified HIR
  -> Roslyn SemanticModel
  -> language-neutral bound semantics
  -> deterministic, effect-aware graph projection

Later:
  -> semantic MIR
  -> Roslyn IL/CoreCLR backend
  -> shared debugger/source map
  -> Unreal Engine adapter
```

The HIR is the semantic contract. IL and future visual layouts are derived
artifacts; neither is used as a decompilation source.

CLI inputs inside the current working directory use normalized relative source
paths, so artifacts remain stable when the repository is checked out elsewhere.

## Requirements

- .NET SDK 10.0.201 or a compatible feature-band roll-forward

## Build and test

```bash
dotnet restore UnifiedScript.slnx
dotnet build UnifiedScript.slnx --no-restore
dotnet test UnifiedScript.slnx --no-build
python3 tools/validate_project.py
```

## Analyze the sample

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze samples/DoorController.cs \
  --out artifacts/sample
```

This emits:

```text
artifacts/sample/DoorController.hir.json
artifacts/sample/DoorController.semantics.json
artifacts/sample/DoorController.graph.json
```

Without `--out`, the graph JSON is written to stdout.

## Repository layout

```text
src/UnifiedScript.Core/      Semantic IDs, diagnostics, HIR, [Visual]
src/UnifiedScript.Semantics/ Language-neutral types, symbols, effects, bindings
src/UnifiedScript.CSharp/    Roslyn profile validation and lowering
src/UnifiedScript.Graph/     Deterministic visual graph projection
src/UnifiedScript.Cli/       Analysis command-line interface
tests/UnifiedScript.Tests/   Unit and vertical-slice tests
samples/                     Input programs for manual inspection
docs/                        Architecture and milestone plans
```

## Scope boundary

This milestone does not include:

- Unreal Engine bindings;
- a UE graph editor;
- IL emission or CoreCLR hosting inside another process;
- semantic type binding against UE APIs;
- multi-file or `.csproj` compilation loading;
- a configurable API/effect catalog;
- flow-sensitive nullable state in the interchange model;
- code generation from graph edits back into Roslyn syntax;
- loops, switch, lambdas, exceptions, or general-purpose C# visualization.

See [ARCHITECTURE.md](docs/ARCHITECTURE.md),
[IDENTITY_SIDECAR.md](docs/IDENTITY_SIDECAR.md),
[M1_ROSLYN_GRAPH_FOUNDATION.md](docs/milestones/M1_ROSLYN_GRAPH_FOUNDATION.md),
and [M2_SEMANTIC_BINDING.md](docs/milestones/M2_SEMANTIC_BINDING.md)
for the planned sequence.
