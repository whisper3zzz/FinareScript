#if WITH_DEV_AUTOMATION_TESTS

#include "Generated/FinareBlockBreakerDataPlaneExtension.gen.h"

#include "Misc/AutomationTest.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareGeneratedDataPlaneExtensionRegistryTest,
    "Finare.Unreal.DataPlane.GeneratedExtensionRegistry",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareGeneratedDataPlaneExtensionRegistryTest::RunTest(
    const FString& Parameters)
{
    (void)Parameters;
    using namespace UE::Finare::Extensions::BlockBreaker;

    FFinareDataPlaneExtensionRegistry Registry;
    FString Error;
    TestTrue(
        *FString::Printf(TEXT("Generated extension registers: %s"), *Error),
        Registry.Register(Descriptor, Error));
    TestEqual(TEXT("One extension is registered"), Registry.GetExtensionCount(), 1);
    TestEqual(TEXT("Both generated channels are registered"), Registry.GetChannelCount(), 2);
    TestTrue(
        TEXT("Identical registration is idempotent"),
        Registry.Register(Descriptor, Error));
    FFinareDataPlaneExtensionChannelDescriptor TamperedChannels[] =
    {
        Descriptor.Channels[0],
        Descriptor.Channels[1],
    };
    ++TamperedChannels[0].MaximumEntryCount;
    FFinareDataPlaneExtensionDescriptor TamperedDescriptor = Descriptor;
    TamperedDescriptor.Channels = TamperedChannels;
    TestFalse(
        TEXT("Idempotent registration requires the exact generated channel set"),
        Registry.Register(TamperedDescriptor, Error));

    FBlockBreakerTargetSnapshotV1 Row;
    TestTrue(
        *FString::Printf(TEXT("Native snapshot rows validate: %s"), *Error),
        Registry.ValidateRows(
            Descriptor,
            TargetSnapshots,
            EFinareDataPlaneExtensionChannelKind::Snapshot,
            EFinareGameplayDataPhaseV4::Idle,
            sizeof(Row),
            &Row,
            1,
            Error));
    TestFalse(
        TEXT("A snapshot descriptor cannot be used as an event"),
        Registry.ValidateRows(
            Descriptor,
            TargetSnapshots,
            EFinareDataPlaneExtensionChannelKind::Event,
            EFinareGameplayDataPhaseV4::Idle,
            sizeof(Row),
            &Row,
            1,
            Error));
    TestFalse(
        TEXT("Native publication cannot mutate a managed borrowed-view phase"),
        Registry.ValidateRows(
            Descriptor,
            TargetSnapshots,
            EFinareDataPlaneExtensionChannelKind::Snapshot,
            EFinareGameplayDataPhaseV4::ManagedTick,
            sizeof(Row),
            &Row,
            1,
            Error));
    TestFalse(
        TEXT("Per-channel entry bounds are enforced before copying"),
        Registry.ValidateRows(
            Descriptor,
            TargetSnapshots,
            EFinareDataPlaneExtensionChannelKind::Snapshot,
            EFinareGameplayDataPhaseV4::Idle,
            sizeof(Row),
            &Row,
            TargetSnapshots.MaximumEntryCount + 1,
            Error));

    FFinareDataPlaneExtensionDescriptor Drifted = Descriptor;
    ++Drifted.SchemaFingerprint;
    TestFalse(
        TEXT("An existing extension key rejects fingerprint drift"),
        Registry.Register(Drifted, Error));
    TestFalse(
        TEXT("Unregister requires the exact generated fingerprint"),
        Registry.Unregister(Descriptor.Key, Drifted.SchemaFingerprint));
    TestTrue(
        TEXT("Exact unregister succeeds"),
        Registry.Unregister(Descriptor.Key, Descriptor.SchemaFingerprint));
    TestEqual(TEXT("Extension lifecycle clears channels"), Registry.GetChannelCount(), 0);
    TestEqual(TEXT("Extension lifecycle clears identity"), Registry.GetExtensionCount(), 0);
    return true;
}

#endif
