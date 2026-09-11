#if WITH_DEV_AUTOMATION_TESTS

#include "FinareInvocationBridge.h"
#include "FinareSoftObjectPath.h"

#include "Async/Async.h"
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "FinareComponent.h"
#include "FinareInteropFixture.h"
#include "FinareWorldLibrary.h"
#include "UObject/UObjectGlobals.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareSoftObjectPathValidationTest,
    "Finare.Unreal.InvocationBridge.SoftObjectPathValidation",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareSoftObjectPathValidationTest::RunTest(const FString& Parameters)
{
    struct FCase
    {
        const TCHAR* Path;
        bool bExpected;
    };
    const FCase Cases[] = {
        {TEXT("/Game/Props/Cube.Cube"), true},
        {TEXT("/Game/Foo(Bar)/Cube.Cube:Mesh.Nested"), true},
        {TEXT("/Script/Engine.StaticMeshActor"), true},
        {TEXT("/Game/A.A:Sub/Object"), false},
        {TEXT("/Game/A.A?B"), false},
        {TEXT("/Game/A.A#B"), false},
        {TEXT("/Game/A.A/:Sub"), false},
        {TEXT("/Game/A"), false},
    };
    for (const FCase& Case : Cases)
    {
        TestEqual(
            FString::Printf(TEXT("Soft path validation: %s"), Case.Path),
            FinareSoftObjectPath::IsValid(Case.Path),
            Case.bExpected);
    }

    const FString TooLongPackage = TEXT("/Game/") + FString::ChrN(1024, TEXT('A')) + TEXT(".A");
    TestFalse(TEXT("Soft path rejects an overlong package FName"),
        FinareSoftObjectPath::IsValid(TooLongPackage));
    const FString TooLongAsset = TEXT("/Game/A.") + FString::ChrN(1024, TEXT('A'));
    TestFalse(TEXT("Soft path rejects an overlong asset FName"),
        FinareSoftObjectPath::IsValid(TooLongAsset));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareStableMemberLocatorValidationTest,
    "Finare.Unreal.InvocationBridge.StableMemberLocatorValidation",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareStableMemberLocatorValidationTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Stable locator fixture world exists"), World);
    if (World == nullptr)
    {
        return false;
    }
    World->AddToRoot();

    {
        FFinareObjectRegistry Registry(World);
        FFinareInvocationBridge Bridge(Registry);
        UFinareComponent* Component = NewObject<UFinareComponent>(World);
        FFinareObjectHandleResult HandleResult;
        const FFinareObjectHandle Handle = Registry.RegisterObject(Component, HandleResult);
        TestTrue(TEXT("Stable locator fixture registration succeeds"), HandleResult.IsSuccess());

        FFinareValue Value;
        FFinareInvocationResult Result;
        const FString UppercasePath =
            TEXT("finare-class/us_0123456789ABCDEF0123456789abcdef:bStartAutomatically");
        TestFalse(
            TEXT("Stable locator rejects uppercase hexadecimal class IDs"),
            Bridge.ReadProperty(Handle, UppercasePath, Value, Result));
        TestTrue(
            TEXT("Uppercase stable locator failure is a member-path error"),
            Result.Error == EFinareInvocationError::InvalidMemberPath);

        const FString ForeignPath =
            TEXT("finare-class/us_0123456789abcdef0123456789abcdef:bStartAutomatically");
        TestFalse(
            TEXT("Stable locator rejects a class ID absent from the target hierarchy"),
            Bridge.ReadProperty(Handle, ForeignPath, Value, Result));
        TestTrue(
            TEXT("Foreign stable locator failure identifies the target hierarchy"),
            Result.Error == EFinareInvocationError::TargetTypeNotFound);
    }

    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return !HasAnyErrors();
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareInvocationBridgeStaticReflectionTest,
    "Finare.Unreal.InvocationBridge.StaticReflection",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareInvocationBridgeStaticReflectionTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    UWorld* OtherWorld = UWorld::CreateWorld(EWorldType::Game, false);
    TestNotNull(TEXT("Static reflection fixture world exists"), World);
    TestNotNull(TEXT("Static reflection foreign world exists"), OtherWorld);
    if (World == nullptr || OtherWorld == nullptr)
    {
        return false;
    }
    World->AddToRoot();
    OtherWorld->AddToRoot();

    {
        FFinareObjectRegistry Registry(World);
        FFinareInvocationBridge Bridge(Registry);
        UFinareComponent* Context = NewObject<UFinareComponent>(World);
        FFinareObjectHandleResult HandleResult;
        const FFinareObjectHandle ContextHandle = Registry.RegisterObject(Context, HandleResult);
        TestTrue(TEXT("Static reflection context registration succeeds"), HandleResult.IsSuccess());

        const TArray<FFinareValue> Arguments = {
            FFinareValue::FromObjectHandle(ContextHandle),
        };
        FFinareValue Value;
        FFinareInvocationResult Result;
        const FString FunctionPath =
            TEXT("/Script/FinareRuntime.FinareWorldLibrary:GetWorldTimeSeconds");

        TestTrue(
            TEXT("Static Blueprint function accepts a valid non-library context object"),
            Bridge.Invoke(ContextHandle, FunctionPath, Arguments, Value, Result));
        float WorldTime = 0.0f;
        TestTrue(TEXT("Static reflection result is a float"), Value.TryGetFloat32(WorldTime));
        TestTrue(TEXT("Static reflection result is a valid world time"), WorldTime >= 0.0f);
        TestEqual(TEXT("Static reflection invocation succeeds"), Result.Error, EFinareInvocationError::None);

        const TArray<FFinareValue> ObjectArguments = {
            FFinareValue::FromObjectHandle(ContextHandle),
            FFinareValue::FromString(UFinareWorldLibrary::StaticClass()->GetPathName()),
        };
        TestTrue(
            TEXT("Static reflection can return an object handle"),
            Bridge.Invoke(
                ContextHandle,
                TEXT("/Script/FinareRuntime.FinareWorldLibrary:LoadObjectByPath"),
                ObjectArguments,
                Value,
                Result));
        FFinareObjectHandle ReturnedHandle;
        TestTrue(
            TEXT("Static object return decodes as a registered object handle"),
            Value.TryGetObjectHandle(ReturnedHandle));
        FFinareObjectHandleResult ReturnedHandleResult;
        TestTrue(
            TEXT("Static object return resolves to the requested class object"),
            Registry.ResolveObject(ReturnedHandle, ReturnedHandleResult) ==
                UFinareWorldLibrary::StaticClass());

        UFinareComponent* GarbageContext = NewObject<UFinareComponent>(World);
        FFinareObjectHandleResult GarbageHandleResult;
        const FFinareObjectHandle StaleHandle =
            Registry.RegisterObject(GarbageContext, GarbageHandleResult);
        TestTrue(TEXT("Garbage context registration succeeds"), GarbageHandleResult.IsSuccess());
        FFinareObjectHandleResult StaleReleaseResult;
        TestTrue(
            TEXT("Static reflection stale context release succeeds"),
            Registry.ReleaseObject(StaleHandle, StaleReleaseResult));
        FFinareInvocationResult StaleResult;
        TestFalse(
            TEXT("Static reflection rejects a garbage context handle"),
            Bridge.Invoke(
                StaleHandle,
                FunctionPath,
                Arguments,
                Value,
                StaleResult));
        TestTrue(
            TEXT("Garbage static context reports a handle failure"),
            StaleResult.Error == EFinareInvocationError::ObjectHandleFailure);

        UFinareComponent* ForeignContext = NewObject<UFinareComponent>(OtherWorld);
        FFinareObjectHandleResult ForeignContextResult;
        const FFinareObjectHandle ForeignContextHandle =
            Registry.RegisterObject(ForeignContext, ForeignContextResult);
        TestTrue(
            TEXT("Cross-world static context registration is rejected"),
            ForeignContextHandle.IsNull() &&
                ForeignContextResult.Error == EFinareObjectHandleError::WorldMismatch);

        // The static-function exception is scoped to Invoke. Property access
        // and delegate binding must still reject a foreign declaring class,
        // even when the member name resolves to that same static function.
        TestFalse(
            TEXT("Property access does not treat a foreign static function as an owner"),
            Bridge.ReadProperty(
                ContextHandle,
                FunctionPath,
                Value,
                Result));
        TestTrue(
            TEXT("Foreign static property lookup reports a target mismatch"),
            Result.Error == EFinareInvocationError::TargetTypeMismatch);

        UObject* EventSource = nullptr;
        const FMulticastDelegateProperty* EventProperty = nullptr;
        TestFalse(
            TEXT("Event binding does not treat a foreign static function as an owner"),
            Bridge.ResolveMulticastDelegate(
                ContextHandle,
                FunctionPath,
                EventSource,
                EventProperty,
                Result));
        TestTrue(
            TEXT("Foreign static event lookup reports a target mismatch"),
            Result.Error == EFinareInvocationError::TargetTypeMismatch);

        UFinareInteropFixture* ForeignInstance = NewObject<UFinareInteropFixture>(World);
        FFinareObjectHandleResult ForeignHandleResult;
        const FFinareObjectHandle ForeignHandle =
            Registry.RegisterObject(ForeignInstance, ForeignHandleResult);
        TestTrue(TEXT("Foreign instance registration succeeds"), ForeignHandleResult.IsSuccess());
        const TArray<FFinareValue> NoArguments;
        TestFalse(
            TEXT("Instance invocation still requires the declaring class"),
            Bridge.Invoke(
                ForeignHandle,
                TEXT("/Script/FinareRuntime.FinareComponent:Add"),
                NoArguments,
                Value,
                Result));
        TestTrue(
            TEXT("Foreign instance invocation reports a target mismatch"),
            Result.Error == EFinareInvocationError::TargetTypeMismatch);
    }

    World->RemoveFromRoot();
    OtherWorld->RemoveFromRoot();
    World->DestroyWorld(false);
    OtherWorld->DestroyWorld(false);
    return !HasAnyErrors();
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FFinareInvocationBridgePrimitivesTest,
    "Finare.Unreal.InvocationBridge.Primitives",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareInvocationBridgePrimitivesTest::RunTest(const FString& Parameters)
{
    static const FString TypePath =
        TEXT("/Script/FinareRuntime.FinareComponent");
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
        FFinareObjectRegistry Registry(World);
        FFinareInvocationBridge Bridge(Registry);
        FFinareObjectHandleResult HandleResult;
        FFinareInvocationResult Result;
        FFinareValue Value;

        UFinareComponent* Component = NewObject<UFinareComponent>(World);
        const FFinareObjectHandle Handle = Registry.RegisterObject(Component, HandleResult);
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
                FFinareValue::FromString(ExpectedAssetPath),
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
                FFinareValue::FromString(TEXT("forbidden")),
                Result));
        TestTrue(
            TEXT("Read-only failure is explicit"),
            Result.Error == EFinareInvocationError::PropertyNotWritable);

        TestFalse(
            TEXT("Property type mismatch rejects writes"),
            Bridge.WriteProperty(
                Handle,
                ScriptAssetPath,
                FFinareValue::FromBoolean(true),
                Result));
        TestTrue(
            TEXT("Type mismatch is explicit"),
            Result.Error == EFinareInvocationError::ValueTypeMismatch);

        const TArray<FFinareValue> AddArguments = {
            FFinareValue::FromInt32(19),
            FFinareValue::FromInt32(23),
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
                TConstArrayView<FFinareValue>(),
                Value,
                Result));
        TestTrue(TEXT("Void call executes native behavior"), Component->ConsumeReloadRequest());
        TestTrue(TEXT("Void call returns a void value"), Value.Kind == EFinareValueKind::Void);

        UFinareInteropFixture* Interop = NewObject<UFinareInteropFixture>(World);
        const FFinareObjectHandle InteropHandle =
            Registry.RegisterObject(Interop, HandleResult);
        TestTrue(TEXT("Common-type fixture registration succeeds"), HandleResult.IsSuccess());
        UFinareInteropFixture* InterfaceCandidate = NewObject<UFinareInteropFixture>(World);
        const FFinareObjectHandle InterfaceCandidateHandle =
            Registry.RegisterObject(InterfaceCandidate, HandleResult);
        TestTrue(TEXT("Native interface candidate registration succeeds"), HandleResult.IsSuccess());

        constexpr uint64 VectorValueId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:VectorValue");
        constexpr uint64 ModeId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:Mode");
        constexpr uint64 AddInt64Id = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:AddInt64");
        constexpr uint64 AddVectorId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:AddVector");
        constexpr uint64 EchoTransformId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:EchoTransform");
        constexpr uint64 InterfaceValueId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:InterfaceValue");
        constexpr uint64 EchoInterfaceId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:EchoInterface");
        constexpr uint64 MutateInterfacesId = FinareMemberId(
            "/Script/FinareRuntime.FinareInteropFixture:MutateInterfaces");
        static const FString InterfaceValuePath =
            TEXT("/Script/FinareRuntime.FinareInteropFixture:InterfaceValue");
        static const FString EchoInterfacePath =
            TEXT("/Script/FinareRuntime.FinareInteropFixture:EchoInterface");

        TestTrue(
            TEXT("Null interface property read succeeds"),
            Bridge.ReadPropertyById(InteropHandle, InterfaceValueId, Value, Result));
        FFinareObjectHandle DecodedInterfaceHandle;
        TestTrue(
            TEXT("Null interface property uses the object ABI kind"),
            Value.TryGetObjectHandle(DecodedInterfaceHandle) && DecodedInterfaceHandle.IsNull());

        const FFinareValue InterfaceValue =
            FFinareValue::FromObjectHandle(InterfaceCandidateHandle);
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

        const TArray<FFinareValue> EchoInterfaceArguments = {InterfaceValue};
        TestTrue(
            TEXT("Scalar interface return invocation succeeds"),
            Bridge.Invoke(InteropHandle, EchoInterfacePath, EchoInterfaceArguments, Value, Result));
        TestTrue(
            TEXT("Scalar interface return uses the object ABI kind"),
            Value.TryGetObjectHandle(DecodedInterfaceHandle) &&
                DecodedInterfaceHandle == InterfaceCandidateHandle);

        const FString InterfaceConcreteOnlyPath =
            TEXT("/Script/FinareRuntime.FinareInteropContract:VectorValue");
        const TConstArrayView<FFinareValue> NoInterfaceArguments;
        TestFalse(
            TEXT("Interface fallback rejects members absent from the interface declaration"),
            Bridge.Invoke(InteropHandle, InterfaceConcreteOnlyPath, NoInterfaceArguments, Value, Result));
        TestTrue(
            TEXT("Interface fallback reports an invalid interface member"),
            Result.Error == EFinareInvocationError::InvalidMemberPath);

        const TArray<FFinareValue> MutateInterfaceArguments = {InterfaceValue, InterfaceValue};
        TArray<FFinareValue> MutateInterfaceOutputs;
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
        FFinareObjectHandle OutInterfaceHandle;
        FFinareObjectHandle AccumulatorHandle;
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
                FFinareValue::FromObjectHandle({}),
                Result) &&
                Interop->InterfaceValue.GetObject() == nullptr);
        TestTrue(
            TEXT("Interface property can be assigned again after null"),
            Bridge.WritePropertyById(InteropHandle, InterfaceValueId, InterfaceValue, Result) &&
                Interop->InterfaceValue.GetObject() == InterfaceCandidate);

        UFinareComponent* NonImplementer = NewObject<UFinareComponent>(World);
        const FFinareObjectHandle NonImplementerHandle =
            Registry.RegisterObject(NonImplementer, HandleResult);
        TestTrue(TEXT("Non-implementer registration succeeds"), HandleResult.IsSuccess());
        TestFalse(
            TEXT("Non-implementer interface write is rejected"),
            Bridge.WritePropertyById(
                InteropHandle,
                InterfaceValueId,
                FFinareValue::FromObjectHandle(NonImplementerHandle),
                Result));
        TestTrue(
            TEXT("Non-implementer rejection preserves the interface property"),
            Result.Error == EFinareInvocationError::ValueTypeMismatch &&
                Interop->InterfaceValue.GetObject() == InterfaceCandidate);

        UWorld* OtherWorld = UWorld::CreateWorld(EWorldType::Game, false);
        TestNotNull(TEXT("Wrong-world fixture world exists"), OtherWorld);
        if (OtherWorld != nullptr)
        {
            OtherWorld->AddToRoot();
            FFinareObjectRegistry OtherRegistry(OtherWorld);
            UFinareInteropFixture* OtherInterop = NewObject<UFinareInteropFixture>(OtherWorld);
            const FFinareObjectHandle OtherHandle =
                OtherRegistry.RegisterObject(OtherInterop, HandleResult);
            TestTrue(TEXT("Wrong-world fixture registration succeeds"), HandleResult.IsSuccess());
            TestFalse(
                TEXT("Wrong-world interface write is rejected"),
                Bridge.WritePropertyById(
                    InteropHandle,
                    InterfaceValueId,
                    FFinareValue::FromObjectHandle(OtherHandle),
                    Result));
            TestTrue(
                TEXT("Wrong-world rejection preserves the interface property"),
                Result.Error == EFinareInvocationError::ObjectHandleFailure &&
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
                FFinareValue::FromVector(WrittenVector),
                Result));
        TestTrue(TEXT("Generated FVector write reaches native state"), Interop->VectorValue.Equals(WrittenVector));

        TestTrue(
            TEXT("Generated enum write succeeds"),
            Bridge.WritePropertyById(
                InteropHandle,
                ModeId,
                FFinareValue::FromEnum(
                    static_cast<int64>(EFinareInteropMode::Active),
                    FName(TEXT("/Script/FinareRuntime.EFinareInteropMode"))),
                Result));
        TestTrue(
            TEXT("Generated enum write retains its declared type"),
            Interop->Mode == EFinareInteropMode::Active);

        const TArray<FFinareValue> Int64Arguments = {
            FFinareValue::FromInt64(40),
            FFinareValue::FromInt64(2),
        };
        TestTrue(
            TEXT("Generated int64 invocation succeeds"),
            Bridge.InvokeById(InteropHandle, AddInt64Id, Int64Arguments, Value, Result));
        int64 Int64Result = 0;
        TestTrue(TEXT("Generated int64 result has the expected kind"), Value.TryGetInt64(Int64Result));
        TestEqual(TEXT("Generated int64 invocation returns its value"), Int64Result, static_cast<int64>(42));

        const TArray<FFinareValue> VectorArguments = {
            FFinareValue::FromVector(FVector(10.0, 20.0, 30.0)),
            FFinareValue::FromVector(FVector(1.0, 2.0, 3.0)),
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
        const TArray<FFinareValue> TransformArguments = {
            FFinareValue::FromTransform(ExpectedTransform),
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
                TConstArrayView<FFinareValue>(AddArguments.GetData(), 1),
                Value,
                Result));
        TestTrue(
            TEXT("Argument count failure is explicit"),
            Result.Error == EFinareInvocationError::ArgumentCountMismatch);

        const TArray<FFinareValue> InvalidArguments = {
            FFinareValue::FromString(TEXT("nineteen")),
            FFinareValue::FromInt32(23),
        };
        TestFalse(
            TEXT("Incorrect argument type is rejected"),
            Bridge.Invoke(Handle, AddPath, InvalidArguments, Value, Result));
        TestTrue(
            TEXT("Argument type failure is explicit"),
            Result.Error == EFinareInvocationError::ValueTypeMismatch);

        const TFuture<EFinareObjectHandleError> OffThreadResult = Async(
            EAsyncExecution::ThreadPool,
            [&Bridge, Handle]()
            {
                FFinareInvocationResult ThreadResult;
                FFinareValue ThreadValue;
                Bridge.ReadProperty(Handle, StartAutomaticallyPath, ThreadValue, ThreadResult);
                return ThreadResult.HandleError;
            });
        TestTrue(
            TEXT("Off-thread invocation is rejected by the handle authority"),
            OffThreadResult.Get() == EFinareObjectHandleError::NotGameThread);

        TestTrue(TEXT("Fixture release succeeds"), Registry.ReleaseObject(Handle, HandleResult));
        TestFalse(
            TEXT("Stale handle cannot read a property"),
            Bridge.ReadProperty(Handle, StartAutomaticallyPath, Value, Result));
        TestTrue(
            TEXT("Stale handle failure preserves the lifecycle category"),
            Result.Error == EFinareInvocationError::ObjectHandleFailure &&
                Result.HandleError == EFinareObjectHandleError::StaleHandle);

        UFinareInteropFixture* StaleCandidate = NewObject<UFinareInteropFixture>(World);
        const FFinareObjectHandle StaleCandidateHandle =
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
                FFinareValue::FromObjectHandle(StaleCandidateHandle),
                Result));
        TestTrue(
            TEXT("Stale interface value write preserves the native property"),
            Result.Error == EFinareInvocationError::ObjectHandleFailure &&
                Interop->InterfaceValue.GetObject() == InterfaceCandidate);
    }

    World->RemoveFromRoot();
    World->DestroyWorld(false);
    return true;
}

#endif
