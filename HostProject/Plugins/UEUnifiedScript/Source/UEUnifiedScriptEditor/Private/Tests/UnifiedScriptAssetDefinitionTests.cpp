#if WITH_DEV_AUTOMATION_TESTS

#include "UnifiedScriptAssetDefinition.h"

#include "AssetDefinitionRegistry.h"
#include "Framework/Docking/TabManager.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
#include "ToolMenus.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FUnifiedScriptAssetWorkflowContractTest,
    "UnifiedScript.Unreal.ScriptAsset.WorkflowContract",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FUnifiedScriptAssetWorkflowContractTest::RunTest(const FString& Parameters)
{
    UUnifiedScriptAsset* Asset = NewObject<UUnifiedScriptAsset>(
        GetTransientPackage(),
        TEXT("M13ScriptAssetWorkflow"),
        RF_Transient);
    Asset->SourcePath = TEXT("{ProjectDir}/Managed/M13ScriptAssetWorkflow.cs");
    Asset->AuthoringSurfacePath =
        TEXT("{ProjectDir}/../samples/Unreal/UnifiedScript.Unreal.Demo.surface.json");
    const FAssetData AssetData(Asset);

    const UAssetDefinitionRegistry* Registry = UAssetDefinitionRegistry::Get();
    const UAssetDefinition* RegisteredDefinition =
        Registry->GetAssetDefinitionForAsset(AssetData);
    const UAssetDefinition_UnifiedScriptAsset* Definition =
        Cast<UAssetDefinition_UnifiedScriptAsset>(RegisteredDefinition);
    if (!TestNotNull(
            TEXT("Script Assets resolve to the UnifiedScript Asset Definition"),
            Definition))
    {
        return false;
    }

    TestEqual(
        TEXT("Asset Definition owns exactly UUnifiedScriptAsset"),
        Definition->GetAssetClass().Get(),
        UUnifiedScriptAsset::StaticClass());
    const FAssetSupportResponse Rename = Definition->CanRename(AssetData);
    const FAssetSupportResponse Duplicate = Definition->CanDuplicate(AssetData);
    TestFalse(TEXT("Bare Script Asset rename is blocked"), Rename.IsSupported());
    TestFalse(TEXT("Bare Script Asset duplication is blocked"), Duplicate.IsSupported());
    TestTrue(
        TEXT("Rename explains the multi-artifact identity hazard"),
        Rename.GetErrorText().ToString().Contains(TEXT("source, proxy, deployment")));
    TestTrue(
        TEXT("Duplicate points authors to the complete-class operation"),
        Duplicate.GetErrorText().ToString().Contains(TEXT("Duplicate UnifiedScript Class")));

    UUnifiedScriptBlueprint* GeneratedBlueprint =
        NewObject<UUnifiedScriptBlueprint>(
            GetTransientPackage(),
            TEXT("M13GeneratedBlueprintWorkflow"),
            RF_Transient);
    const FAssetData BlueprintData(GeneratedBlueprint);
    const UAssetDefinition_UnifiedScriptBlueprint* BlueprintDefinition =
        Cast<UAssetDefinition_UnifiedScriptBlueprint>(
            Registry->GetAssetDefinitionForAsset(BlueprintData));
    if (!TestNotNull(
            TEXT("Generated proxies resolve to the UnifiedScript Blueprint definition"),
            BlueprintDefinition))
    {
        return false;
    }
    TestEqual(
        TEXT("Blueprint definition owns exactly UUnifiedScriptBlueprint"),
        BlueprintDefinition->GetAssetClass().Get(),
        UUnifiedScriptBlueprint::StaticClass());
    TestFalse(
        TEXT("Content Browser blocks generated Blueprint rename"),
        BlueprintDefinition->CanRename(BlueprintData).IsSupported());
    const FAssetSupportResponse BlueprintDuplicate =
        BlueprintDefinition->CanDuplicate(BlueprintData);
    TestFalse(
        TEXT("Content Browser blocks generated Blueprint duplicate"),
        BlueprintDuplicate.IsSupported());
    TestTrue(
        TEXT("Generated Blueprint duplicate guidance names the complete-class action"),
        BlueprintDuplicate.GetErrorText().ToString().Contains(
            TEXT("Duplicate UnifiedScript Class")));

    TArray<FAssetData> Assets{AssetData};
    FAssetSourceFilesArgs SourceArgs;
    SourceArgs.Assets = Assets;
    SourceArgs.FilePathFormat = EPathUse::AbsolutePath;
    FString ResolvedSource;
    const EAssetCommandResult SourceResult = Definition->GetSourceFiles(
        SourceArgs,
        [&ResolvedSource](const FAssetSourceFilesResult& Result)
        {
            ResolvedSource = Result.FilePath;
            return true;
        });
    FString ExpectedSource = FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::GetPath(FPaths::GetProjectFilePath()),
        TEXT("Managed/M13ScriptAssetWorkflow.cs")));
    FPaths::NormalizeFilename(ExpectedSource);
    TestEqual(
        TEXT("Script Asset exposes its exact managed source to editor tooling"),
        SourceResult,
        EAssetCommandResult::Handled);
    TestEqual(TEXT("Managed source token resolves project-locally"), ResolvedSource, ExpectedSource);

    FAssetOpenArgs OpenArgs;
    OpenArgs.Assets = Assets;
    OpenArgs.OpenMethod = EAssetOpenMethod::Edit;
    TestEqual(
        TEXT("Normal asset opening is handled by UnifiedScript"),
        Definition->OpenAssets(OpenArgs),
        EAssetCommandResult::Handled);
    TestTrue(
        TEXT("Normal asset opening creates the shared Authoring tab"),
        FGlobalTabmanager::Get()->FindExistingLiveTab(
            FTabId(FName(TEXT("UnifiedScriptAuthoringSurface")))).IsValid());

    const UToolMenu* Menu = UToolMenus::Get()->FindMenu(
        TEXT("ContentBrowser.AssetContextMenu.UnifiedScriptAsset"));
    if (!TestNotNull(TEXT("Script Asset owns a class-specific Content Browser menu"), Menu))
    {
        return false;
    }
    TestTrue(TEXT("Context menu opens exact Authoring"), Menu->ContainsEntry(TEXT("UnifiedScript.OpenAuthoring")));
    TestTrue(TEXT("Context menu opens exact C# source"), Menu->ContainsEntry(TEXT("UnifiedScript.OpenSource")));
    TestTrue(TEXT("Context menu locates exact generated class"), Menu->ContainsEntry(TEXT("UnifiedScript.LocateGeneratedClass")));
    TestTrue(TEXT("Context menu exposes repair"), Menu->ContainsEntry(TEXT("UnifiedScript.OpenRepair")));
    TestTrue(TEXT("Context menu exposes complete-class rename"), Menu->ContainsEntry(TEXT("UnifiedScript.RenameClass")));
    TestTrue(TEXT("Context menu exposes complete-class duplicate"), Menu->ContainsEntry(TEXT("UnifiedScript.DuplicateClass")));
    TestTrue(TEXT("Context menu exposes complete-class delete"), Menu->ContainsEntry(TEXT("UnifiedScript.DeleteClass")));
    return true;
}

#endif
