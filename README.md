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
- deterministic multi-document compilation requests and cross-file binding;
- resolved types, declaration references, overloads, and conservative call effects;
- a versioned, configurable method-effect catalog;
- deterministic, effect-aware HIR-to-graph projection with control and data edges;
- a versioned semantic patch protocol with source/configuration conflict guards;
- transactional graph-literal-to-C# source rewriting with trivia preservation;
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

Graph literal edit
  -> semantic patch bound to source + semantic configuration IDs
  -> Roslyn literal replacement
  -> full profile and semantic revalidation
  -> updated C# source (only if the whole patch succeeds)

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

## Analyze multiple source files

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  analyze-files \
  samples/MultiFile/GameplayApi.cs \
  samples/MultiFile/DamageScript.cs \
  --assembly UnifiedScript.MultiFileSample \
  --effects samples/MultiFile/effects.json \
  --out artifacts/multi-file
```

`analyze-files` sorts logical source paths before parsing, so changing command
line input order does not change HIR, semantic IDs, or Graph JSON. `--reference`
may be repeated to add explicit metadata assemblies; their contents contribute
to the semantic configuration ID.

Effect catalogs use `unifiedscript.effects/v0.1`. Rules match assembly,
containing type, and method metadata names. `*` is supported as a whole-field
wildcard. User rules override the bootstrap `System.Math`/`System.MathF` rules;
unmatched calls remain conservatively read/write effectful.

## Apply a visual literal patch

The patching sample changes the graph literal `1.0f` to `1.5f` and writes a
new C# source file:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-patch \
  samples/Patching/DamageRules.cs \
  --patch samples/Patching/ScaleDamage.patch.json \
  --out artifacts/patched/DamageRules.cs
```

`unifiedscript.patch/v0.1` binds every patch to both the source compilation ID
and semantic configuration ID emitted in Graph schema v0.2. It also carries the
expected old literal text. A stale source, changed reference/effect
configuration, missing target, changed old value, non-literal replacement, or
semantically invalid result rejects the entire patch and writes no source.

`apply-patch` currently accepts one source document and one or more literal
replacement operations. The underlying patch applier supports multi-document
compilation requests, but persistent ID sidecars and structural graph edits are
still M3 work.

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
- `.csproj`/MSBuild compilation loading;
- namespace/API allow and deny policies;
- flow-sensitive nullable state in the interchange model;
- persistent IDs across arbitrary text movement and value-length changes;
- graph-to-source structural edits such as rewiring, inserting, or deleting nodes;
- loops, switch, lambdas, exceptions, or general-purpose C# visualization.

See [ARCHITECTURE.md](docs/ARCHITECTURE.md),
[IDENTITY_SIDECAR.md](docs/IDENTITY_SIDECAR.md),
[M1_ROSLYN_GRAPH_FOUNDATION.md](docs/milestones/M1_ROSLYN_GRAPH_FOUNDATION.md),
[M2_SEMANTIC_BINDING.md](docs/milestones/M2_SEMANTIC_BINDING.md), and
[M3_BIDIRECTIONAL_PATCHING.md](docs/milestones/M3_BIDIRECTIONAL_PATCHING.md)
for the planned sequence.
