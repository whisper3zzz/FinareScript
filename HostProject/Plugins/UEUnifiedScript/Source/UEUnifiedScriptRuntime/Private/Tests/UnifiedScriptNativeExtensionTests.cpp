#if WITH_DEV_AUTOMATION_TESTS

#include "Engine/World.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptInvocationBridge.h"
#include "UnifiedScriptNativeExtensionFixture.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/UnrealType.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptNativeExtensionDispatchTest,
    "UnifiedScript.Unreal.NativeExtensions.GeneratedDispatch",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptNativeExtensionDispatchTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Fixture World"), World)) return false;
    {
        FUnifiedScriptObjectRegistry Registry(World);
        FUnifiedScriptInvocationBridge Bridge(Registry);
        FUnifiedScriptObjectHandleResult HandleResult;
        UUnifiedScriptNativeExtensionFixture* Object = NewObject<UUnifiedScriptNativeExtensionFixture>(World);
        const FUnifiedScriptObjectHandle Handle = Registry.RegisterObject(Object, HandleResult);
        TestNull(TEXT("Counter is not a UPROPERTY"), FindFProperty<FProperty>(Object->GetClass(), TEXT("Counter")));
        TestNull(TEXT("Scale is not a UFUNCTION"), Object->FindFunction(TEXT("Scale")));
        constexpr uint64 CounterId = UnifiedScriptMemberId("/Script/UEUnifiedScriptRuntime.UnifiedScriptNativeExtensionFixture:Counter");
        constexpr uint64 ScaleId = UnifiedScriptMemberId("/Script/UEUnifiedScriptRuntime.UnifiedScriptNativeExtensionFixture:Scale(int32)");
        constexpr uint64 DoubleId = UnifiedScriptMemberId("/Script/UEUnifiedScriptRuntime.UnifiedScriptNativeExtensionFixture:Scale(double)");
        constexpr uint64 AccumulateId = UnifiedScriptMemberId("/Script/UEUnifiedScriptRuntime.UnifiedScriptNativeExtensionFixture:Accumulate(TArray<int32>const&,int32&,FString&)");
        FUnifiedScriptInvocationResult Result;
        FUnifiedScriptValue Value;
        TestTrue(TEXT("Write non-reflected field"), Bridge.WritePropertyById(Handle, CounterId, FUnifiedScriptValue::FromInt32(6), Result));
        TestEqual(TEXT("Native field changed"), Object->Counter, 6);
        TestTrue(TEXT("Read non-reflected field"), Bridge.ReadPropertyById(Handle, CounterId, Value, Result));
        int32 Integer = 0;
        TestTrue(TEXT("Integer field type"), Value.TryGetInt32(Integer));
        TestEqual(TEXT("Integer field result"), Integer, 6);
        const TArray<FUnifiedScriptValue> IntArguments = { FUnifiedScriptValue::FromInt32(7) };
        TestTrue(TEXT("Exact int overload"), Bridge.InvokeById(Handle, ScaleId, IntArguments, Value, Result));
        TestTrue(TEXT("Integer return type"), Value.TryGetInt32(Integer));
        TestEqual(TEXT("Integer overload result"), Integer, 42);
        const TArray<FUnifiedScriptValue> DoubleArguments = { FUnifiedScriptValue::FromFloat64(0.5) };
        TestTrue(TEXT("Exact double overload"), Bridge.InvokeById(Handle, DoubleId, DoubleArguments, Value, Result));
        double Fraction = 0;
        TestTrue(TEXT("Double return type"), Value.TryGetFloat64(Fraction));
        TestEqual(TEXT("Double overload result"), Fraction, 3.0);
        const TArray<FUnifiedScriptValue> RefArguments = {
            FUnifiedScriptValue::FromArray({ FUnifiedScriptValue::FromInt32(2), FUnifiedScriptValue::FromInt32(3) }, UnifiedScriptMemberId("TArray<int32>")),
            FUnifiedScriptValue::FromInt32(10),
        };
        TArray<FUnifiedScriptValue> Outputs;
        TestTrue(TEXT("Const container plus ref/out"), Bridge.InvokeById(Handle, AccumulateId, RefArguments, Value, Outputs, Result));
        if (TestEqual(TEXT("Two output values"), Outputs.Num(), 2))
        {
            FString Label;
            TestTrue(TEXT("ref integer decoded"), Outputs[0].TryGetInt32(Integer));
            TestEqual(TEXT("ref value updated"), Integer, 15);
            TestTrue(TEXT("out string decoded"), Outputs[1].TryGetString(Label));
            TestEqual(TEXT("out value updated"), Label, FString(TEXT("total:15")));
        }
        UObject* WrongType = NewObject<UUnifiedScriptComponent>(World);
        const FUnifiedScriptObjectHandle WrongHandle = Registry.RegisterObject(WrongType, HandleResult);
        TestFalse(TEXT("Wrong UObject type rejected"), Bridge.InvokeById(WrongHandle, ScaleId, IntArguments, Value, Result));
        TestTrue(TEXT("Target type diagnostic"), Result.Error == EUnifiedScriptInvocationError::TargetTypeMismatch);
        Registry.ReleaseObject(Handle, HandleResult);
        TestFalse(TEXT("Stale identity rejected"), Bridge.ReadPropertyById(Handle, CounterId, Value, Result));
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptNativeExtensionManagedTest,
    "UnifiedScript.Unreal.NativeExtensions.ManagedRoundTrip",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptNativeExtensionManagedTest::RunTest(const FString& Parameters)
{
    const int32 InitialWorldCount = FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount();
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Managed fixture World"), World)) return false;
    UUnifiedScriptWorldSubsystem* Host = World->GetSubsystem<UUnifiedScriptWorldSubsystem>();
    const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (TestNotNull(TEXT("World host"), Host) && TestTrue(TEXT("Plugin is installed"), Plugin.IsValid()))
    {
        UUnifiedScriptNativeExtensionFixture* Object = NewObject<UUnifiedScriptNativeExtensionFixture>(World);
        FUnifiedScriptObjectHandleResult HandleResult;
        const FUnifiedScriptObjectHandle Handle = Host->RegisterObject(Object, HandleResult);
        FString Error;
        const FString Assembly = FPaths::Combine(Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo/UnifiedScript.Unreal.Demo.dll"));
        const bool bStarted = Host->CreateManagedScript(7001, Handle, Handle, Assembly,
            TEXT("UnifiedScript.Unreal.Demo.NativeExtensionDemoScript"), Error);
        TestTrue(*FString::Printf(TEXT("Managed generated bindings execute: %s"), *Error), bStarted);
        if (bStarted)
        {
            TestEqual(TEXT("Managed calls mutated native field"), Object->Counter, 16);
            TestTrue(TEXT("Managed tick keeps native binding"), Host->TickManagedScript(7001, 0.016f, Error));
            TestEqual(TEXT("Native field after managed tick"), Object->Counter, 17);
            int32 Generation = 0;
            TestTrue(TEXT("Native bindings survive managed generation replacement"), Host->ReloadManagedScript(
                7001, Assembly, TEXT("UnifiedScript.Unreal.Demo.NativeExtensionDemoScript"), Generation, Error));
            TestTrue(TEXT("Replacement generation is active"), Generation > 0);
            TestEqual(TEXT("Replacement OnStart executes generated bindings"), Object->Counter, 16);
            TestTrue(TEXT("Managed script stops"), Host->DestroyManagedScript(7001, Error));
        }
    }
    World->RemoveFromRoot();
    World->DestroyWorld(false);
    TestEqual(TEXT("World-owned managed context released"), FUnifiedScriptCoreClrHost::Get().GetManagedWorldCount(), InitialWorldCount);
    return true;
}

#endif
