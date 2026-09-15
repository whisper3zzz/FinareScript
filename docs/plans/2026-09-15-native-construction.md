# 2026-09-15 — Native C# construction

Status: complete (synchronous Editor construction tranche)

## Outcome and scope

C# defines an ordinary UE Character with camera and mesh default subobjects.
The native constructor runs before gameplay, including the CDO. UE owns
attachment, archetype instancing, saved component defaults and registration.
This tranche targets synchronous Editor loading; async loading and cooked
delivery are not accepted by this work.

## Decisions and remaining work

- `OnConstruct(UnrealConstructionContext)` is distinct from `OnStart` and
  `OnBeginPlay`. A short-lived collectible context reuses chunk verification;
  no managed World or gameplay generation is created for a constructor.
- A generated-class `ClassConstructor` trampoline calls the real native parent
  once, then the effective C# hook while `FObjectInitializer` is active.
  C# helper inheritance uses normal `base.OnConstruct`; compiler identity
  copies do not execute a source class against an obsolete native parent.
- Constructor handles accept only the receiver and its owned object tree.
  All managed views expire on return or exception and reject another thread.
- CreateDefaultSubobject and SetupAttachment are thin native constructor
  operations; there is no component composition schema. Existing module
  wrappers provide typed objects and property access.
- Blueprint archetype initialization and old-CDO copying happen after the
  callback. Actual CDO writes are retained for that defaults merge. Source
  fallback values yield to constructor defaults; authored overrides retain
  precedence. These transient values are not an executable construction plan.
- The exact compiled program is retained with the class. A changed program
  containing construction requires stopping and rebuilding its CDO. This is
  conservative: even a gameplay-only edit in that program requires rebuilding.
  Same-program gameplay reload does not reconstruct components.
- Blueprint layout consumes `FBPVariableDescription.DefaultValue`. Merge
  constructor defaults with Kismet's pending default-term map, not the emptied
  declaration field. Declaration-only materialization remains valid before a
  program has been compiled.

## Relevant verification

- Managed construction, async-void rejection, object views, reflected property
  generation, Script Class identity and lifecycle tests: **52 passed**.
- UE Editor build passed. **Six CMD Automation tests passed**: CharacterDefaults,
  constructor registry scope, registry lifecycle/reference transport, scalar
  Blueprint state and collection state materialization.
- CharacterDefaults verifies actual default subobjects, the first CDO's scalar
  and component reference, save/unload/reopen, edited FOV, two independent
  instances, BeginPlay, same-program reload and rejection of active publication
  with a changed constructor identity. Results:
  `artifacts/native-construction/automation.log`.
- The public preparation script passed with a current full-module reflection
  input; the API-library CI entry prepares and runs the new example. The full
  API-library gate and cooked acceptance were not rerun for this Editor tranche.
- The local runner's summary rejected the `Finare.Unreal.ObjectRegistry` prefix
  because it expects exact test names. All three expanded registry tests and
  the other three tests reported success in the engine log; those exact records
  and absence of engine failures were checked separately. The new CI entry and
  example command use an exact name.
