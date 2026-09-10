#include "FinareAssetDefinition.h"

#include "AssetRegistry/AssetRegistryModule.h"
#include "ContentBrowserMenuContexts.h"
#include "ContentBrowserModule.h"
#include "Containers/Ticker.h"
#include "Dialogs/DlgPickAssetPath.h"
#include "Editor.h"
#include "Engine/Blueprint.h"
#include "IContentBrowserSingleton.h"
#include "Misc/MessageDialog.h"
#include "Misc/Paths.h"
#include "SourceCodeNavigation.h"
#include "Styling/AppStyle.h"
#include "ToolMenus.h"
#include "FinareEditorModule.h"
#include "FinareAsset.h"
#include "FinareAuthoringSurface.h"
#include "FinareBlueprint.h"
#include "FinareClassAssetOperations.h"

#include UE_INLINE_GENERATED_CPP_BY_NAME(FinareAssetDefinition)

#define LOCTEXT_NAMESPACE "FinareAssetDefinition"

DEFINE_LOG_CATEGORY_STATIC(LogFinareAssetDefinition, Log, All);

namespace UE::Finare::Editor::Private
{
FDelegateHandle AssetsCanDeleteHandle;
FDelegateHandle PreDestructiveAssetActionHandle;
FDelegateHandle InMemoryAssetCreatedHandle;
FTSTicker::FDelegateHandle DuplicateCleanupHandle;
TArray<TWeakObjectPtr<UBlueprint>> PendingBlueprintDuplicates;

bool RunPendingBlueprintDuplicateCleanup(float DeltaTime)
{
    DuplicateCleanupHandle.Reset();
    TArray<FString> Errors;
    UE::Finare::Editor::ProcessPendingFinareBlueprintDuplicateCleanup(
        Errors);
    for (const FString& Error : Errors)
    {
        UE_LOG(
            LogFinareAssetDefinition,
            Error,
            TEXT("%s"),
            *Error);
    }
    return false;
}

void GuardGenericBlueprintDuplicate(UObject* Object)
{
    if (FFinareClassAssetOperations::IsAssetToolsMutationActive())
    {
        return;
    }
    UBlueprint* Blueprint = Cast<UBlueprint>(Object);
    if (!FFinareClassAssetOperations::IsOrphanedGeneratedBlueprintDuplicate(
            Blueprint))
    {
        return;
    }
    PendingBlueprintDuplicates.AddUnique(Blueprint);
    UE_LOG(
        LogFinareAssetDefinition,
        Warning,
        TEXT("Rejected Blueprint-only duplicate '%s'; use Duplicate Finare Class on the Script Asset."),
        *Blueprint->GetPathName());
    if (!DuplicateCleanupHandle.IsValid())
    {
        DuplicateCleanupHandle = FTSTicker::GetCoreTicker().AddTicker(
            FTickerDelegate::CreateStatic(&RunPendingBlueprintDuplicateCleanup));
    }
}

bool ContainsCompleteScriptClassAsset(const TArray<UObject*>& Objects)
{
    return Objects.ContainsByPredicate(
        [](const UObject* Object)
        {
            return FFinareClassAssetOperations::IsCompleteClassAsset(Object);
        });
}

void GuardBareDelete(
    const TArray<UObject*>& Objects,
    FCanDeleteAssetResult& OutCanDelete)
{
    if (!FFinareClassAssetOperations::IsAssetToolsMutationActive() &&
        ContainsCompleteScriptClassAsset(Objects))
    {
        OutCanDelete.Set(false);
    }
}

void GuardBareDestructiveAction(FOnPreDestructiveAssetActionArgs& Args)
{
    if (FFinareClassAssetOperations::IsAssetToolsMutationActive() ||
        !ContainsCompleteScriptClassAsset(Args.Objects))
    {
        return;
    }
    Args.Result.bSuccess = false;
    Args.Result.ErrorMessage =
        TEXT("Use the Finare Class actions on its Script Asset; operating on only the generated Blueprint or sidecar would break the complete class identity.");
}

TArray<UFinareAsset*> LoadSelectedScriptAssets(const FToolMenuContext& Context)
{
    const UContentBrowserAssetContextMenuContext* AssetContext =
        UContentBrowserAssetContextMenuContext::FindContextWithAssets(Context);
    return AssetContext != nullptr
        ? AssetContext->LoadSelectedObjects<UFinareAsset>()
        : TArray<UFinareAsset*>();
}

void OpenAuthoring(const FToolMenuContext& Context)
{
    for (UFinareAsset* Asset : LoadSelectedScriptAssets(Context))
    {
        IFinareEditorModule::Get().OpenAuthoringSurface(Asset);
    }
}

void OpenSource(const FToolMenuContext& Context)
{
    for (const UFinareAsset* Asset : LoadSelectedScriptAssets(Context))
    {
        const FString SourcePath =
            FFinareAuthoringSurfaceReader::ResolveConfiguredPath(Asset->SourcePath);
        if (IFileManager::Get().FileExists(*SourcePath))
        {
            FSourceCodeNavigation::OpenSourceFile(SourcePath);
        }
    }
}

void LocateGeneratedClass(const FToolMenuContext& Context)
{
    TArray<UObject*> AssetsToLocate;
    for (const UFinareAsset* Asset : LoadSelectedScriptAssets(Context))
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
    IFinareEditorModule::Get().OpenProjectSetup();
}

FString GetBlueprintPackagePath(const UFinareAsset& ScriptAsset)
{
    const UClass* GeneratedClass = ScriptAsset.GeneratedClassPath.TryLoadClass<UObject>();
    return GeneratedClass != nullptr && GeneratedClass->ClassGeneratedBy != nullptr
        ? GeneratedClass->ClassGeneratedBy->GetOutermost()->GetName()
        : FString();
}

bool PromptForTargetBlueprintPath(
    const UFinareAsset& ScriptAsset,
    bool bDuplicate,
    FString& OutPackagePath)
{
    const FString CurrentPackagePath = GetBlueprintPackagePath(ScriptAsset);
    if (CurrentPackagePath.IsEmpty())
    {
        return false;
    }
    const FString Directory = FPackageName::GetLongPackagePath(CurrentPackagePath);
    FString SuggestedName = FPackageName::GetLongPackageAssetName(CurrentPackagePath);
    if (bDuplicate)
    {
        SuggestedName += TEXT("_Copy");
    }
    const TSharedRef<SDlgPickAssetPath> Dialog = SNew(SDlgPickAssetPath)
        .Title(bDuplicate
            ? LOCTEXT("DuplicateClassDialog", "Duplicate Finare Class")
            : LOCTEXT("RenameClassDialog", "Rename Finare Class"))
        .DefaultAssetPath(FText::FromString(Directory / SuggestedName));
    if (Dialog->ShowModal() != EAppReturnType::Ok)
    {
        return false;
    }
    OutPackagePath = Dialog->GetFullAssetPath().ToString();
    return !OutPackagePath.IsEmpty();
}

void RunClassOperation(const FToolMenuContext& Context, bool bDuplicate)
{
    for (UFinareAsset* Asset : LoadSelectedScriptAssets(Context))
    {
        FString TargetPackagePath;
        if (!PromptForTargetBlueprintPath(*Asset, bDuplicate, TargetPackagePath))
        {
            continue;
        }
        FFinareClassAssetOperationResult Result;
        const bool bSucceeded = bDuplicate
            ? FFinareClassAssetOperations::Duplicate(
                Asset,
                TargetPackagePath,
                Result)
            : FFinareClassAssetOperations::Rename(
                Asset,
                TargetPackagePath,
                Result);
        if (!bSucceeded)
        {
            FMessageDialog::Open(
                EAppMsgType::Ok,
                FText::FromString(Result.Error),
                LOCTEXT("ClassOperationFailed", "Finare Class operation failed"));
            continue;
        }

        TArray<UObject*> AssetsToLocate;
        AssetsToLocate.Add(Result.Blueprint);
        AssetsToLocate.Add(Result.ScriptAsset);
        FContentBrowserModule& ContentBrowser =
            FModuleManager::LoadModuleChecked<FContentBrowserModule>(TEXT("ContentBrowser"));
        ContentBrowser.Get().SyncBrowserToAssets(AssetsToLocate);
    }
}

void RenameClass(const FToolMenuContext& Context)
{
    RunClassOperation(Context, false);
}

void DuplicateClass(const FToolMenuContext& Context)
{
    RunClassOperation(Context, true);
}

void DeleteClass(const FToolMenuContext& Context)
{
    for (UFinareAsset* Asset : LoadSelectedScriptAssets(Context))
    {
        const EAppReturnType::Type Confirmation = FMessageDialog::Open(
            EAppMsgType::YesNo,
            FText::Format(
                LOCTEXT(
                    "DeleteClassConfirmation",
                    "Delete Finare Class '{0}'? This removes its generated Blueprint, Script Asset, managed source, deployment entry, and matching first-Play request."),
                FText::FromString(Asset->ScriptTypeName)),
            LOCTEXT("DeleteClassDialog", "Delete Finare Class"));
        if (Confirmation != EAppReturnType::Yes)
        {
            continue;
        }
        FFinareClassAssetOperationResult Result;
        if (!FFinareClassAssetOperations::Delete(Asset, Result))
        {
            FMessageDialog::Open(
                EAppMsgType::Ok,
                FText::FromString(Result.Error),
                LOCTEXT(
                    "DeleteClassFailed",
                    "Finare Class deletion failed"));
        }
    }
}
}

