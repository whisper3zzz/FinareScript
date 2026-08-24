#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptInvocationBridge.h"

#include "Async/Async.h"
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "UnifiedScriptComponent.h"
#include "UObject/UObjectGlobals.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptInvocationBridgePrimitivesTest,
    "UnifiedScript.Unreal.InvocationBridge.Primitives",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptInvocationBridgePrimitivesTest::RunTest(const FString& Parameters)
{
    static const FString TypePath =
        TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptComponent");
    static const FString ScriptAssetPath = TypePath + TEXT(":ScriptAssetPath");
    static const FString StartAutomaticallyPath = TypePath + TEXT(":bStartAutomatically");
    static const FString ActiveGenerationPath = TypePath + TEXT(":ActiveGeneration");
    static const FString AddPath = TypePath + TEXT(":Add");
    static const FString ReloadPath = TypePath + TEXT(":RequestReload");

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
        FUnifiedScriptObjectHandleResult HandleResult;
        FUnifiedScriptInvocationResult Result;
        FUnifiedScriptValue Value;

        UUnifiedScriptComponent* Component = NewObject<UUnifiedScriptComponent>(World);
        const FUnifiedScriptObjectHandle Handle = Registry.RegisterObject(Component, HandleResult);
        TestTrue(TEXT("Fixture registration succeeds"), HandleResult.IsSuccess());

        TestTrue(
            TEXT("Boolean property read succeeds"),
            Bridge.ReadProperty(Handle, StartAutomaticallyPath, Value, Result));
        bool bStartAutomatically = false;
        TestTrue(TEXT("Boolean result has the expected kind"), Value.TryGetBoolean(bStartAutomatically));
        TestTrue(TEXT("Boolean property value is preserved"), bStartAutomatically);

        const FString ExpectedAssetPath = TEXT("/Game/Scripts/DamagePolicy");
        TestTrue(
            TEXT("String property write succeeds"),
            Bridge.WriteProperty(
                Handle,
                ScriptAssetPath,
                FUnifiedScriptValue::FromString(ExpectedAssetPath),
                Result));
        TestEqual(TEXT("Native property receives the string"), Component->ScriptAssetPath, ExpectedAssetPath);
        TestTrue(
            TEXT("String property read succeeds"),
            Bridge.ReadProperty(Handle, ScriptAssetPath, Value, Result));
        FString ActualAssetPath;
        TestTrue(TEXT("String result has the expected kind"), Value.TryGetString(ActualAssetPath));
        TestEqual(TEXT("String property round-trips"), ActualAssetPath, ExpectedAssetPath);

        TestFalse(
            TEXT("Blueprint-read-only property rejects writes"),
            Bridge.WriteProperty(
                Handle,
                ActiveGenerationPath,
                FUnifiedScriptValue::FromString(TEXT("forbidden")),
                Result));
        TestTrue(
            TEXT("Read-only failure is explicit"),
            Result.Error == EUnifiedScriptInvocationError::PropertyNotWritable);

        TestFalse(
            TEXT("Property type mismatch rejects writes"),
            Bridge.WriteProperty(
                Handle,
                ScriptAssetPath,
                FUnifiedScriptValue::FromBoolean(true),
                Result));
        TestTrue(
            TEXT("Type mismatch is explicit"),
            Result.Error == EUnifiedScriptInvocationError::ValueTypeMismatch);

        const TArray<FUnifiedScriptValue> AddArguments = {
            FUnifiedScriptValue::FromInt32(19),
            FUnifiedScriptValue::FromInt32(23),
        };
        TestTrue(
            TEXT("UFunction invocation succeeds"),
            Bridge.Invoke(Handle, AddPath, AddArguments, Value, Result));
        int32 Sum = 0;
        TestTrue(TEXT("Return value has the expected kind"), Value.TryGetInt32(Sum));
        TestEqual(TEXT("ProcessEvent returns the fixture result"), Sum, 42);

        TestTrue(
            TEXT("Void UFunction invocation succeeds"),
            Bridge.Invoke(
                Handle,
                ReloadPath,
                TConstArrayView<FUnifiedScriptValue>(),
                Value,
                Result));
        TestTrue(TEXT("Void call executes native behavior"), Component->ConsumeReloadRequest());
        TestTrue(TEXT("Void call returns a void value"), Value.Kind == EUnifiedScriptValueKind::Void);

        TestFalse(
            TEXT("Incorrect argument count is rejected"),
            Bridge.Invoke(
                Handle,
                AddPath,
                TConstArrayView<FUnifiedScriptValue>(AddArguments.GetData(), 1),
                Value,
                Result));
        TestTrue(
            TEXT("Argument count failure is explicit"),
            Result.Error == EUnifiedScriptInvocationError::ArgumentCountMismatch);

        const TArray<FUnifiedScriptValue> InvalidArguments = {
            FUnifiedScriptValue::FromString(TEXT("nineteen")),
            FUnifiedScriptValue::FromInt32(23),
        };
        TestFalse(
            TEXT("Incorrect argument type is rejected"),
            Bridge.Invoke(Handle, AddPath, InvalidArguments, Value, Result));
        TestTrue(
            TEXT("Argument type failure is explicit"),
            Result.Error == EUnifiedScriptInvocationError::ValueTypeMismatch);

        const TFuture<EUnifiedScriptObjectHandleError> OffThreadResult = Async(
            EAsyncExecution::ThreadPool,
            [&Bridge, Handle]()
            {
                FUnifiedScriptInvocationResult ThreadResult;
                FUnifiedScriptValue ThreadValue;
                Bridge.ReadProperty(Handle, StartAutomaticallyPath, ThreadValue, ThreadResult);
                return ThreadResult.HandleError;
            });
        TestTrue(
            TEXT("Off-thread invocation is rejected by the handle authority"),
            OffThreadResult.Get() == EUnifiedScriptObjectHandleError::NotGameThread);

        TestTrue(TEXT("Fixture release succeeds"), Registry.ReleaseObject(Handle, HandleResult));
        TestFalse(
            TEXT("Stale handle cannot read a property"),
            Bridge.ReadProperty(Handle, StartAutomaticallyPath, Value, Result));
        TestTrue(
            TEXT("Stale handle failure preserves the lifecycle category"),
            Result.Error == EUnifiedScriptInvocationError::ObjectHandleFailure &&
                Result.HandleError == EUnifiedScriptObjectHandleError::StaleHandle);
    }

    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

#endif
