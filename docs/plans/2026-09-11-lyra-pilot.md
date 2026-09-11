# 2026-09-11 — Lyra SDK adoption and health HUD pilot

Status: installed normal ActorComponent workflow and original health/HUD passed; health logic migration in progress

## Outcome and scope

The user selected `D:\Project\LyraStarterGame` for the external-project pilot.
Work uses the independent copy `D:\Project\FinareLyraPilot-20260911`; the original
project is the reference and is not an implementation checkout. Source, Config,
Content and plugin content were copied; generated caches and native binaries
were excluded. The available engine is UE 5.8.1, matching Finare's declared
Win64 target.

The selected first slice is health changes driving a HUD through Lyra's actual
GAS-backed health component. Use the shipped SDK's public generated calls,
events, UI and asset-loading workflow. Preserve Lyra's ability-system ownership
and other gameplay. The pilot must not substitute a hand-written native facade
or a test-only health producer for the production event route.

## Decisions and remaining work

Finish the C-4 class-editing integration before accepting this pilot. Preparation
can identify environment issues while that implementation runs. Install the
current Finare SDK into the copy, discover the selected Lyra/engine APIs, and
implement the slice using generated bindings and ordinary Script Classes.

`ULyraHealthComponent` exposes `FindHealthComponent`, health getters and dynamic
health/death events. Its attribute source is `ULyraHealthSet` through
`ULyraAbilitySystemComponent`. Existing healing GameplayEffects and HUD content
provide production inputs/reference behavior. Select the concrete experience
and widget from the actual project during integration rather than inventing an
equivalence result from source inspection.

Read-only asset export identifies the reference as
`/Game/UI/Hud/W_Healthbar`, embedded as `BW_Healthbar` in
`/ShooterCore/UserInterface/W_ShooterHUDLayout`. The health widget derives from
`UUserWidget`; the layout derives from `ULyraHUDLayout`. Its Blueprint binds
`AController.OnPossessedPawnChanged` and `ULyraHealthComponent.OnHealthChanged`,
resolves the health component with `FindHealthComponent`, and consumes
`GetHealthNormalized`. Presentation updates material scalar parameters and
`UCommonNumericTextBlock` values/interpolation, with existing elimination
animation behavior. The pilot must preserve pawn-replacement subscription
cleanup as well as damage/heal updates; a standalone progress bar would not
establish equivalent behavior for this widget.

Compare the original and migrated health/UI observations, including a real
damage/heal transition, reload and teardown. Exercise asset/Soft Reference
loading and failure cleanup in the same installed workflow. Reproduce the
documented setup on a second independent checkout and verify the Win64 cooked
result uses its own staged chunk and host. Lyra does not provide a Puerts
baseline; this pilot must not advertise a matched Puerts performance result.

## Relevant verification

The ordinary ActorComponent factory created `/Game/FinarePilot/HealthHud` and
its Script Asset, source and deployment entry in the independent copy. Its
ClassId is `us_22d5d092487a142d439ae8814ca123b5`. The selected Lyra/UMG API
contract generates 17 accepted members, with one direct thunk and 16 existing
reflection routes, and no rejected members. Full installed onboarding passed
in `Saved/Finare/Onboarding/20260911-140218/onboarding-report.json`. This still
uses the factory template; the health subscription logic has not been migrated.

Fresh ordinary loading of the generated component then crashed on the async
loading thread. `FFinareBlueprintCompilerContext::FinishCompilingClass` calls
the game-thread-only `BindManagedFunctions` during UE skeleton compilation;
the runtime load hooks already defer that work to the game thread. Evidence:
`artifacts/finare-usability/lyra-health-class-play-engine-2.log`, Editor exit 3.
Revision `3a13fc1` defers these compiler binding hooks to the existing game-thread
load/instance path, retaining the runtime assertions and asynchronous loading.

Normal factory/configuration edits also expose an SDK upgrade conflict:
adoption currently treats a customized deployment/configuration template as a
conflict even when the incoming template has not changed. Preserve project
customizations when the SDK has no change to that template, while retaining
conflicts for two-sided template changes and modified product payload files.
The preview is `artifacts/finare-usability/lyra-upgrade-after-class-preview.log`.
Revision `06665b5` implements that preservation rule using the nearest receipt's
kind and template hash. Twelve adoption tests pass, including two upgrades,
rollback preservation and two-sided template conflicts.

