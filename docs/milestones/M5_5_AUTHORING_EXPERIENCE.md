# M5.5 Script Asset and Authoring Experience

## Outcome

State declarations and behavior bodies are text/visual/AI-authorable through
one semantic model, with no required Blueprint-only variables and without
turning source lines into a spaghetti graph.

## Why this precedes more runtime work

M5.1 through M5.4 prove one CoreCLR execution/debug backend. The largest
remaining product risk is no longer whether code can run; it is whether the
authoring model actually removes the Blueprint and AI bottlenecks. Async
execution, native managed-debug traps, and UE binding should not harden an asset
model that still omits state declarations.

## Planned vertical slice

- backend-neutral Script Asset and Declaration IR
- stable semantic identities for type-level state and function signatures
- supported type/default/exposure binding from C# properties or fields
- canonical Declaration IR JSON and strict validation
- typed declaration semantic patches with source/configuration preconditions
- transactional Roslyn application and identity-sidecar reconciliation
- actual Graph/Declaration artifacts consumed by the visual editor
- declaration list, inspector, and Structure/Split/Code synchronization
- coordinate-free AI outline plus declaration/body semantic patch preview
- existing CoreCLR debug pause painted onto real visual statement cards
- a sample asset with zero Blueprint-only declarations

## Deliberate limits

The first slice does not bind Unreal reflection, cook assets, or generate
UObjects. Exposure metadata remains engine-neutral until M6 consumes it.
Arbitrary class declarations, constructors, events/delegates, inheritance,
generic state, object ownership, replication mechanics, and editor layout
collaboration remain later bounded additions.
