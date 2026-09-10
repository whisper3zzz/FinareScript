#if WITH_DEV_AUTOMATION_TESTS

#include "FinareNativeTransport.h"

#include "Containers/StringConv.h"
#include "Components/BoxComponent.h"
#include "Engine/World.h"
#include "Engine/StaticMeshActor.h"
#include "Misc/AutomationTest.h"
#include "FinareComponent.h"
#include "FinareGeneratedThunkRegistry.h"
#include "Misc/ScopeExit.h"
#include "Generated/FinareBlockBreakerDataPlaneExtension.gen.h"
#include "FinareInteropFixture.h"
#include "FinareWorldSubsystem.h"
#include "UObject/UObjectGlobals.h"

#include <bit>

namespace UE::Finare::NativeTransport::Tests
{
constexpr uint64 CommonValueMember = FinareMemberId("/Script/FinareTests.CommonValues:Echo");
FFinareValue CommonValueStorage;

FFinareGeneratedThunkTable CommonValueTable()
{
    FFinareGeneratedThunkTable Table;
    Table.DescribeReadResult = [](uint64 Member, EFinareValueKind& Kind, uint64& TypeId)
    { Kind = CommonValueStorage.Kind; TypeId = CommonValueStorage.TypeIdValue; return Member == CommonValueMember; };
    Table.DescribeInvokeResultCount = [](uint64 Member, uint32& Count)
    { Count = 1; return Member == CommonValueMember; };
    Table.DescribeInvokeResult = [](uint64 Member, uint32 Index, EFinareValueKind& Kind, uint64& TypeId)
    { Kind = CommonValueStorage.Kind; TypeId = CommonValueStorage.TypeIdValue; return Member == CommonValueMember && Index == 0; };
    Table.ReadProperty = [](UObject*, FFinareObjectRegistry&, uint64 Member, FFinareValue& Value, FFinareInvocationResult& Result)
    { if (Member != CommonValueMember) return false; Value = CommonValueStorage; Result = {}; return true; };
    Table.WriteProperty = [](UObject*, FFinareObjectRegistry&, uint64 Member, const FFinareValue& Value, FFinareInvocationResult& Result)
    { if (Member != CommonValueMember) return false; CommonValueStorage = Value; Result = {}; return true; };
    Table.Invoke = [](UObject*, FFinareObjectRegistry&, uint64 Member, TConstArrayView<FFinareValue> Args,
        FFinareValue& Value, TArray<FFinareValue>& Outputs, FFinareInvocationResult& Result)
    { if (Member != CommonValueMember || Args.Num() != 1) return false; Value = Args[0]; Outputs.Reset(); Result = {}; return true; };
    return Table;
}

bool CommonValuesEqual(const FFinareValue& A, const FFinareValue& B)
{
    return A.Kind == B.Kind && A.Int8Value == B.Int8Value && A.UInt8Value == B.UInt8Value &&
        A.Int16Value == B.Int16Value && A.UInt16Value == B.UInt16Value && A.UInt32Value == B.UInt32Value &&
        A.Int64Value == B.Int64Value && A.UInt64Value == B.UInt64Value && A.TypeIdValue == B.TypeIdValue &&
        A.TypePath == B.TypePath && A.RotatorValue == B.RotatorValue && A.TransformValue.Equals(B.TransformValue);
}

struct FCapturedResult
{
    bool bCalled = false;
    bool bSuccess = false;
    EFinareValueKind ValueKind = EFinareValueKind::Void;
    bool BooleanValue = false;
    int32 Int32Value = 0;
    FString StringValue;
    TArray<uint8> ValuePayload;
    EFinareInvocationError Error = EFinareInvocationError::None;
    EFinareObjectHandleError HandleError = EFinareObjectHandleError::None;
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

void CaptureEvent(void* UserData, const FFinareNativeEventFrameV4* Frame)
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
    Result.ValueKind = static_cast<EFinareValueKind>(ValueKind);
    Result.BooleanValue = BooleanValue != 0;
    Result.Int32Value = Int32Value;
    Result.ValuePayload.Reset();
    if (StringData != nullptr && StringLength > 0)
    {
        Result.ValuePayload.Append(StringData, StringLength);
    }
    Result.StringValue = Result.ValueKind == EFinareValueKind::String ||
            Result.ValueKind == EFinareValueKind::Name ||
            Result.ValueKind == EFinareValueKind::Text
        ? Decode(StringData, StringLength)
        : FString();
    Result.Error = static_cast<EFinareInvocationError>(Error);
    Result.HandleError = static_cast<EFinareObjectHandleError>(HandleError);
    Result.Message = Decode(MessageData, MessageLength);
}

void WriteUInt64LittleEndian(TArrayView<uint8> Destination, uint64 Value)
{
    check(Destination.Num() == sizeof(uint64));
    for (int32 Index = 0; Index < Destination.Num(); ++Index)
    {
        Destination[Index] = static_cast<uint8>(Value >> (Index * 8));
    }
}

uint64 ReadUInt64LittleEndian(TConstArrayView<uint8> Source)
{
    check(Source.Num() == sizeof(uint64));
    uint64 Value = 0;
    for (int32 Index = 0; Index < Source.Num(); ++Index)
    {
        Value |= static_cast<uint64>(Source[Index]) << (Index * 8);
    }
    return Value;
}

double ReadDoubleLittleEndian(TConstArrayView<uint8> Source)
{
    return std::bit_cast<double>(ReadUInt64LittleEndian(Source));
}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareNativeTransportAbiTest,
    "Finare.Unreal.NativeTransport.Abi",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeTransportAbiTest::RunTest(const FString& Parameters)
{
    using namespace UE::Finare::NativeTransport::Tests;

    const FFinareNativeTransportV1* Transport =
        Finare_GetNativeTransportV1();
    TestNotNull(TEXT("Version-one transport table exists"), Transport);
    if (Transport == nullptr)
    {
        return false;
    }
    TestEqual(TEXT("Transport ABI version is stable"), Transport->AbiVersion, 1u);
    TestEqual(
        TEXT("Transport table size is explicit"),
        Transport->TableSize,
        static_cast<uint32>(sizeof(FFinareNativeTransportV1)));
    TestEqual(
        TEXT("Transport value size is explicit"),
        Transport->ValueSize,
        static_cast<uint32>(sizeof(FFinareNativeValueV1)));
    TestNotNull(TEXT("Read function exists"), Transport->ReadProperty);
    TestNotNull(TEXT("Write function exists"), Transport->WriteProperty);
    TestNotNull(TEXT("Invoke function exists"), Transport->Invoke);
    TestNotNull(TEXT("Context retain function exists"), Transport->RetainContext);
    TestNotNull(TEXT("Context release function exists"), Transport->ReleaseContext);

    const FFinareNativeTransportV2* FastTransport =
        Finare_GetNativeTransportV2();
    TestNotNull(TEXT("Version-two transport table exists"), FastTransport);
    if (FastTransport == nullptr)
    {
        return false;
    }
    TestEqual(TEXT("Fast transport ABI is version two"), FastTransport->AbiVersion, 2u);
    TestEqual(
        TEXT("Fast transport table size is explicit"),
        FastTransport->TableSize,
        static_cast<uint32>(sizeof(FFinareNativeTransportV2)));
    TestNotNull(TEXT("Fast read function exists"), FastTransport->ReadPropertyById);
    TestNotNull(TEXT("Fast write function exists"), FastTransport->WritePropertyById);
    TestNotNull(TEXT("Fast invoke function exists"), FastTransport->InvokeById);
    TestNotNull(TEXT("Fast transport publishes reverse event ABI"), FastTransport->EventTransport);
    TestNotNull(TEXT("Fast transport publishes Shared Data ABI"), FastTransport->SharedDataTransport);
    TestNotNull(TEXT("Fast transport publishes Gameplay Data ABI"), FastTransport->GameplayDataTransport);

    const FFinareSharedDataTransportV4* SharedDataTransport =
        Finare_GetSharedDataTransportV4();
    TestNotNull(TEXT("Version-four Shared Data transport exists"), SharedDataTransport);
    if (SharedDataTransport == nullptr)
    {
        return false;
    }
    TestEqual(
        TEXT("Shared Data ABI is version four"),
        SharedDataTransport->AbiVersion,
        UE::Finare::SharedData::AbiVersion);
    TestEqual(
        TEXT("Shared Transform row layout is fixed"),
        SharedDataTransport->TransformEntrySize,
        static_cast<uint32>(sizeof(FFinareTransformSnapshotEntryV4)));
    TestEqual(
        TEXT("Shared Transform schema is stable"),
        SharedDataTransport->TransformSchemaId,
        UE::Finare::SharedData::TransformSnapshotSchemaId);
    TestNotNull(
        TEXT("Shared Data transport acquires Transform snapshots"),
        SharedDataTransport->AcquireTransformSnapshot);

    const FFinareGameplayDataTransportV4* GameplayDataTransport =
        Finare_GetGameplayDataTransportV4();
    TestNotNull(TEXT("Version-four Gameplay Data transport exists"), GameplayDataTransport);
    if (GameplayDataTransport == nullptr)
    {
        return false;
    }
    TestEqual(
        TEXT("Gameplay command row layout is fixed"),
        GameplayDataTransport->CommandEntrySize,
        static_cast<uint32>(sizeof(FFinareCommandV4)));
    TestEqual(
        TEXT("Gameplay event row layout is fixed"),
        GameplayDataTransport->EventEntrySize,
        static_cast<uint32>(sizeof(FFinareGameplayEventV4)));
    TestEqual(
        TEXT("Batch trace request layout is fixed"),
        GameplayDataTransport->TraceRequestEntrySize,
        static_cast<uint32>(sizeof(FFinareTraceRequestV4)));
    TestEqual(
        TEXT("Batch overlap request layout is fixed"),
        GameplayDataTransport->OverlapRequestEntrySize,
        static_cast<uint32>(sizeof(FFinareOverlapRequestV1)));
    TestEqual(
        TEXT("Batch overlap result layout is fixed"),
        GameplayDataTransport->OverlapResultEntrySize,
        static_cast<uint32>(sizeof(FFinareOverlapResultV1)));
    TestNotNull(TEXT("Gameplay command begin exists"), GameplayDataTransport->BeginCommandBatch);
    TestNotNull(TEXT("Gameplay event acquisition exists"), GameplayDataTransport->AcquireEvents);
    TestNotNull(TEXT("Gameplay batch trace exists"), GameplayDataTransport->ExecuteTraceBatch);
    TestNotNull(
        TEXT("Gameplay batch overlap exists"),
        GameplayDataTransport->ExecuteOverlapBatch);

    const FFinareNativeEventTransportV4* EventTransport =
        Finare_GetNativeEventTransportV4();
    TestNotNull(TEXT("Version-four event transport exists"), EventTransport);
    if (EventTransport == nullptr)
    {
        return false;
    }
    TestEqual(TEXT("Event transport ABI is version four"), EventTransport->AbiVersion, 4u);
    TestEqual(
        TEXT("Event frame layout is fixed"),
        EventTransport->FrameSize,
        static_cast<uint32>(sizeof(FFinareNativeEventFrameV4)));
    TestEqual(
        TEXT("Event transport value layout reuses ABI v3"),
        EventTransport->ValueSize,
        static_cast<uint32>(sizeof(FFinareNativeValueV3)));
    TestNotNull(TEXT("Event subscribe function exists"), EventTransport->Subscribe);
    TestNotNull(TEXT("Event unsubscribe function exists"), EventTransport->Unsubscribe);

    TestEqual(
        TEXT("ABI v3 value layout is fixed"),
        static_cast<uint32>(sizeof(FFinareNativeValueV3)),
        48u);
    TestEqual(
        TEXT("ABI v3 call-frame layout is fixed"),
        static_cast<uint32>(sizeof(FFinareNativeCallFrameV3)),
        72u);
    TestEqual(
        TEXT("ABI v3 transport table includes the correlated-result suffix"),
        static_cast<uint32>(sizeof(FFinareNativeTransportV3)),
        96u);
    TestEqual(TEXT("ABI v3 old fixed-lane prefix is preserved"),
        static_cast<uint32>(STRUCT_OFFSET(FFinareNativeTransportV3, InvokeCorrelatedById)), 80u);
    TestEqual(TEXT("ABI v3 correlation cleanup offset is explicit"),
        static_cast<uint32>(STRUCT_OFFSET(FFinareNativeTransportV3, ReleaseCorrelatedResult)), 88u);
    TestEqual(
        TEXT("ABI v3 status byte offset is fixed"),
        static_cast<uint32>(STRUCT_OFFSET(FFinareNativeCallFrameV3, Status)),
        58u);
    TestEqual(
        TEXT("ABI v3 legacy Map kind is stable"),
        static_cast<uint8>(EFinareNativeValueKindV3::Map),
        static_cast<uint8>(24));
    const FFinareNativeTransportV3* CallFrameTransport =
        Finare_GetNativeTransportV3();
    TestNotNull(TEXT("Version-three transport table exists"), CallFrameTransport);
    if (CallFrameTransport == nullptr)
    {
        return false;
    }
    TestEqual(TEXT("Call-frame transport ABI is version three"), CallFrameTransport->AbiVersion, 3u);
    TestEqual(
        TEXT("Call-frame table size is explicit"),
        CallFrameTransport->TableSize,
        static_cast<uint32>(sizeof(FFinareNativeTransportV3)));
    TestEqual(
        TEXT("Call-frame value size is explicit"),
        CallFrameTransport->ValueSize,
        static_cast<uint32>(sizeof(FFinareNativeValueV3)));
    TestEqual(
        TEXT("Call-frame size is explicit"),
        CallFrameTransport->CallFrameSize,
        static_cast<uint32>(sizeof(FFinareNativeCallFrameV3)));
    TestTrue(
        TEXT("Call-frame transport advertises fixed scalars"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EFinareNativeCapabilitiesV3::FixedScalars)) != 0);
    TestNotNull(
        TEXT("Call-frame transport exposes the int32x2 scalar lane"),
        CallFrameTransport->InvokeInt32x2ById);
    TestNotNull(
        TEXT("Call-frame transport exposes the mixed fixed-value lane"),
        CallFrameTransport->InvokeFixedById);
    TestNotNull(TEXT("Correlated invocation entrypoint exists"), CallFrameTransport->InvokeCorrelatedById);
    TestNotNull(TEXT("Correlated cleanup entrypoint exists"), CallFrameTransport->ReleaseCorrelatedResult);
    TestTrue(TEXT("Correlated-result capability is advertised"),
        (CallFrameTransport->Capabilities & static_cast<uint64>(EFinareNativeCapabilitiesV3::CorrelatedResults)) != 0);
    TestTrue(
        TEXT("Call-frame transport advertises generation-safe object handles"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EFinareNativeCapabilitiesV3::ObjectHandles)) != 0);
    TestTrue(
        TEXT("Call-frame transport advertises UTF-8 input support"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EFinareNativeCapabilitiesV3::Utf8Text)) != 0);
    TestTrue(
        TEXT("Call-frame transport advertises staged UTF-8 results"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EFinareNativeCapabilitiesV3::Utf8Results)) != 0);
    TestTrue(
        TEXT("Call-frame transport advertises arena-backed container inputs"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EFinareNativeCapabilitiesV3::Containers)) != 0);
    TestTrue(
        TEXT("Call-frame transport advertises output and reference parameters"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EFinareNativeCapabilitiesV3::OutputAndReference)) != 0);
    TestTrue(
        TEXT("Call-frame transport advertises staged container results"),
        (CallFrameTransport->Capabilities &
            static_cast<uint64>(EFinareNativeCapabilitiesV3::ContainerResults)) != 0);
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
        FFinareObjectRegistry Registry(World);
        FFinareInvocationBridge Bridge(Registry);
        FFinareNativeBridgeContext* Context =
            new FFinareNativeBridgeContext(Bridge);
        TestTrue(
            TEXT("Live context can be retained"),
            Transport->RetainContext(Context) != 0);
        Transport->ReleaseContext(Context);
        FFinareObjectHandleResult HandleResult;
        UFinareComponent* Component = NewObject<UFinareComponent>(World);
        const FFinareObjectHandle Handle = Registry.RegisterObject(Component, HandleResult);
        TestTrue(TEXT("Fixture registration succeeds"), HandleResult.IsSuccess());

        const FTCHARToUTF8 StartPath(
            TEXT("/Script/FinareRuntime.FinareComponent:bStartAutomatically"));
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
            Captured.ValueKind == EFinareValueKind::Boolean &&
                Captured.BooleanValue);

        const FTCHARToUTF8 AssetPath(
            TEXT("/Script/FinareRuntime.FinareComponent:ScriptAssetPath"));
        const FTCHARToUTF8 AssetValue(TEXT("/Game/Scripts/AbiFixture"));
        FFinareNativeValueV1 NativeString;
        NativeString.Kind = static_cast<uint8>(EFinareValueKind::String);
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

        UFinareInteropFixture* InteropFixture =
            NewObject<UFinareInteropFixture>(World);
        InteropFixture->ObjectValue = InteropFixture;
        InteropFixture->ClassValue = UFinareComponent::StaticClass();
        const FFinareObjectHandle CommonValueHandle =
            Registry.RegisterObject(InteropFixture, HandleResult);
        TestTrue(TEXT("Common-value fixture registration succeeds"), HandleResult.IsSuccess());

        const FName CodecOwner(TEXT("FinareCommonValueTransportTests"));
        if (TestTrue(TEXT("Register isolated common-value transport fixture"),
            FFinareGeneratedThunkRegistry::RegisterTable(CodecOwner, CommonValueTable())))
        {
            ON_SCOPE_EXIT { FFinareGeneratedThunkRegistry::UnregisterTable(CodecOwner); CommonValueStorage = {}; };
            const TArray<FFinareValue> Values = {
                FFinareValue::FromInt8(MIN_int8), FFinareValue::FromInt8(MAX_int8),
                FFinareValue::FromUInt8(MAX_uint8), FFinareValue::FromInt16(MIN_int16),
                FFinareValue::FromInt16(MAX_int16), FFinareValue::FromUInt16(MAX_uint16),
                FFinareValue::FromUInt32(MAX_uint32), FFinareValue::FromInt64(MIN_int64),
                FFinareValue::FromInt64(MAX_int64), FFinareValue::FromUInt64(MAX_uint64),
                FFinareValue::FromEnum(-1, TEXT("/Script/FinareRuntime.EFinareInteropMode")),
                FFinareValue::FromEnumTypeId(2, FinareMemberId("/Script/FinareRuntime.EFinareInteropMode")),
                FFinareValue::FromRotator(FRotator(-90, 123.125, 360)),
                FFinareValue::FromTransform(FTransform(FQuat(.5, .5, .5, .5), FVector(11, -22, 33), FVector(2, 3, 4))),
            };
            for (const FFinareValue& Expected : Values)
            {
                CommonValueStorage = Expected;
                Captured = {};
                FastTransport->ReadPropertyById(Context, CommonValueHandle.Value, CommonValueMember, &Capture, &Captured);
                TestTrue(TEXT("Common-value encoder reaches legacy callback"), Captured.bSuccess);
                FFinareNativeValueV1 Native;
                Native.Kind = static_cast<uint8>(Captured.ValueKind);
                Native.Int32Value = Captured.Int32Value;
                TArray<uint8> Payload = Captured.ValuePayload;
                Native.StringData = Payload.GetData();
                Native.StringLength = Payload.Num();
                CommonValueStorage = {};
                Captured = {};
                FastTransport->WritePropertyById(Context, CommonValueHandle.Value, CommonValueMember, &Native, &Capture, &Captured);
                TestTrue(TEXT("Common-value legacy decode succeeds"), Captured.bSuccess);
                TestTrue(TEXT("Common-value round-trip retains exact integer/type identity and component order"), CommonValuesEqual(CommonValueStorage, Expected));
                Captured = {};
                FastTransport->InvokeById(Context, CommonValueHandle.Value, CommonValueMember, &Native, 1, &Capture, &Captured);
                TestTrue(TEXT("Common-value function input/return succeeds"), Captured.bSuccess);
                TestEqual(TEXT("Common-value function retains kind"), Captured.ValueKind, Expected.Kind);
                TestTrue(TEXT("Common-value function retains payload bytes"), Captured.ValuePayload == Payload);
                TestEqual(TEXT("Common-value function retains scalar bits"), Captured.Int32Value, Native.Int32Value);
            }
            const EFinareValueKind InvalidKinds[] = {EFinareValueKind::Int8, EFinareValueKind::UInt8,
                EFinareValueKind::Int16, EFinareValueKind::UInt16, EFinareValueKind::Int64,
                EFinareValueKind::UInt64, EFinareValueKind::Enum, EFinareValueKind::Rotator, EFinareValueKind::Transform};
            for (EFinareValueKind Kind : InvalidKinds)
            {
                FFinareNativeValueV1 Invalid;
                Invalid.Kind = static_cast<uint8>(Kind);
                Invalid.Int32Value = MAX_int32;
                Invalid.StringLength = 1;
                CommonValueStorage = {};
                Captured = {};
                FastTransport->WritePropertyById(Context, CommonValueHandle.Value, CommonValueMember, &Invalid, &Capture, &Captured);
                TestFalse(TEXT("Invalid common-value payload is rejected"), Captured.bSuccess);
                TestEqual(TEXT("Failed decode does not dispatch a write"), CommonValueStorage.Kind, EFinareValueKind::Void);
            }
        }

        const uint64 CommonValueFastCalls = Bridge.GetFastInvocationCount();
        const FTCHARToUTF8 FloatPath(TEXT("/Script/FinareRuntime.FinareInteropFixture:FloatValue"));
        const FTCHARToUTF8 DoublePath(TEXT("/Script/FinareRuntime.FinareInteropFixture:DoubleValue"));
        FFinareNativeValueV1 NativeFloat;
        NativeFloat.Kind = static_cast<uint8>(EFinareValueKind::Float32);
        NativeFloat.Int32Value = std::bit_cast<int32>(-0.0f);
        Captured = {};
        Transport->WriteProperty(Context, CommonValueHandle.Value, reinterpret_cast<const uint8*>(FloatPath.Get()), FloatPath.Length(), &NativeFloat, &Capture, &Captured);
        TestTrue(TEXT("Legacy float property write succeeds"), Captured.bSuccess);
        TestEqual(TEXT("Legacy float write preserves signed zero"), std::bit_cast<int32>(InteropFixture->FloatValue), NativeFloat.Int32Value);
        Captured = {};
        Transport->ReadProperty(Context, CommonValueHandle.Value, reinterpret_cast<const uint8*>(FloatPath.Get()), FloatPath.Length(), &Capture, &Captured);
        TestTrue(TEXT("Legacy float property read succeeds"), Captured.bSuccess);
        TestEqual(TEXT("Legacy float result kind"), Captured.ValueKind, EFinareValueKind::Float32);
        TestEqual(TEXT("Legacy float result preserves signed zero"), Captured.Int32Value, NativeFloat.Int32Value);
        const FTCHARToUTF8 FloatCall(TEXT("/Script/FinareRuntime.FinareInteropFixture:FixedArity1"));
        NativeFloat.Int32Value = std::bit_cast<int32>(2.25f);
        Captured = {};
        Transport->Invoke(Context, CommonValueHandle.Value, reinterpret_cast<const uint8*>(FloatCall.Get()), FloatCall.Length(), &NativeFloat, 1, &Capture, &Captured);
        TestTrue(TEXT("Legacy function accepts float input"), Captured.bSuccess);
        TestEqual(TEXT("Legacy function returns float result"), std::bit_cast<float>(Captured.Int32Value), 3.75f);

        uint8 DoublePayload[8];
        constexpr double DoubleValue = -12345.12500000001;
        WriteUInt64LittleEndian(MakeArrayView(DoublePayload), std::bit_cast<uint64>(DoubleValue));
        FFinareNativeValueV1 NativeDouble;
        NativeDouble.Kind = static_cast<uint8>(EFinareValueKind::Float64);
        NativeDouble.StringData = DoublePayload;
        NativeDouble.StringLength = sizeof(DoublePayload);
        Captured = {};
        Transport->WriteProperty(Context, CommonValueHandle.Value, reinterpret_cast<const uint8*>(DoublePath.Get()), DoublePath.Length(), &NativeDouble, &Capture, &Captured);
        TestTrue(TEXT("Legacy double property write succeeds"), Captured.bSuccess);
        TestEqual(TEXT("Legacy double retains binary64 precision"), InteropFixture->DoubleValue, DoubleValue);
        Captured = {};
        Transport->ReadProperty(Context, CommonValueHandle.Value, reinterpret_cast<const uint8*>(DoublePath.Get()), DoublePath.Length(), &Capture, &Captured);
        TestTrue(TEXT("Legacy double property read succeeds"), Captured.bSuccess);
        TestEqual(TEXT("Legacy double result kind"), Captured.ValueKind, EFinareValueKind::Float64);
        if (TestEqual(TEXT("Legacy double result owns eight payload bytes"), Captured.ValuePayload.Num(), 8))
            TestEqual(TEXT("Legacy double round-trip"), ReadDoubleLittleEndian(Captured.ValuePayload), DoubleValue);
        for (int32 InvalidLength : {0, 7, 9})
        {
            NativeDouble.StringLength = InvalidLength;
            Captured = {};
            Transport->WriteProperty(Context, CommonValueHandle.Value, reinterpret_cast<const uint8*>(DoublePath.Get()), DoublePath.Length(), &NativeDouble, &Capture, &Captured);
            TestFalse(TEXT("Malformed double payload is rejected"), Captured.bSuccess);
            TestEqual(TEXT("Malformed payload leaves UObject state intact"), InteropFixture->DoubleValue, DoubleValue);
        }
        NativeDouble.StringLength = sizeof(DoublePayload);
        NativeDouble.StringData = nullptr;
        Captured = {};
        Transport->WriteProperty(Context, CommonValueHandle.Value, reinterpret_cast<const uint8*>(DoublePath.Get()), DoublePath.Length(), &NativeDouble, &Capture, &Captured);
        TestFalse(TEXT("Null double payload is rejected"), Captured.bSuccess);

        const FTCHARToUTF8 VectorPath(
            TEXT("/Script/FinareRuntime.FinareInteropFixture:VectorValue"));
        Captured = {};
        Transport->ReadProperty(
            Context,
            CommonValueHandle.Value,
            reinterpret_cast<const uint8*>(VectorPath.Get()),
            VectorPath.Length(),
            &Capture,
            &Captured);
        TestTrue(TEXT("FVector read succeeds through legacy ABI"), Captured.bSuccess);
        TestEqual(TEXT("FVector payload has three binary64 cells"), Captured.ValuePayload.Num(), 24);
        if (Captured.ValuePayload.Num() == 24)
        {
            TestEqual(
                TEXT("FVector X crosses the little-endian payload"),
                ReadDoubleLittleEndian(MakeArrayView(Captured.ValuePayload.GetData(), 8)),
                1.0);
            TestEqual(
                TEXT("FVector Y crosses the little-endian payload"),
                ReadDoubleLittleEndian(MakeArrayView(Captured.ValuePayload.GetData() + 8, 8)),
                2.0);
            TestEqual(
                TEXT("FVector Z crosses the little-endian payload"),
                ReadDoubleLittleEndian(MakeArrayView(Captured.ValuePayload.GetData() + 16, 8)),
                3.0);
        }

        const FTCHARToUTF8 ObjectPath(
            TEXT("/Script/FinareRuntime.FinareInteropFixture:ObjectValue"));
        Captured = {};
        Transport->ReadProperty(
            Context,
            CommonValueHandle.Value,
            reinterpret_cast<const uint8*>(ObjectPath.Get()),
            ObjectPath.Length(),
            &Capture,
            &Captured);
        TestTrue(TEXT("UObject handle read succeeds through legacy ABI"), Captured.bSuccess);
        TestEqual(TEXT("UObject handle payload is fixed-width"), Captured.ValuePayload.Num(), 8);
        uint64 ObjectHandleValue = 0;
        if (Captured.ValuePayload.Num() == 8)
        {
            ObjectHandleValue = ReadUInt64LittleEndian(Captured.ValuePayload);
            FFinareObjectHandleResult ResolveResult;
            TestEqual(
                TEXT("UObject payload resolves through the registry"),
                Registry.ResolveObject({ObjectHandleValue}, ResolveResult),
                static_cast<UObject*>(InteropFixture));
        }

        const FTCHARToUTF8 ClassPath(
            TEXT("/Script/FinareRuntime.FinareInteropFixture:ClassValue"));
        Captured = {};
        Transport->ReadProperty(
            Context,
            CommonValueHandle.Value,
            reinterpret_cast<const uint8*>(ClassPath.Get()),
            ClassPath.Length(),
            &Capture,
            &Captured);
        TestTrue(TEXT("UClass handle read succeeds through legacy ABI"), Captured.bSuccess);
        TestEqual(TEXT("UClass handle payload is fixed-width"), Captured.ValuePayload.Num(), 8);
        uint64 ClassHandleValue = 0;
        if (Captured.ValuePayload.Num() == 8)
        {
            ClassHandleValue = ReadUInt64LittleEndian(Captured.ValuePayload);
            FFinareObjectHandleResult ResolveResult;
            TestEqual(
                TEXT("UClass payload resolves through the registry"),
                Registry.ResolveObject({ClassHandleValue}, ResolveResult),
                static_cast<UObject*>(UFinareComponent::StaticClass()));
        }

        uint8 VectorWritePayload[24] = {};
        WriteUInt64LittleEndian(
            MakeArrayView(VectorWritePayload, 8),
            std::bit_cast<uint64>(4.5));
        WriteUInt64LittleEndian(
            MakeArrayView(VectorWritePayload + 8, 8),
            std::bit_cast<uint64>(-5.25));
        WriteUInt64LittleEndian(
            MakeArrayView(VectorWritePayload + 16, 8),
            std::bit_cast<uint64>(6.75));
        FFinareNativeValueV1 NativeVector;
        NativeVector.Kind = static_cast<uint8>(EFinareValueKind::Vector);
        NativeVector.StringData = VectorWritePayload;
        NativeVector.StringLength = sizeof(VectorWritePayload);
        Captured = {};
        Transport->WriteProperty(
            Context,
            CommonValueHandle.Value,
            reinterpret_cast<const uint8*>(VectorPath.Get()),
            VectorPath.Length(),
            &NativeVector,
            &Capture,
            &Captured);
        TestTrue(TEXT("FVector write succeeds through legacy ABI"), Captured.bSuccess);
        TestTrue(
            TEXT("FVector binary payload reaches UObject storage"),
            InteropFixture->VectorValue.Equals(FVector(4.5, -5.25, 6.75)));

        uint8 ObjectWritePayload[8] = {};
        WriteUInt64LittleEndian(MakeArrayView(ObjectWritePayload), Handle.Value);
        FFinareNativeValueV1 NativeObject;
        NativeObject.Kind = static_cast<uint8>(EFinareValueKind::Object);
        NativeObject.StringData = ObjectWritePayload;
        NativeObject.StringLength = sizeof(ObjectWritePayload);
        Captured = {};
        Transport->WriteProperty(
            Context,
            CommonValueHandle.Value,
            reinterpret_cast<const uint8*>(ObjectPath.Get()),
            ObjectPath.Length(),
            &NativeObject,
            &Capture,
            &Captured);
        TestTrue(TEXT("UObject handle write succeeds through legacy ABI"), Captured.bSuccess);
        TestEqual(
            TEXT("UObject handle payload reaches UObject storage"),
            InteropFixture->ObjectValue.Get(),
            static_cast<UObject*>(Component));

        uint8 ClassWritePayload[8] = {};
        WriteUInt64LittleEndian(MakeArrayView(ClassWritePayload), ClassHandleValue);
        FFinareNativeValueV1 NativeClass;
        NativeClass.Kind = static_cast<uint8>(EFinareValueKind::Class);
        NativeClass.StringData = ClassWritePayload;
        NativeClass.StringLength = sizeof(ClassWritePayload);
        Captured = {};
        Transport->WriteProperty(
            Context,
            CommonValueHandle.Value,
            reinterpret_cast<const uint8*>(ClassPath.Get()),
            ClassPath.Length(),
            &NativeClass,
            &Capture,
            &Captured);
        TestTrue(TEXT("UClass handle write succeeds through legacy ABI"), Captured.bSuccess);
        TestEqual(
            TEXT("UClass handle payload reaches UObject storage"),
            InteropFixture->ClassValue.Get(),
            UFinareComponent::StaticClass());

        const FTCHARToUTF8 AddPath(
            TEXT("/Script/FinareRuntime.FinareComponent:Add"));
        FFinareNativeValueV1 AddArguments[2];
        AddArguments[0].Kind = static_cast<uint8>(EFinareValueKind::Int32);
        AddArguments[0].Int32Value = 20;
        AddArguments[1].Kind = static_cast<uint8>(EFinareValueKind::Int32);
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
            Captured.ValueKind == EFinareValueKind::Int32 &&
                Captured.Int32Value == 42);

        constexpr uint64 StartId = FinareMemberId(
            "/Script/FinareRuntime.FinareComponent:bStartAutomatically");
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

        constexpr uint64 AssetId = FinareMemberId(
            "/Script/FinareRuntime.FinareComponent:ScriptAssetPath");
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

        constexpr uint64 AddId = FinareMemberId(
            "/Script/FinareRuntime.FinareComponent:Add");
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
            Bridge.GetFastInvocationCount() - CommonValueFastCalls,
            3ull);

        FFinareNativeValueV3 V3Arguments[2];
        V3Arguments[0].Kind = EFinareNativeValueKindV3::Int32;
        V3Arguments[0].Payload0 = 20;
        V3Arguments[1].Kind = EFinareNativeValueKindV3::Int32;
        V3Arguments[1].Payload0 = 22;
        FFinareNativeValueV3 V3Result;
        V3Result.Kind = EFinareNativeValueKindV3::Int32;
        FFinareNativeCallFrameV3 V3Frame;
        V3Frame.Arguments = V3Arguments;
        V3Frame.ArgumentCount = 2;
        V3Frame.Results = &V3Result;
        V3Frame.ResultCapacity = 1;
        V3Frame.ResultCount = 1;
        CallFrameTransport->InvokeById(Context, Handle.Value, AddId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 generated Add succeeds"),
            V3Frame.Status == EFinareNativeCallStatusV3::Success);
        TestTrue(
            TEXT("ABI v3 int32 result stays inline"),
            V3Frame.ResultCount == 1 &&
                V3Result.Kind == EFinareNativeValueKindV3::Int32 &&
                static_cast<int32>(V3Result.Payload0) == 42);

        V3Result = {};
        V3Result.Kind = EFinareNativeValueKindV3::Int32;
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
            FixedStatus == static_cast<uint8>(EFinareNativeCallStatusV3::Success) &&
                FixedInvocationError == 0 &&
                FixedHandleError == 0 &&
                V3Result.Kind == EFinareNativeValueKindV3::Int32 &&
                static_cast<int32>(V3Result.Payload0) == 42);

        constexpr uint64 ReloadId = FinareMemberId(
            "/Script/FinareRuntime.FinareComponent:RequestReload");
        V3Result = {};
        V3Result.Kind = EFinareNativeValueKindV3::Int32;
        V3Frame = {};
        V3Frame.Results = &V3Result;
        V3Frame.ResultCapacity = 1;
        V3Frame.ResultCount = 1;
        CallFrameTransport->InvokeById(Context, Handle.Value, ReloadId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 rejects a wrong result descriptor before dispatch"),
            V3Frame.Status == EFinareNativeCallStatusV3::InvalidFrame);
        TestFalse(
            TEXT("Rejected ABI v3 frame does not execute a side effect"),
            Component->ConsumeReloadRequest());

        UFinareInteropFixture* Interop = NewObject<UFinareInteropFixture>(World);
        const FFinareObjectHandle InteropHandle =
            Registry.RegisterObject(Interop, HandleResult);
        const auto EncodeFloat = [](float Value)
        {
            FFinareNativeValueV3 Cell;
            Cell.Kind = EFinareNativeValueKindV3::Float32;
            uint32 Bits = 0;
            FMemory::Memcpy(&Bits, &Value, sizeof(float));
            Cell.Payload0 = Bits;
            return Cell;
        };
        const auto EncodeVector = [](const FVector& Value)
        {
            FFinareNativeValueV3 Cell;
            Cell.Kind = EFinareNativeValueKindV3::Vector3;
            const double Components[] = {Value.X, Value.Y, Value.Z};
            FMemory::Memcpy(&Cell.Payload0, &Components[0], sizeof(double));
            FMemory::Memcpy(&Cell.Payload1, &Components[1], sizeof(double));
            FMemory::Memcpy(&Cell.Payload2, &Components[2], sizeof(double));
            return Cell;
        };
        const auto DecodeFloat = [](const FFinareNativeValueV3& Cell)
        {
            float Value = 0.0f;
            const uint32 Bits = static_cast<uint32>(Cell.Payload0);
            FMemory::Memcpy(&Value, &Bits, sizeof(float));
            return Value;
        };
        const auto DecodeVector = [](const FFinareNativeValueV3& Cell)
        {
            double Components[3] = {};
            FMemory::Memcpy(&Components[0], &Cell.Payload0, sizeof(double));
            FMemory::Memcpy(&Components[1], &Cell.Payload1, sizeof(double));
            FMemory::Memcpy(&Components[2], &Cell.Payload2, sizeof(double));
            return FVector(Components[0], Components[1], Components[2]);
        };
        const auto InvokeFixed = [&](
            uint64 MemberId,
            const FFinareNativeValueV3* Arguments,
            uint32 ArgumentCount,
            FFinareNativeValueV3& Result)
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
                static_cast<uint8>(EFinareNativeCallStatusV3::Success);
        };

        FFinareNativeValueV3 FixedResult;
        FixedResult.Kind = EFinareNativeValueKindV3::Int32;
        TestTrue(
            TEXT("Fixed arity 0 int32 signature executes"),
            InvokeFixed(
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:FixedArity0"),
                nullptr,
                0,
                FixedResult) &&
                static_cast<int32>(FixedResult.Payload0) == 17);

        FFinareNativeValueV3 FixedArguments[4];
        FixedArguments[0] = EncodeFloat(2.5f);
        FixedResult = {};
        FixedResult.Kind = EFinareNativeValueKindV3::Float32;
        TestTrue(
            TEXT("Fixed arity 1 float signature executes"),
            InvokeFixed(
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:FixedArity1"),
                FixedArguments,
                1,
                FixedResult) &&
                FMath::IsNearlyEqual(DecodeFloat(FixedResult), 4.0f));

        FixedArguments[0] = {};
        FixedArguments[0].Kind = EFinareNativeValueKindV3::Boolean;
        FixedArguments[0].Payload0 = 1;
        FixedArguments[1] = {};
        FixedArguments[1].Kind = EFinareNativeValueKindV3::Int32;
        FixedArguments[1].Payload0 = 7;
        FixedResult = {};
        FixedResult.Kind = EFinareNativeValueKindV3::Boolean;
        TestTrue(
            TEXT("Fixed arity 2 bool/int32 signature executes"),
            InvokeFixed(
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:FixedArity2"),
                FixedArguments,
                2,
                FixedResult) &&
                FixedResult.Payload0 == 1);

        FixedArguments[0] = {};
        FixedArguments[0].Kind = EFinareNativeValueKindV3::Int32;
        FixedArguments[0].Payload0 = 9;
        FixedArguments[1] = {};
        FixedArguments[1].Kind = EFinareNativeValueKindV3::Int32;
        FixedArguments[1].Payload0 = 4;
        FixedResult = {};
        FixedResult.Kind = EFinareNativeValueKindV3::Int32;
        TestTrue(
            TEXT("Reflected default-parameter function keeps its generated native signature"),
            InvokeFixed(
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:AddWithDefault"),
                FixedArguments,
                2,
                FixedResult) &&
                static_cast<int32>(FixedResult.Payload0) == 13);

        FixedArguments[0] = {};
        FixedArguments[0].Kind = EFinareNativeValueKindV3::Object;
        FixedArguments[0].Payload0 = InteropHandle.Value;
        FixedArguments[1] = {};
        FixedArguments[1].Kind = EFinareNativeValueKindV3::Boolean;
        FixedArguments[1].Payload0 = 1;
        FixedArguments[2] = EncodeFloat(1.0f);
        FixedResult = {};
        FixedResult.Kind = EFinareNativeValueKindV3::Object;
        TestTrue(
            TEXT("Fixed arity 3 object-handle signature executes"),
            InvokeFixed(
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:FixedArity3"),
                FixedArguments,
                3,
                FixedResult) &&
                FixedResult.Payload0 == InteropHandle.Value);

        FixedArguments[0] = EncodeVector(FVector(1.0, 2.0, 3.0));
        FixedArguments[1] = {};
        FixedArguments[1].Kind = EFinareNativeValueKindV3::Int32;
        FixedArguments[1].Payload0 = 2;
        FixedArguments[2] = EncodeFloat(3.0f);
        FixedArguments[3] = {};
        FixedArguments[3].Kind = EFinareNativeValueKindV3::Boolean;
        FixedArguments[3].Payload0 = 1;
        FixedResult = {};
        FixedResult.Kind = EFinareNativeValueKindV3::Vector3;
        TestTrue(
            TEXT("Fixed arity 4 FVector mixed signature executes"),
            InvokeFixed(
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:FixedArity4"),
                FixedArguments,
                4,
                FixedResult) &&
                DecodeVector(FixedResult).Equals(FVector(7.0, 8.0, 9.0)));

        constexpr uint64 InteropChangedId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:OnInteropChanged");
        const FTCHARToUTF8 InteropChangedPath(
            TEXT("/Script/FinareRuntime.FinareInteropFixture:OnInteropChanged"));
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
        constexpr uint64 ModeId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:Mode");
        constexpr uint64 ModeTypeId = FinareMemberId(
            "/Script/FinareRuntime.EFinareInteropMode");
        FFinareNativeValueV3 EnumArgument;
        EnumArgument.Kind = EFinareNativeValueKindV3::Enum;
        EnumArgument.TypeId = ModeTypeId;
        EnumArgument.Payload0 = static_cast<uint64>(EFinareInteropMode::Active);
        V3Frame = {};
        V3Frame.Arguments = &EnumArgument;
        V3Frame.ArgumentCount = 1;
        CallFrameTransport->WritePropertyById(
            Context, InteropHandle.Value, ModeId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 typed enum write succeeds"),
            V3Frame.Status == EFinareNativeCallStatusV3::Success &&
                Interop->Mode == EFinareInteropMode::Active);

        V3Result = {};
        V3Result.Kind = EFinareNativeValueKindV3::Enum;
        V3Result.TypeId = ModeTypeId;
        V3Frame = {};
        V3Frame.Results = &V3Result;
        V3Frame.ResultCapacity = 1;
        V3Frame.ResultCount = 1;
        CallFrameTransport->ReadPropertyById(
            Context, InteropHandle.Value, ModeId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 typed enum read succeeds"),
            V3Frame.Status == EFinareNativeCallStatusV3::Success &&
                V3Result.TypeId == ModeTypeId &&
                V3Result.Payload0 == static_cast<uint64>(EFinareInteropMode::Active));

        UObject* ObjectValue = NewObject<UFinareInteropFixture>(World);
        const FFinareObjectHandle ObjectValueHandle =
            Registry.RegisterObject(ObjectValue, HandleResult);
        TestTrue(TEXT("ABI v3 object value registration succeeds"), HandleResult.IsSuccess());
        constexpr uint64 ObjectValueId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:ObjectValue");
        constexpr uint64 EchoObjectId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:EchoObject");
        FFinareNativeValueV3 ObjectArgument;
        ObjectArgument.Kind = EFinareNativeValueKindV3::Object;
        ObjectArgument.Payload0 = ObjectValueHandle.Value;
        V3Frame = {};
        V3Frame.Arguments = &ObjectArgument;
        V3Frame.ArgumentCount = 1;
        CallFrameTransport->WritePropertyById(
            Context, InteropHandle.Value, ObjectValueId, &V3Frame);
        TestTrue(
            TEXT("ABI v3 object-handle write resolves through the World registry"),
            V3Frame.Status == EFinareNativeCallStatusV3::Success &&
                Interop->ObjectValue == ObjectValue);

        V3Result = {};
        V3Result.Kind = EFinareNativeValueKindV3::Object;
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
            V3Frame.Status == EFinareNativeCallStatusV3::Success &&
                V3Result.Kind == EFinareNativeValueKindV3::Object &&
                V3Result.Payload0 == ObjectValueHandle.Value);

        for (bool bNullNativeObject : {false, true})
        {
            auto NativeArgument = ObjectArgument;
            NativeArgument.Kind = EFinareNativeValueKindV3::NativeObject;
            NativeArgument.TypeId = FinareMemberId("/Native/FinareRuntime.NativeCounter");
            NativeArgument.Payload0 = bNullNativeObject ? 0 : ObjectValueHandle.Value;
            NativeArgument.Flags = bNullNativeObject ? EFinareNativeValueFlagsV3::Null : EFinareNativeValueFlagsV3::None;
            V3Frame = {};
            V3Frame.Arguments = &NativeArgument;
            V3Frame.ArgumentCount = 1;
            CallFrameTransport->WritePropertyById(Context, InteropHandle.Value, ObjectValueId, &V3Frame);
            TestTrue(TEXT("Native object tag cannot enter reflected property lane"), V3Frame.Status != EFinareNativeCallStatusV3::Success);
            TestTrue(TEXT("Rejected native object tag preserves UObject property"), Interop->ObjectValue == ObjectValue);
            FFinareNativeValueV3 ObjectResult;
            ObjectResult.Kind = EFinareNativeValueKindV3::Object;
            TestFalse(TEXT("Native object tag cannot enter reflected fixed-call lane"), InvokeFixed(EchoObjectId, &NativeArgument, 1, ObjectResult));
        }

        UClass* ClassValue = UFinareInteropFixture::StaticClass();
        const FFinareObjectHandle ClassValueHandle =
            Registry.RegisterObject(ClassValue, HandleResult);
        TestTrue(TEXT("ABI v3 class value registration succeeds"), HandleResult.IsSuccess());
        constexpr uint64 EchoClassId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:EchoClass");
        FFinareNativeValueV3 ClassArgument;
        ClassArgument.Kind = EFinareNativeValueKindV3::Class;
        ClassArgument.Payload0 = ClassValueHandle.Value;
        V3Result = {};
        V3Result.Kind = EFinareNativeValueKindV3::Class;
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
            V3Frame.Status == EFinareNativeCallStatusV3::Success &&
                V3Result.Kind == EFinareNativeValueKindV3::Class &&
                V3Result.Payload0 == ClassValueHandle.Value);

        const auto WriteSoftPath = [&](uint64 PropertyId, const FString& Path)
        {
            const FTCHARToUTF8 EncodedPath(*Path);
            TArray<uint8> Arena;
            Arena.Append(
                reinterpret_cast<const uint8*>(EncodedPath.Get()),
                EncodedPath.Length());
            FFinareNativeValueV3 SoftArgument;
            SoftArgument.Kind = EFinareNativeValueKindV3::String;
            SoftArgument.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
            SoftArgument.Payload1 = Arena.Num();
            FFinareNativeCallFrameV3 SoftFrame;
            SoftFrame.Arguments = &SoftArgument;
            SoftFrame.ArgumentCount = 1;
            SoftFrame.ByteBuffer = Arena.GetData();
            SoftFrame.ByteCapacity = Arena.Num();
            SoftFrame.ByteCount = Arena.Num();
            CallFrameTransport->WritePropertyById(
                Context, InteropHandle.Value, PropertyId, &SoftFrame);
            return SoftFrame.Status == EFinareNativeCallStatusV3::Success;
        };
        const FString ExpectedSoftObjectPath =
            TEXT("/Game/UnifiedScript/M7SoftObject.M7SoftObject");
        TestTrue(
            TEXT("ABI v3 writes a generated soft-object path"),
            WriteSoftPath(
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:SoftObjectValue"),
                ExpectedSoftObjectPath) &&
                Interop->SoftObjectValue.ToSoftObjectPath().ToString() ==
                    ExpectedSoftObjectPath);
        const FString ExpectedSoftClassPath =
            TEXT("/Script/FinareRuntime.FinareInteropFixture");
        TestTrue(
            TEXT("ABI v3 writes a generated soft-class path"),
            WriteSoftPath(
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:SoftClassValue"),
                ExpectedSoftClassPath) &&
                Interop->SoftClassValue.ToSoftObjectPath().ToString() ==
                    ExpectedSoftClassPath);

        constexpr uint64 StringValueId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:StringValue");
        constexpr uint64 StringLengthId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:StringLength");
        const FString ExpectedString = TEXT("可视化脚本");
        const FTCHARToUTF8 Utf8String(*ExpectedString);
        TArray<uint8> Utf8Arena;
        Utf8Arena.Append(
            reinterpret_cast<const uint8*>(Utf8String.Get()),
            Utf8String.Length());
        FFinareNativeValueV3 StringArgument;
        StringArgument.Kind = EFinareNativeValueKindV3::String;
        StringArgument.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
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
            V3Frame.Status == EFinareNativeCallStatusV3::Success &&
                Interop->StringValue == ExpectedString);

        V3Result = {};
        V3Result.Kind = EFinareNativeValueKindV3::Int32;
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
            V3Frame.Status == EFinareNativeCallStatusV3::Success &&
                V3Result.Kind == EFinareNativeValueKindV3::Int32 &&
                static_cast<int32>(V3Result.Payload0) == ExpectedString.Len());

        V3Result = {};
        V3Result.Kind = EFinareNativeValueKindV3::String;
        V3Result.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
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
        if (V3Frame.Status == EFinareNativeCallStatusV3::Success &&
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
            V3Frame.Status == EFinareNativeCallStatusV3::Success &&
                DecodedStringResult == ExpectedString);

        auto InvokeInt32Container = [&](
            EFinareNativeValueKindV3 Kind,
            uint64 TypeId,
            uint64 FunctionId,
            TConstArrayView<int32> FlattenedValues,
            uint32 EntryCount,
            int32 ExpectedResult)
        {
            TArray<FFinareNativeValueV3> Cells;
            Cells.SetNum(FlattenedValues.Num());
            for (int32 Index = 0; Index < FlattenedValues.Num(); ++Index)
            {
                Cells[Index].Kind = EFinareNativeValueKindV3::Int32;
                Cells[Index].Payload0 = static_cast<uint64>(FlattenedValues[Index]);
            }
            TArray<uint8> Arena;
            Arena.SetNumUninitialized(Cells.Num() * sizeof(FFinareNativeValueV3));
            FMemory::Memcpy(Arena.GetData(), Cells.GetData(), Arena.Num());
            FFinareNativeValueV3 ContainerArgument;
            ContainerArgument.Kind = Kind;
            ContainerArgument.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
            ContainerArgument.TypeId = TypeId;
            ContainerArgument.Payload1 = Arena.Num();
            ContainerArgument.Payload2 = EntryCount;
            ContainerArgument.Payload3 = sizeof(FFinareNativeValueV3);
            FFinareNativeValueV3 ContainerResult;
            ContainerResult.Kind = EFinareNativeValueKindV3::Int32;
            FFinareNativeCallFrameV3 ContainerFrame;
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
            return ContainerFrame.Status == EFinareNativeCallStatusV3::Success &&
                ContainerResult.Kind == EFinareNativeValueKindV3::Int32 &&
                static_cast<int32>(ContainerResult.Payload0) == ExpectedResult;
        };

        const TArray<int32> ArrayValues = {1, 2, 3};
        TestTrue(
            TEXT("ABI v3 invokes a generated TArray input thunk"),
            InvokeInt32Container(
                EFinareNativeValueKindV3::Array,
                FinareMemberId("TArray<int32>"),
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:SumArray"),
                ArrayValues,
                ArrayValues.Num(),
                6));
        const TArray<int32> SetValues = {1, 2, 3};
        TestTrue(
            TEXT("ABI v3 invokes a generated TSet input thunk"),
            InvokeInt32Container(
                EFinareNativeValueKindV3::Set,
                FinareMemberId("TSet<int32>"),
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:CountSet"),
                SetValues,
                SetValues.Num(),
                3));
        const TArray<int32> MapValues = {1, 2, 3, 4};
        TestTrue(
            TEXT("ABI v3 invokes a generated TMap input thunk"),
            InvokeInt32Container(
                EFinareNativeValueKindV3::Map,
                FinareMemberId("TMap<int32,int32>"),
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:SumMap"),
                MapValues,
                MapValues.Num() / 2,
                10));

        auto InvokeEchoContainer = [&Bridge, CallFrameTransport, Context, InteropHandle](
            EFinareNativeValueKindV3 Kind,
            uint64 TypeId,
            uint64 FunctionId,
            TConstArrayView<int32> FlattenedValues,
            uint32 EntryCount,
            int32 ExpectedSum)
        {
            TArray<FFinareNativeValueV3> Cells;
            Cells.SetNum(FlattenedValues.Num());
            for (int32 Index = 0; Index < FlattenedValues.Num(); ++Index)
            {
                Cells[Index].Kind = EFinareNativeValueKindV3::Int32;
                Cells[Index].Payload0 = static_cast<uint64>(FlattenedValues[Index]);
            }
            const uint32 InputByteCount =
                Cells.Num() * sizeof(FFinareNativeValueV3);
            TArray<uint8> Arena;
            Arena.SetNumUninitialized(InputByteCount);
            FMemory::Memcpy(Arena.GetData(), Cells.GetData(), InputByteCount);
            FFinareNativeValueV3 Argument;
            Argument.Kind = Kind;
            Argument.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
            Argument.TypeId = TypeId;
            Argument.Payload1 = InputByteCount;
            Argument.Payload2 = EntryCount;
            Argument.Payload3 = sizeof(FFinareNativeValueV3);
            FFinareNativeValueV3 Result;
            Result.Kind = Kind;
            Result.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
            Result.TypeId = TypeId;
            Result.Payload3 = sizeof(FFinareNativeValueV3);
            FFinareNativeCallFrameV3 Frame;
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
            if (Frame.Status != EFinareNativeCallStatusV3::BufferTooSmall ||
                Frame.RequiredByteCapacity <= InputByteCount)
            {
                return false;
            }

            TArray<uint8> RetryArena;
            RetryArena.SetNumUninitialized(Frame.RequiredByteCapacity);
            FMemory::Memcpy(RetryArena.GetData(), Arena.GetData(), InputByteCount);
            Result = {};
            Result.Kind = Kind;
            Result.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
            Result.TypeId = TypeId;
            Result.Payload3 = sizeof(FFinareNativeValueV3);
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
            if (Frame.Status != EFinareNativeCallStatusV3::Success ||
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
                FFinareNativeValueV3 OutputCell;
                FMemory::Memcpy(
                    &OutputCell,
                    RetryArena.GetData() + Result.Payload0 +
                        Index * sizeof(FFinareNativeValueV3),
                    sizeof(OutputCell));
                if (OutputCell.Kind != EFinareNativeValueKindV3::Int32)
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
                EFinareNativeValueKindV3::Array,
                FinareMemberId("TArray<int32>"),
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:EchoArray"),
                ArrayValues,
                ArrayValues.Num(),
                6));
        TestTrue(
            TEXT("ABI v3 retries a staged TSet result without re-dispatch"),
            InvokeEchoContainer(
                EFinareNativeValueKindV3::Set,
                FinareMemberId("TSet<int32>"),
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:EchoSet"),
                SetValues,
                SetValues.Num(),
                6));
        TestTrue(
            TEXT("ABI v3 retries a staged TMap result without re-dispatch"),
            InvokeEchoContainer(
                EFinareNativeValueKindV3::Map,
                FinareMemberId("TMap<int32,int32>"),
                FinareMemberId(
                    "/Script/FinareRuntime.FinareInteropFixture:EchoMap"),
                MapValues,
                MapValues.Num() / 2,
                10));

        constexpr uint64 MutateValuesId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:MutateValues");
        FFinareNativeValueV3 MutateArguments[2];
        MutateArguments[0].Kind = EFinareNativeValueKindV3::Int32;
        MutateArguments[0].Payload0 = 7;
        MutateArguments[1].Kind = EFinareNativeValueKindV3::Int32;
        MutateArguments[1].Payload0 = 10;
        FFinareNativeValueV3 MutateResults[3];
        MutateResults[0].Kind = EFinareNativeValueKindV3::Boolean;
        MutateResults[1].Kind = EFinareNativeValueKindV3::Int32;
        MutateResults[2].Kind = EFinareNativeValueKindV3::Int32;
        FFinareNativeCallFrameV3 MutateFrame;
        MutateFrame.Arguments = MutateArguments;
        MutateFrame.ArgumentCount = UE_ARRAY_COUNT(MutateArguments);
        MutateFrame.Results = MutateResults;
        MutateFrame.ResultCapacity = UE_ARRAY_COUNT(MutateResults);
        MutateFrame.ResultCount = UE_ARRAY_COUNT(MutateResults);
        CallFrameTransport->InvokeById(
            Context, InteropHandle.Value, MutateValuesId, &MutateFrame);
        TestTrue(
            TEXT("ABI v3 returns the value plus output and input/output parameters"),
            MutateFrame.Status == EFinareNativeCallStatusV3::Success &&
                MutateFrame.ResultCount == UE_ARRAY_COUNT(MutateResults) &&
                MutateResults[0].Kind == EFinareNativeValueKindV3::Boolean &&
                MutateResults[0].Payload0 == 1 &&
                MutateResults[1].Kind == EFinareNativeValueKindV3::Int32 &&
                static_cast<int32>(MutateResults[1].Payload0) == 14 &&
                MutateResults[2].Kind == EFinareNativeValueKindV3::Int32 &&
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
            Captured.Error == EFinareInvocationError::ObjectHandleFailure &&
                Captured.HandleError == EFinareObjectHandleError::StaleHandle);

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
            Captured.Error == EFinareInvocationError::TransportUnavailable);

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
            Captured.Error == EFinareInvocationError::TransportUnavailable);
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
    FFinareSharedTransformSnapshotTest,
    "Finare.Unreal.SharedData.TransformSnapshot",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareSharedTransformSnapshotTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Shared snapshot World exists"), World);
    if (World == nullptr)
    {
        return false;
    }
    World->AddToRoot();

    UFinareWorldSubsystem* Host =
        World->GetSubsystem<UFinareWorldSubsystem>();
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

    FFinareNativeBridgeContext* Context = Host->GetNativeInvocationContext();
    const FFinareSharedDataTransportV4* Transport =
        Host->GetNativeTransport()->SharedDataTransport;
    TestNotNull(TEXT("Shared snapshot context exists"), Context);
    TestNotNull(TEXT("Shared snapshot transport is discoverable"), Transport);
    if (Context == nullptr || Transport == nullptr)
    {
        World->RemoveFromRoot();
        World->DestroyWorld(false);
        return false;
    }

    TestEqual(
        TEXT("Generated shared view layout is published"),
        Transport->GeneratedChannelViewSize,
        static_cast<uint32>(sizeof(FFinareGeneratedDataViewV1)));
    TestNotNull(
        TEXT("Generated snapshot acquisition is discoverable"),
        Transport->AcquireGeneratedSnapshot);
    Host->EndManagedFrame();
    FString ExtensionError;
    TestTrue(
        *FString::Printf(
            TEXT("Generated BlockBreaker extension registers: %s"),
            *ExtensionError),
        Host->RegisterGeneratedDataPlaneExtension(
            UE::Finare::Extensions::BlockBreaker::Descriptor,
            ExtensionError));
    TArray<UE::Finare::Extensions::BlockBreaker::FBlockBreakerTargetSnapshotV1>
        TargetRows;
    auto& TargetRow = TargetRows.AddDefaulted_GetRef();
    TargetRow.LocationX = 101.0;
    TargetRow.Threat = 0.75;
    TargetRow.FrameNumber = 9;
    TestTrue(
        TEXT("Registered extension publishes a typed target snapshot"),
        Host->PublishGeneratedSnapshotRows(
            UE::Finare::Extensions::BlockBreaker::Descriptor,
            UE::Finare::Extensions::BlockBreaker::TargetSnapshots,
            TConstArrayView<
                UE::Finare::Extensions::BlockBreaker::FBlockBreakerTargetSnapshotV1>(
                TargetRows)));
    FFinareGeneratedDataViewV1 TargetView;
    TestTrue(
        TEXT("Extension target snapshot crosses the existing ABI"),
        Transport->AcquireGeneratedSnapshot(
            Context,
            UE::Finare::Extensions::BlockBreaker::TargetSnapshots.SchemaId,
            sizeof(UE::Finare::Extensions::BlockBreaker::FBlockBreakerTargetSnapshotV1),
            &TargetView) != 0);
    TestTrue(
        TEXT("Extension snapshot preserves its generated identity and row"),
        TargetView.EntryCount == 1 &&
            TargetView.EntrySize ==
                UE::Finare::Extensions::BlockBreaker::TargetSnapshots.EntrySize &&
            reinterpret_cast<const UE::Finare::Extensions::BlockBreaker::
                FBlockBreakerTargetSnapshotV1*>(TargetView.Entries)->Threat == 0.75);
    Host->BeginManagedFrame();
    TestFalse(
        TEXT("Extension publication rejects the managed borrowed-view phase"),
        Host->PublishGeneratedSnapshotRows(
            UE::Finare::Extensions::BlockBreaker::Descriptor,
            UE::Finare::Extensions::BlockBreaker::TargetSnapshots,
            TConstArrayView<
                UE::Finare::Extensions::BlockBreaker::FBlockBreakerTargetSnapshotV1>(
                TargetRows)));
    TArray<FFinareFixedValueCollectionSnapshotV1> GeneratedRows;
    FFinareFixedValueCollectionSnapshotV1& GeneratedRow =
        GeneratedRows.AddDefaulted_GetRef();
    GeneratedRow.SequenceId = 11;
    GeneratedRow.VectorX = 1.0;
    GeneratedRow.VectorY = 2.0;
    GeneratedRow.VectorZ = 3.0;
    GeneratedRow.ValueCount = 2;
    GeneratedRow.Value0 = 10;
    GeneratedRow.Value1 = 20;
    TestTrue(
        TEXT("World host publishes a generated fixed-container snapshot"),
        Host->PublishGeneratedSnapshotRows(
            UE::Finare::SharedData::FixedValueCollectionSchemaId,
            TConstArrayView<FFinareFixedValueCollectionSnapshotV1>(
                GeneratedRows)));
    FFinareGeneratedDataViewV1 GeneratedView;
    TestTrue(
        TEXT("Generated fixed-container snapshot is acquired through the ABI"),
        Transport->AcquireGeneratedSnapshot(
            Context,
            UE::Finare::SharedData::FixedValueCollectionSchemaId,
            sizeof(FFinareFixedValueCollectionSnapshotV1),
            &GeneratedView) != 0);
    TestTrue(
        TEXT("Generated snapshot keeps schema, World, generation, and row contents"),
        GeneratedView.SchemaId ==
                UE::Finare::SharedData::FixedValueCollectionSchemaId &&
            GeneratedView.WorldHostId == Host->GetWorldHostId() &&
            GeneratedView.PublicationId != 0 &&
            GeneratedView.EntryCount == 1 &&
            GeneratedView.EntrySize ==
                sizeof(FFinareFixedValueCollectionSnapshotV1) &&
            reinterpret_cast<const FFinareFixedValueCollectionSnapshotV1*>(
                GeneratedView.Entries)->Value1 == 20);
    FFinareGeneratedDataViewV1 WrongGeneratedView;
    TestFalse(
        TEXT("Generated snapshot acquisition rejects row-size drift"),
        Transport->AcquireGeneratedSnapshot(
            Context,
            UE::Finare::SharedData::FixedValueCollectionSchemaId,
            sizeof(FFinareFixedValueCollectionSnapshotV1) - 8,
            &WrongGeneratedView) != 0);
    TestFalse(
        TEXT("Generated snapshot publication rejects the schema entry bound"),
        Host->PublishGeneratedSnapshotRows(
            UE::Finare::SharedData::FixedValueCollectionSchemaId,
            sizeof(FFinareFixedValueCollectionSnapshotV1),
            GeneratedRows.GetData(),
            UE::Finare::SharedData::MaximumGeneratedSnapshotCount + 1));

    FFinareSharedSnapshotViewV4 FirstView;
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
        static_cast<uint32>(sizeof(FFinareTransformSnapshotEntryV4)));

    const FFinareTransformSnapshotEntryV4* FirstActorEntry = nullptr;
    for (uint32 Index = 0; Index < FirstView.EntryCount; ++Index)
    {
        const FFinareTransformSnapshotEntryV4& Entry = FirstView.Entries[Index];
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
    FFinareSharedSnapshotViewV4 SecondView;
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
        const FFinareTransformSnapshotEntryV4& Entry = SecondView.Entries[Index];
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
        UE::Finare::DataPlaneSchema::CompatibilityId);
    TestEqual(
        TEXT("Generated Transform capacity is published"),
        Transport->MaximumTransformSnapshotCount,
        UE::Finare::SharedData::MaximumTransformSnapshotCount);
    TestNotNull(TEXT("Shared capacity telemetry is discoverable"), Transport->GetTelemetry);
    FFinareSharedDataTelemetryV1 Telemetry;
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
        UE::Finare::SharedData::MaximumTransformSnapshotCount);
    TestTrue(
        TEXT("Published Transform count is bounded"),
        Telemetry.PublishedEntryCount <= Telemetry.MaximumEntryCount);

    TestTrue(
        TEXT("Managed ownership can retain the shared context"),
        Transport->RetainContext(Context) != 0);
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    FFinareSharedSnapshotViewV4 DetachedView;
    TestFalse(
        TEXT("World teardown invalidates the shared-memory view"),
        Transport->AcquireTransformSnapshot(Context, &DetachedView) != 0);
    FFinareGeneratedDataViewV1 DetachedGeneratedView;
    TestFalse(
        TEXT("World teardown invalidates generated snapshot channels"),
        Transport->AcquireGeneratedSnapshot(
            Context,
            UE::Finare::SharedData::FixedValueCollectionSchemaId,
            sizeof(FFinareFixedValueCollectionSnapshotV1),
            &DetachedGeneratedView) != 0);
    Transport->ReleaseContext(Context);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareGameplayDataPlaneTest,
    "Finare.Unreal.GameplayData.CommandEventAndBatchTrace",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareGameplayDataPlaneTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Gameplay data World exists"), World);
    if (World == nullptr)
    {
        return false;
    }
    World->AddToRoot();
    UFinareWorldSubsystem* Host =
        World->GetSubsystem<UFinareWorldSubsystem>();
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

    FFinareObjectHandleResult HandleResult;
    const FFinareObjectHandle TargetHandle =
        Host->RegisterObject(Target, HandleResult);
    TestTrue(TEXT("Command target handle is registered"), HandleResult.IsSuccess());
    const FFinareObjectHandle ClassHandle =
        Host->RegisterObject(AStaticMeshActor::StaticClass(), HandleResult);
    TestTrue(TEXT("Spawn class handle is registered"), HandleResult.IsSuccess());

    const FFinareNativeTransportV2* Bootstrap = Host->GetNativeTransport();
    FFinareNativeBridgeContext* Context = Host->GetNativeInvocationContext();
    const FFinareGameplayDataTransportV4* Transport =
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
        static_cast<uint8>(EFinareGameplayDataPhaseV4::ManagedTick));
    TestEqual(
        TEXT("Gameplay data publishes the generated compatibility set"),
        Transport->LayoutCompatibilityId,
        UE::Finare::DataPlaneSchema::CompatibilityId);
    TestTrue(
        TEXT("Gameplay data accepts forward-compatible schema revisions"),
        Transport->LayoutRevision >= UE::Finare::DataPlaneSchema::Revision);
    TestEqual(
        TEXT("Generated command capacity is published"),
        Transport->MaximumCommandCount,
        UE::Finare::GameplayData::MaximumCommandCount);
    TestNotNull(TEXT("Gameplay capacity telemetry is discoverable"), Transport->GetTelemetry);
    TestEqual(
        TEXT("Generated overlap capacity is published"),
        Transport->MaximumOverlapCount,
        UE::Finare::GameplayData::MaximumOverlapCount);
    TestNotNull(TEXT("Batch overlap reservation is discoverable"), Transport->BeginOverlapBatch);
    TestEqual(
        TEXT("Generated event view layout is published"),
        Transport->GeneratedChannelViewSize,
        static_cast<uint32>(sizeof(FFinareGeneratedDataViewV1)));
    TestNotNull(
        TEXT("Generated event acquisition is discoverable"),
        Transport->AcquireGeneratedEvents);

    Host->EndManagedFrame();
    FString ExtensionError;
    TestTrue(
        *FString::Printf(
            TEXT("Generated BlockBreaker event extension registers: %s"),
            *ExtensionError),
        Host->RegisterGeneratedDataPlaneExtension(
            UE::Finare::Extensions::BlockBreaker::Descriptor,
            ExtensionError));
    TArray<UE::Finare::Extensions::BlockBreaker::FBlockBreakerImpactEventV1>
        ImpactEvents;
    auto& ImpactEvent = ImpactEvents.AddDefaulted_GetRef();
    ImpactEvent.SequenceId = 70;
    ImpactEvent.Target = {Host->GetWorldHostId(), TargetHandle.Value};
    ImpactEvent.ImpactX = 15.0;
    ImpactEvent.Damage = 32.0;
    TestTrue(
        TEXT("Registered extension queues a typed impact event"),
        Host->QueueGeneratedEventRows(
            UE::Finare::Extensions::BlockBreaker::Descriptor,
            UE::Finare::Extensions::BlockBreaker::ImpactEvents,
            TConstArrayView<
                UE::Finare::Extensions::BlockBreaker::FBlockBreakerImpactEventV1>(
                ImpactEvents)));
    Host->BeginManagedFrame();
    FFinareGeneratedDataViewV1 ImpactView;
    TestTrue(
        TEXT("Extension impact event crosses the existing ABI"),
        Transport->AcquireGeneratedEvents(
            Context,
            UE::Finare::Extensions::BlockBreaker::ImpactEvents.SchemaId,
            sizeof(UE::Finare::Extensions::BlockBreaker::FBlockBreakerImpactEventV1),
            &ImpactView) != 0);
    TestTrue(
        TEXT("Extension event preserves its generated identity and row"),
        ImpactView.EntryCount == 1 &&
            ImpactView.EntrySize ==
                UE::Finare::Extensions::BlockBreaker::ImpactEvents.EntrySize &&
            reinterpret_cast<const UE::Finare::Extensions::BlockBreaker::
                FBlockBreakerImpactEventV1*>(ImpactView.Entries)->Damage == 32.0);
    TestFalse(
        TEXT("Extension event queue rejects the managed borrowed-view phase"),
        Host->QueueGeneratedEventRows(
            UE::Finare::Extensions::BlockBreaker::Descriptor,
            UE::Finare::Extensions::BlockBreaker::ImpactEvents,
            TConstArrayView<
                UE::Finare::Extensions::BlockBreaker::FBlockBreakerImpactEventV1>(
                ImpactEvents)));

    TArray<FFinareBufferedGameplayEventV1> GeneratedEvents;
    FFinareBufferedGameplayEventV1& GeneratedEvent =
        GeneratedEvents.AddDefaulted_GetRef();
    GeneratedEvent.SequenceId = 71;
    GeneratedEvent.EventId = 0xB10C;
    GeneratedEvent.Value = 42;
    GeneratedEvent.Weight = 1.5;
    GeneratedEvent.Context = {Host->GetWorldHostId(), TargetHandle.Value};
    TestTrue(
        TEXT("World host queues a schema-generated gameplay event"),
        Host->QueueGeneratedEventRows(
            UE::Finare::GameplayData::BufferedGameplayEventSchemaId,
            TConstArrayView<FFinareBufferedGameplayEventV1>(GeneratedEvents)));

    FFinareCommandBatchViewV4 RejectedCommandView;
    TestFalse(
        TEXT("Command reservations reject schema capacity overflow"),
        Transport->BeginCommandBatch(
            Context,
            UE::Finare::GameplayData::MaximumCommandCount + 1,
            &RejectedCommandView) != 0);
    FFinareTraceRequestViewV4 RejectedTraceView;
    TestFalse(
        TEXT("Trace reservations reject schema capacity overflow"),
        Transport->BeginTraceBatch(
            Context,
            UE::Finare::GameplayData::MaximumTraceCount + 1,
            &RejectedTraceView) != 0);
    FFinareOverlapRequestViewV1 RejectedOverlapView;
    TestFalse(
        TEXT("Overlap reservations reject schema capacity overflow"),
        Transport->BeginOverlapBatch(
            Context,
            UE::Finare::GameplayData::MaximumOverlapCount + 1,
            &RejectedOverlapView) != 0);

    FFinareCommandBatchViewV4 CommandView;
    TestTrue(
        TEXT("Managed phase reserves a command batch"),
        Transport->BeginCommandBatch(Context, 2, &CommandView) != 0);
    CommandView.Commands[0].Type = EFinareCommandTypeV4::SetActorTransform;
    CommandView.Commands[0].Target = {Host->GetWorldHostId(), TargetHandle.Value};
    CommandView.Commands[0].Vector0X = 100.0;
    CommandView.Commands[0].Vector0Y = 200.0;
    CommandView.Commands[0].Vector0Z = 300.0;
    CommandView.Commands[0].RotationW = 1.0;
    CommandView.Commands[0].Vector2X = 1.0;
    CommandView.Commands[0].Vector2Y = 1.0;
    CommandView.Commands[0].Vector2Z = 1.0;
    CommandView.Commands[1].Type = EFinareCommandTypeV4::SpawnActor;
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
    FFinareGameplayEventViewV4 EventView;
    TestTrue(
        TEXT("Next frame publishes command completion events"),
        Transport->AcquireEvents(Context, &EventView) != 0);
    TestEqual(TEXT("Two committed commands publish two events"), EventView.EventCount, 2u);
    TestTrue(
        TEXT("SetTransform completion succeeds"),
        EventView.Events[0].Status == EFinareCommandStatusV4::Success &&
            EventView.Events[0].CommandType ==
                EFinareCommandTypeV4::SetActorTransform);
    TestTrue(
        TEXT("SpawnActor completion returns a World-scoped handle"),
        EventView.Events[1].Status == EFinareCommandStatusV4::Success &&
            EventView.Events[1].Kind == EFinareGameplayEventKindV4::ActorSpawned &&
            EventView.Events[1].Argument.WorldHostId == Host->GetWorldHostId() &&
            EventView.Events[1].Argument.ObjectHandle != 0);

    FFinareGeneratedDataViewV1 GeneratedEventView;
    TestTrue(
        TEXT("Next frame publishes the generated gameplay event buffer"),
        Transport->AcquireGeneratedEvents(
            Context,
            UE::Finare::GameplayData::BufferedGameplayEventSchemaId,
            sizeof(FFinareBufferedGameplayEventV1),
            &GeneratedEventView) != 0);
    TestTrue(
        TEXT("Generated events preserve World, publication, and typed payload"),
        GeneratedEventView.WorldHostId == Host->GetWorldHostId() &&
            GeneratedEventView.PublicationId != 0 &&
            GeneratedEventView.EntryCount == 1 &&
            GeneratedEventView.EntrySize ==
                sizeof(FFinareBufferedGameplayEventV1) &&
            reinterpret_cast<const FFinareBufferedGameplayEventV1*>(
                GeneratedEventView.Entries)->Value == 42);
    FFinareGeneratedDataViewV1 WrongGeneratedEventView;
    TestFalse(
        TEXT("Generated event acquisition rejects row-size drift"),
        Transport->AcquireGeneratedEvents(
            Context,
            UE::Finare::GameplayData::BufferedGameplayEventSchemaId,
            sizeof(FFinareBufferedGameplayEventV1) - 8,
            &WrongGeneratedEventView) != 0);
    TestFalse(
        TEXT("Generated event queue rejects the schema entry bound"),
        Host->QueueGeneratedEventRows(
            UE::Finare::GameplayData::BufferedGameplayEventSchemaId,
            sizeof(FFinareBufferedGameplayEventV1),
            GeneratedEvents.GetData(),
            UE::Finare::GameplayData::MaximumGeneratedEventCount + 1));

    FFinareTraceRequestViewV4 TraceView;
    TestTrue(
        TEXT("Managed phase reserves a three-shape trace batch"),
        Transport->BeginTraceBatch(Context, 3, &TraceView) != 0);
    for (uint32 Index = 0; Index < 3; ++Index)
    {
        TraceView.Requests[Index].Shape =
            static_cast<EFinareTraceShapeV4>(Index);
        TraceView.Requests[Index].CollisionChannel = ECC_Visibility;
        TraceView.Requests[Index].StartX = -100.0;
        TraceView.Requests[Index].EndX = 100.0;
        TraceView.Requests[Index].ExtentX = 5.0;
        TraceView.Requests[Index].ExtentY = 5.0;
        TraceView.Requests[Index].ExtentZ = 5.0;
        TraceView.Requests[Index].RotationW = 1.0;
    }
    FFinareTraceResultViewV4 TraceResults;
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
        TraceResults.Results[0].Status == EFinareTraceStatusV4::Success &&
            TraceResults.Results[1].Status == EFinareTraceStatusV4::Success &&
            TraceResults.Results[2].Status == EFinareTraceStatusV4::Success);
    TestTrue(
        TEXT("Line trace returns the reflected FHitResult data"),
        static_cast<uint8>(TraceResults.Results[0].Flags) &
            static_cast<uint8>(EFinareTraceResultFlagsV4::BlockingHit));

    FFinareOverlapRequestViewV1 OverlapView;
    TestTrue(
        TEXT("Managed phase reserves a sphere/box overlap batch"),
        Transport->BeginOverlapBatch(Context, 2, &OverlapView) != 0);
    OverlapView.Requests[0].Shape = EFinareTraceShapeV4::Sphere;
    OverlapView.Requests[0].CollisionChannel = ECC_Visibility;
    OverlapView.Requests[0].ExtentX = 50.0;
    OverlapView.Requests[1].Shape = EFinareTraceShapeV4::Box;
    OverlapView.Requests[1].CollisionChannel = ECC_Visibility;
    OverlapView.Requests[1].ExtentX = 50.0;
    OverlapView.Requests[1].ExtentY = 50.0;
    OverlapView.Requests[1].ExtentZ = 50.0;
    OverlapView.Requests[1].RotationW = 1.0;
    FFinareOverlapResultViewV1 OverlapResults;
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
        OverlapResults.Results[0].Status == EFinareTraceStatusV4::Success &&
            OverlapResults.Results[1].Status == EFinareTraceStatusV4::Success);
    TestTrue(
        TEXT("Overlap result returns World-scoped Actor handles"),
        OverlapResults.Results[0].Flags ==
                EFinareOverlapResultFlagsV1::HasOverlap &&
            OverlapResults.Results[0].HitActor.WorldHostId ==
                Host->GetWorldHostId() &&
            OverlapResults.Results[0].HitActor.ObjectHandle != 0);

    FFinareCommandBatchViewV4 EventOverflowView;
    const uint32 OverflowEventCount =
        UE::Finare::GameplayData::MaximumEventCount + 1;
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
    FFinareGameplayDataTelemetryV1 Telemetry;
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
            reinterpret_cast<FFinareGameplayDataTelemetryV1*>(&PrefixProbe),
            sizeof(uint32) * 2) != 0);
    TestEqual(
        TEXT("Telemetry prefix reports the current schema revision"),
        PrefixProbe.SchemaRevision,
        UE::Finare::DataPlaneSchema::Revision);
    TestEqual(
        TEXT("Telemetry prefix reports the native full structure size"),
        PrefixProbe.StructSize,
        static_cast<uint32>(sizeof(FFinareGameplayDataTelemetryV1)));
    TestEqual(
        TEXT("Telemetry prefix copy does not overwrite caller memory"),
        PrefixProbe.Canary,
        0xA55AA55AA55AA55Aull);
    TestFalse(
        TEXT("Telemetry rejects a buffer smaller than its version header"),
        Transport->GetTelemetry(
            Context,
            reinterpret_cast<FFinareGameplayDataTelemetryV1*>(&PrefixProbe),
            sizeof(uint32)) != 0);
    TestEqual(
        TEXT("Command execution preserves monotonically assigned order"),
        Telemetry.LastExecutedSequenceId,
        Telemetry.LastCommittedSequenceId);
    Host->BeginManagedFrame();
    FFinareGeneratedDataViewV1 EmptyGeneratedEventView;
    TestTrue(
        TEXT("Generated event channel remains queryable on the next frame"),
        Transport->AcquireGeneratedEvents(
            Context,
            UE::Finare::GameplayData::BufferedGameplayEventSchemaId,
            sizeof(FFinareBufferedGameplayEventV1),
            &EmptyGeneratedEventView) != 0);
    TestTrue(
        TEXT("Generated event visibility is one-frame and overflow is observable"),
        EmptyGeneratedEventView.PublicationId > GeneratedEventView.PublicationId &&
            EmptyGeneratedEventView.EntryCount == 0 &&
            EmptyGeneratedEventView.DroppedEntryCount ==
                UE::Finare::GameplayData::MaximumGeneratedEventCount + 1ull);
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareAsyncServiceTest,
    "Finare.Unreal.AsyncService.DelayLoadCancelAndLifecycle",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareAsyncServiceTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Async service World exists"), World);
    if (World == nullptr)
    {
        return false;
    }
    World->AddToRoot();
    UFinareWorldSubsystem* Host =
        World->GetSubsystem<UFinareWorldSubsystem>();
    const FFinareNativeTransportV2* Bootstrap =
        Host != nullptr ? Host->GetNativeTransport() : nullptr;
    FFinareNativeBridgeContext* Context =
        Host != nullptr ? Host->GetNativeInvocationContext() : nullptr;
    const FFinareAsyncTransportV5* Transport =
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
        static_cast<uint32>(sizeof(FFinareAsyncRequestV5)));
    const uint64 RequiredCapabilities =
        static_cast<uint64>(EFinareAsyncCapabilitiesV5::Delay) |
        static_cast<uint64>(EFinareAsyncCapabilitiesV5::AsyncAssetLoad) |
        static_cast<uint64>(EFinareAsyncCapabilitiesV5::Cancellation) |
        static_cast<uint64>(EFinareAsyncCapabilitiesV5::Timeout) |
        static_cast<uint64>(EFinareAsyncCapabilitiesV5::GenerationOwnership) |
        static_cast<uint64>(EFinareAsyncCapabilitiesV5::WorldTeardown);
    TestEqual(
        TEXT("Async service advertises the complete M7.2 lifecycle contract"),
        Transport->Capabilities & RequiredCapabilities,
        RequiredCapabilities);

    auto Submit = [&](uint64 RequestId,
                      EFinareAsyncOperationV5 Operation,
                      float DurationSeconds,
                      float TimeoutSeconds,
                      const FString& Payload = FString())
    {
        const FTCHARToUTF8 PayloadUtf8(*Payload);
        FFinareAsyncRequestV5 Request;
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

    FFinareAsyncCompletionViewV5 View;
    TestTrue(
        TEXT("Generation-owned Delay request is accepted"),
        Submit(1, EFinareAsyncOperationV5::Delay, 0.5f, 0.0f));
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
                EFinareAsyncCompletionStatusV5::Succeeded);

    TestTrue(
        TEXT("Timed Delay request is accepted"),
        Submit(2, EFinareAsyncOperationV5::Delay, 5.0f, 0.1f));
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
                EFinareAsyncCompletionStatusV5::TimedOut);

    const FString ClassPath = UFinareComponent::StaticClass()->GetPathName();
    TestTrue(
        TEXT("Async class load is accepted"),
        Submit(3, EFinareAsyncOperationV5::LoadClass, 0.0f, 1.0f, ClassPath));
    TestTrue(
        TEXT("Resolved class load completion can be polled"),
        Transport->PollCompletions(Context, 101, 7, &View) != 0);
    TestTrue(
        TEXT("Async class load returns a generation-safe object handle"),
        View.Count == 1 && View.Completions[0].RequestId == 3 &&
            View.Completions[0].Status ==
                EFinareAsyncCompletionStatusV5::Succeeded &&
            View.Completions[0].ResultHandle != 0);

    TestTrue(
        TEXT("Cancelable Delay request is accepted"),
        Submit(4, EFinareAsyncOperationV5::Delay, 5.0f, 0.0f));
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
                EFinareAsyncCompletionStatusV5::Canceled);

    TestTrue(
        TEXT("First rollover request is accepted"),
        Submit(5, EFinareAsyncOperationV5::Delay, 5.0f, 0.0f));
    TestTrue(
        TEXT("Second rollover request is accepted"),
        Submit(6, EFinareAsyncOperationV5::Delay, 5.0f, 0.0f));
    TestEqual(
        TEXT("Generation rollover cancels every owned request"),
        Transport->CancelOwner(Context, 101, 7),
        2u);
    TestEqual(
        TEXT("Generation rollover leaves no native async request"),
        Host->GetPendingAsyncRequestCount(),
        0);

    const uint64 CapacityRejectionsBefore =
        FFinareAsyncService::GetGlobalCapacityRejectionCount();
    bool bAcceptedEveryBoundedRequest = true;
    for (uint32 Index = 0;
         Index < UE::Finare::AsyncService::MaximumInFlightRequestCount;
         ++Index)
    {
        bAcceptedEveryBoundedRequest &= Submit(
            1000 + Index,
            EFinareAsyncOperationV5::Delay,
            60.0f,
            0.0f);
    }
    TestTrue(
        TEXT("Async service accepts requests through its declared capacity"),
        bAcceptedEveryBoundedRequest);
    TestEqual(
        TEXT("Async service retains exactly its bounded pending capacity"),
        Host->GetPendingAsyncRequestCount(),
        static_cast<int32>(
            UE::Finare::AsyncService::MaximumInFlightRequestCount));
    TestFalse(
        TEXT("Async service rejects one request beyond its bounded capacity"),
        Submit(
            1000 + UE::Finare::AsyncService::MaximumInFlightRequestCount,
            EFinareAsyncOperationV5::Delay,
            60.0f,
            0.0f));
    TestEqual(
        TEXT("Async capacity rejection is observable"),
        FFinareAsyncService::GetGlobalCapacityRejectionCount(),
        CapacityRejectionsBefore + 1);
    TestEqual(
        TEXT("Capacity saturation can be canceled deterministically"),
        Transport->CancelOwner(Context, 101, 7),
        UE::Finare::AsyncService::MaximumInFlightRequestCount);

    const uint64 PayloadRejectionsBefore =
        FFinareAsyncService::GetGlobalPayloadRejectionCount();
    const uint8 OversizedPayloadSentinel = 'x';
    FFinareAsyncRequestV5 OversizedPayloadRequest;
    OversizedPayloadRequest.RequestId = 9000;
    OversizedPayloadRequest.OwnerInstanceId = 101;
    OversizedPayloadRequest.OwnerGeneration = 7;
    OversizedPayloadRequest.Operation = EFinareAsyncOperationV5::LoadObject;
    OversizedPayloadRequest.PayloadData = &OversizedPayloadSentinel;
    OversizedPayloadRequest.PayloadLength =
        UE::Finare::AsyncService::MaximumPayloadByteCount + 1;
    TestFalse(
        TEXT("Async service rejects payloads beyond its declared byte capacity"),
        Transport->SubmitRequest(Context, &OversizedPayloadRequest) != 0);
    TestEqual(
        TEXT("Async payload rejection is observable"),
        FFinareAsyncService::GetGlobalPayloadRejectionCount(),
        PayloadRejectionsBefore + 1);

    TestTrue(
        TEXT("Managed ownership can retain the async context"),
        Transport->RetainContext(Context) != 0);
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    World = nullptr;
    FFinareAsyncRequestV5 DetachedRequest;
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