The SDK at `06665b5` installed with one create, 29 updates, 266 unchanged files
and no conflicts; receipt `20260911-063100-3e14c46a`. The customized health API
contract and both deployment entries survived. Full installed onboarding passed
in `Saved/Finare/Onboarding/20260911-143100/onboarding-report.json`. The contract
now also selects the original `OnPosessedPawnChanged` function for reuse of its
presentation initialization after subscription management moves to C#.

Actual fresh class loading, ordinary Actor component attachment and PIE now
pass in `lyra-health-class-play-engine-4.log` and `lyra-health-class-play.json`
under the artifact directory, with Editor exit 0. The generated `StartValue`
property becomes 2 on BeginPlay; native `AddOne(41)` returns 42; the Blueprint
event adds 5 to produce 7; generation 1 reloads to 2 with state 8 and retains
native method dispatch. Explicit stop and World destruction succeed. The
earlier `-3.log` harness read the hosted ScriptState bag rather than the generated
UE property; that assertion was corrected to inspect the actual production
state owner. No product state-sync code was changed for that harness correction.
This establishes the normal class carrier, not migrated health subscriptions.

The unmodified-copy `LyraEditor Win64 Development` build passed against UE 5.8.1
(561 actions, 144.88 seconds). Its output is
`artifacts/finare-usability/lyra-original-editor-build.log`; this is environment
preparation, not Finare adoption or gameplay acceptance.

The original health widget, shooter HUD layout and elimination experience load
and export successfully in the copy through a read-only Python commandlet.
`artifacts/finare-usability/lyra-health-hud-export.json` and the adjacent T3D
exports identify their actual graph and asset dependencies. No Lyra gameplay,
configuration or asset was saved by this inspection.

The SDK built from committed revision `f3ebe07` passed packaging and installed
through the packaged `adopt` command in the independent Lyra copy (294 creates,
one update, no conflicts). The adoption receipt is
`Saved/Finare/Adoption/transactions/20260911-033829-48d8bf89/transaction.json`.
The original Lyra project remains untouched. This SDK precedes the uncommitted
native-publication changes and will need updating before their acceptance.

Initial onboarding stopped at UHT: LyraEditor disallows raw reflected UObject
pointer members, exposing `FinareInteropFixture.ActorArrayValue` as incompatible.
Keep Lyra's pointer policy and fix the plugin. Source inspection also identified
that adoption picks the alphabetically first non-Editor target (`LyraClient`)
instead of the declared Game target (`LyraGame`). The first-script template
additionally constructs a Pawn wrapper from its default FinareActor target;
its `GetController` call requires correction and actual first-Play validation.
Evidence is in `artifacts/finare-usability/lyra-adopt-f3ebe07.log` and
`lyra-onboarding-f3ebe07.log`; native onboarding has not passed.

Revision `a3a1ab2` fixes the reflected Actor array for Lyra's TObjectPtr policy,
selects targets by their declared TargetType, and makes the starter script treat
its Instigator Pawn as optional. The installed SDK upgrade applied 30 updates
with no conflicts. Full onboarding passed in
`Saved/Finare/Onboarding/20260911-121007/onboarding-report.json`: Editor and Game
builds, reflection, binding generation, managed chunk, deployment and three
project-setup Automation tests. The source fix also passed the three native
object-reference collection tests in the repository host.

Actual FirstPlay then failed before starting PIE: the deployed Script Asset
expects its declaration under `Binaries/Managed/Scripts`, while the onboarding
template stages the chunk and declaration in `Binaries/Managed`. The template
also references an authoring Surface which `emit` does not yet produce or stage.
The receipt above therefore proves native onboarding, not a runnable starter
script. Fix the product templates and emission/staging path, then repeat the
installed workflow; do not repair the pilot by copying missing outputs manually.
Evidence: `artifacts/finare-usability/lyra-first-play-engine.log`.

