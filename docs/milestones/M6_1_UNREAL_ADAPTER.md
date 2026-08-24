# M6.1: Unreal Engine adapter

## Status

Phase A reflection/binding code is implemented. The .NET suite and repository
invariants pass, and UE 5.7.4 UHT accepts the plugin headers. Full native C++
compilation and a live commandlet export smoke test were stopped by request and
remain an explicit verification item. Runtime invocation, UObject lifetime,
world domains, Slate authoring, and packaging remain open.

## Completed Phase A contract

1. A real UE 5.7 host project owns an enabled two-module plugin.
2. UHT sees `UUnifiedScriptComponent` and the headless export commandlet.
3. The exporter selects named native modules and emits deterministic reflected
   class, property, function, and recursive type data.
4. `UnifiedScript.Unreal` strictly validates and normalizes schema v0.1 without
   exposing UE types to compiler projects.
5. `ue-bindings` generates deterministic typed wrappers over an opaque handle
   and invocation bridge.
6. Unsupported native shapes produce stable `USUE2xxx` diagnostics rather than
   guessed interop code.
7. A checked-in manifest and generated wrapper cover component state plus void
   and returning function calls.
8. One local helper discovers the engine and runs build, export, and generation
   without committing an absolute UE path.

## Non-goals of Phase A

- no raw pointer is treated as a durable managed identity;
- no `FProperty` memory is read or written from managed code;
- no `ProcessEvent` call is issued;
- no CoreCLR host is embedded in the editor process;
- no Blueprint variable is generated as a second declaration source;
- no editor UI parses C# or Graph independently of Authoring Surface.

## Phase B acceptance gate

The next phase is complete only when a live fixture UObject can be wrapped by a
generation-checked weak handle, a primitive property can be read/written, a
fixture UFunction can be invoked through `ProcessEvent`, destroyed objects fail
with a stable error, and all access is rejected off the owning game thread.
The managed wrapper generated in Phase A must be the caller; a one-off test API
does not satisfy the gate.