FText UAssetDefinition_FinareAsset::GetAssetDisplayName() const
{
    return LOCTEXT("DisplayName", "Finare Script Asset");
}

FText UAssetDefinition_FinareBlueprint::GetAssetDisplayName() const
{
    return LOCTEXT("BlueprintDisplayName", "Finare Generated Blueprint");
}

TSoftClassPtr<UObject> UAssetDefinition_FinareBlueprint::GetAssetClass() const
{
    return UFinareBlueprint::StaticClass();
}

FAssetSupportResponse UAssetDefinition_FinareBlueprint::CanRename(
    const FAssetData& InAsset) const
{
    if (FFinareClassAssetOperations::IsAssetToolsMutationActive())
    {
        return FAssetSupportResponse::Supported();
    }
    return FAssetSupportResponse::Error(LOCTEXT(
        "BlueprintRenameBlocked",
        "Use Rename Finare Class on the Script Asset; renaming only the generated Blueprint would break the complete class identity."));
}

FAssetSupportResponse UAssetDefinition_FinareBlueprint::CanDuplicate(
    const FAssetData& InAsset) const
{
    if (FFinareClassAssetOperations::IsAssetToolsMutationActive())
    {
        return FAssetSupportResponse::Supported();
    }
    return FAssetSupportResponse::Error(LOCTEXT(
        "BlueprintDuplicateBlocked",
        "Use Duplicate Finare Class on the Script Asset; duplicating only the generated Blueprint would omit its source, sidecar, deployment, and semantic identity."));
}

