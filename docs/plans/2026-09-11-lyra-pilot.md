# 2026-09-11 — Lyra SDK adoption and health HUD pilot

Status: planned

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

SDK installation, generated API coverage, actual health/HUD behavior, reload,
second-checkout reproduction and cooked execution remain to be verified.
