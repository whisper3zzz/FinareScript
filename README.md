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
- transactional graph-to-C# literal, operator, and argument-connection rewriting;
- opt-in `.usids` sidecars that preserve node identity across formatting and edits;
- explicit patch identity pins and tombstones for deleted semantic nodes;
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

Graph edit
  -> semantic patch bound to source + semantic configuration IDs
  -> typed/preconditioned Roslyn replacement
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

## Initialize persistent identities

Create the source-controlled identity sidecar once:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  identity-init \
  samples/Patching/DamageRules.cs \
  --out artifacts/identity/DamageRules.cs.usids
```

`unifiedscript.identity/v0.1` stores random 128-bit semantic IDs, structural and
shape fingerprints, token-context evidence, anchors, generations, and
tombstones. Supplying it with `--identity` makes analysis reconcile the current
HIR against the stored evidence. Unchanged sources reproduce byte-identical
sidecars; whitespace and comment edits preserve IDs even though the source
digest and generation advance.

The checked-in sample sidecar is
`samples/Patching/DamageRules.cs.usids`. `analyze` and `analyze-files` accept
repeatable `--identity`; when `--out` is present, updated sidecars are emitted
beside the other analysis artifacts without overwriting the input files.

## Apply a visual literal patch

The patching sample changes the graph literal `1.0f` to `100.0f`, deliberately
changing its source width while retaining the same persistent node ID:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-patch \
  samples/Patching/DamageRules.cs \
  --identity samples/Patching/DamageRules.cs.usids \
  --patch samples/Patching/ScaleDamage.patch.json \
  --out artifacts/patched/DamageRules.cs \
  --out-identity artifacts/patched/DamageRules.cs.usids
```

`unifiedscript.patch/v0.2` binds every patch to both the source compilation ID
and semantic configuration ID emitted in Graph schema v0.2. It also carries the
expected old literal text. A stale source, changed reference/effect
configuration, missing target, changed old value, non-literal replacement, or
semantically invalid result rejects the entire patch and writes no source.

The Roslyn replacement receives a temporary identity pin, so the edited node's
old ID is explicitly carried into the reconciled sidecar even when its
fingerprint and span length change. Deleted live entries move to tombstones;
ambiguous matches allocate new IDs and emit `UVS3101` rather than guessing.

## Apply structural graph patches

Patch v0.2 adds `changeOperator` and `reconnectArgument`. The checked-in
operator sample changes the binary node from multiplication to division while
requiring the expected result, left-port, and right-port type IDs:

```bash
dotnet run --project src/UnifiedScript.Cli -- \
  apply-patch \
  samples/Patching/DamageRules.cs \
  --identity samples/Patching/DamageRules.cs.usids \
  --patch samples/Patching/DivideDamage.patch.json \
  --out artifacts/operator/DamageRules.cs \
  --out-identity artifacts/operator/DamageRules.cs.usids
```

Operator changes are restricted to the same result-preserving family, such as
arithmetic-to-arithmetic or comparison-to-comparison. Both unary and binary
operators are supported. The edited operator node receives an identity pin.

`reconnectArgument` safely exchanges two existing positional arguments of one
resolved invocation. Its patch records both indices, both expected endpoint
IDs, and both expected parameter type IDs. The types must match exactly and
both argument subtrees must be effect-free; otherwise changing their C#
left-to-right evaluation order is rejected. The invocation and both moved
argument roots retain their persistent IDs, so Graph edges move without
inventing duplicate semantic nodes.

Overlapping operations—for example changing an operator inside an invocation
while also reconnecting that invocation in the same patch—are rejected and
must be submitted as sequential patches with a fresh baseline. With
`--identity`, `--out-identity` is required and source plus updated sidecar are
written as one rollback-capable output transaction. The in-memory patcher
supports multi-document requests; the CLI commit boundary remains
single-document.

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
- graph-to-source insertion, deletion, and general cross-expression rewiring;
- loops, switch, lambdas, exceptions, or general-purpose C# visualization.

See [ARCHITECTURE.md](docs/ARCHITECTURE.md),
[IDENTITY_SIDECAR.md](docs/IDENTITY_SIDECAR.md),
[M1_ROSLYN_GRAPH_FOUNDATION.md](docs/milestones/M1_ROSLYN_GRAPH_FOUNDATION.md),
[M2_SEMANTIC_BINDING.md](docs/milestones/M2_SEMANTIC_BINDING.md), and
[M3_BIDIRECTIONAL_PATCHING.md](docs/milestones/M3_BIDIRECTIONAL_PATCHING.md)
for the planned sequence.
