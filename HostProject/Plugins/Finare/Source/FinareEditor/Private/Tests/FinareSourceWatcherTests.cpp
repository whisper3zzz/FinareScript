#if WITH_DEV_AUTOMATION_TESTS
#include "FinareSourceWatcher.h"
#include "HAL/FileManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Misc/Guid.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareSourceWatcherTest,
	"Finare.Unreal.ProjectSetup.SourceSave", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareSourceWatcherTest::RunTest(const FString& Parameters)
{
	TestTrue(TEXT("Ordinary IDE source is watched"), FFinareSourceWatcher::IsSourceInput(TEXT("D:/Game/Managed/Player.cs")));
	TestFalse(TEXT("Generated bindings cannot cause a build loop"), FFinareSourceWatcher::IsSourceInput(TEXT("D:/Game/Managed/Generated/Bindings.cs")));
	TestFalse(TEXT("MSBuild output cannot cause a build loop"), FFinareSourceWatcher::IsSourceInput(TEXT("D:/Game/Managed/obj/Debug/AssemblyInfo.cs")));
	struct FState
	{
		FString Root;
		TUniquePtr<FFinareSourceWatcher> Watcher;
		int32 Calls = 0;
		~FState() { Watcher.Reset(); IFileManager::Get().DeleteDirectory(*Root, false, true); }
	};
	auto State = MakeShared<FState>();
	State->Root = FPaths::Combine(FPaths::ProjectDir(), TEXT("FinareWatchTest-" ) + FGuid::NewGuid().ToString());
	IFileManager::Get().MakeDirectory(*State->Root, true);
	const FString Config = FPaths::Combine(State->Root, TEXT("onboarding.json"));
	FFileHelper::SaveStringToFile(TEXT("{\"managed\":{\"projects\":[\"Game.csproj\"]}}"), *Config);
	State->Watcher = MakeUnique<FFinareSourceWatcher>(Config, [Weak = TWeakPtr<FState>(State)]()
	{
		if (auto Pinned = Weak.Pin()) ++Pinned->Calls;
	});
	double Start = FPlatformTime::Seconds();
	ADD_LATENT_AUTOMATION_COMMAND(FFunctionLatentCommand([State, Start]()
	{
		if (FPlatformTime::Seconds() - Start < 0.5) return false;
		FFileHelper::SaveStringToFile(TEXT("class Player {}"), *FPaths::Combine(State->Root, TEXT("Player.cs")));
		FFileHelper::SaveStringToFile(TEXT("class Enemy {}"), *FPaths::Combine(State->Root, TEXT("Enemy.cs")));
		FFileHelper::SaveStringToFile(TEXT("generated"), *FPaths::Combine(State->Root, TEXT("Bindings.g.cs")));
		return true;
	}));
	ADD_LATENT_AUTOMATION_COMMAND(FFunctionLatentCommand([this, State, Start]()
	{
		if (FPlatformTime::Seconds() - Start < 3.0) return false;
		TestEqual(TEXT("IDE save burst produces one debounced build request"), State->Calls, 1);
		return true;
	}));
	return true;
}
#endif
