#if WITH_DEV_AUTOMATION_TESTS

#include "FinareApiDiscoveryComponent.h"
#include "FinareCoreClrHost.h"
#include "FinareGeneratedThunkRegistry.h"
#include "FinareInvocationBridge.h"
#include "FinareNativeTransport.h"
#include "Engine/World.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareApiDiscoveryAccessTest,
	"Finare.Unreal.ApiDiscovery.PublicReflectionAccess",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareApiDiscoveryAccessTest::RunTest(const FString& Parameters)
{
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("World exists"), World)) return false;
	{
		FFinareObjectRegistry Registry(World);
		FFinareInvocationBridge Bridge(Registry);
		UFinareApiDiscoveryComponent* Component = NewObject<UFinareApiDiscoveryComponent>(World);
		FFinareObjectHandleResult Registered;
		const FFinareObjectHandle Handle = Registry.RegisterObject(Component, Registered);
		FFinareInvocationResult Result;
		FFinareValue Value;
		const FString Prefix = TEXT("/Script/FinareRuntime.FinareApiDiscoveryComponent:");
		const TArray<FFinareValue> Arguments = {FFinareValue::FromInt32(20), FFinareValue::FromInt32(22)};
		TestTrue(TEXT("Ordinary public UFUNCTION executes"), Bridge.Invoke(Handle, Prefix + TEXT("DiscoveryAdd"), Arguments, Value, Result));
		TestEqual(TEXT("Reflected result is 42"), Value.Int32Value, 42);
		TestTrue(TEXT("Ordinary public property writes"), Bridge.WriteProperty(Handle, Prefix + TEXT("DiscoveryResult"), Value, Result));
		TestTrue(TEXT("Ordinary public property reads"), Bridge.ReadProperty(Handle, Prefix + TEXT("DiscoveryResult"), Value, Result));
		TestEqual(TEXT("Property round trip is 42"), Value.Int32Value, 42);
		TestFalse(TEXT("Read-only property rejects writes"), Bridge.WriteProperty(Handle, Prefix + TEXT("DiscoveryReadOnly"), Value, Result));
		TestEqual(TEXT("Read-only value preserved"), Component->DiscoveryReadOnly, 7);
		TestFalse(TEXT("Private property rejects reads"), Bridge.ReadProperty(Handle, Prefix + TEXT("DiscoveryPrivateValue"), Value, Result));
		TestFalse(TEXT("Private function rejects calls"), Bridge.Invoke(Handle, Prefix + TEXT("DiscoveryPrivate"), {}, Value, Result));
	}
	World->RemoveFromRoot();
	World->DestroyWorld(false);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareApiDiscoveryManagedTest,
	"Finare.Unreal.ApiDiscovery.ReflectedManagedCall",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareApiDiscoveryManagedTest::RunTest(const FString& Parameters)
{
	const FString Assembly = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectSavedDir(), TEXT("Finare/ApiDiscovery/Finare.ApiDiscovery.dll")));
	if (!TestTrue(TEXT("Run tools/prepare-api-discovery.ps1 before this test"), FPaths::FileExists(Assembly))) return false;
	uint32 ResultCount = 0;
	TestFalse(TEXT("New member has no generated thunk"), FFinareGeneratedThunkRegistry::DescribeInvokeResultCount(
		FinareMemberId("/Script/FinareRuntime.FinareApiDiscoveryComponent:DiscoveryAdd"), ResultCount));
	const int32 InitialWorldCount = FFinareCoreClrHost::Get().GetManagedWorldCount();
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("Managed fixture World exists"), World)) return false;
	UFinareApiDiscoveryComponent* Component = NewObject<UFinareApiDiscoveryComponent>(World);
	Component->ScriptAssemblyPath = Assembly;
	Component->ScriptTypeName = TEXT("Finare.ApiDiscovery.DiscoveryScript");
	TestTrue(TEXT("Generated C# wrapper starts and invokes UE"), Component->StartScript());
	TestEqual(TEXT("C# invoked the new UFUNCTION and wrote its result"), Component->DiscoveryResult, 42);
	Component->StopScript();
	World->RemoveFromRoot();
	World->DestroyWorld(false);
	TestEqual(TEXT("Managed World lifetime is balanced"), FFinareCoreClrHost::Get().GetManagedWorldCount(), InitialWorldCount);
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareApiLibraryOutputsTest,
	"Finare.Unreal.ApiDiscovery.GenericOutputs",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareApiLibraryOutputsTest::RunTest(const FString& Parameters)
{
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("World exists"), World)) return false;
	{
		FFinareObjectRegistry Registry(World);
		FFinareInvocationBridge Bridge(Registry);
		auto* Context = new FFinareNativeBridgeContext(Bridge);
		auto* Component = NewObject<UFinareApiDiscoveryComponent>(World);
		FFinareObjectHandleResult Registered;
		const auto Handle = Registry.RegisterObject(Component, Registered);
		constexpr uint64 Id = FinareMemberId("/Script/FinareRuntime.FinareApiDiscoveryComponent:DiscoveryOutputs");
		uint32 Count = 0;
		TestFalse(TEXT("Output function has no generated native thunk"),
			FFinareGeneratedThunkRegistry::DescribeInvokeResultCount(Id, Count));
		const auto* Transport = Finare_GetNativeTransportV3();
		TArray<FFinareValue> Descriptors;
		FFinareInvocationResult DescriptionResult;
		TestTrue(TEXT("Reflected result descriptors resolve without a thunk"),
			Bridge.DescribeReflectedInvocation(Handle, Id, Descriptors, DescriptionResult));
		TestEqual(TEXT("Return plus ref and out descriptors"), Descriptors.Num(), 3);
		FFinareNativeValueV3 Args[2];
		Args[0].Kind = Args[1].Kind = EFinareNativeValueKindV3::Int32;
		Args[0].Payload0 = 2; Args[1].Payload0 = 40;
		FFinareNativeValueV3 Results[3];
		Results[0].Kind = EFinareNativeValueKindV3::Int32; // Deliberately wrong return descriptor.
		Results[1].Kind = EFinareNativeValueKindV3::Int32;
		Results[2].Kind = EFinareNativeValueKindV3::String;
		Results[2].Flags = EFinareNativeValueFlagsV3::ArenaBacked;
		uint8 Arena[128] = {};
		FFinareNativeCallFrameV3 Frame;
		auto PrepareFrame = [&](uint32 Capacity)
		{
			Frame = FFinareNativeCallFrameV3{};
			Frame.Arguments = Args; Frame.ArgumentCount = 2;
			Frame.Results = Results; Frame.ResultCount = Frame.ResultCapacity = 3;
			Frame.ByteBuffer = Arena; Frame.ByteCapacity = Capacity;
		};
		PrepareFrame(sizeof(Arena));
		Transport->InvokeCorrelatedById(Context, 123, Handle.Value, Id, &Frame);
		TestTrue(TEXT("Invalid result descriptor fails"), Frame.Status != EFinareNativeCallStatusV3::Success);
		TestEqual(TEXT("Invalid request never executes gameplay"), Component->DiscoveryCalls, 0);
		Results[0].Kind = EFinareNativeValueKindV3::Boolean;
		PrepareFrame(1);
		Transport->InvokeCorrelatedById(Context, 124, Handle.Value, Id, &Frame);
		TestTrue(TEXT("String output requests a larger result arena"), Frame.Status == EFinareNativeCallStatusV3::BufferTooSmall);
		TestEqual(TEXT("First invocation executes once"), Component->DiscoveryCalls, 1);
		PrepareFrame(sizeof(Arena));
		Transport->InvokeCorrelatedById(Context, 124, Handle.Value, Id, &Frame);
		TestTrue(TEXT("Generic ref/out call succeeds"), Frame.Status == EFinareNativeCallStatusV3::Success);
		TestEqual(TEXT("Ref value is published in parameter order"), Results[1].Payload0, 42ull);
		TestEqual(TEXT("Output retry does not execute twice"), Component->DiscoveryCalls, 1);
		TestEqual(TEXT("String output length"), Results[2].Payload1, 8ull);
		Transport->ReleaseCorrelatedResult(Context, 124);
		Component->DiscoveryData.Label = TEXT("before");
		FFinareValue Data;
		const FString DataPath = TEXT("/Script/FinareRuntime.FinareApiDiscoveryComponent:DiscoveryData");
		TestTrue(TEXT("Generic struct reads public supported fields"), Bridge.ReadProperty(Handle, DataPath, Data, DescriptionResult));
		TestEqual(TEXT("Fixed arrays are excluded from the canonical struct layout"), Data.ContainerValues.Num(), 3);
		if (Data.ContainerValues.Num() == 3)
		{
			Data.ContainerValues[0] = FFinareValue::FromString(TEXT("must not leak"));
			Data.ContainerValues[2] = FFinareValue::FromString(TEXT("wrong integer"));
			TestFalse(TEXT("Invalid late struct field rejects the entire write"), Bridge.WriteProperty(Handle, DataPath, Data, DescriptionResult));
			TestEqual(TEXT("Earlier fields remain unchanged after failure"), Component->DiscoveryData.Label, FString(TEXT("before")));
			TestEqual(TEXT("Unprojected native data remains unchanged"), Component->DiscoveryData.Fixed[0], 7);
		}
		Context->Release();
	}
	World->RemoveFromRoot(); World->DestroyWorld(false);
	return true;
}

#endif
