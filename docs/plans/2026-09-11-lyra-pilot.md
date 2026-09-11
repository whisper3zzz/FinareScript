# 2026-09-11 — Lyra SDK adoption and health HUD pilot

Status: native onboarding passed; actual FirstPlay exposed missing authoring artifacts

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
Evidence: `artifacts/finare-usability/lyra-first-play.json` and
`lyra-first-play-engine.log`.

The selected health/HUD APIs and W_Healthbar Blueprint also export through the
installed SDK into `artifacts/finare-usability/lyra-health-pilot.reflection.json`.
Generated call/event coverage, actual health/HUD behavior, reload,
second-checkout reproduction and cooked execution remain to be verified.
