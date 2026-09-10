#if WITH_DEV_AUTOMATION_TESTS
#include "Async/Async.h"
#include "Engine/World.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
#include "FinareCallbackProxy.h"
#include "FinareCallbackService.h"
#include "FinareNativeValueCodec.h"
#include "FinareWorldSubsystem.h"
#include "UObject/GarbageCollection.h"
#include "UObject/UnrealType.h"

namespace UE::Finare::CallbackTests
{
using Status = EFinareCallbackStatus;
struct FHandler
{
    int32 Calls = 0, Releases = 0;
    bool bInvalidOutput = false, bReleaseSelf = false;
    FFinareNativeBridgeContext* Context = nullptr;
    TArray<FFinareNativeValueV3> Cells;
    TArray<uint8> Bytes;

    static void Receive(void* UserData, const FFinareCallbackRequest* Request, FFinareCallbackResponse* Response)
    {
        using namespace UE::Finare::NativeTransport::Private;
        auto& Self = *static_cast<FHandler*>(UserData);
        ++Self.Calls;
        if (Request->ArgumentCount != 3) return;
        FFinareNativeCallFrameV3 Frame;
        Frame.ByteBuffer = const_cast<uint8*>(Request->ByteBuffer);
        Frame.ByteCapacity = Request->ByteCount;
        FFinareValue Value, Text;
        if (!DecodeValueV3(Request->Arguments[0], Frame, Request->ByteCount, Value, 0) ||
            !DecodeValueV3(Request->Arguments[1], Frame, Request->ByteCount, Text, 0)) return;
        if (Self.bReleaseSelf) Self.Context->ReleaseCallback(Request->Scope, Request->Token);
        const FFinareValue Results[] = {
            FFinareValue::FromBoolean(Value.Int32Value == 7),
            FFinareValue::FromString(TEXT("reply:") + Text.StringValue),
            Self.bInvalidOutput ? FFinareValue::FromString(TEXT("invalid-vector"))
                : FFinareValue::FromVector(FVector(Value.Int32Value, 2, 3))
        };
        Self.Cells.SetNum(3);
        Self.Bytes.SetNum(4096);
        Frame.ByteBuffer = Self.Bytes.GetData();
        Frame.ByteCapacity = Self.Bytes.Num();
        uint32 Count = 0;
        for (int32 Index = 0; Index < 3; ++Index)
            if (!EncodeValueV3(Results[Index], Frame, Self.Cells[Index], Count, 0)) return;
        Response->Status = Status::Success;
        Response->Results = Self.Cells.GetData();
        Response->ResultCount = 3;
        Response->ByteBuffer = Self.Bytes.GetData();
        Response->ByteCount = Count;
        Response->Owner = &Self;
        Response->Release = [](void* Owner) { ++static_cast<FHandler*>(Owner)->Releases; };
    }
};
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareCallbackTransportTest,
    "Finare.Unreal.Callbacks.NativeLifecycle", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FFinareCallbackTransportTest::RunTest(const FString& Parameters)
{
    using namespace UE::Finare::CallbackTests;
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    UWorld* OtherWorld = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Callback World"), World) || !TestNotNull(TEXT("Other World"), OtherWorld)) return false;
    {
        FFinareObjectRegistry Registry(World), OtherRegistry(OtherWorld);
        FFinareInvocationBridge Bridge(Registry);
        auto* Context = new FFinareNativeBridgeContext(Bridge);
        FHandler Handler;
        Handler.Context = Context;
        UFunction* Store = UFinareCallbackService::StaticClass()->FindFunctionByName(TEXT("StoreCallback"));
        FDelegateProperty* Property = FindFProperty<FDelegateProperty>(Store, TEXT("Callback"));
        if (TestNotNull(TEXT("Reflected callback property"), Property))
        {
            const FString SignaturePath = Property->SignatureFunction->GetPathName();
            const uint64 SignatureId = FinareMemberId(TCHAR_TO_UTF8(*SignaturePath));
            FProperty* TextProperty = FindFProperty<FProperty>(Property->SignatureFunction, TEXT("Text"));
            TestTrue(TEXT("UHT retains explicit ref input"), TextProperty && TextProperty->HasAllPropertyFlags(CPF_ReferenceParm | CPF_OutParm));
            const auto* Table = Finare_GetCallbackTransport();
            TestEqual(TEXT("Callback table is appended to V2"), Finare_GetNativeTransportV2()->CallbackTransport, Table);
            uint64 Scope = 0, OtherScope = 0, Token = 0;
            TestTrue(TEXT("Open callback scope"), Table->OpenScope(Context, &Scope) == Status::Success);
            Table->OpenScope(Context, &OtherScope);
            FTCHARToUTF8 Path(*SignaturePath);
            auto Register = [&]
            {
                return Table->RegisterCallback(Context, Scope, reinterpret_cast<const uint8*>(Path.Get()), Path.Length(), &FHandler::Receive, &Handler, &Token);
            };
            TestTrue(TEXT("Register reflected callback"), Register() == Status::Success);
            FFinareInvocationResult Result;
            FScriptDelegate Delegate;
            const auto Value = FFinareValue::FromCallbackToken(Token, Scope, SignatureId);
            TestFalse(TEXT("Other World rejected"), Finare_ResolveCallback(OtherRegistry, Value, SignatureId, Delegate, Result));
            TestFalse(TEXT("Wrong signature rejected"), Finare_ResolveCallback(Registry, Value, SignatureId + 1, Delegate, Result));
            TestFalse(TEXT("Wrong scope rejected"), Finare_ResolveCallback(Registry,
                FFinareValue::FromCallbackToken(Token, OtherScope, SignatureId), SignatureId, Delegate, Result));
            TestTrue(TEXT("Correct callback resolved"), Finare_ResolveCallback(Registry, Value, SignatureId, Delegate, Result));
            FFinareQueryCallback Callback;
            Callback.BindUFunction(Delegate.GetUObject(), Delegate.GetFunctionName());
            CollectGarbage(RF_NoFlags);
            FString Text = TEXT("input");
            FVector Position = FVector::ZeroVector;
            TestTrue(TEXT("Native receives callback return"), Callback.Execute(7, Text, Position));
            TestEqual(TEXT("Native receives ref output"), Text, FString(TEXT("reply:input")));
            TestEqual(TEXT("Native receives out value"), Position, FVector(7, 2, 3));
            TestEqual(TEXT("Handler invoked once"), Handler.Calls, 1);
            TestEqual(TEXT("Response released once"), Handler.Releases, 1);

            Handler.bInvalidOutput = true;
            Text = TEXT("preserved"); Position = FVector(8, 9, 10);
            AddExpectedError(TEXT("callback outputs were not published"), EAutomationExpectedErrorFlags::Contains, 1);
            TestFalse(TEXT("Invalid output returns default"), Callback.Execute(7, Text, Position));
            TestEqual(TEXT("Invalid output does not partly publish ref"), Text, FString(TEXT("preserved")));
            TestEqual(TEXT("Invalid output does not partly publish out"), Position, FVector(8, 9, 10));
            Handler.bInvalidOutput = false;
            TestTrue(TEXT("Worker release is retryable"), Async(EAsyncExecution::ThreadPool, [&]
                { return Table->ReleaseCallback(Context, Scope, Token); }).Get() == Status::WrongThread);
            AddExpectedError(TEXT("callback invocation requires the game thread"), EAutomationExpectedErrorFlags::Contains, 1);
            TestTrue(TEXT("Worker invocation never reaches managed code or mutates ref/out"), Async(EAsyncExecution::ThreadPool, [&]
            {
                FString WorkerText = TEXT("worker"); FVector WorkerPosition(8, 9, 10);
                return !Callback.Execute(7, WorkerText, WorkerPosition) && WorkerText == TEXT("worker") && WorkerPosition == FVector(8, 9, 10);
            }).Get());
            TestEqual(TEXT("Worker invocation did not run handler"), Handler.Calls, 2);
            TestTrue(TEXT("Other owner cannot release"), Table->ReleaseCallback(Context, OtherScope, Token) == Status::WrongOwner);
            Handler.bReleaseSelf = true;
            Text = TEXT("self");
            TestTrue(TEXT("Self-disposal still delivers current result"), Callback.Execute(7, Text, Position));
            TestEqual(TEXT("Current handler kept alive"), Text, FString(TEXT("reply:self")));
            const int32 Before = Handler.Calls;
            TestFalse(TEXT("Saved native copy is disabled immediately"), Callback.Execute(7, Text, Position));
            TestEqual(TEXT("Retired copy does not invoke handler"), Handler.Calls, Before);
            TestFalse(TEXT("Retired argument rejected"), Finare_ResolveCallback(Registry, Value, SignatureId, Delegate, Result));
            CollectGarbage(RF_NoFlags);
            TestFalse(TEXT("Retired proxy collected"), Callback.IsBound());

            Handler.bReleaseSelf = false;
            TestTrue(TEXT("Register another callback"), Register() == Status::Success);
            Finare_ResolveCallback(Registry, FFinareValue::FromCallbackToken(Token, Scope, SignatureId), SignatureId, Delegate, Result);
            Callback.BindUFunction(Delegate.GetUObject(), Delegate.GetFunctionName());
            Context->Detach();
            TestFalse(TEXT("World detach disables retained copies"), Callback.Execute(7, Text, Position));
            TestEqual(TEXT("Detached callback has no managed side effect"), Handler.Calls, Before);
            TestTrue(TEXT("Detached close is unavailable"), Table->CloseScope(Context, Scope) == Status::Unavailable);
            TestEqual(TEXT("Every owned response released"), Handler.Releases, Handler.Calls);
        }
        Context->Detach();
        Context->Release();
    }
    OtherWorld->RemoveFromRoot(); OtherWorld->DestroyWorld(false);
    World->RemoveFromRoot(); World->DestroyWorld(false);
    return true;
}
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareCallbackManagedTest,
    "Finare.Unreal.Callbacks.GeneratedManagedLifetime", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FFinareCallbackManagedTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Managed callback World"), World)) return false;
    auto* Host = World->GetSubsystem<UFinareWorldSubsystem>();
    const auto Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
    if (TestNotNull(TEXT("World host"), Host) && TestTrue(TEXT("Plugin"), Plugin.IsValid()))
    {
        auto* Context = Host->GetNativeInvocationContext();
        TStrongObjectPtr<UFinareCallbackService> Service(NewObject<UFinareCallbackService>(World));
        FFinareObjectHandleResult HandleResult;
        const auto Handle = Host->RegisterObject(Service.Get(), HandleResult);
        const FString Assembly = FPaths::Combine(Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo/Finare.Unreal.Demo.dll"));
        const FString Type = TEXT("Finare.Unreal.Demo.CallbackGameplayDemoScript");
        const FString Failure = TEXT("Finare.Unreal.Demo.CallbackStartFailureDemoScript");
        FString Error;
        TestFalse(TEXT("Failed startup rejects callback generation"), Host->CreateManagedScript(7050, Handle, Handle, Assembly, Failure, Error));
        TestTrue(*Error, Error.Contains(TEXT("expected startup failure")));
        TestEqual(TEXT("Failed startup leaves no callbacks"), Context->GetCallbackCount(), 0);
        const bool bStarted = Host->CreateManagedScript(7051, Handle, Handle, Assembly, Type, Error);
        TestTrue(*FString::Printf(TEXT("Generated callback startup: %s"), *Error), bStarted);
        if (bStarted)
        {
            TestEqual(TEXT("One retained callback"), Context->GetCallbackCount(), 1);
            CollectGarbage(RF_NoFlags);
            FString Text = TEXT("native"); FVector Position;
            TestTrue(TEXT("Native invokes the retained managed callback"), Service->Evaluate(7, Text, Position));
            TestEqual(TEXT("Native receives arena-backed managed ref string"), Text, FString::ChrN(9000, TEXT('x')) + TEXT("native"));
            TestEqual(TEXT("Native receives managed vector output"), Position, FVector(7, 2, 3));
            TestTrue(TEXT("Generated call from managed tick"), Host->TickManagedScript(7051, 0.016f, Error));
            int32 Generation = 0;
            TestFalse(TEXT("Reject candidate after callback registration"), Host->ReloadManagedScript(7051, Assembly, Failure, Generation, Error));
            TestTrue(*Error, Error.Contains(TEXT("expected startup failure")));
            TestEqual(TEXT("Rollback retires only candidate callback"), Context->GetCallbackCount(), 1);
            TestTrue(TEXT("Retained generation callback works after rollback"), Host->TickManagedScript(7051, 0.016f, Error));
            TestTrue(TEXT("Reload replaces callback generation"), Host->ReloadManagedScript(7051, Assembly, Type, Generation, Error));
            TestEqual(TEXT("Reload does not retain the old callback"), Context->GetCallbackCount(), 1);
            TestTrue(TEXT("New generation invokes callback"), Host->TickManagedScript(7051, 0.016f, Error));
            TestTrue(TEXT("Destroy script"), Host->DestroyManagedScript(7051, Error));
            TestEqual(TEXT("Destroy disables callback without user Dispose"), Context->GetCallbackCount(), 0);
            Text = TEXT("destroyed");
            TestFalse(TEXT("Native retained slot cannot invoke destroyed script"), Service->Evaluate(7, Text, Position));
            TestEqual(TEXT("Retired call preserves ref value"), Text, FString(TEXT("destroyed")));
            TestTrue(TEXT("Start another callback for World teardown"), Host->CreateManagedScript(7052, Handle, Handle, Assembly, Type, Error));
            Context->TryRetain();
            World->RemoveFromRoot(); World->DestroyWorld(false);
            TestEqual(TEXT("World teardown clears all callback proxies"), Context->GetCallbackCount(), 0);
            Text = TEXT("world");
            TestFalse(TEXT("Native slot disabled after World teardown"), Service->Evaluate(7, Text, Position));
            Context->Release();
            World = nullptr;
        }
    }
    if (World) { World->RemoveFromRoot(); World->DestroyWorld(false); }
    return true;
}
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareCallbackWorkerTest,
    "Finare.Unreal.Callbacks.WorkerReentry", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FFinareCallbackWorkerTest::RunTest(const FString& Parameters)
{
    UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
    if (!TestNotNull(TEXT("Worker callback World"), World)) return false;
    auto* Host = World->GetSubsystem<UFinareWorldSubsystem>();
    const auto Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
    if (TestNotNull(TEXT("Host"), Host) && TestTrue(TEXT("Plugin"), Plugin.IsValid()))
    {
        auto* Context = Host->GetNativeInvocationContext();
        TStrongObjectPtr<UFinareCallbackService> Service(NewObject<UFinareCallbackService>(World));
        FFinareObjectHandleResult HandleResult;
        const auto Handle = Host->RegisterObject(Service.Get(), HandleResult);
        const FString Assembly = FPaths::Combine(Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo/Finare.Unreal.Demo.dll"));
        FString Error;
        if (TestTrue(TEXT("Start two worker queries"), Host->CreateManagedScript(7053, Handle, Handle, Assembly,
            TEXT("Finare.Unreal.Demo.CallbackWorkerDemoScript"), Error)))
        {
            const double Deadline = FPlatformTime::Seconds() + 10.0;
            // Queue both forward calls before dispatching either. Callbacks then
            // reenter this same context on the game thread, including arena growth.
            while (Context->GetGlobalPendingWorkerDispatchCount() < 2 && FPlatformTime::Seconds() < Deadline)
                FPlatformProcess::Sleep(0.001f);
            TestTrue(TEXT("Both workers queued"), Context->GetGlobalPendingWorkerDispatchCount() >= 2);
            bool bTickSucceeded = true;
            while (Service->LastQueryValue != 200 && FPlatformTime::Seconds() < Deadline && bTickSucceeded)
            {
                Context->PumpGameThreadCalls();
                bTickSucceeded = Host->TickManagedScript(7053, 0.016f, Error);
                FPlatformProcess::Sleep(0.001f);
            }
            TestTrue(*FString::Printf(TEXT("Worker results verified: %s"), *Error), bTickSucceeded);
            TestEqual(TEXT("Both results and ref/out values delivered"), Service->LastQueryValue, 200);
            TestEqual(TEXT("Native calls executed exactly once each"), Service->EvaluationCount, 2);
            TestEqual(TEXT("All correlated results consumed"), Context->GetCorrelatedResultCount(), 0);
            Context->SuspendWorkerDispatch(); // Also releases queued work on test failure.
            TestTrue(TEXT("Worker script destroyed"), Host->DestroyManagedScript(7053, Error));
            TestEqual(TEXT("Worker callback released with generation"), Context->GetCallbackCount(), 0);
        }
    }
    World->RemoveFromRoot(); World->DestroyWorld(false);
    return true;
}
#endif