FLinearColor UAssetDefinition_FinareAsset::GetAssetColor() const
{
    return FLinearColor(FColor(39, 168, 206));
}

TSoftClassPtr<UObject> UAssetDefinition_FinareAsset::GetAssetClass() const
{
    return UFinareAsset::StaticClass();
}

TConstArrayView<FAssetCategoryPath>
UAssetDefinition_FinareAsset::GetAssetCategories() const
{
    static const FAssetCategoryPath Categories[] = {
        FAssetCategoryPath(EAssetCategoryPaths::Gameplay)
    };
    return Categories;
}

EAssetCommandResult UAssetDefinition_FinareAsset::OpenAssets(
    const FAssetOpenArgs& OpenArgs) const
{
    bool bHandled = false;
    for (UFinareAsset* Asset : OpenArgs.LoadObjects<UFinareAsset>())
    {
        IFinareEditorModule::Get().OpenAuthoringSurface(Asset);
        bHandled = true;
    }
    return bHandled ? EAssetCommandResult::Handled : EAssetCommandResult::Unhandled;
}

FAssetSupportResponse UAssetDefinition_FinareAsset::CanRename(
    const FAssetData& InAsset) const
{
    if (FFinareClassAssetOperations::IsAssetToolsMutationActive())
    {
        return FAssetSupportResponse::Supported();
    }
    return FAssetSupportResponse::Error(LOCTEXT(
        "RenameBlocked",
        "Use Rename Finare Class; renaming only the Script Asset would break its source, proxy, deployment, and semantic identity."));
}

