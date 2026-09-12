#if WITH_DEV_AUTOMATION_TESTS

#include "FinareActor.h"
#include "FinareAsset.h"
#include "FinareComponent.h"
#include "FinareCoreClrHost.h"
#include "Engine/World.h"
#include "Engine/Engine.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinarePureCSharpReloadTest,
	"Finare.Unreal.PureCSharp.SourceDrivenReload",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinarePureCSharpReloadTest::RunTest(const FString& Parameters)
{
	const FString Root = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectSavedDir(), TEXT("Finare/PureCSharp")));
	const FString FirstChunk = FPaths::Combine(Root, TEXT("v1/Finare.PureCSharp.uschunk"));
	const FString SecondChunk = FPaths::Combine(Root, TEXT("v2/Finare.PureCSharp.uschunk"));
	if (!TestTrue(TEXT("Run tools/prepare-pure-csharp.ps1 before this test"),
		FPaths::FileExists(FirstChunk) && FPaths::FileExists(SecondChunk))) return false;
	const int32 InitialWorldCount = FFinareCoreClrHost::Get().GetManagedWorldCount();
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("C# fixture World exists"), World)) return false;
	GEngine->CreateNewWorldContext(EWorldType::Game).SetCurrentWorld(World);
	AFinareActor* Actor = World->SpawnActor<AFinareActor>();
	if (TestNotNull(TEXT("Native Actor hosts ordinary C#"), Actor))
	{
		UFinareComponent* Component = Actor->ScriptComponent;
		UFinareAsset* Asset = NewObject<UFinareAsset>(World);
		Asset->CompiledChunkPath = FirstChunk;
		Asset->ScriptTypeName = TEXT("Finare.PureCSharp.FirstScript");
		Asset->DeclarationAssetPath = TEXT("PureCSharp/FirstScript");
		Component->ScriptAsset = Asset;
		const bool bStarted = Component->StartScript();
		TestTrue(*FString::Printf(TEXT("Template starts from its emitted chunk: %s"), *Component->LastScriptError), bStarted);
		TestEqual(TEXT("C# calls generated UE APIs and increments initial state"),
			Component->GetScriptStateInt32(TEXT("StartValue"), -1), 2);
		Component->TickComponent(0.016f, LEVELTICK_All, nullptr);
		Asset->CompiledChunkPath = SecondChunk;
		const bool bReloaded = Component->ReloadScript();
		TestTrue(*FString::Printf(TEXT("Ordinary C# body change reloads: %s"), *Component->LastScriptError), bReloaded);
		TestEqual(TEXT("New body adds two to the preserved state"),
			Component->GetScriptStateInt32(TEXT("StartValue"), -1), 4);
		TestEqual(TEXT("Reload publishes generation two"), Component->ActiveGeneration, FString(TEXT("2")));
		TestTrue(TEXT("No runtime error"), Component->LastScriptError.IsEmpty());
		Component->StopScript();
		Actor->Destroy();
	}
	World->RemoveFromRoot();
	World->DestroyWorld(false);
	GEngine->DestroyWorldContext(World);
	TestEqual(TEXT("Managed World lifetime is balanced"), FFinareCoreClrHost::Get().GetManagedWorldCount(), InitialWorldCount);
	return true;
}

#endif
