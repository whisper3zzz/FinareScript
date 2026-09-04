#if WITH_DEV_AUTOMATION_TESTS
#include "Engine/World.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
#include "UnifiedScriptNativeExtensionFixture.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptWorldSubsystem.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FUnifiedScriptByteBufferWireTest,
    "UnifiedScript.Unreal.BinaryBuffers.WireValidation", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FUnifiedScriptByteBufferWireTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Binary World"), World)) return false;
    {
        FUnifiedScriptObjectRegistry Registry(World);
        FUnifiedScriptInvocationBridge Bridge(Registry);
        auto* Context = new FUnifiedScriptNativeBridgeContext(Bridge);
        auto* Object = NewObject<UUnifiedScriptNativeExtensionFixture>(World);
        FUnifiedScriptObjectHandleResult Result;
        const auto Handle = Registry.RegisterObject(Object, Result);
        const auto* Table = UEUnifiedScript_GetNativeTransportV3();
        TestTrue(TEXT("Binary capability"), (Table->Capabilities & static_cast<uint64>(EUnifiedScriptNativeCapabilitiesV3::ByteBuffers)) != 0);
        uint8 Bytes[] = {91, 0, 255, 4};
        FUnifiedScriptNativeValueV3 Cell;
        Cell.Kind = EUnifiedScriptNativeValueKindV3::ByteBuffer;
        Cell.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        Cell.Payload0 = 1;
        Cell.Payload1 = 3;
        const uint64 Id = UnifiedScriptMemberId("/Script/UEUnifiedScriptRuntime.UnifiedScriptNativeExtensionFixture:StoreBytes(FUnifiedScriptByteBufferconst&)");
        const auto Call = [&](const FUnifiedScriptNativeValueV3& Argument)
        {
            FUnifiedScriptNativeCallFrameV3 Frame;
            Frame.Arguments = &Argument;
            Frame.ArgumentCount = 1;
            Frame.ByteBuffer = Bytes;
            Frame.ByteCapacity = Frame.ByteCount = sizeof(Bytes);
            Table->InvokeById(Context, Handle.Value, Id, &Frame);
            return Frame.Status;
        };
        TestTrue(TEXT("Offset/embedded zero input"), Call(Cell) == EUnifiedScriptNativeCallStatusV3::Success);
        TestEqual(TEXT("One native effect"), Object->BinaryCalls, 1);
        Bytes[1] = 33;
        if (TestEqual(TEXT("Native snapshot size"), Object->Payload.Num(), 3))
            TestEqual(TEXT("Native retains an owned snapshot"), Object->Payload.GetView()[0], uint8(0));
        const FUnifiedScriptByteBuffer Retained = Object->Payload;
        for (int32 Mutation = 0; Mutation < 8; ++Mutation)
        {
            auto Invalid = Cell;
            switch (Mutation)
            {
            case 0: Invalid.Payload0 = MAX_uint64; break;
            case 1: Invalid.Payload1 = MAX_uint64; break;
            case 2: Invalid.Flags = EUnifiedScriptNativeValueFlagsV3::None; break;
            case 3: Invalid.Reserved16 = 1; break;
            case 4: Invalid.Auxiliary = 1; break;
            case 5: Invalid.TypeId = 1; break;
            case 6: Invalid.Payload2 = 1; break;
            case 7: Invalid.Payload3 = 1; break;
            }
            TestTrue(TEXT("Malformed payload rejected before native invocation"), Call(Invalid) != EUnifiedScriptNativeCallStatusV3::Success);
        }
        TestEqual(TEXT("Malformed cells have no native effects"), Object->BinaryCalls, 1);
        Object->Payload = {};
        Context->Detach();
        Context->Release();
        if (Retained.Num() == 3)
            TestEqual(TEXT("Retained binary snapshot survives context detach"), Retained.GetView()[1], uint8(255));
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FUnifiedScriptByteBufferManagedTest,
    "UnifiedScript.Unreal.BinaryBuffers.ManagedRoundTrip", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FUnifiedScriptByteBufferManagedTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Binary managed World"), World)) return false;
    auto* Host = World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    const auto Plugin = IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (TestNotNull(TEXT("Host"), Host) && TestTrue(TEXT("Plugin"), Plugin.IsValid()))
    {
        auto* Object = NewObject<UUnifiedScriptNativeExtensionFixture>(World);
        FUnifiedScriptObjectHandleResult Result;
        const auto Handle = Host->RegisterObject(Object, Result);
        const FString Assembly = FPaths::Combine(Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll"));
        const FString Type = TEXT("UnifiedScript.Unreal.Demo.BinaryPayloadDemoScript");
        FString Error;
        const bool Started = Host->CreateManagedScript(7021, Handle, Handle, Assembly, Type, Error);
        TestTrue(*FString::Printf(TEXT("Generated binary calls: %s"), *Error), Started);
        if (Started)
        {
            TestEqual(TEXT("Output retry never repeats native calls"), Object->BinaryCalls, 4);
            int32 Generation = 0;
            TestTrue(*FString::Printf(TEXT("Binary reload: %s"), *Error), Host->ReloadManagedScript(7021, Assembly, Type, Generation, Error));
            TestEqual(TEXT("Reload binary calls"), Object->BinaryCalls, 8);
            TestTrue(TEXT("Managed snapshot cleanup"), Host->DestroyManagedScript(7021, Error));
            TestTrue(TEXT("Create binary script for World teardown"), Host->CreateManagedScript(7022, Handle, Handle, Assembly, Type, Error));
        }
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}
#endif
