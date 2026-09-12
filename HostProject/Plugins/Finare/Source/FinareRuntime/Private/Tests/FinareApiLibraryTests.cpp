#if WITH_DEV_AUTOMATION_TESTS

#include "FinareApiDiscoveryComponent.h"
#include "FinareAsset.h"
#include "FinareCoreClrHost.h"
#include "FinareGeneratedThunkRegistry.h"
#include "Engine/World.h"
#include "Engine/Engine.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareApiLibraryReloadTest,
	"Finare.Unreal.ApiLibrary.NewCallReload",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareApiLibraryReloadTest::RunTest(const FString& Parameters)
{
	const FString Root = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectSavedDir(), TEXT("Finare/ApiLibrary")));
	const FString First = FPaths::Combine(Root, TEXT("v1/Finare.ApiLibrary.uschunk"));
	const FString Second = FPaths::Combine(Root, TEXT("v2/Finare.ApiLibrary.uschunk"));
	if (!TestTrue(TEXT("Run tools/prepare-api-library.ps1 before this test"),
		FPaths::FileExists(First) && FPaths::FileExists(Second))) return false;
	uint32 Count = 0;
	TestFalse(TEXT("New scalar call has no generated thunk"), FFinareGeneratedThunkRegistry::DescribeInvokeResultCount(
		FinareMemberId("/Script/FinareRuntime.FinareApiDiscoveryComponent:DiscoveryOffset"), Count));
	TestFalse(TEXT("New ref/out call has no generated thunk"), FFinareGeneratedThunkRegistry::DescribeInvokeResultCount(
		FinareMemberId("/Script/FinareRuntime.FinareApiDiscoveryComponent:DiscoveryOutputs"), Count));
	const int32 InitialWorldCount = FFinareCoreClrHost::Get().GetManagedWorldCount();
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("Fixture World exists"), World)) return false;
	GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(World);
	UFinareApiDiscoveryComponent* Component = NewObject<UFinareApiDiscoveryComponent>(World);
	UFinareAsset* Asset = NewObject<UFinareAsset>(World);
	Asset->CompiledChunkPath = First;
	Asset->ScriptTypeName = TEXT("Finare.ApiLibrary.FirstScript");
	Asset->DeclarationAssetPath = TEXT("ApiLibrary/FirstScript");
	Component->ScriptAsset = Asset;
	TestTrue(*FString::Printf(TEXT("Initial C# chunk starts: %s"), *Component->LastScriptError), Component->StartScript());
	TestEqual(TEXT("Initial body uses available API"), Component->DiscoveryResult, 42);
	Asset->CompiledChunkPath = Second;
	const bool bReloaded = Component->ReloadScript();
	TestTrue(*FString::Printf(TEXT("Adding unused API calls reloads in the same process: %s"), *Component->LastScriptError), bReloaded);
	TestEqual(TEXT("New scalar and struct ref/out calls update preserved state"), Component->DiscoveryResult, 55);
	TestEqual(TEXT("Generic struct property receives the new value"), Component->DiscoveryData.Value, 54);
	TestEqual(TEXT("Generic struct collection field round trips"), Component->DiscoveryData.Numbers.Num(), 2);
	TestEqual(TEXT("Unsupported fixed-array fields remain intact"), Component->DiscoveryData.Fixed[0], 7);
	TestEqual(TEXT("Managed string output arrives"), Component->ManagedState, FString(TEXT("total=54")));
	TestEqual(TEXT("Managed ref/out call executes once"), Component->DiscoveryCalls, 1);
	TestEqual(TEXT("Generation two is published"), Component->ActiveGeneration, FString(TEXT("2")));
	TestTrue(TEXT("No runtime error"), Component->LastScriptError.IsEmpty());
	Component->StopScript();
	World->RemoveFromRoot(); World->DestroyWorld(false);
	GEngine->DestroyWorldContext(World);
	TestEqual(TEXT("Managed World lifetime is balanced"), FFinareCoreClrHost::Get().GetManagedWorldCount(), InitialWorldCount);
	return true;
}

#endif
