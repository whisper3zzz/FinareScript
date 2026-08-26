# Unreal API Contract

The project API contract is the product boundary between broad Unreal
reflection and the API actually shipped to a UnifiedScript game module. One
strict allowlist drives all generated consumers:

```text
selected UClass reflection
        + project API contract
        -> typed C# wrappers
        -> direct native C++ thunks
        -> binding coverage report
        -> visual API node catalog
```

This prevents managed code, the native dispatcher, and the visual palette from
silently exposing different Unreal members.

## Contract schema

`unifiedscript.unreal-api-contract/v0.1` requires a contract name, managed
namespace, generated-thunk header and C++ namespace, plus one or more exact
type/member selectors. Type paths and member names must exist in the supplied
`unifiedscript.unreal-reflection/v0.2` document. Unknown fields, duplicate
selectors, stale types, and stale members fail generation.

```json
{
  "schemaVersion": "unifiedscript.unreal-api-contract/v0.1",
  "contractName": "Game.Gameplay",
  "managedNamespace": "Game.UnifiedScript.Generated",
  "nativeHeader": "GameGeneratedThunks.h",
  "nativeNamespace": "UE::UnifiedScript::Generated::Game",
  "types": [
    {
      "path": "/Script/Engine.Actor",
      "members": ["GetActorForwardVector", "GetActorRightVector"]
    }
  ]
}
```

Member lists are explicit rather than wildcard-based. Adding a Blueprint API
to an engine class therefore cannot accidentally expand the shipped scripting
surface after an engine upgrade.

## Reflection export

The editor exporter supports an exact class allowlist so projects can reflect
selected Engine classes without exporting every Engine UObject:

```powershell
UnrealEditor-Cmd.exe Game.uproject -run=UnifiedScriptExport `
  -Output=D:\Game\Generated\Game.reflection.json `
  '-Modules="Game,Engine"' `
  '-Classes="/Script/Game.Player,/Script/Engine.Actor,/Script/Engine.Pawn"' `
  -unattended -nop4 -nosplash
```

## Atomic generation

`ue-bindings` accepts the contract and writes every requested output as one
transaction. Contract metadata owns both generated namespaces.

```powershell
unifiedscript ue-bindings Generated\Game.reflection.json `
  --contract Contracts\Game.unreal-api-contract.json `
  --out Managed\Generated\Game.bindings.generated.cs `
  --native-out Source\Game\Generated\Game.native-thunks.generated.cpp `
  --nodes-out Generated\Game.api-nodes.json `
  --report Generated\Game.binding-coverage.json `
  --fail-on-rejected
```

The `unifiedscript.unreal-api-node-catalog/v0.1` output is a
reflection-compatible visual catalog with stable member IDs, generated managed
type names, categories, purity, and typed control/data pins. Script Assets can
set `ApiNodeCatalogPath`; the Slate authoring surface then builds its Unreal API
palette from that product-specific catalog instead of the repository demo
manifest.

Functions with more than four parameters use the generic ABI v3 CallFrame
path, including names and enums. Fixed signatures with zero to four parameters
retain the specialized fast path.

## BlockBreaker slice

BlockBreaker selects ten native types and 43 members. Its bootstrap path uses
generated `Actor`, `Pawn`, `SceneComponent`, `CameraComponent`,
`StaticMeshComponent`, and `UnifiedScriptWorldLibrary` wrappers. The World
service loads assets/classes, retains loaded resources for the World-host
lifetime, and spawns Actors. `GameplayStatics` and the selected `FHitResult`
shape remain in the product contract for compatibility and cold-path use, but
the current player shot no longer invokes them across ABI v3.

The migrated rifle performs its visibility query through one Gameplay Data
Batch Trace and writes emitter/damage work into one command reservation. It
observes the two completion events on the following managed frame. Seven Gun
facade functions and their transient cached hit/effect state remain deleted.
The current standalone 10,000-frame data-plane run executes one complete
midpoint shot/hit and reports 120,027 generated ABI v3 calls with zero legacy
calls. The current matched three-trial gameplay gate prepares both runtimes
before measurement, alternates execution order, and reports a median of
3,497.508 ms for UnifiedScript versus 3,901.037 ms for Puerts on the local UE
5.8.1 machine (`UnifiedScript/Puerts = 0.896559`).
