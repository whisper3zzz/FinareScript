# 2026-09-04 — Native extension bindings

Status: in-progress

## Outcome and scope

Make public, non-reflected native APIs usable through the same contract-driven
C#, C++ and visual-node generation as reflected APIs. This is M13.5 capability
work, not a prerequisite to completing the entire Script Class model.

Puerts explicitly supports non-UFUNCTION/UPROPERTY members through
[static binding declarations](https://github.com/Tencent/puerts/blob/master/doc/unreal/zhcn/template_binding.md).
Our existing generator can call public native members, but contract selection
only admits reflected input. Reuse that generator instead of adding facades.

## Decisions and remaining work

- Add an optional, independently versioned native-extension declaration to the
  existing API contract. Existing contracts keep their canonical representation.
  The owner must be an exported native UObject class with matching C++ identity
  and header; an extension cannot replace a reflected member.
- Preserve exact overload identity, const methods and value/const-reference/
  mutable-reference parameter passing. Generate exact C++ member pointers.
- Native-only members must never fall back to reflection. Record their origin
  in the shared model and reject unsupported generated routes before publication.
- First implementation covers instance/static methods, overloads, fields,
  values/containers and ref/out on existing UObject identities. Demonstrate
  generated C# calls against real native members lacking reflection metadata.
  Implemented through the normal API contract; native-only origin survives
  proposal, generation, runtime generation views, coverage and node metadata.
- Ordinary C++ object construction, owned/borrowed opaque handles, inheritance,
  converters and native callbacks remain subsequent parts of this module. They
  require a distinct ownership contract; do not pretend they are UObjects or
  close the full native-extension roadmap item after the first implementation.

### Ordinary C++ runtime checkpoint

- Implemented `FUnifiedScriptNativeObjectRegistry`, owned by the World bridge
  context, and optional Native Object ABI v1 appended to the existing v2 table.
  Existing UObject ABI cells and prefixes are unchanged. Native object and scope
  IDs are separate process-unique tokens, never pointers and never recycled.
- Owned pointers have one deleting owner. Borrowed children are tied to a live
  same-scope parent and retired before parent destruction. Native code must
  invalidate aliases before moving/replacing borrowed storage. Pointer and
  child/scope indexes avoid scanning every live object on registration/release;
  subtree invalidation is iterative rather than recursive.
- Managed generation bridges open scopes lazily. Explicit lease disposal,
  generation disposal, transport disposal and World detach clean ownership.
  Worker operations are rejected without side effects; failed worker disposal
  remains retryable on the game thread. No finalizer runs native destruction.
- A stopped previous generation retains native objects until reload commit
  retires it. A failed candidate releases its own scope; restarting the previous
  generation can reuse its existing native handles. `OnStop` alone does not
  destroy the retained generation's native objects. Application code that
  reallocates resources in `OnStart` must release or reuse its existing leases.
- Failed native factories roll back allocations made during that invocation;
  this does not promise rollback of arbitrary native method side effects.
- The current native-object lane carries up to 16 fixed value inputs, one fixed
  value result and a separate optional object handle. It deliberately rejects
  arena-backed values and UObject cells. Rich value/container/ref/out codecs and
  native-object argument contracts remain to be connected, not silently mapped
  to scalars or UObject handles.
- **Next:** extend declarations and shared generation to native object types,
  constructors, member/field calls and ownership-qualified return values.
  Generate native registration, typed C# wrappers, coverage and visual nodes.
  The current manual member registrations exist only inside the CMD test fixture;
  they are not a new project-facade authoring workflow or a claim of parity.

## Relevant verification

Contract tests cover deterministic generation, collisions, unsupported routes,
exact overloads and no reflection fallback. Compile and execute the generated
native dispatch with CMD Automation and exercise the generated managed wrappers.
Keep the protected local engine/configuration and generated-header edits intact.

The complete managed suite passes 714 tests. The two native CMD tests pass,
including generated C# calls, reload and World teardown; see
`artifacts/m13-5-native/automation-reload.log`. The first runtime run exposed a
missing capability forwarder on the generation bridge, now fixed and covered.
The combined current-source UE CMD suite also passes all 31 tests in
`artifacts/m13-5-native/unreal-suite.log`; the Demo chunk was regenerated.
The ordinary C++ runtime now passes the complete managed suite (729 tests), UE
Editor build and complete CMD suite (34 tests), including three new ownership,
transport and managed-lifecycle tests. It covers failed construction/startup,
reload rollback, old-handle retention, explicit/forgotten Dispose, World teardown,
deep borrowed chains and many independent owned objects. See
`artifacts/m13-5-native-objects/managed/managed-complete.trx` and
`artifacts/m13-5-native-objects/unreal-suite.log`. The Demo chunk was regenerated.
Ordinary C++ generated authoring, richer codecs and cooked execution remain open.
