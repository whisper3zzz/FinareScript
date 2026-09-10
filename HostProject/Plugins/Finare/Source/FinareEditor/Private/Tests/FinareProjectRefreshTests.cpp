#if WITH_DEV_AUTOMATION_TESTS

#include "Widgets/SFinareAuthoringSurface.h"
#include "Widgets/Input/SEditableTextBox.h"
#include "FinareEditorModule.h"
#include "FinareActor.h"
#include "FinareComponent.h"
#include "Editor.h"
#include "Engine/World.h"
#include "GameFramework/GameModeBase.h"
#include "HAL/FileManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "PlayInEditorDataTypes.h"
#include "Settings/LevelEditorPlaySettings.h"
#include "Tests/AutomationEditorCommon.h"

// An opt-in live project test. The PowerShell driver owns the server and disk
// edit; this command uses the same Slate refresh and build coordinator as users.
class FFinareProjectRefreshCommand final : public IAutomationLatentCommand
{
    FAutomationTestBase& Test;
    TSharedPtr<SFinareAuthoringSurface> Surface;
    TWeakObjectPtr<AFinareActor> Actor;
    FString TestUrl;
    FString Signal;
    FString OriginalGeneration;
    double Started = FPlatformTime::Seconds();
    int32 Phase = 0;
    bool bPreviousPlay;

    bool CheckHeight(float Expected)
    {
        FFinareManagedFunctionValueV1 Value;
        FString Error;
        if (!Test.TestTrue(TEXT("Invoke the live managed PreviewHeight"),
            Actor->GetFinareComponent()->InvokeManagedScriptFunction(TEXT("PreviewHeight"), {}, Value, Error)))
        {
            Test.AddError(Error);
            return false;
        }
        const uint32 Bits = static_cast<uint32>(Value.Payload);
        float Height;
        FMemory::Memcpy(&Height, &Bits, sizeof(Height));
        return Test.TestTrue(TEXT("PreviewHeight returns a float"), Value.Kind == EFinareManagedFunctionValueKindV1::Single) &&
            Test.TestEqual(TEXT("Live behavior matches the authored value"), Height, Expected);
    }

public:
    FFinareProjectRefreshCommand(FAutomationTestBase& InTest, const FString& Url, const FString& InSignal)
        : Test(InTest), TestUrl(Url), Signal(InSignal)
    {
        Surface = SNew(SFinareAuthoringSurface);
        FAutomationEditorCommonUtils::CreateNewMap();
        auto* Settings = NewObject<ULevelEditorPlaySettings>();
        Settings->SetPlayNetMode(EPlayNetMode::PIE_Standalone);
        Settings->SetPlayNumberOfClients(1);
        Settings->SetRunUnderOneProcess(true);
        Settings->GameGetsMouseControl = false;
        FRequestPlaySessionParams Session;
        Session.EditorPlaySettings = Settings;
        Session.GameModeOverride = AGameModeBase::StaticClass();
        Session.bAllowOnlineSubsystem = false;
        // Initial outputs are prepared by the driver; the changed-source build
        // below still runs the real editor-attached coordinator and component swap.
        bPreviousPlay = IFinareEditorModule::Get().SetPrecompiledAutomationPlay(true);
        GEditor->RequestPlaySession(Session);
    }

    virtual ~FFinareProjectRefreshCommand() override
    {
        Surface.Reset();
        if (GEditor->PlayWorld) GEditor->EndPlayMap();
        IFinareEditorModule::Get().SetPrecompiledAutomationPlay(bPreviousPlay);
    }

