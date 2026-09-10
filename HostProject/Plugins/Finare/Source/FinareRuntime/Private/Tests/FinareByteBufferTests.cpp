#if WITH_DEV_AUTOMATION_TESTS
#include "Engine/World.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
#include "FinareNativeExtensionFixture.h"
#include "FinareNativeTransport.h"
#include "FinareWorldSubsystem.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareByteBufferWireTest,
    "Finare.Unreal.BinaryBuffers.WireValidation", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FFinareByteBufferWireTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Binary World"), World)) return false;
    {
        FFinareObjectRegistry Registry(World);
        FFinareInvocationBridge Bridge(Registry);
        auto* Context = new FFinareNativeBridgeContext(Bridge);
        auto* Object = NewObject<UFinareNativeExtensionFixture>(World);
        FFinareObjectHandleResult Result;
        const auto Handle = Registry.RegisterObject(Object, Result);
        const auto* Table = Finare_GetNativeTransportV3();
        TestTrue(TEXT("Binary capability"), (Table->Capabilities & static_cast<uint64>(EFinareNativeCapabilitiesV3::ByteBuffers)) != 0);
        uint8 Bytes[] = {91, 0, 255, 4};
        FFinareNativeValueV3 Cell;
        Cell.Kind = EFinareNativeValueKindV3::ByteBuffer;
        Cell.Flags = EFinareNativeValueFlagsV3::ArenaBacked;
        Cell.Payload0 = 1;
        Cell.Payload1 = 3;
        const uint64 Id = FinareMemberId("/Script/FinareRuntime.FinareNativeExtensionFixture:StoreBytes(FFinareByteBufferconst&)");
        const auto Call = [&](const FFinareNativeValueV3& Argument)
        {
            FFinareNativeCallFrameV3 Frame;
            Frame.Arguments = &Argument;
            Frame.ArgumentCount = 1;
            Frame.ByteBuffer = Bytes;
            Frame.ByteCapacity = Frame.ByteCount = sizeof(Bytes);
            Table->InvokeById(Context, Handle.Value, Id, &Frame);
            return Frame.Status;
        };
        TestTrue(TEXT("Offset/embedded zero input"), Call(Cell) == EFinareNativeCallStatusV3::Success);
        TestEqual(TEXT("One native effect"), Object->BinaryCalls, 1);
        Bytes[1] = 33;
        if (TestEqual(TEXT("Native snapshot size"), Object->Payload.Num(), 3))
            TestEqual(TEXT("Native retains an owned snapshot"), Object->Payload.GetView()[0], uint8(0));
        const FFinareByteBuffer Retained = Object->Payload;
        for (int32 Mutation = 0; Mutation < 8; ++Mutation)
        {
            auto Invalid = Cell;
            switch (Mutation)
            {
            case 0: Invalid.Payload0 = MAX_uint64; break;
            case 1: Invalid.Payload1 = MAX_uint64; break;
            case 2: Invalid.Flags = EFinareNativeValueFlagsV3::None; break;
            case 3: Invalid.Reserved16 = 1; break;
            case 4: Invalid.Auxiliary = 1; break;
            case 5: Invalid.TypeId = 1; break;
            case 6: Invalid.Payload2 = 1; break;
            case 7: Invalid.Payload3 = 1; break;
            }
            TestTrue(TEXT("Malformed payload rejected before native invocation"), Call(Invalid) != EFinareNativeCallStatusV3::Success);
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

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareByteBufferManagedTest,
    "Finare.Unreal.BinaryBuffers.ManagedRoundTrip", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FFinareByteBufferManagedTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Binary managed World"), World)) return false;
    auto* Host = World->GetSubsystem<UFinareWorldSubsystem>();
    const auto Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
    if (TestNotNull(TEXT("Host"), Host) && TestTrue(TEXT("Plugin"), Plugin.IsValid()))
    {
        auto* Object = NewObject<UFinareNativeExtensionFixture>(World);
        FFinareObjectHandleResult Result;
        const auto Handle = Host->RegisterObject(Object, Result);
        const FString Assembly = FPaths::Combine(Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo/Finare.Unreal.Demo.dll"));
        const FString Type = TEXT("Finare.Unreal.Demo.BinaryPayloadDemoScript");
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
