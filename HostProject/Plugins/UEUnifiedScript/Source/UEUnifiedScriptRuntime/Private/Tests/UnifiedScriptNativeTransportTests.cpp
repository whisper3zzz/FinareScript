#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptNativeTransport.h"

#include "Containers/StringConv.h"
#include "Components/BoxComponent.h"
#include "Engine/World.h"
#include "Engine/StaticMeshActor.h"
#include "Misc/AutomationTest.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptInteropFixture.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/UObjectGlobals.h"

namespace UE::UnifiedScript::NativeTransport::Tests
{
struct FCapturedResult
{
    bool bCalled = false;
    bool bSuccess = false;
    EUnifiedScriptValueKind ValueKind = EUnifiedScriptValueKind::Void;
    bool BooleanValue = false;
    int32 Int32Value = 0;
    FString StringValue;
    EUnifiedScriptInvocationError Error = EUnifiedScriptInvocationError::None;
    EUnifiedScriptObjectHandleError HandleError = EUnifiedScriptObjectHandleError::None;
    FString Message;
};

struct FCapturedEvent
{
    int32 CallCount = 0;
    uint64 SubscriptionId = 0;
    uint64 Source = 0;
    uint64 EventId = 0;
    int32 NewValue = 0;
    uint64 ContextHandle = 0;
};

void CaptureEvent(void* UserData, const FUnifiedScriptNativeEventFrameV4* Frame)
{
    FCapturedEvent& Captured = *static_cast<FCapturedEvent*>(UserData);
    if (Frame == nullptr || Frame->AbiVersion != 4 || Frame->FrameSize != sizeof(*Frame) ||
        Frame->ArgumentCount != 2 || Frame->Arguments == nullptr)
    {
        return;
    }
    ++Captured.CallCount;
    Captured.SubscriptionId = Frame->SubscriptionId;
    Captured.Source = Frame->Source;
    Captured.EventId = Frame->EventId;
    Captured.NewValue = static_cast<int32>(Frame->Arguments[0].Payload0);
    Captured.ContextHandle = Frame->Arguments[1].Payload0;
}

FString Decode(const uint8* Data, int32 Length)
{
    if (Data == nullptr || Length == 0)
    {
        return {};
    }
    const FUTF8ToTCHAR Converted(reinterpret_cast<const UTF8CHAR*>(Data), Length);
    return FString(Converted.Length(), Converted.Get());
}

void Capture(
    void* UserData,
    uint8 bSuccess,
    uint8 ValueKind,
    uint8 BooleanValue,
    int32 Int32Value,
    const uint8* StringData,
    int32 StringLength,
    uint8 Error,
    uint8 HandleError,
    const uint8* MessageData,
    int32 MessageLength)
{
    FCapturedResult& Result = *static_cast<FCapturedResult*>(UserData);
    Result.bCalled = true;
    Result.bSuccess = bSuccess != 0;
    Result.ValueKind = static_cast<EUnifiedScriptValueKind>(ValueKind);
    Result.BooleanValue = BooleanValue != 0;
    Result.Int32Value = Int32Value;
    Result.StringValue = Decode(StringData, StringLength);
    Result.Error = static_cast<EUnifiedScriptInvocationError>(Error);
    Result.HandleError = static_cast<EUnifiedScriptObjectHandleError>(HandleError);
    Result.Message = Decode(MessageData, MessageLength);
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptNativeTransportAbiTest,
    "UnifiedScript.Unreal.NativeTransport.Abi",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptNativeTransportAbiTest::RunTest(const FString& Parameters)
{
    using namespace UE::UnifiedScript::NativeTransport::Tests;

    const FUnifiedScriptNativeTransportV1* Transport =
        UEUnifiedScript_GetNativeTransportV1();
    TestNotNull(TEXT("Version-one transport table exists"), Transport);
    if (Transport == nullptr)
    {
        return false;
    }
    TestEqual(TEXT("Transport ABI version is stable"), Transport->AbiVersion, 1u);
    TestEqual(
        TEXT("Transport table size is explicit"),
        Transport->TableSize,
        static_cast<uint32>(sizeof(FUnifiedScriptNativeTransportV1)));
    TestEqual(
        TEXT("Transport value size is explicit"),
        Transport->ValueSize,
        static_cast<uint32>(sizeof(FUnifiedScriptNativeValueV1)));
    TestNotNull(TEXT("Read function exists"), Transport->ReadProperty);
    TestNotNull(TEXT("Write function exists"), Transport->WriteProperty);
    TestNotNull(TEXT("Invoke function exists"), Transport->Invoke);
    TestNotNull(TEXT("Context retain function exists"), Transport->RetainContext);
    TestNotNull(TEXT("Context release function exists"), Transport->ReleaseContext);

    const FUnifiedScriptNativeTransportV2* FastTransport =
        UEUnifiedScript_GetNativeTransportV2();
    TestNotNull(TEXT("Version-two transport table exists"), FastTransport);
    if (FastTransport == nullptr)
    {
        return false;
    }
    TestEqual(TEXT("Fast transport ABI is version two"), FastTransport->AbiVersion, 2u);
    TestEqual(
        TEXT("Fast transport table size is explicit"),
        FastTransport->TableSize,
        static_cast<uint32>(sizeof(FUnifiedScriptNativeTransportV2)));
    TestNotNull(TEXT("Fast read function exists"), FastTransport->ReadPropertyById);
    TestNotNull(TEXT("Fast write function exists"), FastTransport->WritePropertyById);
    TestNotNull(TEXT("Fast invoke function exists"), FastTransport->InvokeById);
    TestNotNull(TEXT("Fast transport publishes reverse event ABI"), FastTransport->EventTransport);
    TestNotNull(TEXT("Fast transport publishes Shared Data ABI"), FastTransport->SharedDataTransport);
    TestNotNull(TEXT("Fast transport publishes Gameplay Data ABI"), FastTransport->GameplayDataTransport);

    const FUnifiedScriptSharedDataTransportV4* SharedDataTransport =
        UEUnifiedScript_GetSharedDataTransportV4();
    TestNotNull(TEXT("Version-four Shared Data transport exists"), SharedDataTransport);
    if (SharedDataTransport == nullptr)
    {
        return false;
    }
    TestEqual(
        TEXT("Shared Data ABI is version four"),
        SharedDataTransport->AbiVersion,
        UE::UnifiedScript::SharedData::AbiVersion);
    TestEqual(
        TEXT("Shared Transform row layout is fixed"),
        SharedDataTransport->TransformEntrySize,
        static_cast<uint32>(sizeof(FUnifiedScriptTransformSnapshotEntryV4)));
    TestEqual(
        TEXT("Shared Transform schema is stable"),
        SharedDataTransport->TransformSchemaId,
        UE::UnifiedScript::SharedData::TransformSnapshotSchemaId);
    TestNotNull(
        TEXT("Shared Data transport acquires Transform snapshots"),
        SharedDataTransport->AcquireTransformSnapshot);

    const FUnifiedScriptGameplayDataTransportV4* GameplayDataTransport =
        UEUnifiedScript_GetGameplayDataTransportV4();
    TestNotNull(TEXT("Version-four Gameplay Data transport exists"), GameplayDataTransport);
    if (GameplayDataTransport == nullptr)
    {
        return false;
    }
    TestEqual(
        TEXT("Gameplay command row layout is fixed"),
        GameplayDataTransport->CommandEntrySize,
        static_cast<uint32>(sizeof(FUnifiedScriptCommandV4)));
    TestEqual(
        TEXT("Gameplay event row layout is fixed"),
        GameplayDataTransport->EventEntrySize,
        static_cast<uint32>(sizeof(FUnifiedScriptGameplayEventV4)));
    TestEqual(
        TEXT("Batch trace request layout is fixed"),
        GameplayDataTransport->TraceRequestEntrySize,
        static_cast<uint32>(sizeof(FUnifiedScriptTraceRequestV4)));
    TestEqual(
        TEXT("Batch overlap request layout is fixed"),
        GameplayDataTransport->OverlapRequestEntrySize,
        static_cast<uint32>(sizeof(FUnifiedScriptOverlapRequestV1)));
    TestEqual(
        TEXT("Batch overlap result layout is fixed"),
        GameplayDataTransport->OverlapResultEntrySize,
        static_cast<uint32>(sizeof(FUnifiedScriptOverlapResultV1)));
    TestNotNull(TEXT("Gameplay command begin exists"), GameplayDataTransport->BeginCommandBatch);
    TestNotNull(TEXT("Gameplay event acquisition exists"), GameplayDataTransport->AcquireEvents);
    TestNotNull(TEXT("Gameplay batch trace exists"), GameplayDataTransport->ExecuteTraceBatch);
    TestNotNull(
        TEXT("Gameplay batch overlap exists"),
        GameplayDataTransport->ExecuteOverlapBatch);

    const FUnifiedScriptNativeEventTransportV4* EventTransport =
        UEUnifiedScript_GetNativeEventTransportV4();
    TestNotNull(TEXT("Version-four event transport exists"), EventTransport);
    if (EventTransport == nullptr)
    {
        return false;
    }
    TestEqual(TEXT("Event transport ABI is version four"), EventTransport->AbiVersion, 4u);
    TestEqual(
        TEXT("Event frame layout is fixed"),
        EventTransport->FrameSize,
        static_cast<uint32>(sizeof(FUnifiedScriptNativeEventFrameV4)));
    TestEqual(
        TEXT("Event transport value layout reuses ABI v3"),
        EventTransport->ValueSize,
        static_cast<uint32>(sizeof(FUnifiedScriptNativeValueV3)));
    TestNotNull(TEXT("Event subscribe function exists"), EventTransport->Subscribe);
    TestNotNull(TEXT("Event unsubscribe function exists"), EventTransport->Unsubscribe);

    TestEqual(
        TEXT("ABI v3 value layout is fixed"),
        static_cast<uint32>(sizeof(FUnifiedScriptNativeValueV3)),
        48u);
    TestEqual(
        TEXT("ABI v3 call-frame layout is fixed"),
        static_cast<uint32>(sizeof(FUnifiedScriptNativeCallFrameV3)),
        72u);
    TestEqual(
        TEXT("ABI v3 transport-table layout is fixed"),
        static_cast<uint32>(sizeof(FUnifiedScriptNativeTransportV3)),
        80u);
    TestEqual(
        TEXT("ABI v3 status byte offset is fixed"),
        static_cast<uint32>(STRUCT_OFFSET(FUnifiedScriptNativeCallFrameV3, Status)),
        58u);
    TestEqual(
        TEXT("ABI v3 highest published value kind is stable"),
        static_cast<uint8>(EUnifiedScriptNativeValueKindV3::Map),
        static_cast<uint8>(24));
    const FUnifiedScriptNativeTransportV3* CallFrameTransport =
        UEUnifiedScript_GetNativeTransportV3();
    TestNotNull(TEXT("Version-three transport table exists"), CallFrameTransport);
    if (CallFrameTransport == nullptr)
    {
        return false;
    }
    TestEqual(TEXT("Call-frame transport ABI is version three"), CallFrameTransport->AbiVersion, 3u);
    TestEqual(
        TEXT("Call-frame table size is explicit"),
        CallFrameTransport->TableSize,
        static_cast<uint32>(sizeof(FUnifiedScriptNativeTransportV3)));
    TestEqual(
        TEXT("Call-frame value size is explicit"),
        CallFrameTransport->ValueSize,
        static_cast<uint32>(sizeof(FUnifiedScriptNativeValueV3)));
    TestEqual(
        TEXT("Call-frame size is explicit"),
        CallFrameTransport->CallFrameSize,
        static_cast<uint32>(sizeof(FUnifiedScriptNativeCallFrameV3)));
    TestTrue(
        TEXT("Call-frame transport advertises fixed scalars"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::FixedScalars)) != 0);
    TestNotNull(
        TEXT("Call-frame transport exposes the int32x2 scalar lane"),
        CallFrameTransport->InvokeInt32x2ById);
    TestNotNull(
        TEXT("Call-frame transport exposes the mixed fixed-value lane"),
        CallFrameTransport->InvokeFixedById);
    TestTrue(
        TEXT("Call-frame transport advertises generation-safe object handles"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::ObjectHandles)) != 0);
    TestTrue(
        TEXT("Call-frame transport advertises UTF-8 input support"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::Utf8Text)) != 0);
    TestTrue(
        TEXT("Call-frame transport advertises staged UTF-8 results"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::Utf8Results)) != 0);
    TestTrue(
        TEXT("Call-frame transport advertises arena-backed container inputs"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::Containers)) != 0);
    TestTrue(
        TEXT("Call-frame transport advertises output and reference parameters"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::OutputAndReference)) != 0);
    TestTrue(
        TEXT("Call-frame transport advertises staged container results"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::ContainerResults)) != 0);
    TestNotNull(TEXT("Call-frame read function exists"), CallFrameTransport->ReadPropertyById);
    TestNotNull(TEXT("Call-frame write function exists"), CallFrameTransport->WritePropertyById);
    TestNotNull(TEXT("Call-frame invoke function exists"), CallFrameTransport->InvokeById);

    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Fixture world exists"), World);
    if (World == nullptr)
    {
        return false;
    }
    World->AddToRoot();

    {
        FUnifiedScriptObjectRegistry Registry(World);
        FUnifiedScriptInvocationBridge Bridge(Registry);
        FUnifiedScriptNativeBridgeContext* Context =
            new FUnifiedScriptNativeBridgeContext(Bridge);
        TestTrue(
            TEXT("Live context can be retained"),
            Transport->RetainContext(Context) != 0);
        Transport->ReleaseContext(Context);
        FUnifiedScriptObjectHandleResult HandleResult;
        UUnifiedScriptComponent* Component = NewObject<UUnifiedScriptComponent>(World);
        const FUnifiedScriptObjectHandle Handle = Registry.RegisterObject(Component, HandleResult);
        TestTrue(TEXT("Fixture registration succeeds"), HandleResult.IsSuccess());

        const FTCHARToUTF8 StartPath(
            TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:bStartAutomatically"));
        FCapturedResult Captured;
        Transport->ReadProperty(
            Context,
            Handle.Value,
            reinterpret_cast<const uint8*>(StartPath.Get()),
            StartPath.Length(),
            &Capture,
            &Captured);
        TestTrue(TEXT("Read callback executes synchronously"), Captured.bCalled);
        TestTrue(TEXT("Boolean read succeeds through ABI"), Captured.bSuccess);
        TestTrue(
            TEXT("Boolean result crosses ABI"),
            Captured.ValueKind == EUnifiedScriptValueKind::Boolean &&
                Captured.BooleanValue);

        const FTCHARToUTF8 AssetPath(
            TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:ScriptAssetPath"));
        const FTCHARToUTF8 AssetValue(TEXT("/Game/Scripts/AbiFixture"));
        FUnifiedScriptNativeValueV1 NativeString;
        NativeString.Kind = static_cast<uint8>(EUnifiedScriptValueKind::String);
        NativeString.StringData = reinterpret_cast<const uint8*>(AssetValue.Get());
        NativeString.StringLength = AssetValue.Length();
        Captured = {};
        Transport->WriteProperty(
            Context,
            Handle.Value,
            reinterpret_cast<const uint8*>(AssetPath.Get()),
            AssetPath.Length(),
            &NativeString,
            &Capture,
            &Captured);
        TestTrue(TEXT("String write succeeds through ABI"), Captured.bSuccess);
        TestEqual(
            TEXT("String input is copied into UObject storage"),
            Component->ScriptAssetPath,
            FString(TEXT("/Game/Scripts/AbiFixture")));

        const FTCHARToUTF8 AddPath(
            TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:Add"));
        FUnifiedScriptNativeValueV1 AddArguments[2];
        AddArguments[0].Kind = static_cast<uint8>(EUnifiedScriptValueKind::Int32);
        AddArguments[0].Int32Value = 20;
        AddArguments[1].Kind = static_cast<uint8>(EUnifiedScriptValueKind::Int32);
        AddArguments[1].Int32Value = 22;
        Captured = {};
        Transport->Invoke(
            Context,
            Handle.Value,
            reinterpret_cast<const uint8*>(AddPath.Get()),
            AddPath.Length(),
            AddArguments,
            2,
            &Capture,
            &Captured);
        TestTrue(TEXT("ProcessEvent succeeds through ABI"), Captured.bSuccess);
        TestTrue(
            TEXT("int32 return crosses ABI"),
            Captured.ValueKind == EUnifiedScriptValueKind::Int32 &&
                Captured.Int32Value == 42);

        constexpr uint64 StartId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:bStartAutomatically");
        Captured = {};
        FastTransport->ReadPropertyById(
            Context,
            Handle.Value,
            StartId,
            &Capture,
            &Captured);
        TestTrue(
            TEXT("Generated boolean read bypasses member-path decoding"),
            Captured.bSuccess && Captured.BooleanValue);

        constexpr uint64 AssetId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:ScriptAssetPath");
        const FTCHARToUTF8 FastAssetValue(TEXT("/Game/Scripts/FastAbiFixture"));
        NativeString.StringData = reinterpret_cast<const uint8*>(FastAssetValue.Get());
        NativeString.StringLength = FastAssetValue.Length();
        Captured = {};
        FastTransport->WritePropertyById(
            Context,
            Handle.Value,
            AssetId,
            &NativeString,
            &Capture,
            &Captured);
        TestTrue(TEXT("Generated string write uses the ABI v2 thunk"), Captured.bSuccess);
        TestEqual(
            TEXT("Fast string input reaches UObject storage directly"),
            Component->ScriptAssetPath,
            FString(TEXT("/Game/Scripts/FastAbiFixture")));

        constexpr uint64 AddId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:Add");
        Captured = {};
        FastTransport->InvokeById(
            Context,
            Handle.Value,
            AddId,
            AddArguments,
            2,
            &Capture,
            &Captured);
        TestTrue(
            TEXT("Generated Add invocation bypasses ProcessEvent"),
            Captured.bSuccess && Captured.Int32Value == 42);
        TestEqual(
            TEXT("Bridge counts successful generated fast calls"),
            Bridge.GetFastInvocationCount(),
            3ull);

        FUnifiedScriptNativeValueV3 V3Arguments[2];
        V3Arguments[0].Kind = EUnifiedScriptNativeValueKindV3::Int32;
        V3Arguments[0].Payload0 = 20;
        V3Arguments[1].Kind = EUnifiedScriptNativeValueKindV3::Int32;
        V3Arguments[1].Payload0 = 22;
        FUnifiedScriptNativeValueV3 V3Result;
        V3Result.Kind = EUnifiedScriptNativeValueKindV3::Int32;
        FUnifiedScriptNativeCallFrameV3 V3Frame;
        V3Frame.Arguments = V3Arguments;
        V3Frame.ArgumentCount = 2;
        V3Frame.Results = &V3Result;
        V3Frame.ResultCapacity = 1;
        V3Frame.ResultCount = 1;
        CallFrameTransport->InvokeById(Context, Handle.Value, AddId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 generated Add succeeds"),
            V3Frame.Status == EUnifiedScriptNativeCallStatusV3::Success);
        TestTrue(
            TEXT("ABI v3 int32 result stays inline"),
            V3Frame.ResultCount == 1 &&
                V3Result.Kind == EUnifiedScriptNativeValueKindV3::Int32 &&
                static_cast<int32>(V3Result.Payload0) == 42);

        V3Result = {};
        V3Result.Kind = EUnifiedScriptNativeValueKindV3::Int32;
        uint8 FixedInvocationError = 0;
        uint8 FixedHandleError = 0;
        const uint8 FixedStatus = CallFrameTransport->InvokeFixedById(
            Context,
            Handle.Value,
            AddId,
            V3Arguments,
            2,
            &V3Result,
            &FixedInvocationError,
            &FixedHandleError);
        TestTrue(
            TEXT("ABI v3 mixed fixed-value Add succeeds"),
            FixedStatus == static_cast<uint8>(EUnifiedScriptNativeCallStatusV3::Success) &&
                FixedInvocationError == 0 &&
                FixedHandleError == 0 &&
                V3Result.Kind == EUnifiedScriptNativeValueKindV3::Int32 &&
                static_cast<int32>(V3Result.Payload0) == 42);

        constexpr uint64 ReloadId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent:RequestReload");
        V3Result = {};
        V3Result.Kind = EUnifiedScriptNativeValueKindV3::Int32;
        V3Frame = {};
        V3Frame.Results = &V3Result;
        V3Frame.ResultCapacity = 1;
        V3Frame.ResultCount = 1;
        CallFrameTransport->InvokeById(Context, Handle.Value, ReloadId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 rejects a wrong result descriptor before dispatch"),
            V3Frame.Status == EUnifiedScriptNativeCallStatusV3::InvalidFrame);
        TestFalse(
            TEXT("Rejected ABI v3 frame does not execute a side effect"),
            Component->ConsumeReloadRequest());

        UUnifiedScriptInteropFixture* Interop = NewObject<UUnifiedScriptInteropFixture>(World);
        const FUnifiedScriptObjectHandle InteropHandle =
            Registry.RegisterObject(Interop, HandleResult);
        const auto EncodeFloat = [](float Value)
        {
            FUnifiedScriptNativeValueV3 Cell;
            Cell.Kind = EUnifiedScriptNativeValueKindV3::Float32;
            uint32 Bits = 0;
            FMemory::Memcpy(&Bits, &Value, sizeof(float));
            Cell.Payload0 = Bits;
            return Cell;
        };
        const auto EncodeVector = [](const FVector& Value)
        {
            FUnifiedScriptNativeValueV3 Cell;
            Cell.Kind = EUnifiedScriptNativeValueKindV3::Vector3;
            const double Components[] = {Value.X, Value.Y, Value.Z};
            FMemory::Memcpy(&Cell.Payload0, &Components[0], sizeof(double));
            FMemory::Memcpy(&Cell.Payload1, &Components[1], sizeof(double));
            FMemory::Memcpy(&Cell.Payload2, &Components[2], sizeof(double));
            return Cell;
        };
        const auto DecodeFloat = [](const FUnifiedScriptNativeValueV3& Cell)
        {
            float Value = 0.0f;
            const uint32 Bits = static_cast<uint32>(Cell.Payload0);
            FMemory::Memcpy(&Value, &Bits, sizeof(float));
            return Value;
        };
        const auto DecodeVector = [](const FUnifiedScriptNativeValueV3& Cell)
        {
            double Components[3] = {};
            FMemory::Memcpy(&Components[0], &Cell.Payload0, sizeof(double));
            FMemory::Memcpy(&Components[1], &Cell.Payload1, sizeof(double));
            FMemory::Memcpy(&Components[2], &Cell.Payload2, sizeof(double));
            return FVector(Components[0], Components[1], Components[2]);
        };
        const auto InvokeFixed = [&](
            uint64 MemberId,
            const FUnifiedScriptNativeValueV3* Arguments,
            uint32 ArgumentCount,
            FUnifiedScriptNativeValueV3& Result)
        {
            FixedInvocationError = 0;
            FixedHandleError = 0;
            return CallFrameTransport->InvokeFixedById(
                Context,
                InteropHandle.Value,
                MemberId,
                Arguments,
                ArgumentCount,
                &Result,
                &FixedInvocationError,
                &FixedHandleError) ==
                static_cast<uint8>(EUnifiedScriptNativeCallStatusV3::Success);
        };

        FUnifiedScriptNativeValueV3 FixedResult;
        FixedResult.Kind = EUnifiedScriptNativeValueKindV3::Int32;
        TestTrue(
            TEXT("Fixed arity 0 int32 signature executes"),
            InvokeFixed(
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:FixedArity0"),
                nullptr,
                0,
                FixedResult) &&
                static_cast<int32>(FixedResult.Payload0) == 17);

        FUnifiedScriptNativeValueV3 FixedArguments[4];
        FixedArguments[0] = EncodeFloat(2.5f);
        FixedResult = {};
        FixedResult.Kind = EUnifiedScriptNativeValueKindV3::Float32;
        TestTrue(
            TEXT("Fixed arity 1 float signature executes"),
            InvokeFixed(
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:FixedArity1"),
                FixedArguments,
                1,
                FixedResult) &&
                FMath::IsNearlyEqual(DecodeFloat(FixedResult), 4.0f));

        FixedArguments[0] = {};
        FixedArguments[0].Kind = EUnifiedScriptNativeValueKindV3::Boolean;
        FixedArguments[0].Payload0 = 1;
        FixedArguments[1] = {};
        FixedArguments[1].Kind = EUnifiedScriptNativeValueKindV3::Int32;
        FixedArguments[1].Payload0 = 7;
        FixedResult = {};
        FixedResult.Kind = EUnifiedScriptNativeValueKindV3::Boolean;
        TestTrue(
            TEXT("Fixed arity 2 bool/int32 signature executes"),
            InvokeFixed(
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:FixedArity2"),
                FixedArguments,
                2,
                FixedResult) &&
                FixedResult.Payload0 == 1);

        FixedArguments[0] = {};
        FixedArguments[0].Kind = EUnifiedScriptNativeValueKindV3::Int32;
        FixedArguments[0].Payload0 = 9;
        FixedArguments[1] = {};
        FixedArguments[1].Kind = EUnifiedScriptNativeValueKindV3::Int32;
        FixedArguments[1].Payload0 = 4;
        FixedResult = {};
        FixedResult.Kind = EUnifiedScriptNativeValueKindV3::Int32;
        TestTrue(
            TEXT("Reflected default-parameter function keeps its generated native signature"),
            InvokeFixed(
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:AddWithDefault"),
                FixedArguments,
                2,
                FixedResult) &&
                static_cast<int32>(FixedResult.Payload0) == 13);

        FixedArguments[0] = {};
        FixedArguments[0].Kind = EUnifiedScriptNativeValueKindV3::Object;
        FixedArguments[0].Payload0 = InteropHandle.Value;
        FixedArguments[1] = {};
        FixedArguments[1].Kind = EUnifiedScriptNativeValueKindV3::Boolean;
        FixedArguments[1].Payload0 = 1;
        FixedArguments[2] = EncodeFloat(1.0f);
        FixedResult = {};
        FixedResult.Kind = EUnifiedScriptNativeValueKindV3::Object;
        TestTrue(
            TEXT("Fixed arity 3 object-handle signature executes"),
            InvokeFixed(
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:FixedArity3"),
                FixedArguments,
                3,
                FixedResult) &&
                FixedResult.Payload0 == InteropHandle.Value);

        FixedArguments[0] = EncodeVector(FVector(1.0, 2.0, 3.0));
        FixedArguments[1] = {};
        FixedArguments[1].Kind = EUnifiedScriptNativeValueKindV3::Int32;
        FixedArguments[1].Payload0 = 2;
        FixedArguments[2] = EncodeFloat(3.0f);
        FixedArguments[3] = {};
        FixedArguments[3].Kind = EUnifiedScriptNativeValueKindV3::Boolean;
        FixedArguments[3].Payload0 = 1;
        FixedResult = {};
        FixedResult.Kind = EUnifiedScriptNativeValueKindV3::Vector3;
        TestTrue(
            TEXT("Fixed arity 4 FVector mixed signature executes"),
            InvokeFixed(
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:FixedArity4"),
                FixedArguments,
                4,
                FixedResult) &&
                DecodeVector(FixedResult).Equals(FVector(7.0, 8.0, 9.0)));

        constexpr uint64 InteropChangedId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:OnInteropChanged");
        const FTCHARToUTF8 InteropChangedPath(
            TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:OnInteropChanged"));
        FCapturedEvent CapturedEvent;
        const int32 RegistryCountBeforeEvent = Registry.Num();
        const uint64 EventSubscriptionId = EventTransport->Subscribe(
            Context,
            InteropHandle.Value,
            InteropChangedId,
            reinterpret_cast<const uint8*>(InteropChangedPath.Get()),
            InteropChangedPath.Length(),
            &CaptureEvent,
            &CapturedEvent);
        TestTrue(TEXT("Arbitrary reflected event subscription succeeds"), EventSubscriptionId != 0);
        Interop->BroadcastInteropChanged(73, Interop);
        TestTrue(
            TEXT("Arbitrary delegate payload crosses reverse ABI v4"),
            CapturedEvent.CallCount == 1 &&
                CapturedEvent.SubscriptionId == EventSubscriptionId &&
                CapturedEvent.Source == InteropHandle.Value &&
                CapturedEvent.EventId == InteropChangedId &&
                CapturedEvent.NewValue == 73 &&
                CapturedEvent.ContextHandle == InteropHandle.Value);
        TestEqual(
            TEXT("Temporary event object handles are released after callback"),
            Registry.Num(),
            RegistryCountBeforeEvent);
        TestTrue(
            TEXT("Arbitrary reflected event subscription releases explicitly"),
            EventTransport->Unsubscribe(Context, EventSubscriptionId) != 0);
        Interop->BroadcastInteropChanged(99, Interop);
        TestEqual(
            TEXT("Released reflected event no longer dispatches"),
            CapturedEvent.CallCount,
            1);
        TestTrue(TEXT("ABI v3 interop fixture registration succeeds"), HandleResult.IsSuccess());
        constexpr uint64 ModeId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:Mode");
        constexpr uint64 ModeTypeId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.EUnifiedScriptInteropMode");
        FUnifiedScriptNativeValueV3 EnumArgument;
        EnumArgument.Kind = EUnifiedScriptNativeValueKindV3::Enum;
        EnumArgument.TypeId = ModeTypeId;
        EnumArgument.Payload0 = static_cast<uint64>(EUnifiedScriptInteropMode::Active);
        V3Frame = {};
        V3Frame.Arguments = &EnumArgument;
        V3Frame.ArgumentCount = 1;
        CallFrameTransport->WritePropertyById(
            Context, InteropHandle.Value, ModeId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 typed enum write succeeds"),
            V3Frame.Status == EUnifiedScriptNativeCallStatusV3::Success &&
                Interop->Mode == EUnifiedScriptInteropMode::Active);

        V3Result = {};
        V3Result.Kind = EUnifiedScriptNativeValueKindV3::Enum;
        V3Result.TypeId = ModeTypeId;
        V3Frame = {};
        V3Frame.Results = &V3Result;
        V3Frame.ResultCapacity = 1;
        V3Frame.ResultCount = 1;
        CallFrameTransport->ReadPropertyById(
            Context, InteropHandle.Value, ModeId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 typed enum read succeeds"),
            V3Frame.Status == EUnifiedScriptNativeCallStatusV3::Success &&
                V3Result.TypeId == ModeTypeId &&
                V3Result.Payload0 == static_cast<uint64>(EUnifiedScriptInteropMode::Active));

        UObject* ObjectValue = NewObject<UUnifiedScriptInteropFixture>(World);
        const FUnifiedScriptObjectHandle ObjectValueHandle =
            Registry.RegisterObject(ObjectValue, HandleResult);
        TestTrue(TEXT("ABI v3 object value registration succeeds"), HandleResult.IsSuccess());
        constexpr uint64 ObjectValueId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:ObjectValue");
        constexpr uint64 EchoObjectId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoObject");
        FUnifiedScriptNativeValueV3 ObjectArgument;
        ObjectArgument.Kind = EUnifiedScriptNativeValueKindV3::Object;
        ObjectArgument.Payload0 = ObjectValueHandle.Value;
        V3Frame = {};
        V3Frame.Arguments = &ObjectArgument;
        V3Frame.ArgumentCount = 1;
        CallFrameTransport->WritePropertyById(
            Context, InteropHandle.Value, ObjectValueId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 object-handle write resolves through the World registry"),
            V3Frame.Status == EUnifiedScriptNativeCallStatusV3::Success &&
                Interop->ObjectValue == ObjectValue);

        V3Result = {};
        V3Result.Kind = EUnifiedScriptNativeValueKindV3::Object;
        V3Frame = {};
        V3Frame.Arguments = &ObjectArgument;
        V3Frame.ArgumentCount = 1;
        V3Frame.Results = &V3Result;
        V3Frame.ResultCapacity = 1;
        V3Frame.ResultCount = 1;
        CallFrameTransport->InvokeById(
            Context, InteropHandle.Value, EchoObjectId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 object-handle return stays generation-safe"),
            V3Frame.Status == EUnifiedScriptNativeCallStatusV3::Success &&
                V3Result.Kind == EUnifiedScriptNativeValueKindV3::Object &&
                V3Result.Payload0 == ObjectValueHandle.Value);

        UClass* ClassValue = UUnifiedScriptInteropFixture::StaticClass();
        const FUnifiedScriptObjectHandle ClassValueHandle =
            Registry.RegisterObject(ClassValue, HandleResult);
        TestTrue(TEXT("ABI v3 class value registration succeeds"), HandleResult.IsSuccess());
        constexpr uint64 EchoClassId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoClass");
        FUnifiedScriptNativeValueV3 ClassArgument;
        ClassArgument.Kind = EUnifiedScriptNativeValueKindV3::Class;
        ClassArgument.Payload0 = ClassValueHandle.Value;
        V3Result = {};
        V3Result.Kind = EUnifiedScriptNativeValueKindV3::Class;
        V3Frame = {};
        V3Frame.Arguments = &ClassArgument;
        V3Frame.ArgumentCount = 1;
        V3Frame.Results = &V3Result;
        V3Frame.ResultCapacity = 1;
        V3Frame.ResultCount = 1;
        CallFrameTransport->InvokeById(
            Context, InteropHandle.Value, EchoClassId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 class-handle return stays generation-safe"),
            V3Frame.Status == EUnifiedScriptNativeCallStatusV3::Success &&
                V3Result.Kind == EUnifiedScriptNativeValueKindV3::Class &&
                V3Result.Payload0 == ClassValueHandle.Value);

        const auto WriteSoftPath = [&](uint64 PropertyId, const FString& Path)
        {
            const FTCHARToUTF8 EncodedPath(*Path);
            TArray<uint8> Arena;
            Arena.Append(
                reinterpret_cast<const uint8*>(EncodedPath.Get()),
                EncodedPath.Length());
            FUnifiedScriptNativeValueV3 SoftArgument;
            SoftArgument.Kind = EUnifiedScriptNativeValueKindV3::String;
            SoftArgument.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
            SoftArgument.Payload1 = Arena.Num();
            FUnifiedScriptNativeCallFrameV3 SoftFrame;
            SoftFrame.Arguments = &SoftArgument;
            SoftFrame.ArgumentCount = 1;
            SoftFrame.ByteBuffer = Arena.GetData();
            SoftFrame.ByteCapacity = Arena.Num();
            SoftFrame.ByteCount = Arena.Num();
            CallFrameTransport->WritePropertyById(
                Context, InteropHandle.Value, PropertyId, &SoftFrame);
            return SoftFrame.Status == EUnifiedScriptNativeCallStatusV3::Success;
        };
        const FString ExpectedSoftObjectPath =
            TEXT("/Game/UnifiedScript/M7SoftObject.M7SoftObject");
        TestTrue(
            TEXT("ABI v3 writes a generated soft-object path"),
            WriteSoftPath(
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:SoftObjectValue"),
                ExpectedSoftObjectPath) &&
                Interop->SoftObjectValue.ToSoftObjectPath().ToString() ==
                    ExpectedSoftObjectPath);
        const FString ExpectedSoftClassPath =
            TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture");
        TestTrue(
            TEXT("ABI v3 writes a generated soft-class path"),
            WriteSoftPath(
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:SoftClassValue"),
                ExpectedSoftClassPath) &&
                Interop->SoftClassValue.ToSoftObjectPath().ToString() ==
                    ExpectedSoftClassPath);

        constexpr uint64 StringValueId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:StringValue");
        constexpr uint64 StringLengthId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:StringLength");
        const FString ExpectedString = TEXT("可视化脚本");
        const FTCHARToUTF8 Utf8String(*ExpectedString);
        TArray<uint8> Utf8Arena;
        Utf8Arena.Append(
            reinterpret_cast<const uint8*>(Utf8String.Get()),
            Utf8String.Length());
        FUnifiedScriptNativeValueV3 StringArgument;
        StringArgument.Kind = EUnifiedScriptNativeValueKindV3::String;
        StringArgument.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        StringArgument.Payload1 = Utf8Arena.Num();
        V3Frame = {};
        V3Frame.Arguments = &StringArgument;
        V3Frame.ArgumentCount = 1;
        V3Frame.ByteBuffer = Utf8Arena.GetData();
        V3Frame.ByteCapacity = Utf8Arena.Num();
        V3Frame.ByteCount = Utf8Arena.Num();
        CallFrameTransport->WritePropertyById(
            Context, InteropHandle.Value, StringValueId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 writes an arena-backed UTF-8 string"),
            V3Frame.Status == EUnifiedScriptNativeCallStatusV3::Success &&
                Interop->StringValue == ExpectedString);

        V3Result = {};
        V3Result.Kind = EUnifiedScriptNativeValueKindV3::Int32;
        V3Frame = {};
        V3Frame.Arguments = &StringArgument;
        V3Frame.ArgumentCount = 1;
        V3Frame.Results = &V3Result;
        V3Frame.ResultCapacity = 1;
        V3Frame.ResultCount = 1;
        V3Frame.ByteBuffer = Utf8Arena.GetData();
        V3Frame.ByteCapacity = Utf8Arena.Num();
        V3Frame.ByteCount = Utf8Arena.Num();
        CallFrameTransport->InvokeById(
            Context, InteropHandle.Value, StringLengthId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 invokes with UTF-8 input and an inline scalar result"),
            V3Frame.Status == EUnifiedScriptNativeCallStatusV3::Success &&
                V3Result.Kind == EUnifiedScriptNativeValueKindV3::Int32 &&
                static_cast<int32>(V3Result.Payload0) == ExpectedString.Len());

        V3Result = {};
        V3Result.Kind = EUnifiedScriptNativeValueKindV3::String;
        V3Result.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        TArray<uint8> Utf8ResultArena;
        Utf8ResultArena.SetNumUninitialized(256);
        V3Frame = {};
        V3Frame.Results = &V3Result;
        V3Frame.ResultCapacity = 1;
        V3Frame.ResultCount = 1;
        V3Frame.ByteBuffer = Utf8ResultArena.GetData();
        V3Frame.ByteCapacity = Utf8ResultArena.Num();
        CallFrameTransport->ReadPropertyById(
            Context, InteropHandle.Value, StringValueId, &V3Frame);
        FString DecodedStringResult;
        if (V3Frame.Status == EUnifiedScriptNativeCallStatusV3::Success &&
            V3Result.Payload0 <= V3Frame.ByteCount &&
            V3Result.Payload1 <= V3Frame.ByteCount - V3Result.Payload0)
        {
            const FUTF8ToTCHAR Decoded(
                reinterpret_cast<const UTF8CHAR*>(
                    Utf8ResultArena.GetData() + V3Result.Payload0),
                static_cast<int32>(V3Result.Payload1));
            DecodedStringResult = FString(Decoded.Length(), Decoded.Get());
        }
        TestTrue(
            TEXT("ABI v3 returns an arena-backed UTF-8 property value"),
            V3Frame.Status == EUnifiedScriptNativeCallStatusV3::Success &&
                DecodedStringResult == ExpectedString);

        auto InvokeInt32Container = [&](
            EUnifiedScriptNativeValueKindV3 Kind,
            uint64 TypeId,
            uint64 FunctionId,
            TConstArrayView<int32> FlattenedValues,
            uint32 EntryCount,
            int32 ExpectedResult)
        {
            TArray<FUnifiedScriptNativeValueV3> Cells;
            Cells.SetNum(FlattenedValues.Num());
            for (int32 Index = 0; Index < FlattenedValues.Num(); ++Index)
            {
                Cells[Index].Kind = EUnifiedScriptNativeValueKindV3::Int32;
                Cells[Index].Payload0 = static_cast<uint64>(FlattenedValues[Index]);
            }
            TArray<uint8> Arena;
            Arena.SetNumUninitialized(Cells.Num() * sizeof(FUnifiedScriptNativeValueV3));
            FMemory::Memcpy(Arena.GetData(), Cells.GetData(), Arena.Num());
            FUnifiedScriptNativeValueV3 ContainerArgument;
            ContainerArgument.Kind = Kind;
            ContainerArgument.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
            ContainerArgument.TypeId = TypeId;
            ContainerArgument.Payload1 = Arena.Num();
            ContainerArgument.Payload2 = EntryCount;
            ContainerArgument.Payload3 = sizeof(FUnifiedScriptNativeValueV3);
            FUnifiedScriptNativeValueV3 ContainerResult;
            ContainerResult.Kind = EUnifiedScriptNativeValueKindV3::Int32;
            FUnifiedScriptNativeCallFrameV3 ContainerFrame;
            ContainerFrame.Arguments = &ContainerArgument;
            ContainerFrame.ArgumentCount = 1;
            ContainerFrame.Results = &ContainerResult;
            ContainerFrame.ResultCapacity = 1;
            ContainerFrame.ResultCount = 1;
            ContainerFrame.ByteBuffer = Arena.GetData();
            ContainerFrame.ByteCapacity = Arena.Num();
            ContainerFrame.ByteCount = Arena.Num();
            CallFrameTransport->InvokeById(
                Context, InteropHandle.Value, FunctionId, &ContainerFrame);
            return ContainerFrame.Status == EUnifiedScriptNativeCallStatusV3::Success &&
                ContainerResult.Kind == EUnifiedScriptNativeValueKindV3::Int32 &&
                static_cast<int32>(ContainerResult.Payload0) == ExpectedResult;
        };

        const TArray<int32> ArrayValues = {1, 2, 3};
        TestTrue(
            TEXT("ABI v3 invokes a generated TArray input thunk"),
            InvokeInt32Container(
                EUnifiedScriptNativeValueKindV3::Array,
                UnifiedScriptMemberId("TArray<int32>"),
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:SumArray"),
                ArrayValues,
                ArrayValues.Num(),
                6));
        const TArray<int32> SetValues = {1, 2, 3};
        TestTrue(
            TEXT("ABI v3 invokes a generated TSet input thunk"),
            InvokeInt32Container(
                EUnifiedScriptNativeValueKindV3::Set,
                UnifiedScriptMemberId("TSet<int32>"),
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:CountSet"),
                SetValues,
                SetValues.Num(),
                3));
        const TArray<int32> MapValues = {1, 2, 3, 4};
        TestTrue(
            TEXT("ABI v3 invokes a generated TMap input thunk"),
            InvokeInt32Container(
                EUnifiedScriptNativeValueKindV3::Map,
                UnifiedScriptMemberId("TMap<int32,int32>"),
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:SumMap"),
                MapValues,
                MapValues.Num() / 2,
                10));

        auto InvokeEchoContainer = [&Bridge, CallFrameTransport, Context, InteropHandle](
            EUnifiedScriptNativeValueKindV3 Kind,
            uint64 TypeId,
            uint64 FunctionId,
            TConstArrayView<int32> FlattenedValues,
            uint32 EntryCount,
            int32 ExpectedSum)
        {
            TArray<FUnifiedScriptNativeValueV3> Cells;
            Cells.SetNum(FlattenedValues.Num());
            for (int32 Index = 0; Index < FlattenedValues.Num(); ++Index)
            {
                Cells[Index].Kind = EUnifiedScriptNativeValueKindV3::Int32;
                Cells[Index].Payload0 = static_cast<uint64>(FlattenedValues[Index]);
            }
            const uint32 InputByteCount =
                Cells.Num() * sizeof(FUnifiedScriptNativeValueV3);
            TArray<uint8> Arena;
            Arena.SetNumUninitialized(InputByteCount);
            FMemory::Memcpy(Arena.GetData(), Cells.GetData(), InputByteCount);
            FUnifiedScriptNativeValueV3 Argument;
            Argument.Kind = Kind;
            Argument.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
            Argument.TypeId = TypeId;
            Argument.Payload1 = InputByteCount;
            Argument.Payload2 = EntryCount;
            Argument.Payload3 = sizeof(FUnifiedScriptNativeValueV3);
            FUnifiedScriptNativeValueV3 Result;
            Result.Kind = Kind;
            Result.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
            Result.TypeId = TypeId;
            Result.Payload3 = sizeof(FUnifiedScriptNativeValueV3);
            FUnifiedScriptNativeCallFrameV3 Frame;
            Frame.Arguments = &Argument;
            Frame.ArgumentCount = 1;
            Frame.Results = &Result;
            Frame.ResultCapacity = 1;
            Frame.ResultCount = 1;
            Frame.ByteBuffer = Arena.GetData();
            Frame.ByteCapacity = Arena.Num();
            Frame.ByteCount = InputByteCount;
            const uint64 CallsBefore = Bridge.GetFastInvocationCount();
            CallFrameTransport->InvokeById(
                Context, InteropHandle.Value, FunctionId, &Frame);
            if (Frame.Status != EUnifiedScriptNativeCallStatusV3::BufferTooSmall ||
                Frame.RequiredByteCapacity <= InputByteCount)
            {
                return false;
            }

            TArray<uint8> RetryArena;
            RetryArena.SetNumUninitialized(Frame.RequiredByteCapacity);
            FMemory::Memcpy(RetryArena.GetData(), Arena.GetData(), InputByteCount);
            Result = {};
            Result.Kind = Kind;
            Result.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
            Result.TypeId = TypeId;
            Result.Payload3 = sizeof(FUnifiedScriptNativeValueV3);
            Frame = {};
            Frame.Arguments = &Argument;
            Frame.ArgumentCount = 1;
            Frame.Results = &Result;
            Frame.ResultCapacity = 1;
            Frame.ResultCount = 1;
            Frame.ByteBuffer = RetryArena.GetData();
            Frame.ByteCapacity = RetryArena.Num();
            Frame.ByteCount = InputByteCount;
            CallFrameTransport->InvokeById(
                Context, InteropHandle.Value, FunctionId, &Frame);
            if (Frame.Status != EUnifiedScriptNativeCallStatusV3::Success ||
                Bridge.GetFastInvocationCount() != CallsBefore + 1 ||
                Result.Kind != Kind || Result.TypeId != TypeId ||
                Result.Payload2 != EntryCount ||
                Result.Payload1 != InputByteCount ||
                Result.Payload0 > Frame.ByteCount ||
                Result.Payload1 > Frame.ByteCount - Result.Payload0)
            {
                return false;
            }
            int32 Sum = 0;
            for (int32 Index = 0; Index < Cells.Num(); ++Index)
            {
                FUnifiedScriptNativeValueV3 OutputCell;
                FMemory::Memcpy(
                    &OutputCell,
                    RetryArena.GetData() + Result.Payload0 +
                        Index * sizeof(FUnifiedScriptNativeValueV3),
                    sizeof(OutputCell));
                if (OutputCell.Kind != EUnifiedScriptNativeValueKindV3::Int32)
                {
                    return false;
                }
                Sum += static_cast<int32>(OutputCell.Payload0);
            }
            return Sum == ExpectedSum;
        };

        TestTrue(
            TEXT("ABI v3 retries a staged TArray result without re-dispatch"),
            InvokeEchoContainer(
                EUnifiedScriptNativeValueKindV3::Array,
                UnifiedScriptMemberId("TArray<int32>"),
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoArray"),
                ArrayValues,
                ArrayValues.Num(),
                6));
        TestTrue(
            TEXT("ABI v3 retries a staged TSet result without re-dispatch"),
            InvokeEchoContainer(
                EUnifiedScriptNativeValueKindV3::Set,
                UnifiedScriptMemberId("TSet<int32>"),
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoSet"),
                SetValues,
                SetValues.Num(),
                6));
        TestTrue(
            TEXT("ABI v3 retries a staged TMap result without re-dispatch"),
            InvokeEchoContainer(
                EUnifiedScriptNativeValueKindV3::Map,
                UnifiedScriptMemberId("TMap<int32,int32>"),
                UnifiedScriptMemberId(
                    "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoMap"),
                MapValues,
                MapValues.Num() / 2,
                10));

        constexpr uint64 MutateValuesId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:MutateValues");
        FUnifiedScriptNativeValueV3 MutateArguments[2];
        MutateArguments[0].Kind = EUnifiedScriptNativeValueKindV3::Int32;
        MutateArguments[0].Payload0 = 7;
        MutateArguments[1].Kind = EUnifiedScriptNativeValueKindV3::Int32;
        MutateArguments[1].Payload0 = 10;
        FUnifiedScriptNativeValueV3 MutateResults[3];
        MutateResults[0].Kind = EUnifiedScriptNativeValueKindV3::Boolean;
        MutateResults[1].Kind = EUnifiedScriptNativeValueKindV3::Int32;
        MutateResults[2].Kind = EUnifiedScriptNativeValueKindV3::Int32;
        FUnifiedScriptNativeCallFrameV3 MutateFrame;
        MutateFrame.Arguments = MutateArguments;
        MutateFrame.ArgumentCount = UE_ARRAY_COUNT(MutateArguments);
        MutateFrame.Results = MutateResults;
        MutateFrame.ResultCapacity = UE_ARRAY_COUNT(MutateResults);
        MutateFrame.ResultCount = UE_ARRAY_COUNT(MutateResults);
        CallFrameTransport->InvokeById(
            Context, InteropHandle.Value, MutateValuesId, &MutateFrame);
        TestTrue(
            TEXT("ABI v3 returns the value plus output and input/output parameters"),
            MutateFrame.Status == EUnifiedScriptNativeCallStatusV3::Success &&
                MutateFrame.ResultCount == UE_ARRAY_COUNT(MutateResults) &&
                MutateResults[0].Kind == EUnifiedScriptNativeValueKindV3::Boolean &&
                MutateResults[0].Payload0 == 1 &&
                MutateResults[1].Kind == EUnifiedScriptNativeValueKindV3::Int32 &&
                static_cast<int32>(MutateResults[1].Payload0) == 14 &&
                MutateResults[2].Kind == EUnifiedScriptNativeValueKindV3::Int32 &&
                static_cast<int32>(MutateResults[2].Payload0) == 17);

        TestTrue(TEXT("Fixture release succeeds"), Registry.ReleaseObject(Handle, HandleResult));
        Captured = {};
        Transport->ReadProperty(
            Context,
            Handle.Value,
            reinterpret_cast<const uint8*>(StartPath.Get()),
            StartPath.Length(),
            &Capture,
            &Captured);
        TestFalse(TEXT("Stale handle fails through ABI"), Captured.bSuccess);
        TestTrue(
            TEXT("Stale lifecycle category crosses ABI"),
            Captured.Error == EUnifiedScriptInvocationError::ObjectHandleFailure &&
                Captured.HandleError == EUnifiedScriptObjectHandleError::StaleHandle);

        Captured = {};
        Transport->ReadProperty(
            nullptr,
            Handle.Value,
            reinterpret_cast<const uint8*>(StartPath.Get()),
            StartPath.Length(),
            &Capture,
            &Captured);
        TestTrue(
            TEXT("Missing context has a stable transport error"),
            Captured.Error == EUnifiedScriptInvocationError::TransportUnavailable);

        Context->Detach();
        Captured = {};
        Transport->ReadProperty(
            Context,
            Handle.Value,
            reinterpret_cast<const uint8*>(StartPath.Get()),
            StartPath.Length(),
            &Capture,
            &Captured);
        TestTrue(
            TEXT("Detached context has a stable transport error"),
            Captured.Error == EUnifiedScriptInvocationError::TransportUnavailable);
        TestFalse(
            TEXT("Detached context cannot be retained"),
            Transport->RetainContext(Context) != 0);
        Context->Release();
    }

    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptSharedTransformSnapshotTest,
    "UnifiedScript.Unreal.SharedData.TransformSnapshot",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptSharedTransformSnapshotTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Shared snapshot World exists"), World);
    if (World == nullptr)
    {
        return false;
    }
    World->AddToRoot();

    UUnifiedScriptWorldSubsystem* Host =
        World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    AStaticMeshActor* Actor = World->SpawnActor<AStaticMeshActor>();
    TestNotNull(TEXT("Shared snapshot World host exists"), Host);
    TestNotNull(TEXT("Shared snapshot Actor exists"), Actor);
    if (Host == nullptr || Actor == nullptr)
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }

    Actor->SetActorLocation(FVector(10.0, 20.0, 30.0));
    Host->BeginManagedFrame();

    FUnifiedScriptNativeBridgeContext* Context = Host->GetNativeInvocationContext();
    const FUnifiedScriptSharedDataTransportV4* Transport =
        Host->GetNativeTransport()->SharedDataTransport;
    TestNotNull(TEXT("Shared snapshot context exists"), Context);
    TestNotNull(TEXT("Shared snapshot transport is discoverable"), Transport);
    if (Context == nullptr || Transport == nullptr)
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }

    FUnifiedScriptSharedSnapshotViewV4 FirstView;
    TestTrue(
        TEXT("First Transform snapshot is acquired"),
        Transport->AcquireTransformSnapshot(Context, &FirstView) != 0);
    TestEqual(
        TEXT("Snapshot is scoped to the owning World"),
        FirstView.WorldHostId,
        Host->GetWorldHostId());
    TestEqual(
        TEXT("Snapshot publishes the fixed Transform row"),
        FirstView.EntrySize,
        static_cast<uint32>(sizeof(FUnifiedScriptTransformSnapshotEntryV4)));

    const FUnifiedScriptTransformSnapshotEntryV4* FirstActorEntry = nullptr;
    for (uint32 Index = 0; Index < FirstView.EntryCount; ++Index)
    {
        const FUnifiedScriptTransformSnapshotEntryV4& Entry = FirstView.Entries[Index];
        if (Entry.TranslationX == 10.0 && Entry.TranslationY == 20.0 &&
            Entry.TranslationZ == 30.0)
        {
            FirstActorEntry = &Entry;
            break;
        }
    }
    TestNotNull(TEXT("Actor Transform is present in shared memory"), FirstActorEntry);
    const uint64 ActorHandle = FirstActorEntry != nullptr
        ? FirstActorEntry->Actor.ObjectHandle
        : 0;
    TestTrue(
        TEXT("Snapshot actor handle is generation-safe and World-scoped"),
        ActorHandle != 0 &&
            FirstActorEntry->Actor.WorldHostId == Host->GetWorldHostId());

    Actor->SetActorLocation(FVector(40.0, 50.0, 60.0));
    Host->BeginManagedFrame();
    FUnifiedScriptSharedSnapshotViewV4 SecondView;
    TestTrue(
        TEXT("Second Transform snapshot is acquired"),
        Transport->AcquireTransformSnapshot(Context, &SecondView) != 0);
    TestTrue(
        TEXT("Publishing advances the snapshot generation"),
        SecondView.Generation > FirstView.Generation);
    TestTrue(
        TEXT("Publishing flips to the other native buffer"),
        SecondView.Entries != FirstView.Entries);

    bool bFoundUpdatedActor = false;
    for (uint32 Index = 0; Index < SecondView.EntryCount; ++Index)
    {
        const FUnifiedScriptTransformSnapshotEntryV4& Entry = SecondView.Entries[Index];
        if (Entry.Actor.ObjectHandle == ActorHandle)
        {
            bFoundUpdatedActor = Entry.TranslationX == 40.0 &&
                Entry.TranslationY == 50.0 && Entry.TranslationZ == 60.0;
            break;
        }
    }
    TestTrue(
        TEXT("Stable Actor handle points at the updated Transform row"),
        bFoundUpdatedActor);
    TestEqual(
        TEXT("Shared data publishes the generated compatibility set"),
        Transport->LayoutCompatibilityId,
        UE::UnifiedScript::DataPlaneSchema::CompatibilityId);
    TestEqual(
        TEXT("Generated Transform capacity is published"),
        Transport->MaximumTransformSnapshotCount,
        UE::UnifiedScript::SharedData::MaximumTransformSnapshotCount);
    TestNotNull(TEXT("Shared capacity telemetry is discoverable"), Transport->GetTelemetry);
    FUnifiedScriptSharedDataTelemetryV1 Telemetry;
    TestTrue(
        TEXT("Shared telemetry is readable through the schema extension"),
        Transport->GetTelemetry(Context, &Telemetry, sizeof(Telemetry)) != 0);
    TestEqual(
        TEXT("Telemetry tracks the currently borrowed generation"),
        Telemetry.PublicationGeneration,
        SecondView.Generation);
    TestEqual(
        TEXT("Published Transform rows stay within the generated bound"),
        Telemetry.MaximumEntryCount,
        UE::UnifiedScript::SharedData::MaximumTransformSnapshotCount);
    TestTrue(
        TEXT("Published Transform count is bounded"),
        Telemetry.PublishedEntryCount <= Telemetry.MaximumEntryCount);

    TestTrue(
        TEXT("Managed ownership can retain the shared context"),
        Transport->RetainContext(Context) != 0);
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    FUnifiedScriptSharedSnapshotViewV4 DetachedView;
    TestFalse(
        TEXT("World teardown invalidates the shared-memory view"),
        Transport->AcquireTransformSnapshot(Context, &DetachedView) != 0);
    Transport->ReleaseContext(Context);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptGameplayDataPlaneTest,
    "UnifiedScript.Unreal.GameplayData.CommandEventAndBatchTrace",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptGameplayDataPlaneTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Gameplay data World exists"), World);
    if (World == nullptr)
    {
        return false;
    }
    World->AddToRoot();
    UUnifiedScriptWorldSubsystem* Host =
        World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    AStaticMeshActor* Target = World->SpawnActor<AStaticMeshActor>();
    AActor* TraceTarget = World->SpawnActor<AActor>();
    TestNotNull(TEXT("Gameplay data World host exists"), Host);
    TestNotNull(TEXT("Command target exists"), Target);
    TestNotNull(TEXT("Trace target exists"), TraceTarget);
    if (Host == nullptr || Target == nullptr || TraceTarget == nullptr)
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }

    UBoxComponent* Box = NewObject<UBoxComponent>(TraceTarget);
    TraceTarget->SetRootComponent(Box);
    TraceTarget->AddInstanceComponent(Box);
    Box->InitBoxExtent(FVector(20.0, 20.0, 20.0));
    Box->SetCollisionEnabled(ECollisionEnabled::QueryOnly);
    Box->SetCollisionResponseToAllChannels(ECR_Block);
    Box->RegisterComponentWithWorld(World);

    FUnifiedScriptObjectHandleResult HandleResult;
    const FUnifiedScriptObjectHandle TargetHandle =
        Host->RegisterObject(Target, HandleResult);
    TestTrue(TEXT("Command target handle is registered"), HandleResult.IsSuccess());
    const FUnifiedScriptObjectHandle ClassHandle =
        Host->RegisterObject(AStaticMeshActor::StaticClass(), HandleResult);
    TestTrue(TEXT("Spawn class handle is registered"), HandleResult.IsSuccess());

    const FUnifiedScriptNativeTransportV2* Bootstrap = Host->GetNativeTransport();
    FUnifiedScriptNativeBridgeContext* Context = Host->GetNativeInvocationContext();
    const FUnifiedScriptGameplayDataTransportV4* Transport =
        Bootstrap != nullptr ? Bootstrap->GameplayDataTransport : nullptr;
    TestNotNull(TEXT("Gameplay transport is discoverable"), Transport);
    TestNotNull(TEXT("Gameplay context exists"), Context);
    if (Transport == nullptr || Context == nullptr)
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }

    uint8 Phase = 0;
    uint64 FrameNumber = 0;
    TestTrue(
        TEXT("World phase is queryable"),
        Transport->GetPhase(Context, &Phase, &FrameNumber) != 0);
    TestEqual(
        TEXT("Managed scripts start inside the managed phase"),
        Phase,
        static_cast<uint8>(EUnifiedScriptGameplayDataPhaseV4::ManagedTick));
    TestEqual(
        TEXT("Gameplay data publishes the generated compatibility set"),
        Transport->LayoutCompatibilityId,
        UE::UnifiedScript::DataPlaneSchema::CompatibilityId);
    TestTrue(
        TEXT("Gameplay data accepts forward-compatible schema revisions"),
        Transport->LayoutRevision >= UE::UnifiedScript::DataPlaneSchema::Revision);
    TestEqual(
        TEXT("Generated command capacity is published"),
        Transport->MaximumCommandCount,
        UE::UnifiedScript::GameplayData::MaximumCommandCount);
    TestNotNull(TEXT("Gameplay capacity telemetry is discoverable"), Transport->GetTelemetry);
    TestEqual(
        TEXT("Generated overlap capacity is published"),
        Transport->MaximumOverlapCount,
        UE::UnifiedScript::GameplayData::MaximumOverlapCount);
    TestNotNull(TEXT("Batch overlap reservation is discoverable"), Transport->BeginOverlapBatch);

    FUnifiedScriptCommandBatchViewV4 RejectedCommandView;
    TestFalse(
        TEXT("Command reservations reject schema capacity overflow"),
        Transport->BeginCommandBatch(
            Context,
            UE::UnifiedScript::GameplayData::MaximumCommandCount + 1,
            &RejectedCommandView) != 0);
    FUnifiedScriptTraceRequestViewV4 RejectedTraceView;
    TestFalse(
        TEXT("Trace reservations reject schema capacity overflow"),
        Transport->BeginTraceBatch(
            Context,
            UE::UnifiedScript::GameplayData::MaximumTraceCount + 1,
            &RejectedTraceView) != 0);
    FUnifiedScriptOverlapRequestViewV1 RejectedOverlapView;
    TestFalse(
        TEXT("Overlap reservations reject schema capacity overflow"),
        Transport->BeginOverlapBatch(
            Context,
            UE::UnifiedScript::GameplayData::MaximumOverlapCount + 1,
            &RejectedOverlapView) != 0);

    FUnifiedScriptCommandBatchViewV4 CommandView;
    TestTrue(
        TEXT("Managed phase reserves a command batch"),
        Transport->BeginCommandBatch(Context, 2, &CommandView) != 0);
    CommandView.Commands[0].Type = EUnifiedScriptCommandTypeV4::SetActorTransform;
    CommandView.Commands[0].Target = {Host->GetWorldHostId(), TargetHandle.Value};
    CommandView.Commands[0].Vector0X = 100.0;
    CommandView.Commands[0].Vector0Y = 200.0;
    CommandView.Commands[0].Vector0Z = 300.0;
    CommandView.Commands[0].RotationW = 1.0;
    CommandView.Commands[0].Vector2X = 1.0;
    CommandView.Commands[0].Vector2Y = 1.0;
    CommandView.Commands[0].Vector2Z = 1.0;
    CommandView.Commands[1].Type = EUnifiedScriptCommandTypeV4::SpawnActor;
    CommandView.Commands[1].Argument0 = {Host->GetWorldHostId(), ClassHandle.Value};
    CommandView.Commands[1].RotationW = 1.0;
    CommandView.Commands[1].Vector2X = 1.0;
    CommandView.Commands[1].Vector2Y = 1.0;
    CommandView.Commands[1].Vector2Z = 1.0;
    TestTrue(
        TEXT("Command batch commit succeeds"),
        Transport->CommitCommandBatch(Context, CommandView.BatchToken, 2) != 0);
    Host->EndManagedFrame();
    TestEqual(
        TEXT("PostActorTick command updates the Actor"),
        Target->GetActorLocation(),
        FVector(100.0, 200.0, 300.0));

    Host->BeginManagedFrame();
    FUnifiedScriptGameplayEventViewV4 EventView;
    TestTrue(
        TEXT("Next frame publishes command completion events"),
        Transport->AcquireEvents(Context, &EventView) != 0);
    TestEqual(TEXT("Two committed commands publish two events"), EventView.EventCount, 2u);
    TestTrue(
        TEXT("SetTransform completion succeeds"),
        EventView.Events[0].Status == EUnifiedScriptCommandStatusV4::Success &&
            EventView.Events[0].CommandType ==
                EUnifiedScriptCommandTypeV4::SetActorTransform);
    TestTrue(
        TEXT("SpawnActor completion returns a World-scoped handle"),
        EventView.Events[1].Status == EUnifiedScriptCommandStatusV4::Success &&
            EventView.Events[1].Kind == EUnifiedScriptGameplayEventKindV4::ActorSpawned &&
            EventView.Events[1].Argument.WorldHostId == Host->GetWorldHostId() &&
            EventView.Events[1].Argument.ObjectHandle != 0);

    FUnifiedScriptTraceRequestViewV4 TraceView;
    TestTrue(
        TEXT("Managed phase reserves a three-shape trace batch"),
        Transport->BeginTraceBatch(Context, 3, &TraceView) != 0);
    for (uint32 Index = 0; Index < 3; ++Index)
    {
        TraceView.Requests[Index].Shape =
            static_cast<EUnifiedScriptTraceShapeV4>(Index);
        TraceView.Requests[Index].CollisionChannel = ECC_Visibility;
        TraceView.Requests[Index].StartX = -100.0;
        TraceView.Requests[Index].EndX = 100.0;
        TraceView.Requests[Index].ExtentX = 5.0;
        TraceView.Requests[Index].ExtentY = 5.0;
        TraceView.Requests[Index].ExtentZ = 5.0;
        TraceView.Requests[Index].RotationW = 1.0;
    }
    FUnifiedScriptTraceResultViewV4 TraceResults;
    TestTrue(
        TEXT("Line/sphere/box traces execute through one boundary call"),
        Transport->ExecuteTraceBatch(
            Context,
            TraceView.BatchToken,
            3,
            &TraceResults) != 0);
    TestEqual(TEXT("Batch trace returns one row per request"), TraceResults.ResultCount, 3u);
    TestTrue(
        TEXT("All three trace shapes execute successfully"),
        TraceResults.Results[0].Status == EUnifiedScriptTraceStatusV4::Success &&
            TraceResults.Results[1].Status == EUnifiedScriptTraceStatusV4::Success &&
            TraceResults.Results[2].Status == EUnifiedScriptTraceStatusV4::Success);
    TestTrue(
        TEXT("Line trace returns the reflected FHitResult data"),
        static_cast<uint8>(TraceResults.Results[0].Flags) &
            static_cast<uint8>(EUnifiedScriptTraceResultFlagsV4::BlockingHit));

    FUnifiedScriptOverlapRequestViewV1 OverlapView;
    TestTrue(
        TEXT("Managed phase reserves a sphere/box overlap batch"),
        Transport->BeginOverlapBatch(Context, 2, &OverlapView) != 0);
    OverlapView.Requests[0].Shape = EUnifiedScriptTraceShapeV4::Sphere;
    OverlapView.Requests[0].CollisionChannel = ECC_Visibility;
    OverlapView.Requests[0].ExtentX = 50.0;
    OverlapView.Requests[1].Shape = EUnifiedScriptTraceShapeV4::Box;
    OverlapView.Requests[1].CollisionChannel = ECC_Visibility;
    OverlapView.Requests[1].ExtentX = 50.0;
    OverlapView.Requests[1].ExtentY = 50.0;
    OverlapView.Requests[1].ExtentZ = 50.0;
    OverlapView.Requests[1].RotationW = 1.0;
    FUnifiedScriptOverlapResultViewV1 OverlapResults;
    TestTrue(
        TEXT("Sphere/box overlaps execute through one boundary call"),
        Transport->ExecuteOverlapBatch(
            Context,
            OverlapView.BatchToken,
            2,
            &OverlapResults) != 0);
    TestEqual(
        TEXT("Batch overlap returns one row per request"),
        OverlapResults.ResultCount,
        2u);
    TestTrue(
        TEXT("Both overlap shapes execute successfully"),
        OverlapResults.Results[0].Status == EUnifiedScriptTraceStatusV4::Success &&
            OverlapResults.Results[1].Status == EUnifiedScriptTraceStatusV4::Success);
    TestTrue(
        TEXT("Overlap result returns World-scoped Actor handles"),
        OverlapResults.Results[0].Flags ==
                EUnifiedScriptOverlapResultFlagsV1::HasOverlap &&
            OverlapResults.Results[0].HitActor.WorldHostId ==
                Host->GetWorldHostId() &&
            OverlapResults.Results[0].HitActor.ObjectHandle != 0);

    FUnifiedScriptCommandBatchViewV4 EventOverflowView;
    const uint32 OverflowEventCount =
        UE::UnifiedScript::GameplayData::MaximumEventCount + 1;
    TestTrue(
        TEXT("One bounded batch can exercise event overflow telemetry"),
        Transport->BeginCommandBatch(Context, OverflowEventCount, &EventOverflowView) != 0);
    TestTrue(
        TEXT("Overflow exercise commits in reservation order"),
        Transport->CommitCommandBatch(
            Context,
            EventOverflowView.BatchToken,
            OverflowEventCount) != 0);

    Host->EndManagedFrame();
    FUnifiedScriptGameplayDataTelemetryV1 Telemetry;
    TestTrue(
        TEXT("Gameplay telemetry is readable through the schema extension"),
        Transport->GetTelemetry(Context, &Telemetry, sizeof(Telemetry)) != 0);
    TestEqual(TEXT("Oversized command reservation is counted"), Telemetry.CommandOverflowCount, 1ull);
    TestEqual(TEXT("Dropped completion event is counted"), Telemetry.EventOverflowCount, 1ull);
    TestEqual(TEXT("Oversized trace reservation is counted"), Telemetry.TraceOverflowCount, 1ull);
    TestEqual(TEXT("Oversized overlap reservation is counted"), Telemetry.OverlapOverflowCount, 1ull);
    TestEqual(
        TEXT("Committed command telemetry covers both reservations"),
        Telemetry.TotalCommittedCommandCount,
        static_cast<uint64>(2 + OverflowEventCount));
    TestEqual(
        TEXT("Every committed command executes despite event overflow"),
        Telemetry.TotalExecutedCommandCount,
        Telemetry.TotalCommittedCommandCount);
    TestEqual(TEXT("Trace telemetry counts all shapes"), Telemetry.TotalExecutedTraceCount, 3ull);
    TestEqual(TEXT("Overlap telemetry counts both shapes"), Telemetry.TotalExecutedOverlapCount, 2ull);
    TestTrue(
        TEXT("World scheduler telemetry counts native pre, managed, and native post phases"),
        Telemetry.NativePreActorTickCount > 0 &&
            Telemetry.ManagedTickCount > 0 &&
            Telemetry.NativePostActorTickCount > 0);
    TestTrue(
        TEXT("World scheduler telemetry measures native pre, managed, and native post time"),
        Telemetry.NativePreActorTickNanoseconds > 0 &&
            Telemetry.ManagedTickNanoseconds > 0 &&
            Telemetry.NativePostActorTickNanoseconds > 0);
    struct alignas(8) FTelemetryPrefixProbe
    {
        uint32 SchemaRevision = 0;
        uint32 StructSize = 0;
        uint64 Canary = 0xA55AA55AA55AA55Aull;
    };
    FTelemetryPrefixProbe PrefixProbe;
    TestTrue(
        TEXT("Telemetry copies only the caller-advertised compatible prefix"),
        Transport->GetTelemetry(
            Context,
            reinterpret_cast<FUnifiedScriptGameplayDataTelemetryV1*>(&PrefixProbe),
            sizeof(uint32) * 2) != 0);
    TestEqual(
        TEXT("Telemetry prefix reports the current schema revision"),
        PrefixProbe.SchemaRevision,
        UE::UnifiedScript::DataPlaneSchema::Revision);
    TestEqual(
        TEXT("Telemetry prefix reports the native full structure size"),
        PrefixProbe.StructSize,
        static_cast<uint32>(sizeof(FUnifiedScriptGameplayDataTelemetryV1)));
    TestEqual(
        TEXT("Telemetry prefix copy does not overwrite caller memory"),
        PrefixProbe.Canary,
        0xA55AA55AA55AA55Aull);
    TestFalse(
        TEXT("Telemetry rejects a buffer smaller than its version header"),
        Transport->GetTelemetry(
            Context,
            reinterpret_cast<FUnifiedScriptGameplayDataTelemetryV1*>(&PrefixProbe),
            sizeof(uint32)) != 0);
    TestEqual(
        TEXT("Command execution preserves monotonically assigned order"),
        Telemetry.LastExecutedSequenceId,
        Telemetry.LastCommittedSequenceId);
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptAsyncServiceTest,
    "UnifiedScript.Unreal.AsyncService.DelayLoadCancelAndLifecycle",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptAsyncServiceTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Async service World exists"), World);
    if (World == nullptr)
    {
        return false;
    }
    World->AddToRoot();
    UUnifiedScriptWorldSubsystem* Host =
        World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    const FUnifiedScriptNativeTransportV2* Bootstrap =
        Host != nullptr ? Host->GetNativeTransport() : nullptr;
    FUnifiedScriptNativeBridgeContext* Context =
        Host != nullptr ? Host->GetNativeInvocationContext() : nullptr;
    const FUnifiedScriptAsyncTransportV5* Transport =
        Bootstrap != nullptr ? Bootstrap->AsyncTransport : nullptr;
    TestNotNull(TEXT("Async service World host exists"), Host);
    TestNotNull(TEXT("Async service context exists"), Context);
    TestNotNull(TEXT("Async service ABI v5 is discoverable"), Transport);
    if (Host == nullptr || Context == nullptr || Transport == nullptr)
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }

    TestEqual(
        TEXT("Async request ABI layout is stable"),
        Transport->RequestSize,
        static_cast<uint32>(sizeof(FUnifiedScriptAsyncRequestV5)));
    const uint64 RequiredCapabilities =
        static_cast<uint64>(EUnifiedScriptAsyncCapabilitiesV5::Delay) |
        static_cast<uint64>(EUnifiedScriptAsyncCapabilitiesV5::AsyncAssetLoad) |
        static_cast<uint64>(EUnifiedScriptAsyncCapabilitiesV5::Cancellation) |
        static_cast<uint64>(EUnifiedScriptAsyncCapabilitiesV5::Timeout) |
        static_cast<uint64>(EUnifiedScriptAsyncCapabilitiesV5::GenerationOwnership) |
        static_cast<uint64>(EUnifiedScriptAsyncCapabilitiesV5::WorldTeardown);
    TestEqual(
        TEXT("Async service advertises the complete M7.2 lifecycle contract"),
        Transport->Capabilities & RequiredCapabilities,
        RequiredCapabilities);

    auto Submit = [&](uint64 RequestId,
                      EUnifiedScriptAsyncOperationV5 Operation,
                      float DurationSeconds,
                      float TimeoutSeconds,
                      const FString& Payload = FString())
    {
        const FTCHARToUTF8 PayloadUtf8(*Payload);
        FUnifiedScriptAsyncRequestV5 Request;
        Request.RequestId = RequestId;
        Request.OwnerInstanceId = 101;
        Request.OwnerGeneration = 7;
        Request.Operation = Operation;
        Request.DurationSeconds = DurationSeconds;
        Request.TimeoutSeconds = TimeoutSeconds;
        Request.PayloadData = reinterpret_cast<const uint8*>(PayloadUtf8.Get());
        Request.PayloadLength = static_cast<uint32>(PayloadUtf8.Length());
        return Transport->SubmitRequest(Context, &Request) != 0;
    };

    FUnifiedScriptAsyncCompletionViewV5 View;
    TestTrue(
        TEXT("Generation-owned Delay request is accepted"),
        Submit(1, EUnifiedScriptAsyncOperationV5::Delay, 0.5f, 0.0f));
    TestTrue(
        TEXT("First owner pump succeeds"),
        Transport->PumpOwner(Context, 101, 7, 0.25f) != 0);
    TestTrue(
        TEXT("Incomplete Delay can be polled"),
        Transport->PollCompletions(Context, 101, 7, &View) != 0);
    TestEqual(TEXT("Delay remains pending before its duration"), View.Count, 0u);
    TestTrue(
        TEXT("Second owner pump completes Delay"),
        Transport->PumpOwner(Context, 101, 7, 0.25f) != 0);
    TestTrue(
        TEXT("Completed Delay can be polled"),
        Transport->PollCompletions(Context, 101, 7, &View) != 0);
    TestTrue(
        TEXT("Delay completion preserves owner and request identity"),
        View.Count == 1 && View.Completions[0].RequestId == 1 &&
            View.Completions[0].OwnerInstanceId == 101 &&
            View.Completions[0].OwnerGeneration == 7 &&
            View.Completions[0].Status ==
                EUnifiedScriptAsyncCompletionStatusV5::Succeeded);

    TestTrue(
        TEXT("Timed Delay request is accepted"),
        Submit(2, EUnifiedScriptAsyncOperationV5::Delay, 5.0f, 0.1f));
    TestTrue(
        TEXT("Timeout pump succeeds"),
        Transport->PumpOwner(Context, 101, 7, 0.1f) != 0);
    TestTrue(
        TEXT("Timed-out request can be polled"),
        Transport->PollCompletions(Context, 101, 7, &View) != 0);
    TestTrue(
        TEXT("Timeout is a stable completion state"),
        View.Count == 1 && View.Completions[0].RequestId == 2 &&
            View.Completions[0].Status ==
                EUnifiedScriptAsyncCompletionStatusV5::TimedOut);

    const FString ClassPath = UUnifiedScriptComponent::StaticClass()->GetPathName();
    TestTrue(
        TEXT("Async class load is accepted"),
        Submit(3, EUnifiedScriptAsyncOperationV5::LoadClass, 0.0f, 1.0f, ClassPath));
    TestTrue(
        TEXT("Resolved class load completion can be polled"),
        Transport->PollCompletions(Context, 101, 7, &View) != 0);
    TestTrue(
        TEXT("Async class load returns a generation-safe object handle"),
        View.Count == 1 && View.Completions[0].RequestId == 3 &&
            View.Completions[0].Status ==
                EUnifiedScriptAsyncCompletionStatusV5::Succeeded &&
            View.Completions[0].ResultHandle != 0);

    TestTrue(
        TEXT("Cancelable Delay request is accepted"),
        Submit(4, EUnifiedScriptAsyncOperationV5::Delay, 5.0f, 0.0f));
    TestTrue(
        TEXT("Individual cancellation succeeds"),
        Transport->CancelRequest(Context, 101, 7, 4) != 0);
    TestTrue(
        TEXT("Canceled request can be polled"),
        Transport->PollCompletions(Context, 101, 7, &View) != 0);
    TestTrue(
        TEXT("Cancellation is a stable completion state"),
        View.Count == 1 && View.Completions[0].RequestId == 4 &&
            View.Completions[0].Status ==
                EUnifiedScriptAsyncCompletionStatusV5::Canceled);

    TestTrue(
        TEXT("First rollover request is accepted"),
        Submit(5, EUnifiedScriptAsyncOperationV5::Delay, 5.0f, 0.0f));
    TestTrue(
        TEXT("Second rollover request is accepted"),
        Submit(6, EUnifiedScriptAsyncOperationV5::Delay, 5.0f, 0.0f));
    TestEqual(
        TEXT("Generation rollover cancels every owned request"),
        Transport->CancelOwner(Context, 101, 7),
        2u);
    TestEqual(
        TEXT("Generation rollover leaves no native async request"),
        Host->GetPendingAsyncRequestCount(),
        0);

    TestTrue(
        TEXT("Managed ownership can retain the async context"),
        Transport->RetainContext(Context) != 0);
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    World = nullptr;
    FUnifiedScriptAsyncRequestV5 DetachedRequest;
    DetachedRequest.RequestId = 7;
    DetachedRequest.OwnerInstanceId = 101;
    DetachedRequest.OwnerGeneration = 7;
    TestFalse(
        TEXT("World teardown rejects new async requests"),
        Transport->SubmitRequest(Context, &DetachedRequest) != 0);
    Transport->ReleaseContext(Context);
    return true;
}

#endif
