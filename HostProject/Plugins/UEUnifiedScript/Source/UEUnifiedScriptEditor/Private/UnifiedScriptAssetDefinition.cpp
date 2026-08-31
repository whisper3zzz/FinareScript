#include "UnifiedScriptAssetDefinition.h"

#include "ContentBrowserMenuContexts.h"
#include "ContentBrowserModule.h"
#include "Engine/Blueprint.h"
#include "IContentBrowserSingleton.h"
#include "Misc/Paths.h"
#include "SourceCodeNavigation.h"
#include "Styling/AppStyle.h"
#include "ToolMenus.h"
#include "UEUnifiedScriptEditorModule.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptAuthoringSurface.h"

#include UE_INLINE_GENERATED_CPP_BY_NAME(UnifiedScriptAssetDefinition)

#define LOCTEXT_NAMESPACE "UnifiedScriptAssetDefinition"

namespace UE::UnifiedScript::Editor::Private
{
TArray<UUnifiedScriptAsset*> LoadSelectedScriptAssets(const FToolMenuContext& Context)
{
    const UContentBrowserAssetContextMenuContext* AssetContext =
        UContentBrowserAssetContextMenuContext::FindContextWithAssets(Context);
    return AssetContext != nullptr
        ? AssetContext->LoadSelectedObjects<UUnifiedScriptAsset>()
        : TArray<UUnifiedScriptAsset*>();
}

void OpenAuthoring(const FToolMenuContext& Context)
{
    for (UUnifiedScriptAsset* Asset : LoadSelectedScriptAssets(Context))
    {
        IUEUnifiedScriptEditorModule::Get().OpenAuthoringSurface(Asset);
    }
}

void OpenSource(const FToolMenuContext& Context)
{
    for (const UUnifiedScriptAsset* Asset : LoadSelectedScriptAssets(Context))
    {
        const FString SourcePath =
            FUnifiedScriptAuthoringSurfaceReader::ResolveConfiguredPath(Asset->SourcePath);
        if (IFileManager::Get().FileExists(*SourcePath))
        {
            FSourceCodeNavigation::OpenSourceFile(SourcePath);
        }
    }
}

void LocateGeneratedClass(const FToolMenuContext& Context)
{
    TArray<UObject*> AssetsToLocate;
    for (const UUnifiedScriptAsset* Asset : LoadSelectedScriptAssets(Context))
    {
        const UClass* GeneratedClass = Asset->GeneratedClassPath.TryLoadClass<UObject>();
        if (GeneratedClass != nullptr && GeneratedClass->ClassGeneratedBy != nullptr)
        {
            AssetsToLocate.AddUnique(GeneratedClass->ClassGeneratedBy);
        }
    }
    if (!AssetsToLocate.IsEmpty())
    {
        FContentBrowserModule& ContentBrowser =
            FModuleManager::LoadModuleChecked<FContentBrowserModule>(TEXT("ContentBrowser"));
        ContentBrowser.Get().SyncBrowserToAssets(AssetsToLocate);
    }
}

void OpenRepair(const FToolMenuContext& Context)
{
    IUEUnifiedScriptEditorModule::Get().OpenProjectSetup();
}
}

FText UAssetDefinition_UnifiedScriptAsset::GetAssetDisplayName() const
{
    return LOCTEXT("DisplayName", "UnifiedScript Script Asset");
}

FLinearColor UAssetDefinition_UnifiedScriptAsset::GetAssetColor() const
{
    return FLinearColor(FColor(39, 168, 206));
}

TSoftClassPtr<UObject> UAssetDefinition_UnifiedScriptAsset::GetAssetClass() const
{
    return UUnifiedScriptAsset::StaticClass();
}

TConstArrayView<FAssetCategoryPath>
UAssetDefinition_UnifiedScriptAsset::GetAssetCategories() const
{
    static const FAssetCategoryPath Categories[] = {
        FAssetCategoryPath(EAssetCategoryPaths::Gameplay)
    };
    return Categories;
}

EAssetCommandResult UAssetDefinition_UnifiedScriptAsset::OpenAssets(
    const FAssetOpenArgs& OpenArgs) const
{
    bool bHandled = false;
    for (UUnifiedScriptAsset* Asset : OpenArgs.LoadObjects<UUnifiedScriptAsset>())
    {
        IUEUnifiedScriptEditorModule::Get().OpenAuthoringSurface(Asset);
        bHandled = true;
    }
    return bHandled ? EAssetCommandResult::Handled : EAssetCommandResult::Unhandled;
}

