#if WITH_DEV_AUTOMATION_TESTS

#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "Misc/AutomationTest.h"
#include "Misc/ScopeExit.h"
#include "FinareInteropFixture.h"
#include "FinareNativeTransport.h"
#include "FinareObjectRegistry.h"
#include "FinareInvocationBridge.h"

namespace UE::Finare::NativeCollectionTests
{
struct FCapturedResult
{
    bool bCalled = false;
    bool bSuccess = false;
    EFinareValueKind Kind = EFinareValueKind::Void;
    TArray<uint8> Payload;
};

void Capture(
    void* UserData,
    uint8 bSuccess,
    uint8 ValueKind,
    uint8,
    int32,
    const uint8* StringData,
    int32 StringLength,
    uint8,
    uint8,
    const uint8*,
    int32)
{
    FCapturedResult& Result = *static_cast<FCapturedResult*>(UserData);
    Result.bCalled = true;
    Result.bSuccess = bSuccess != 0;
    Result.Kind = static_cast<EFinareValueKind>(ValueKind);
    Result.Payload.Reset();
    if (StringData != nullptr && StringLength > 0)
    {
        Result.Payload.Append(StringData, StringLength);
    }
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareNativeCollectionPropertyPathTest,
    "Finare.Unreal.NativeTransport.CollectionPropertyPath",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeCollectionPropertyPathTest::RunTest(const FString& Parameters)
{
    using namespace UE::Finare::NativeCollectionTests;

    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Collection property-path World exists"), World);
    if (World == nullptr)
    {
        return false;
    }
    World->AddToRoot();
    ON_SCOPE_EXIT
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
    };

    FFinareObjectRegistry Registry(World);
    FFinareInvocationBridge Bridge(Registry);
    FFinareNativeBridgeContext* Context =
        new FFinareNativeBridgeContext(Bridge);
    ON_SCOPE_EXIT
    {
        Context->Release();
    };
    const FFinareNativeTransportV1* Transport =
        Finare_GetNativeTransportV1();
    UFinareInteropFixture* Fixture = NewObject<UFinareInteropFixture>(World);
    AActor* Actor = World->SpawnActor<AActor>();
    FFinareObjectHandleResult HandleResult;
    const FFinareObjectHandle FixtureHandle =
        Registry.RegisterObject(Fixture, HandleResult);
    TestTrue(TEXT("Collection fixture is registered"), HandleResult.IsSuccess());
    TestNotNull(TEXT("Collection object element exists"), Actor);
    if (Fixture == nullptr || Actor == nullptr || FixtureHandle.IsNull())
    {
        return false;
    }

    auto ReadPath = [&](const TCHAR* Path, FCapturedResult& OutCaptured)
    {
        const FTCHARToUTF8 PathUtf8(Path);
        OutCaptured = {};
        Transport->ReadProperty(
            Context,
            FixtureHandle.Value,
            reinterpret_cast<const uint8*>(PathUtf8.Get()),
            PathUtf8.Length(),
            &Capture,
            &OutCaptured);
        return OutCaptured.bCalled && OutCaptured.bSuccess;
    };
    auto WritePayload = [&](const TCHAR* Path, uint8 Kind, TArray<uint8>& Payload,
                            FCapturedResult& OutCaptured)
    {
        const FTCHARToUTF8 PathUtf8(Path);
        FFinareNativeValueV1 Native;
        Native.Kind = Kind;
        Native.StringData = Payload.GetData();
        Native.StringLength = Payload.Num();
        OutCaptured = {};
        Transport->WriteProperty(
            Context,
            FixtureHandle.Value,
            reinterpret_cast<const uint8*>(PathUtf8.Get()),
            PathUtf8.Length(),
            &Native,
            &Capture,
            &OutCaptured);
        return OutCaptured.bCalled && OutCaptured.bSuccess;
    };

    constexpr TCHAR ActorArrayPath[] =
        TEXT("/Script/FinareRuntime.FinareInteropFixture:ActorArrayValue");
    constexpr TCHAR ActorClassSetPath[] =
        TEXT("/Script/FinareRuntime.FinareInteropFixture:ActorClassSetValue");
    constexpr TCHAR IntMapPath[] =
        TEXT("/Script/FinareRuntime.FinareInteropFixture:IntMapValue");
    constexpr TCHAR SoftObjectMapPath[] =
        TEXT("/Script/FinareRuntime.FinareInteropFixture:SoftObjectMapValue");

    Fixture->ActorArrayValue = {Actor};
    FCapturedResult ArrayCaptured;
    TestTrue(TEXT("Array property path reads through legacy transport"),
        ReadPath(ActorArrayPath, ArrayCaptured));
    TestTrue(TEXT("Array result uses the owned V3 payload lane"),
        ArrayCaptured.Kind == EFinareValueKind::Array &&
            ArrayCaptured.Payload.Num() > sizeof(FFinareNativeValueV3));
    Fixture->ActorArrayValue.Reset();
    FCapturedResult WriteCaptured;
    TestTrue(TEXT("Array property path writes the packed V3 payload"),
        WritePayload(ActorArrayPath, static_cast<uint8>(EFinareValueKind::Array),
            ArrayCaptured.Payload, WriteCaptured));
    TestTrue(TEXT("Array write restores the actor"),
        Fixture->ActorArrayValue.Num() == 1 && Fixture->ActorArrayValue[0] == Actor);

    Fixture->ActorClassSetValue.Reset();
    Fixture->ActorClassSetValue.Add(AActor::StaticClass());
    FCapturedResult SetCaptured;
    TestTrue(TEXT("Set property path reads through legacy transport"),
        ReadPath(ActorClassSetPath, SetCaptured));
    TestTrue(TEXT("Set result carries its V3 root and arena"),
        SetCaptured.Kind == EFinareValueKind::Set &&
            SetCaptured.Payload.Num() > sizeof(FFinareNativeValueV3));
    Fixture->ActorClassSetValue.Reset();
    TestTrue(TEXT("Set property path writes the packed V3 payload"),
        WritePayload(ActorClassSetPath, static_cast<uint8>(EFinareValueKind::Set),
            SetCaptured.Payload, WriteCaptured));
    TestTrue(TEXT("Set write restores the class identity"),
        Fixture->ActorClassSetValue.Num() == 1 &&
            Fixture->ActorClassSetValue.Contains(AActor::StaticClass()));

    Fixture->IntMapValue.Reset();
    Fixture->IntMapValue.Add(1, 11);
    Fixture->IntMapValue.Add(2, 22);
    Fixture->IntMapValue.Add(3, 33);
    Fixture->IntMapValue.Remove(2);
    FCapturedResult MapCaptured;
    TestTrue(TEXT("Map property path reads through legacy transport"),
        ReadPath(IntMapPath, MapCaptured));
    TestTrue(TEXT("Map result carries its V3 root and arena"),
        MapCaptured.Kind == EFinareValueKind::Map &&
            MapCaptured.Payload.Num() > sizeof(FFinareNativeValueV3) &&
            Fixture->IntMapValue.Num() == 2 && Fixture->IntMapValue.Contains(1) &&
            Fixture->IntMapValue.Contains(3));
    Fixture->IntMapValue.Reset();
    TestTrue(TEXT("Sparse map property path writes the packed V3 payload"),
        WritePayload(IntMapPath, static_cast<uint8>(EFinareValueKind::Map),
            MapCaptured.Payload, WriteCaptured));
    TestTrue(TEXT("Sparse map write restores both key/value pairs"),
        Fixture->IntMapValue.Num() == 2 &&
            Fixture->IntMapValue.Contains(1) && Fixture->IntMapValue[1] == 11 &&
            Fixture->IntMapValue.Contains(3) && Fixture->IntMapValue[3] == 33);

    Fixture->SoftObjectMapValue.Reset();
    Fixture->SoftObjectMapValue.Add(
        FName(TEXT("Asset")),
        TSoftObjectPtr<UObject>(FSoftObjectPath(TEXT("/Game/Missing/Asset.Asset"))));
    FCapturedResult SoftMapCaptured;
    TestTrue(TEXT("Soft leaf map reads as a V3 string cell inside its container"),
        ReadPath(SoftObjectMapPath, SoftMapCaptured));
    Fixture->SoftObjectMapValue.Reset();
    TestTrue(TEXT("Soft leaf map writes through the typed property path"),
        WritePayload(SoftObjectMapPath, static_cast<uint8>(EFinareValueKind::Map),
            SoftMapCaptured.Payload, WriteCaptured));
    TestTrue(TEXT("Soft leaf map preserves its unloaded path"),
        Fixture->SoftObjectMapValue.Num() == 1 &&
            Fixture->SoftObjectMapValue[FName(TEXT("Asset"))].ToSoftObjectPath().ToString() ==
                TEXT("/Game/Missing/Asset.Asset"));

    auto TryBadMapPayload = [&](const TCHAR* Label,
                                TFunctionRef<void(FFinareNativeValueV3&)> MutateRoot,
                                bool bMutateLateCell = false)
    {
        TArray<uint8> BadPayload = MapCaptured.Payload;
        FFinareNativeValueV3 Root;
        FMemory::Memcpy(&Root, BadPayload.GetData(), sizeof(Root));
        MutateRoot(Root);
        FMemory::Memcpy(BadPayload.GetData(), &Root, sizeof(Root));
        if (bMutateLateCell)
        {
            FFinareNativeValueV3 LateRoot;
            FMemory::Memcpy(&LateRoot, BadPayload.GetData(), sizeof(LateRoot));
            const uint64 CellCount = LateRoot.Payload2 *
                (LateRoot.Kind == EFinareNativeValueKindV3::Map ? 2ull : 1ull);
            const uint64 LastCellOffset = sizeof(FFinareNativeValueV3) +
                LateRoot.Payload0 + (CellCount - 1) * sizeof(FFinareNativeValueV3);
            FFinareNativeValueV3 LateCell;
            FMemory::Memcpy(
                &LateCell,
                BadPayload.GetData() + LastCellOffset,
                sizeof(LateCell));
            LateCell.Kind = EFinareNativeValueKindV3::String;
            LateCell.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
            LateCell.Payload0 = MAX_uint64;
            LateCell.Payload1 = 1;
            FMemory::Memcpy(
                BadPayload.GetData() + LastCellOffset,
                &LateCell,
                sizeof(LateCell));
        }
        Fixture->IntMapValue.Reset();
        Fixture->IntMapValue.Add(31, 37);
        FCapturedResult BadCaptured;
        TestFalse(Label,
            WritePayload(IntMapPath, static_cast<uint8>(EFinareValueKind::Map),
                BadPayload, BadCaptured));
        TestTrue(TEXT("Rejected collection payload preserves the original map"),
            Fixture->IntMapValue.Num() == 1 && Fixture->IntMapValue.Contains(31) &&
                Fixture->IntMapValue[31] == 37);
    };

    TryBadMapPayload(TEXT("Wrong collection root kind is rejected"),
        [](FFinareNativeValueV3& Root)
        {
            Root.Kind = EFinareNativeValueKindV3::Array;
        });
    TryBadMapPayload(TEXT("Wrong collection type identity is rejected"),
        [](FFinareNativeValueV3& Root)
        {
            Root.TypeId = 0;
        });
    TryBadMapPayload(TEXT("Wrong nonzero collection type identity is rejected"),
        [](FFinareNativeValueV3& Root)
        {
            Root.TypeId ^= 1ull;
        });
    {
        TArray<uint8> Truncated = MapCaptured.Payload;
        Truncated.SetNum(Truncated.Num() - 1);
        Fixture->IntMapValue.Reset();
        Fixture->IntMapValue.Add(41, 43);
        FCapturedResult BadCaptured;
        TestFalse(TEXT("Truncated collection arena is rejected"),
            WritePayload(IntMapPath, static_cast<uint8>(EFinareValueKind::Map),
                Truncated, BadCaptured));
        TestTrue(TEXT("Truncated arena leaves the map unchanged"),
            Fixture->IntMapValue.Num() == 1 && Fixture->IntMapValue.Contains(41) &&
                Fixture->IntMapValue[41] == 43);
    }
    TryBadMapPayload(TEXT("Out-of-range collection descriptor offset is rejected"),
        [](FFinareNativeValueV3& Root)
        {
            Root.Payload0 = MAX_uint32;
        });
    TryBadMapPayload(TEXT("An invalid late collection leaf is rejected"),
        [](FFinareNativeValueV3&)
        {
        }, true);
    {
        TArray<uint8> DuplicatePayload = MapCaptured.Payload;
        FFinareNativeValueV3 Root;
        FMemory::Memcpy(&Root, DuplicatePayload.GetData(), sizeof(Root));
        const uint64 CellOffset = sizeof(FFinareNativeValueV3) + Root.Payload0;
        FFinareNativeValueV3 FirstKey;
        FFinareNativeValueV3 DuplicateKey;
        FMemory::Memcpy(&FirstKey, DuplicatePayload.GetData() + CellOffset, sizeof(FirstKey));
        FMemory::Memcpy(
            &DuplicateKey,
            DuplicatePayload.GetData() + CellOffset + sizeof(FFinareNativeValueV3) * 2,
            sizeof(DuplicateKey));
        DuplicateKey.Payload0 = FirstKey.Payload0;
        FMemory::Memcpy(
            DuplicatePayload.GetData() + CellOffset + sizeof(FFinareNativeValueV3) * 2,
            &DuplicateKey,
            sizeof(DuplicateKey));
        Fixture->IntMapValue.Reset();
        Fixture->IntMapValue.Add(71, 73);
        FCapturedResult BadCaptured;
        TestFalse(TEXT("Duplicate native map key is rejected"),
            WritePayload(IntMapPath, static_cast<uint8>(EFinareValueKind::Map),
                DuplicatePayload, BadCaptured));
        TestTrue(TEXT("Duplicate map key preserves the destination"),
            Fixture->IntMapValue.Num() == 1 && Fixture->IntMapValue.Contains(71) &&
                Fixture->IntMapValue[71] == 73);
    }
    {
        TArray<uint8> NestedPayload = MapCaptured.Payload;
        FFinareNativeValueV3 Root;
        FMemory::Memcpy(&Root, NestedPayload.GetData(), sizeof(Root));
        FFinareNativeValueV3 NestedCell;
        FMemory::Memcpy(
            &NestedCell,
            NestedPayload.GetData() + sizeof(FFinareNativeValueV3) + Root.Payload0,
            sizeof(NestedCell));
        NestedCell.Kind = EFinareNativeValueKindV3::Map;
        NestedCell.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
        NestedCell.TypeId = 1;
        NestedCell.Payload0 = 0;
        NestedCell.Payload1 = 0;
        NestedCell.Payload2 = 0;
        NestedCell.Payload3 = sizeof(FFinareNativeValueV3);
        FMemory::Memcpy(
            NestedPayload.GetData() + sizeof(FFinareNativeValueV3) + Root.Payload0,
            &NestedCell,
            sizeof(NestedCell));
        Fixture->IntMapValue.Reset();
        Fixture->IntMapValue.Add(51, 53);
        FCapturedResult BadCaptured;
        TestFalse(TEXT("Nested collection cell is rejected before generic decode"),
            WritePayload(IntMapPath, static_cast<uint8>(EFinareValueKind::Map),
                NestedPayload, BadCaptured));
        TestTrue(TEXT("Nested collection rejection preserves the original map"),
            Fixture->IntMapValue.Num() == 1 && Fixture->IntMapValue.Contains(51) &&
                Fixture->IntMapValue[51] == 53);
    }

    return true;
}

#endif
