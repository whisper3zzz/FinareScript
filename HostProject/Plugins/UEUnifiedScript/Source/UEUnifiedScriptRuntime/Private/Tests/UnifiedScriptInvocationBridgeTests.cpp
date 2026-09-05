#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptInvocationBridge.h"

#include "Async/Async.h"
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptInteropFixture.h"
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

        UUnifiedScriptInteropFixture* Interop = NewObject<UUnifiedScriptInteropFixture>(World);
        const FUnifiedScriptObjectHandle InteropHandle =
            Registry.RegisterObject(Interop, HandleResult);
        TestTrue(TEXT("Common-type fixture registration succeeds"), HandleResult.IsSuccess());
        UUnifiedScriptInteropFixture* InterfaceCandidate = NewObject<UUnifiedScriptInteropFixture>(World);
        const FUnifiedScriptObjectHandle InterfaceCandidateHandle =
            Registry.RegisterObject(InterfaceCandidate, HandleResult);
        TestTrue(TEXT("Native interface candidate registration succeeds"), HandleResult.IsSuccess());

        constexpr uint64 VectorValueId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:VectorValue");
        constexpr uint64 ModeId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:Mode");
        constexpr uint64 AddInt64Id = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:AddInt64");
        constexpr uint64 AddVectorId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:AddVector");
        constexpr uint64 EchoTransformId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoTransform");
        constexpr uint64 InterfaceValueId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:InterfaceValue");
        constexpr uint64 EchoInterfaceId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoInterface");
        constexpr uint64 MutateInterfacesId = UnifiedScriptMemberId(
            "/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:MutateInterfaces");
        static const FString InterfaceValuePath =
            TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:InterfaceValue");
        static const FString EchoInterfacePath =
            TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropFixture:EchoInterface");

        TestTrue(
            TEXT("Null interface property read succeeds"),
            Bridge.ReadPropertyById(InteropHandle, InterfaceValueId, Value, Result));
        FUnifiedScriptObjectHandle DecodedInterfaceHandle;
        TestTrue(
            TEXT("Null interface property uses the object ABI kind"),
            Value.TryGetObjectHandle(DecodedInterfaceHandle) && DecodedInterfaceHandle.IsNull());

        const FUnifiedScriptValue InterfaceValue =
            FUnifiedScriptValue::FromObjectHandle(InterfaceCandidateHandle);
        TestTrue(
            TEXT("Interface property write succeeds"),
            Bridge.WriteProperty(InteropHandle, InterfaceValuePath, InterfaceValue, Result));
        TestTrue(
            TEXT("Interface property write preserves its object identity"),
            Interop->InterfaceValue.GetObject() == InterfaceCandidate);
        TestTrue(
            TEXT("Interface property read returns its object handle"),
            Bridge.ReadProperty(InteropHandle, InterfaceValuePath, Value, Result) &&
                Value.TryGetObjectHandle(DecodedInterfaceHandle) &&
                DecodedInterfaceHandle == InterfaceCandidateHandle);

        const TArray<FUnifiedScriptValue> EchoInterfaceArguments = {InterfaceValue};
        TestTrue(
            TEXT("Scalar interface return invocation succeeds"),
            Bridge.Invoke(InteropHandle, EchoInterfacePath, EchoInterfaceArguments, Value, Result));
        TestTrue(
            TEXT("Scalar interface return uses the object ABI kind"),
            Value.TryGetObjectHandle(DecodedInterfaceHandle) &&
                DecodedInterfaceHandle == InterfaceCandidateHandle);

        const FString InterfaceConcreteOnlyPath =
            TEXT("/Script/UEUnifiedScriptRuntime.UnifiedScriptInteropContract:VectorValue");
        const TConstArrayView<FUnifiedScriptValue> NoInterfaceArguments;
        TestFalse(
            TEXT("Interface fallback rejects members absent from the interface declaration"),
            Bridge.Invoke(InteropHandle, InterfaceConcreteOnlyPath, NoInterfaceArguments, Value, Result));
        TestTrue(
            TEXT("Interface fallback reports an invalid interface member"),
            Result.Error == EUnifiedScriptInvocationError::InvalidMemberPath);

        const TArray<FUnifiedScriptValue> MutateInterfaceArguments = {InterfaceValue, InterfaceValue};
        TArray<FUnifiedScriptValue> MutateInterfaceOutputs;
        TestTrue(
            TEXT("Interface ref/out invocation succeeds"),
            Bridge.InvokeById(
                InteropHandle,
                MutateInterfacesId,
                MutateInterfaceArguments,
                Value,
                MutateInterfaceOutputs,
                Result));
        bool bMutated = false;
        TestTrue(TEXT("Interface ref/out return decodes"), Value.TryGetBoolean(bMutated) && bMutated);
        TestEqual(TEXT("Interface ref/out returns both output cells"), MutateInterfaceOutputs.Num(), 2);
        FUnifiedScriptObjectHandle OutInterfaceHandle;
        FUnifiedScriptObjectHandle AccumulatorHandle;
        TestTrue(
            TEXT("Interface out value preserves its object identity"),
            MutateInterfaceOutputs.Num() == 2 &&
                MutateInterfaceOutputs[0].TryGetObjectHandle(OutInterfaceHandle) &&
                OutInterfaceHandle == InterfaceCandidateHandle);
        TestTrue(
            TEXT("Interface ref value preserves its object identity"),
            MutateInterfaceOutputs.Num() == 2 &&
                MutateInterfaceOutputs[1].TryGetObjectHandle(AccumulatorHandle) &&
                AccumulatorHandle == InterfaceCandidateHandle);

        TestTrue(
            TEXT("Interface property can be explicitly cleared"),
            Bridge.WritePropertyById(
                InteropHandle,
                InterfaceValueId,
                FUnifiedScriptValue::FromObjectHandle({}),
                Result) &&
                Interop->InterfaceValue.GetObject() == nullptr);
        TestTrue(
            TEXT("Interface property can be assigned again after null"),
            Bridge.WritePropertyById(InteropHandle, InterfaceValueId, InterfaceValue, Result) &&
                Interop->InterfaceValue.GetObject() == InterfaceCandidate);

        UUnifiedScriptComponent* NonImplementer = NewObject<UUnifiedScriptComponent>(World);
        const FUnifiedScriptObjectHandle NonImplementerHandle =
            Registry.RegisterObject(NonImplementer, HandleResult);
        TestTrue(TEXT("Non-implementer registration succeeds"), HandleResult.IsSuccess());
        TestFalse(
            TEXT("Non-implementer interface write is rejected"),
            Bridge.WritePropertyById(
                InteropHandle,
                InterfaceValueId,
                FUnifiedScriptValue::FromObjectHandle(NonImplementerHandle),
                Result));
        TestTrue(
            TEXT("Non-implementer rejection preserves the interface property"),
            Result.Error == EUnifiedScriptInvocationError::ValueTypeMismatch &&
                Interop->InterfaceValue.GetObject() == InterfaceCandidate);

        UWorld* OtherWorld = UWorld::CreateWorld(EWorldType::Game, false);
        TestNotNull(TEXT("Wrong-world fixture world exists"), OtherWorld);
        if (OtherWorld != nullptr)
        {
            OtherWorld->AddToRoot();
            FUnifiedScriptObjectRegistry OtherRegistry(OtherWorld);
            UUnifiedScriptInteropFixture* OtherInterop = NewObject<UUnifiedScriptInteropFixture>(OtherWorld);
            const FUnifiedScriptObjectHandle OtherHandle =
                OtherRegistry.RegisterObject(OtherInterop, HandleResult);
            TestTrue(TEXT("Wrong-world fixture registration succeeds"), HandleResult.IsSuccess());
            TestFalse(
                TEXT("Wrong-world interface write is rejected"),
                Bridge.WritePropertyById(
                    InteropHandle,
                    InterfaceValueId,
                    FUnifiedScriptValue::FromObjectHandle(OtherHandle),
                    Result));
            TestTrue(
                TEXT("Wrong-world rejection preserves the interface property"),
                Result.Error == EUnifiedScriptInvocationError::ObjectHandleFailure &&
                    Interop->InterfaceValue.GetObject() == InterfaceCandidate);
            OtherWorld->RemoveFromRoot();
            OtherWorld->DestroyWorld(false);
        }

        TestTrue(
            TEXT("Generated FVector read succeeds"),
            Bridge.ReadPropertyById(InteropHandle, VectorValueId, Value, Result));
        FVector VectorValue;
        TestTrue(TEXT("Generated FVector result has the expected kind"), Value.TryGetVector(VectorValue));
        TestTrue(TEXT("Generated FVector read preserves components"), VectorValue.Equals(FVector(1.0, 2.0, 3.0)));

        const FVector WrittenVector(4.0, 5.0, 6.0);
        TestTrue(
            TEXT("Generated FVector write succeeds"),
            Bridge.WritePropertyById(
                InteropHandle,
                VectorValueId,
                FUnifiedScriptValue::FromVector(WrittenVector),
                Result));
        TestTrue(TEXT("Generated FVector write reaches native state"), Interop->VectorValue.Equals(WrittenVector));

        TestTrue(
            TEXT("Generated enum write succeeds"),
            Bridge.WritePropertyById(
                InteropHandle,
                ModeId,
                FUnifiedScriptValue::FromEnum(
                    static_cast<int64>(EUnifiedScriptInteropMode::Active),
                    FName(TEXT("/Script/UEUnifiedScriptRuntime.EUnifiedScriptInteropMode"))),
                Result));
        TestTrue(
            TEXT("Generated enum write retains its declared type"),
            Interop->Mode == EUnifiedScriptInteropMode::Active);

        const TArray<FUnifiedScriptValue> Int64Arguments = {
            FUnifiedScriptValue::FromInt64(40),
            FUnifiedScriptValue::FromInt64(2),
        };
        TestTrue(
            TEXT("Generated int64 invocation succeeds"),
            Bridge.InvokeById(InteropHandle, AddInt64Id, Int64Arguments, Value, Result));
        int64 Int64Result = 0;
        TestTrue(TEXT("Generated int64 result has the expected kind"), Value.TryGetInt64(Int64Result));
        TestEqual(TEXT("Generated int64 invocation returns its value"), Int64Result, static_cast<int64>(42));

        const TArray<FUnifiedScriptValue> VectorArguments = {
            FUnifiedScriptValue::FromVector(FVector(10.0, 20.0, 30.0)),
            FUnifiedScriptValue::FromVector(FVector(1.0, 2.0, 3.0)),
        };
        TestTrue(
            TEXT("Generated FVector invocation succeeds"),
            Bridge.InvokeById(InteropHandle, AddVectorId, VectorArguments, Value, Result));
        TestTrue(TEXT("Generated FVector return decodes"), Value.TryGetVector(VectorValue));
        TestTrue(TEXT("Generated FVector invocation returns its value"), VectorValue.Equals(FVector(11.0, 22.0, 33.0)));

        const FTransform ExpectedTransform(
            FRotator(5.0, 10.0, 15.0),
            FVector(7.0, 8.0, 9.0),
            FVector(2.0));
        const TArray<FUnifiedScriptValue> TransformArguments = {
            FUnifiedScriptValue::FromTransform(ExpectedTransform),
        };
        TestTrue(
            TEXT("Generated FTransform invocation succeeds"),
            Bridge.InvokeById(
                InteropHandle,
                EchoTransformId,
                TransformArguments,
                Value,
                Result));
        FTransform ActualTransform;
        TestTrue(TEXT("Generated FTransform return decodes"), Value.TryGetTransform(ActualTransform));
        TestTrue(TEXT("Generated FTransform invocation returns its value"), ActualTransform.Equals(ExpectedTransform));

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

        UUnifiedScriptInteropFixture* StaleCandidate = NewObject<UUnifiedScriptInteropFixture>(World);
        const FUnifiedScriptObjectHandle StaleCandidateHandle =
            Registry.RegisterObject(StaleCandidate, HandleResult);
        TestTrue(TEXT("Stale interface candidate registration succeeds"), HandleResult.IsSuccess());
        TestTrue(
            TEXT("Stale interface candidate release succeeds"),
            Registry.ReleaseObject(StaleCandidateHandle, HandleResult));
        TestFalse(
            TEXT("Stale interface value handle cannot write a property"),
            Bridge.WritePropertyById(
                InteropHandle,
                InterfaceValueId,
                FUnifiedScriptValue::FromObjectHandle(StaleCandidateHandle),
                Result));
        TestTrue(
            TEXT("Stale interface value write preserves the native property"),
            Result.Error == EUnifiedScriptInvocationError::ObjectHandleFailure &&
                Interop->InterfaceValue.GetObject() == InterfaceCandidate);
    }

    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

#endif