FAssetSupportResponse UAssetDefinition_UnifiedScriptAsset::CanRename(
    const FAssetData& InAsset) const
{
    return FAssetSupportResponse::Error(LOCTEXT(
        "RenameBlocked",
        "Rename the complete UnifiedScript Class through its managed workflow; renaming only the Script Asset would break its source, proxy, deployment, and semantic identity."));
}

FAssetSupportResponse UAssetDefinition_UnifiedScriptAsset::CanDuplicate(
    const FAssetData& InAsset) const
{
    return FAssetSupportResponse::Error(LOCTEXT(
        "DuplicateBlocked",
        "Create a new UnifiedScript Class instead; duplicating only the Script Asset would reuse the original source, proxy, deployment, and semantic identity."));
}

EAssetCommandResult UAssetDefinition_UnifiedScriptAsset::GetSourceFiles(
    const FAssetSourceFilesArgs& InArgs,
    TFunctionRef<bool(const FAssetSourceFilesResult& InSourceFile)> SourceFileFunc) const
{
    bool bHandled = false;
    for (const UUnifiedScriptAsset* Asset : InArgs.LoadObjects<UUnifiedScriptAsset>())
    {
        FString ConfiguredPath = Asset->SourcePath;
        ConfiguredPath.TrimStartAndEndInline();
        if (ConfiguredPath.IsEmpty())
        {
            continue;
        }

        const FString AbsolutePath =
            FUnifiedScriptAuthoringSurfaceReader::ResolveConfiguredPath(ConfiguredPath);
        FAssetSourceFilesResult Result;
        Result.FilePath = InArgs.FilePathFormat == EPathUse::AbsolutePath
            ? AbsolutePath
            : ConfiguredPath;
        Result.DisplayLabel = TEXT("C# Source");
        Result.Timestamp = IFileManager::Get().GetTimeStamp(*AbsolutePath);
        bHandled = true;
        if (!SourceFileFunc(Result))
        {
            break;
        }
    }
    return bHandled ? EAssetCommandResult::Handled : EAssetCommandResult::Unhandled;
}

void UE::UnifiedScript::Editor::RegisterUnifiedScriptAssetContextMenu()
{
    UToolMenu* Menu =
        UE::ContentBrowser::ExtendToolMenu_AssetContextMenu(UUnifiedScriptAsset::StaticClass());
    FToolMenuSection& Section = Menu->FindOrAddSection(
        TEXT("UnifiedScript"),
        LOCTEXT("ContextSection", "UnifiedScript"));

    FToolUIAction OpenAuthoringAction;
    OpenAuthoringAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::UnifiedScript::Editor::Private::OpenAuthoring);
    Section.AddMenuEntry(
        TEXT("UnifiedScript.OpenAuthoring"),
        LOCTEXT("OpenAuthoring", "Open in UnifiedScript Authoring"),
        LOCTEXT("OpenAuthoringTooltip", "Open this exact Script Asset in the shared C#/Graph Authoring Surface."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Code")),
        OpenAuthoringAction);

    FToolUIAction OpenSourceAction;
    OpenSourceAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::UnifiedScript::Editor::Private::OpenSource);
    Section.AddMenuEntry(
        TEXT("UnifiedScript.OpenSource"),
        LOCTEXT("OpenSource", "Open C# Source"),
        LOCTEXT("OpenSourceTooltip", "Open the exact managed source file associated with this Script Asset."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Edit")),
        OpenSourceAction);

    FToolUIAction LocateClassAction;
    LocateClassAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::UnifiedScript::Editor::Private::LocateGeneratedClass);
    Section.AddMenuEntry(
        TEXT("UnifiedScript.LocateGeneratedClass"),
        LOCTEXT("LocateGeneratedClass", "Locate Generated Class"),
        LOCTEXT("LocateGeneratedClassTooltip", "Select the exact generated Blueprint proxy in the Content Browser."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Search")),
        LocateClassAction);

    FToolUIAction RepairAction;
    RepairAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::UnifiedScript::Editor::Private::OpenRepair);
    Section.AddMenuEntry(
        TEXT("UnifiedScript.OpenRepair"),
        LOCTEXT("OpenRepair", "Repair UnifiedScript Class..."),
        LOCTEXT("OpenRepairTooltip", "Open Project Setup to validate and repair product-owned Script Class artifacts."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Settings")),
        RepairAction);
}

#undef LOCTEXT_NAMESPACE
