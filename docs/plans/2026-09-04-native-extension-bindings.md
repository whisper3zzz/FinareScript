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
Ordinary C++ construction/ownership remains the next implementation, and the
new sample has not yet been proven in a cooked package.
