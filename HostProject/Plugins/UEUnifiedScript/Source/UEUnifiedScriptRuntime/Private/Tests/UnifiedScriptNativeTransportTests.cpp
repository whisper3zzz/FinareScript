#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptNativeTransport.h"

#include "Containers/StringConv.h"
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "UnifiedScriptComponent.h"
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