    virtual bool Update() override
    {
        if (FPlatformTime::Seconds() - Started > 180)
        {
            Test.AddError(FString::Printf(TEXT("Project refresh timed out in phase %d."), Phase));
            return true;
        }
        if (Phase == 0)
        {
            UWorld* World = GEditor->PlayWorld;
            if (!World || !World->HasBegunPlay() || Surface->bPatchRequestPending) return false;
            Test.TestFalse(TEXT("Graph-only tab has not fetched C# yet"), Surface->HasCanonicalSourceSnapshot());
            UClass* Class = LoadClass<AFinareActor>(nullptr, TEXT("/Game/UnifiedScriptDemo/BP_AsyncUiDemo.BP_AsyncUiDemo_C"));
            if (!Test.TestNotNull(TEXT("Persistent Script Class exists"), Class)) return true;
            Actor = World->SpawnActor<AFinareActor>(Class);
            if (!Test.TestNotNull(TEXT("Spawn the live Script Class"), Actor.Get()) || !CheckHeight(100)) return true;
            if (!Test.TestTrue(TEXT("Open the real Script Asset in Graph view"),
                Surface->LoadScriptAsset(Actor->GetFinareComponent()->ScriptAsset))) return true;
            // Loading the asset applies its saved endpoint. Override only this
            // test's widget with the driver's isolated port, never the saved asset.
            Surface->ServerUrlTextBox->SetText(FText::FromString(TestUrl));
            OriginalGeneration = Actor->GetFinareComponent()->ActiveGeneration;
            UE_LOG(LogTemp, Display, TEXT("FINARE_PROJECT_REFRESH_READY_FOR_EDIT"));
            Phase = 1;
        }
        if (Phase == 1)
        {
            if (!IFileManager::Get().FileExists(*Signal)) return false;
            if (!Test.TestTrue(TEXT("Request native Build & Reload"), IFinareEditorModule::Get().RequestDailyBuild(
                EFinareDailyBuildReason::Manual, true))) return true;
            Phase = 2;
            return false;
        }
        if (Phase == 2)
        {
            if (IFinareEditorModule::Get().IsDailyBuildRunning() || Surface->bPatchRequestPending) return false;
            const auto& Result = IFinareEditorModule::Get().GetLastDailyBuildResult();
            if (!Test.TestTrue(*Result.Summary, Result.IsSuccess())) return true;
            Test.TestTrue(TEXT("Coordinator reloaded the live component"), Result.ReloadedComponents > 0 && Result.FailedComponents == 0);
            Test.TestNotEqual(TEXT("Live component generation replaced"), Actor->GetFinareComponent()->ActiveGeneration, OriginalGeneration);
            if (!CheckHeight(120)) return true;
            Test.TestTrue(TEXT("Build completion refreshed Slate without restarting the session"),
                Surface->GetCanonicalSourceText().Contains(TEXT("return 120.0f;")));

            Surface->CodeEditBuffer = TEXT("unsaved local edit");
            Surface->bCodeEditDirty = true;
            Surface->HandleRefreshSourceSnapshot();
            Test.TestFalse(TEXT("Refresh refuses an unsaved buffer"), Surface->bPatchRequestPending);
            Test.TestEqual(TEXT("Unsaved text preserved"), Surface->GetCodeEditBuffer(), FString(TEXT("unsaved local edit")));
            Surface->CodeEditBuffer = Surface->WorkbenchSourceText;
            Surface->bCodeEditDirty = false;
            // A stale tab must be able to recover via Refresh, not conflict forever.
            Surface->WorkbenchRevision = -1;
            Surface->HandleRefreshSourceSnapshot();
            Phase = 3;
            return false;
        }
        if (Phase == 3)
        {
            if (Surface->bPatchRequestPending) return false;
            Test.TestFalse(TEXT("Stale-tab refresh completed without an authoring failure"), Surface->bHasAuthoringFailure);
            Test.TestTrue(TEXT("Stale revision replaced"), Surface->WorkbenchRevision >= 0);
            if (!Test.HasAnyErrors()) UE_LOG(LogTemp, Display, TEXT("FINARE_PROJECT_REFRESH PASS native build, live 100 -> 120, automatic Slate refresh, stale tab and dirty buffer."));
            return true;
        }
        return false;
    }
};

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareProjectRefreshTest, "Finare.Unreal.Editor.ProjectRefresh",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareProjectRefreshTest::RunTest(const FString& Parameters)
{
    FString Url, Signal;
    if (!FParse::Value(FCommandLine::Get(), TEXT("FinareAuthoringTestUrl="), Url) ||
        !FParse::Value(FCommandLine::Get(), TEXT("FinareAuthoringTestSignal="), Signal))
    {
        AddInfo(TEXT("Live project fixture not supplied; run tools/test-unreal-async-authoring.ps1 -NativeDailyBuild."));
        return true;
    }
    if (!TestNotNull(TEXT("Editor required"), GEditor) || !TestNull(TEXT("No existing Play session"), GEditor->PlayWorld)) return false;
    ADD_LATENT_AUTOMATION_COMMAND(FFinareProjectRefreshCommand(*this, Url, Signal));
    return true;
}
#endif
