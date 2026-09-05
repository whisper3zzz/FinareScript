#if WITH_DEV_AUTOMATION_TESTS

#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "Misc/AutomationTest.h"
#include "Misc/ScopeExit.h"
#include "UnifiedScriptInteropFixture.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptObjectRegistry.h"
#include "UnifiedScriptInvocationBridge.h"

namespace UE::UnifiedScript::NativeCollectionTests
{
struct FCapturedResult
{
    bool bCalled = false;
    bool bSuccess = false;
    EUnifiedScriptValueKind Kind = EUnifiedScriptValueKind::Void;
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
    Result.Kind = static_cast<EUnifiedScriptValueKind>(ValueKind);
    Result.Payload.Reset();
    if (StringData != nullptr && StringLength > 0)
    {
        Result.Payload.Append(StringData, StringLength);
    }
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptNativeCollectionPropertyPathTest,
    "UnifiedScript.Unreal.NativeTransport.CollectionPropertyPath",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptNativeCollectionPropertyPathTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::NativeCollectionTests;

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

    FUnifiedScriptObjectRegistry Registry(World);
    FUnifiedScriptInvocationBridge Bridge(Registry);
    FUnifiedScriptNativeBridgeContext* Context =
        new FUnifiedScriptNativeBridgeContext(Bridge);
    ON_SCOPE_EXIT
    {
        Context->Release();
    };
    const FUnifiedScriptNativeTransportV1* Transport =
        UEUnifiedScript_GetNativeTransportV1();
    UUnifiedScriptInteropFixture* Fixture = NewObject<UUnifiedScriptInteropFixture>(World);
    AActor* Actor = World->SpawnActor<AActor>();
    FUnifiedScriptObjectHandleResult HandleResult;
    const FUnifiedScriptObjectHandle FixtureHandle =
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
        FUnifiedScriptNativeValueV1 Native;
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
        TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:ActorArrayValue");
    constexpr TCHAR ActorClassSetPath[] =
        TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:ActorClassSetValue");
    constexpr TCHAR IntMapPath[] =
        TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:IntMapValue");
    constexpr TCHAR SoftObjectMapPath[] =
        TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:SoftObjectMapValue");

    Fixture->ActorArrayValue = {Actor};
    FCapturedResult ArrayCaptured;
    TestTrue(TEXT("Array property path reads through legacy transport"),
        ReadPath(ActorArrayPath, ArrayCaptured));
    TestTrue(TEXT("Array result uses the owned V3 payload lane"),
        ArrayCaptured.Kind == EUnifiedScriptValueKind::Array &&
            ArrayCaptured.Payload.Num() > sizeof(FUnifiedScriptNativeValueV3));
    Fixture->ActorArrayValue.Reset();
    FCapturedResult WriteCaptured;
    TestTrue(TEXT("Array property path writes the packed V3 payload"),
        WritePayload(ActorArrayPath, static_cast<uint8>(EUnifiedScriptValueKind::Array),
            ArrayCaptured.Payload, WriteCaptured));
    TestTrue(TEXT("Array write restores the actor"),
        Fixture->ActorArrayValue.Num() == 1 && Fixture->ActorArrayValue[0] == Actor);

