#if WITH_DEV_AUTOMATION_TESTS

#include "FinareApiDiscoveryComponent.h"
#include "FinareCoreClrHost.h"
#include "FinareGeneratedThunkRegistry.h"
#include "FinareInvocationBridge.h"
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

#endif
