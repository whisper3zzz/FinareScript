#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptNativeTransport.h"

#include "Containers/StringConv.h"
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptInteropFixture.h"
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
        64u);
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

#endif