    Fixture->ActorClassSetValue.Reset();
    Fixture->ActorClassSetValue.Add(AActor::StaticClass());
    FCapturedResult SetCaptured;
    TestTrue(TEXT("Set property path reads through legacy transport"),
        ReadPath(ActorClassSetPath, SetCaptured));
    TestTrue(TEXT("Set result carries its V3 root and arena"),
        SetCaptured.Kind == EUnifiedScriptValueKind::Set &&
            SetCaptured.Payload.Num() > sizeof(FUnifiedScriptNativeValueV3));
    Fixture->ActorClassSetValue.Reset();
    TestTrue(TEXT("Set property path writes the packed V3 payload"),
        WritePayload(ActorClassSetPath, static_cast<uint8>(EUnifiedScriptValueKind::Set),
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
        MapCaptured.Kind == EUnifiedScriptValueKind::Map &&
            MapCaptured.Payload.Num() > sizeof(FUnifiedScriptNativeValueV3) &&
            Fixture->IntMapValue.Num() == 2 && Fixture->IntMapValue.Contains(1) &&
            Fixture->IntMapValue.Contains(3));
    Fixture->IntMapValue.Reset();
    TestTrue(TEXT("Sparse map property path writes the packed V3 payload"),
        WritePayload(IntMapPath, static_cast<uint8>(EUnifiedScriptValueKind::Map),
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
        WritePayload(SoftObjectMapPath, static_cast<uint8>(EUnifiedScriptValueKind::Map),
            SoftMapCaptured.Payload, WriteCaptured));
    TestTrue(TEXT("Soft leaf map preserves its unloaded path"),
        Fixture->SoftObjectMapValue.Num() == 1 &&
            Fixture->SoftObjectMapValue[FName(TEXT("Asset"))].ToSoftObjectPath().ToString() ==
                TEXT("/Game/Missing/Asset.Asset"));

    auto TryBadMapPayload = [&](const TCHAR* Label,
                                TFunctionRef<void(FUnifiedScriptNativeValueV3&)> MutateRoot,
                                bool bMutateLateCell = false)
    {
        TArray<uint8> BadPayload = MapCaptured.Payload;
        FUnifiedScriptNativeValueV3 Root;
        FMemory::Memcpy(&Root, BadPayload.GetData(), sizeof(Root));
        MutateRoot(Root);
        FMemory::Memcpy(BadPayload.GetData(), &Root, sizeof(Root));
        if (bMutateLateCell)
        {
            FUnifiedScriptNativeValueV3 LateRoot;
            FMemory::Memcpy(&LateRoot, BadPayload.GetData(), sizeof(LateRoot));
            const uint64 CellCount = LateRoot.Payload2 *
                (LateRoot.Kind == EUnifiedScriptNativeValueKindV3::Map ? 2ull : 1ull);
            const uint64 LastCellOffset = sizeof(FUnifiedScriptNativeValueV3) +
                LateRoot.Payload0 + (CellCount - 1) * sizeof(FUnifiedScriptNativeValueV3);
            FUnifiedScriptNativeValueV3 LateCell;
            FMemory::Memcpy(
                &LateCell,
                BadPayload.GetData() + LastCellOffset,
                sizeof(LateCell));
            LateCell.Kind = EUnifiedScriptNativeValueKindV3::String;
            LateCell.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
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
            WritePayload(IntMapPath, static_cast<uint8>(EUnifiedScriptValueKind::Map),
                BadPayload, BadCaptured));
        TestTrue(TEXT("Rejected collection payload preserves the original map"),
            Fixture->IntMapValue.Num() == 1 && Fixture->IntMapValue.Contains(31) &&
                Fixture->IntMapValue[31] == 37);
    };

    TryBadMapPayload(TEXT("Wrong collection root kind is rejected"),
        [](FUnifiedScriptNativeValueV3& Root)
        {
            Root.Kind = EUnifiedScriptNativeValueKindV3::Array;
        });
    TryBadMapPayload(TEXT("Wrong collection type identity is rejected"),
        [](FUnifiedScriptNativeValueV3& Root)
        {
            Root.TypeId = 0;
        });
    TryBadMapPayload(TEXT("Wrong nonzero collection type identity is rejected"),
        [](FUnifiedScriptNativeValueV3& Root)
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
            WritePayload(IntMapPath, static_cast<uint8>(EUnifiedScriptValueKind::Map),
                Truncated, BadCaptured));
        TestTrue(TEXT("Truncated arena leaves the map unchanged"),
            Fixture->IntMapValue.Num() == 1 && Fixture->IntMapValue.Contains(41) &&
                Fixture->IntMapValue[41] == 43);
    }
    TryBadMapPayload(TEXT("Out-of-range collection descriptor offset is rejected"),
        [](FUnifiedScriptNativeValueV3& Root)
        {
            Root.Payload0 = MAX_uint32;
        });
    TryBadMapPayload(TEXT("An invalid late collection leaf is rejected"),
        [](FUnifiedScriptNativeValueV3&)
        {
        }, true);
    {
        TArray<uint8> DuplicatePayload = MapCaptured.Payload;
        FUnifiedScriptNativeValueV3 Root;
        FMemory::Memcpy(&Root, DuplicatePayload.GetData(), sizeof(Root));
        const uint64 CellOffset = sizeof(FUnifiedScriptNativeValueV3) + Root.Payload0;
        FUnifiedScriptNativeValueV3 FirstKey;
        FUnifiedScriptNativeValueV3 DuplicateKey;
        FMemory::Memcpy(&FirstKey, DuplicatePayload.GetData() + CellOffset, sizeof(FirstKey));
        FMemory::Memcpy(
            &DuplicateKey,
            DuplicatePayload.GetData() + CellOffset + sizeof(FUnifiedScriptNativeValueV3) * 2,
            sizeof(DuplicateKey));
        DuplicateKey.Payload0 = FirstKey.Payload0;
        FMemory::Memcpy(
            DuplicatePayload.GetData() + CellOffset + sizeof(FUnifiedScriptNativeValueV3) * 2,
            &DuplicateKey,
            sizeof(DuplicateKey));
        Fixture->IntMapValue.Reset();
        Fixture->IntMapValue.Add(71, 73);
        FCapturedResult BadCaptured;
        TestFalse(TEXT("Duplicate native map key is rejected"),
            WritePayload(IntMapPath, static_cast<uint8>(EUnifiedScriptValueKind::Map),
                DuplicatePayload, BadCaptured));
        TestTrue(TEXT("Duplicate map key preserves the destination"),
            Fixture->IntMapValue.Num() == 1 && Fixture->IntMapValue.Contains(71) &&
                Fixture->IntMapValue[71] == 73);
    }
    {
        TArray<uint8> NestedPayload = MapCaptured.Payload;
        FUnifiedScriptNativeValueV3 Root;
        FMemory::Memcpy(&Root, NestedPayload.GetData(), sizeof(Root));
        FUnifiedScriptNativeValueV3 NestedCell;
        FMemory::Memcpy(
            &NestedCell,
            NestedPayload.GetData() + sizeof(FUnifiedScriptNativeValueV3) + Root.Payload0,
            sizeof(NestedCell));
        NestedCell.Kind = EUnifiedScriptNativeValueKindV3::Map;
        NestedCell.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        NestedCell.TypeId = 1;
        NestedCell.Payload0 = 0;
        NestedCell.Payload1 = 0;
        NestedCell.Payload2 = 0;
        NestedCell.Payload3 = sizeof(FUnifiedScriptNativeValueV3);
        FMemory::Memcpy(
            NestedPayload.GetData() + sizeof(FUnifiedScriptNativeValueV3) + Root.Payload0,
            &NestedCell,
            sizeof(NestedCell));
        Fixture->IntMapValue.Reset();
        Fixture->IntMapValue.Add(51, 53);
        FCapturedResult BadCaptured;
        TestFalse(TEXT("Nested collection cell is rejected before generic decode"),
            WritePayload(IntMapPath, static_cast<uint8>(EUnifiedScriptValueKind::Map),
                NestedPayload, BadCaptured));
        TestTrue(TEXT("Nested collection rejection preserves the original map"),
            Fixture->IntMapValue.Num() == 1 && Fixture->IntMapValue.Contains(51) &&
                Fixture->IntMapValue[51] == 53);
    }

    return true;
}

#endif