Revisions `87dd614` and `8fc7654` fix complete FirstPlay artifact emission and
SDK ownership across sequential upgrades. The SDK at `f18233a` installed with
23 updates, 272 unchanged files and no conflicts; full onboarding passed in
`Saved/Finare/Onboarding/20260911-130347/onboarding-report.json`. An intentional
missing-Surface ValidateOnly check correctly rejected the absent output with
USONB1201; its failed report is negative coverage, and the output was restored.

Actual Play exposed a separate gate failure: PowerShell 7 onboarding and the
Editor's Windows PowerShell 5.1 child produced different native-source hashes
for the same 659 files because their culture-based punctuation ordering differs.
Revision `a594aa9` uses explicit ordinal ordering for BuildGraph paths, records,
keys and dependencies. Both shell tests and the cross-shell fixture passed;
the installed SDK must still be upgraded and onboarded once with the new
fingerprint algorithm before actual FirstPlay can be accepted. The rejected
run is retained as `lyra-first-play-engine-2.log` and
`lyra-first-play-false-restart.json` under the artifact directory.

The original GAS-backed health/HUD behavior passed in actual PIE on
`/ShooterMaps/Maps/L_Expanse`: native health and the original W_Healthbar numeric
target both followed 100 -> 80 -> 90 through Lyra's DamageSelf/HealSelf effects.
The test waits for the actual spawn damage-immunity tag to clear before damage;
it does not change health or bypass immunity. PIE teardown removed the World.
Evidence: `artifacts/finare-usability/lyra-health-baseline.json` and
`lyra-health-baseline-engine-9.log` (Editor exit 0). Finare was disabled for
this original-behavior reference, so this is not migrated gameplay acceptance.
The independent copy has an Editor-only validation plugin to request PIE in a
hidden viewport; it contains no gameplay calls or health facade.

The extended original reference also passed actual death/respawn through
DamageSelfDestruct. The original health Widget followed the replacement pawn
without being recreated; native health and HUD followed 100 -> 80 -> 90 ->
respawn 100 -> 80. The World was destroyed at teardown. Evidence:
`lyra-health-respawn.json` and `lyra-health-respawn-engine.log` in the same
artifact directory (Editor exit 0).

Revision `69ab6aa` fixes the starter's hosted-state consumption through generated
FinareComponent Get/SetScriptStateInt32 calls. It uses UnrealActorScript lifecycle
hooks, but does not claim generated-class automatic property synchronization for
an ordinary hosted Script Asset. Its compiled template test executes state
1 -> 2 -> 3 across recreated managed instances. Revision `db18d9e` completes
Blueprint function-name selection through API contracts, preserving overloads,
stable paths/IDs and the existing selector whitespace normalization.

The SDK built at `db18d9e` upgraded the independent copy with 25 updates and no
conflicts; receipt `20260911-055003-385ea15f`. Full onboarding passed in
`Saved/Finare/Onboarding/20260911-135017/onboarding-report.json`. Actual FirstPlay
then passed with Finare enabled: initial native state 1 became 2, generation 1
reloaded to 2 with state 3, explicit stop succeeded, and PIE World teardown
completed. The Editor's PS5 daily gate also passed. Evidence:
`artifacts/finare-usability/lyra-first-play.json` and
`lyra-first-play-engine-4.log` (Editor exit 0). This proves the installed starter
workflow; it does not yet prove migrated Lyra gameplay.

The actual health/HUD reflection input now generates the selected 17 members
through the packaged CLI with zero rejected members, then compiles against the
SDK runtime assemblies with zero warnings/errors. The contract explicitly
selects the needed UserWidget parent API as well as W_Healthbar, Controller and
LyraHealthComponent. This bounds the existing dependency-closure behavior.
One Controller call uses a native thunk; the other 16 members use the existing
reflection route, including three event subscriptions. Generated native includes
only Engine Controller/Pawn headers. Evidence: `lyra-health-selected-contract.json`,
`lyra-health-selected-generation-2.log`, `lyra-health-selected-compile.log` and
`lyra-health-selected-bindings/coverage.json` under the artifact directory.

The selected health/HUD APIs and W_Healthbar Blueprint also export through the
installed SDK into `artifacts/finare-usability/lyra-health-pilot.reflection.json`.
Selected binding generation/compilation and original health/HUD behavior are
verified. Migrated health/HUD behavior, its reload/cleanup, second-checkout
reproduction and cooked execution remain to be verified.
