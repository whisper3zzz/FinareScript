# Unreal Script Deployment

UnifiedScript deployment manifests turn text-owned managed program identity
into checked Unreal Data Assets. They are the reusable boundary between a
gameplay module and `UUnifiedScriptComponent`; native gameplay classes no
longer need to embed assembly paths, managed type names, or authoring paths.

## Manifest contract

`unifiedscript.unreal-script-deployment/v0.1` contains a non-empty `scripts`
array. Every entry requires:

- `assetPath`: a unique `/Game/...` package path without the object suffix;
- `assemblyPath`: the managed DLL, with `{ProjectDir}` and `{PluginDir}` tokens
  resolved by the runtime component;
- `typeName`: the complete managed script type;
- `requiredTargetClass`: the native or Blueprint-generated UObject class that
  the component target must derive from.

Source, Declaration IR, Authoring Surface, API node catalog, declaration asset
key, and Workbench URL are optional authoring fields. Unknown JSON fields, duplicate asset paths,
invalid package names, unloaded target classes, and non-UnifiedScript assets at
the destination are rejected during preflight.

The checked-in example is
[`samples/Unreal/UnifiedScript.Unreal.Demo.deployment.json`](../samples/Unreal/UnifiedScript.Unreal.Demo.deployment.json).

## Synchronization

Build the editor target, then run:

```powershell
UnrealEditor-Cmd.exe Project.uproject `
  -run=UnifiedScriptSyncAssets `
  -Input=D:\Project\Game\Deployment\Game.unifiedscript-deployment.json `
  -unattended -nop4 -nosplash
```

Add `-ValidateOnly` for CI preflight without package writes. A normal run
preflights the complete manifest before changing its first package, then
creates or updates each `UUnifiedScriptAsset` deterministically. Re-running the
same manifest updates the same assets and is safe for build automation.

## Runtime target contract

`UUnifiedScriptAsset.RequiredTargetClass` is checked by
`UUnifiedScriptComponent` before assembly loading or managed instance creation.
A Character script cannot silently start on an unrelated Actor, and a failed
target check does not advance a managed generation or replace a running script.
The component continues to accept direct assembly/type configuration only as a
legacy advanced fallback.

## Product boundary

The deployment layer owns script identity, target compatibility, and authoring
artifact locations. API exposure is owned separately by the strict project
contract described in [UNREAL_API_CONTRACT.md](UNREAL_API_CONTRACT.md), so
migration failures stay explicit and auditable.

BlockBreaker is the first real consumer: Player and Rifle are synchronized from
one manifest, start exclusively through their generated Script Assets, retain
34/34 product-contract native-thunk coverage, including selected Engine APIs,
and run without the Puerts plugin.
