# 2026-09-04 — Native extension bindings

Status: in-progress

## Outcome and scope

Make public, non-reflected native APIs usable through the same contract-driven
C#, C++ and visual-node generation as reflected APIs. This is M13.5 capability
work. M13.5 capability breadth precedes the remaining M13.3 Script Class work
and M13.4 external pilot. After a usable native-binding batch, reassess the
cross-family gaps in [the roadmap](../ROADMAP.md#puerts-comparison-and-capability-breadth-target);
do not require every native signature, inheritance case or callback form before
addressing another missing common capability.

Puerts explicitly supports non-UFUNCTION/UPROPERTY members through
[static binding declarations](https://github.com/Tencent/puerts/blob/master/doc/unreal/zhcn/template_binding.md).
Before this module, the generator could call public native members, but contract
selection only admitted reflected input. This work extends the shared generation
path instead of adding facades.

## Decisions and remaining work

### Completed batch: ordinary object arguments

Puerts' [argument holders](https://github.com/Tencent/puerts/blob/master/unreal/Puerts/Source/JsEnv/Public/StaticCall.hpp)
handle object pointers and reference wrappers. Finare now supports that
public path for constructors, static and instance methods, through generated
wrappers and a CoreCLR example rather than only a registry primitive.

- Add explicit pointer/const-pointer and object-reference/const-object-reference
  passing forms. Nullable pointers are opt-in; references are non-null. Preserve
  existing fixed-value/ref-out semantics and overload IDs. Native objects remain
  caller-owned; arguments may be used synchronously but cannot be retained or
  deleted by a conforming binding. Pointer replacement/ref-out, ownership transfer,
  inheritance and callbacks are separate work.
- Carry a distinct typed native-object token in the fixed-size value cell. The
  native-object transport advertises argument support through an optional tail
  capability while preserving its v1 prefix. Old transports keep fixed-value
  support; generated object arguments fail explicitly when unsupported.
- Validate managed scope identity and disposal, then native type/scope/borrowed
  owner liveness before entering the native method. Null and malformed cells,
  stale generations and cross-World handles must not cause native side effects.
- Extend generated C#/C++/node metadata, a composed public native example and
  focused managed/native tests, including actual reload/World cleanup. Generate
  checked-in bindings normally; leave the protected legacy thunk header alone.

### Existing foundation

- Add an optional, independently versioned native-extension declaration to the
  existing API contract. Existing contracts keep their canonical representation.
  A UObject owner must match its exported C++ identity and header; an extension
  cannot replace a reflected member. V0.2 also admits explicitly declared
  `/Native/Module.Type` ordinary C++ owners without claiming reflection metadata.
- Preserve exact overload identity, const methods and value/const-reference/
  mutable-reference parameter passing. Generate exact C++ member pointers.
- Native-only members must never fall back to reflection. Record their origin
  in the shared model and reject unsupported generated routes before publication.
- First implementation covers instance/static methods, overloads, fields,
  values/containers and ref/out on existing UObject identities. Demonstrate
  generated C# calls against real native members lacking reflection metadata.
  Implemented through the normal API contract; native-only origin survives
  proposal, generation, runtime generation views, coverage and node metadata.
- Ordinary C++ construction and owned/borrowed handles now have a distinct
  ownership contract and generated calls. Inheritance, richer codecs, converters
  and callbacks remain follow-ups; do not close the full native-extension roadmap
  item after the generated fixed-value batch.

### Ordinary C++ runtime checkpoint

- Implemented `FFinareNativeObjectRegistry`, owned by the World bridge
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
- The initial native-object lane carried up to 16 fixed value inputs, one fixed
  value result and a separate optional object handle. It deliberately rejects
  arena-backed values and UObject cells. Rich value/container/ref/out codecs and
  native-object argument contracts were not part of that checkpoint. The current
  batch adds typed object inputs; rich value/container/ref/out codecs remain open.
- This runtime checkpoint is now consumed by the generated bindings below.

### Generated ordinary C++ bindings

- V0.2 declares ordinary native types, constructor operations and explicit
  owned/borrowed returns. Legacy v0.1 UObject extensions still round-trip.
  Constructor signatures must be distinct in C#; method overloads use exact
  native function/member pointers. Constructors use normal C++ overload resolution.
- One description generates C# constructors/factories, field accessors and
  methods, fixed-value native thunks, automatic library registration and visual
  metadata. Native type dependencies expose wrapper shells, not extra members.
  Contract policy still rejects unsupported routes before artifact publication.
- Generated `NativeCounter` and `NativeCounterChild` replace manual registration
  in the managed lifecycle example. They exercise overloads, static/void calls,
  vector const references, fields, independent owned clones and borrowed children.
  Duplicate owned returns fail without deleting or invalidating the original.
- The semantic binder accepts generated native wrappers and the bridge context
  as Visual call-boundary values. Tests project construction, calls, fields and
  disposal; the Demo chunk emits. They are not UObject/state handles. Interactive
  palette insertion still has existing receiver/shape limits and is not claimed.
- Module registration serves old/new Worlds, rejects collisions and retires
  its objects/aliases before code unload. Arbitrary unload from within an active
  invocation and cross-module inheritance are not supported.
- **Remaining:** richer value/container/ref/out routes,
  defaults, inheritance, callbacks/converters and cooked execution. This is a
  usable generated-call batch, not completion of the whole native-extension family.
  Binary payload interop has its own completed bounded batch. Follow the current
  roadmap for the next cross-family gap rather than extending every native signature.

## Relevant verification

The object-argument batch passes 57 focused managed ownership/extension tests
and 39 affected generation/contract/ABI tests in
`artifacts/m13-5-native-arguments/managed/`. The UE 5.8.1 Editor build and Demo
chunk emission succeed. All six selected CMD tests pass in
`artifacts/m13-5-native-arguments/native-arguments.log`: five `NativeObjects`
tests plus `NativeTransport.Abi`. These prove generated pointer/reference
calls, exact alias behavior, null policy, failed-call non-mutation, isolated
UObject/native-object tags and actual CoreCLR reload/rollback/World cleanup.
They do not establish cooked execution, physical palette interaction or a new
Puerts performance result. Older results below retain their original scopes.

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
The generated-object batch subsequently passes 744 managed tests and 35 CMD
tests in `artifacts/m13-5-native-generated/`. A final duplicate-owned-return fix
has its own focused rerun rather than relabeling that full-suite evidence.
That rerun passes 23 managed tests and four native-object CMD tests, including
the invalid owned factory and subsequent access to the surviving parent/child.
Results: `managed/native-objects-final.trx` and `native-objects-final.log` under
the same artifact directory. The affected UE rebuild, final Demo chunk emission
and deterministic generation (nine outputs) succeed.
Richer codecs, interactive palette acceptance and cooked execution remain open.
