#if WITH_DEV_AUTOMATION_TESTS

#include "Engine/World.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
#include "FinareCoreClrHost.h"
#include "FinareComponent.h"
#include "FinareInvocationBridge.h"
#include "FinareNativeExtensionFixture.h"
#include "FinareWorldSubsystem.h"
#include "UObject/UnrealType.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareNativeExtensionDispatchTest,
    "Finare.Unreal.NativeExtensions.GeneratedDispatch",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeExtensionDispatchTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Fixture World"), World)) return false;
    {
        FFinareObjectRegistry Registry(World);
        FFinareInvocationBridge Bridge(Registry);
        FFinareObjectHandleResult HandleResult;
        UFinareNativeExtensionFixture* Object = NewObject<UFinareNativeExtensionFixture>(World);
        const FFinareObjectHandle Handle = Registry.RegisterObject(Object, HandleResult);
        TestNull(TEXT("Counter is not a UPROPERTY"), FindFProperty<FProperty>(Object->GetClass(), TEXT("Counter")));
        TestNull(TEXT("Scale is not a UFUNCTION"), Object->FindFunction(TEXT("Scale")));
        constexpr uint64 CounterId = FinareMemberId("/Script/FinareRuntime.FinareNativeExtensionFixture:Counter");
        constexpr uint64 ScaleId = FinareMemberId("/Script/FinareRuntime.FinareNativeExtensionFixture:Scale(int32)");
        constexpr uint64 DoubleId = FinareMemberId("/Script/FinareRuntime.FinareNativeExtensionFixture:Scale(double)");
        constexpr uint64 AccumulateId = FinareMemberId("/Script/FinareRuntime.FinareNativeExtensionFixture:Accumulate(TArray<int32>const&,int32&,FString&)");
        FFinareInvocationResult Result;
        FFinareValue Value;
        TestTrue(TEXT("Write non-reflected field"), Bridge.WritePropertyById(Handle, CounterId, FFinareValue::FromInt32(6), Result));
        TestEqual(TEXT("Native field changed"), Object->Counter, 6);
        TestTrue(TEXT("Read non-reflected field"), Bridge.ReadPropertyById(Handle, CounterId, Value, Result));
        int32 Integer = 0;
        TestTrue(TEXT("Integer field type"), Value.TryGetInt32(Integer));
        TestEqual(TEXT("Integer field result"), Integer, 6);
        const TArray<FFinareValue> IntArguments = { FFinareValue::FromInt32(7) };
        TestTrue(TEXT("Exact int overload"), Bridge.InvokeById(Handle, ScaleId, IntArguments, Value, Result));
        TestTrue(TEXT("Integer return type"), Value.TryGetInt32(Integer));
        TestEqual(TEXT("Integer overload result"), Integer, 42);
        const TArray<FFinareValue> DoubleArguments = { FFinareValue::FromFloat64(0.5) };
        TestTrue(TEXT("Exact double overload"), Bridge.InvokeById(Handle, DoubleId, DoubleArguments, Value, Result));
        double Fraction = 0;
        TestTrue(TEXT("Double return type"), Value.TryGetFloat64(Fraction));
        TestEqual(TEXT("Double overload result"), Fraction, 3.0);
        const TArray<FFinareValue> RefArguments = {
            FFinareValue::FromArray({ FFinareValue::FromInt32(2), FFinareValue::FromInt32(3) }, FinareMemberId("TArray<int32>")),
            FFinareValue::FromInt32(10),
        };
        TArray<FFinareValue> Outputs;
        TestTrue(TEXT("Const container plus ref/out"), Bridge.InvokeById(Handle, AccumulateId, RefArguments, Value, Outputs, Result));
        if (TestEqual(TEXT("Two output values"), Outputs.Num(), 2))
        {
            FString Label;
            TestTrue(TEXT("ref integer decoded"), Outputs[0].TryGetInt32(Integer));
            TestEqual(TEXT("ref value updated"), Integer, 15);
            TestTrue(TEXT("out string decoded"), Outputs[1].TryGetString(Label));
            TestEqual(TEXT("out value updated"), Label, FString(TEXT("total:15")));
        }
        UObject* WrongType = NewObject<UFinareComponent>(World);
        const FFinareObjectHandle WrongHandle = Registry.RegisterObject(WrongType, HandleResult);
        TestFalse(TEXT("Wrong UObject type rejected"), Bridge.InvokeById(WrongHandle, ScaleId, IntArguments, Value, Result));
        TestTrue(TEXT("Target type diagnostic"), Result.Error == EFinareInvocationError::TargetTypeMismatch);
        Registry.ReleaseObject(Handle, HandleResult);
        TestFalse(TEXT("Stale identity rejected"), Bridge.ReadPropertyById(Handle, CounterId, Value, Result));
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareNativeExtensionManagedTest,
    "Finare.Unreal.NativeExtensions.ManagedRoundTrip",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareNativeExtensionManagedTest::RunTest(const FString& Parameters)
{
    const int32 InitialWorldCount = FFinareCoreClrHost::Get().GetManagedWorldCount();
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Managed fixture World"), World)) return false;
    UFinareWorldSubsystem* Host = World->GetSubsystem<UFinareWorldSubsystem>();
    const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
    if (TestNotNull(TEXT("World host"), Host) && TestTrue(TEXT("Plugin is installed"), Plugin.IsValid()))
    {
        UFinareNativeExtensionFixture* Object = NewObject<UFinareNativeExtensionFixture>(World);
        FFinareObjectHandleResult HandleResult;
        const FFinareObjectHandle Handle = Host->RegisterObject(Object, HandleResult);
        FString Error;
        const FString Assembly = FPaths::Combine(Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo/Finare.Unreal.Demo.dll"));
        const bool bStarted = Host->CreateManagedScript(7001, Handle, Handle, Assembly,
            TEXT("Finare.Unreal.Demo.NativeExtensionDemoScript"), Error);
        TestTrue(*FString::Printf(TEXT("Managed generated bindings execute: %s"), *Error), bStarted);
        if (bStarted)
        {
            TestEqual(TEXT("Managed calls mutated native field"), Object->Counter, 16);
            TestTrue(TEXT("Managed tick keeps native binding"), Host->TickManagedScript(7001, 0.016f, Error));
            TestEqual(TEXT("Native field after managed tick"), Object->Counter, 17);
            int32 Generation = 0;
            TestTrue(TEXT("Native bindings survive managed generation replacement"), Host->ReloadManagedScript(
                7001, Assembly, TEXT("Finare.Unreal.Demo.NativeExtensionDemoScript"), Generation, Error));
            TestTrue(TEXT("Replacement generation is active"), Generation > 0);
            TestEqual(TEXT("Replacement OnStart executes generated bindings"), Object->Counter, 16);
            TestTrue(TEXT("Managed script stops"), Host->DestroyManagedScript(7001, Error));
        }
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    TestEqual(TEXT("World-owned managed context released"), FFinareCoreClrHost::Get().GetManagedWorldCount(), InitialWorldCount);
    return true;
}

#endif