FAssetSupportResponse UAssetDefinition_FinareAsset::CanDuplicate(
    const FAssetData& InAsset) const
{
    if (FFinareClassAssetOperations::IsAssetToolsMutationActive())
    {
        return FAssetSupportResponse::Supported();
    }
    return FAssetSupportResponse::Error(LOCTEXT(
        "DuplicateBlocked",
        "Use Duplicate Finare Class; duplicating only the Script Asset would reuse the original source, proxy, deployment, and semantic identity."));
}

EAssetCommandResult UAssetDefinition_FinareAsset::GetSourceFiles(
    const FAssetSourceFilesArgs& InArgs,
    TFunctionRef<bool(const FAssetSourceFilesResult& InSourceFile)> SourceFileFunc) const
{
    bool bHandled = false;
    for (const UFinareAsset* Asset : InArgs.LoadObjects<UFinareAsset>())
    {
        FString ConfiguredPath = Asset->SourcePath;
        ConfiguredPath.TrimStartAndEndInline();
        if (ConfiguredPath.IsEmpty())
        {
            continue;
        }

        const FString AbsolutePath =
            FFinareAuthoringSurfaceReader::ResolveConfiguredPath(ConfiguredPath);
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

void UE::Finare::Editor::RegisterFinareAssetContextMenu()
{
    UToolMenu* Menu =
        UE::ContentBrowser::ExtendToolMenu_AssetContextMenu(UFinareAsset::StaticClass());
    FToolMenuSection& Section = Menu->FindOrAddSection(
        TEXT("Finare"),
        LOCTEXT("ContextSection", "Finare"));

    FToolUIAction OpenAuthoringAction;
    OpenAuthoringAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::Finare::Editor::Private::OpenAuthoring);
    Section.AddMenuEntry(
        TEXT("Finare.OpenAuthoring"),
        LOCTEXT("OpenAuthoring", "Open in Finare Authoring"),
        LOCTEXT("OpenAuthoringTooltip", "Open this exact Script Asset in the shared C#/Graph Authoring Surface."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Code")),
        OpenAuthoringAction);

    FToolUIAction OpenSourceAction;
    OpenSourceAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::Finare::Editor::Private::OpenSource);
    Section.AddMenuEntry(
        TEXT("Finare.OpenSource"),
        LOCTEXT("OpenSource", "Open C# Source"),
        LOCTEXT("OpenSourceTooltip", "Open the exact managed source file associated with this Script Asset."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Edit")),
        OpenSourceAction);

    FToolUIAction LocateClassAction;
    LocateClassAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::Finare::Editor::Private::LocateGeneratedClass);
    Section.AddMenuEntry(
        TEXT("Finare.LocateGeneratedClass"),
        LOCTEXT("LocateGeneratedClass", "Locate Generated Class"),
        LOCTEXT("LocateGeneratedClassTooltip", "Select the exact generated Blueprint proxy in the Content Browser."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Search")),
        LocateClassAction);

    FToolUIAction RepairAction;
    RepairAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::Finare::Editor::Private::OpenRepair);
    Section.AddMenuEntry(
        TEXT("Finare.OpenRepair"),
        LOCTEXT("OpenRepair", "Repair Finare Class..."),
        LOCTEXT("OpenRepairTooltip", "Open Project Setup to validate and repair product-owned Script Class artifacts."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Settings")),
        RepairAction);

    FToolUIAction RenameClassAction;
    RenameClassAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::Finare::Editor::Private::RenameClass);
    Section.AddMenuEntry(
        TEXT("Finare.RenameClass"),
        LOCTEXT("RenameClass", "Rename Finare Class..."),
        LOCTEXT("RenameClassTooltip", "Rename the Blueprint proxy, Script Asset, C# source, deployment entry, and first-Play identity as one rollback-safe transaction."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Edit")),
        RenameClassAction);

    FToolUIAction DuplicateClassAction;
    DuplicateClassAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::Finare::Editor::Private::DuplicateClass);
    Section.AddMenuEntry(
        TEXT("Finare.DuplicateClass"),
        LOCTEXT("DuplicateClass", "Duplicate Finare Class..."),
        LOCTEXT("DuplicateClassTooltip", "Duplicate the complete Script Class with a fresh stable ClassId and an independent source/deployment identity."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Plus")),
        DuplicateClassAction);

    FToolUIAction DeleteClassAction;
    DeleteClassAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::Finare::Editor::Private::DeleteClass);
    Section.AddMenuEntry(
        TEXT("Finare.DeleteClass"),
        LOCTEXT("DeleteClass", "Delete Finare Class..."),
        LOCTEXT("DeleteClassTooltip", "Delete the generated Blueprint, Script Asset, managed source, deployment entry, and matching first-Play request as one guarded transaction."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Delete")),
        DeleteClassAction);
}

void UE::Finare::Editor::RegisterFinareAssetOperationGuards()
{
    using namespace UE::Finare::Editor::Private;
    if (!AssetsCanDeleteHandle.IsValid())
    {
        AssetsCanDeleteHandle = FEditorDelegates::OnAssetsCanDelete.AddStatic(
            &GuardBareDelete);
    }
    if (!PreDestructiveAssetActionHandle.IsValid())
    {
        PreDestructiveAssetActionHandle =
            FEditorDelegates::OnPreDestructiveAssetActionWithArgs.AddStatic(
                &GuardBareDestructiveAction);
    }
    if (!InMemoryAssetCreatedHandle.IsValid())
    {
        IAssetRegistry& AssetRegistry =
            FModuleManager::LoadModuleChecked<FAssetRegistryModule>(TEXT("AssetRegistry")).Get();
        InMemoryAssetCreatedHandle = AssetRegistry.OnInMemoryAssetCreated().AddStatic(
            &GuardGenericBlueprintDuplicate);
    }
}

void UE::Finare::Editor::UnregisterFinareAssetOperationGuards()
{
    using namespace UE::Finare::Editor::Private;
    if (AssetsCanDeleteHandle.IsValid())
    {
        FEditorDelegates::OnAssetsCanDelete.Remove(AssetsCanDeleteHandle);
        AssetsCanDeleteHandle.Reset();
    }
    if (PreDestructiveAssetActionHandle.IsValid())
    {
        FEditorDelegates::OnPreDestructiveAssetActionWithArgs.Remove(
            PreDestructiveAssetActionHandle);
        PreDestructiveAssetActionHandle.Reset();
    }
    if (InMemoryAssetCreatedHandle.IsValid())
    {
        if (FAssetRegistryModule* AssetRegistryModule =
                FModuleManager::GetModulePtr<FAssetRegistryModule>(TEXT("AssetRegistry")))
        {
            AssetRegistryModule->Get().OnInMemoryAssetCreated().Remove(
                InMemoryAssetCreatedHandle);
        }
        InMemoryAssetCreatedHandle.Reset();
    }
    if (DuplicateCleanupHandle.IsValid())
    {
        FTSTicker::GetCoreTicker().RemoveTicker(DuplicateCleanupHandle);
        DuplicateCleanupHandle.Reset();
    }
    PendingBlueprintDuplicates.Reset();
}

int32 UE::Finare::Editor::ProcessPendingFinareBlueprintDuplicateCleanup(
    TArray<FString>& OutErrors)
{
    using namespace UE::Finare::Editor::Private;
    OutErrors.Reset();
    TArray<TWeakObjectPtr<UBlueprint>> Pending =
        MoveTemp(PendingBlueprintDuplicates);
    PendingBlueprintDuplicates.Reset();
    int32 DeletedCount = 0;
    for (const TWeakObjectPtr<UBlueprint>& WeakBlueprint : Pending)
    {
        UBlueprint* Blueprint = WeakBlueprint.Get();
        if (Blueprint == nullptr ||
            !FFinareClassAssetOperations::IsOrphanedGeneratedBlueprintDuplicate(
                Blueprint))
        {
            continue;
        }
        const FString AssetPath = Blueprint->GetPathName();
        FString Error;
        if (FFinareClassAssetOperations::DeleteOrphanedGeneratedBlueprintDuplicate(
                Blueprint,
                Error))
        {
            ++DeletedCount;
        }
        else
        {
            OutErrors.Add(FString::Printf(
                TEXT("Cannot remove rejected Blueprint-only duplicate '%s': %s"),
                *AssetPath,
                *Error));
        }
    }
    return DeletedCount;
}

#undef LOCTEXT_NAMESPACE
