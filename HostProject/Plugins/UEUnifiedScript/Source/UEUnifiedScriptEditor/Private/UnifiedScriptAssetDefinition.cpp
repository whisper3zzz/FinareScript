#include "UnifiedScriptAssetDefinition.h"

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
#include "UEUnifiedScriptEditorModule.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptAuthoringSurface.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptClassAssetOperations.h"

#include UE_INLINE_GENERATED_CPP_BY_NAME(UnifiedScriptAssetDefinition)

#define LOCTEXT_NAMESPACE "UnifiedScriptAssetDefinition"

DEFINE_LOG_CATEGORY_STATIC(LogUnifiedScriptAssetDefinition, Log, All);

namespace UE::UnifiedScript::Editor::Private
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
    UE::UnifiedScript::Editor::ProcessPendingUnifiedScriptBlueprintDuplicateCleanup(
        Errors);
    for (const FString& Error : Errors)
    {
        UE_LOG(
            LogUnifiedScriptAssetDefinition,
            Error,
            TEXT("%s"),
            *Error);
    }
    return false;
}

void GuardGenericBlueprintDuplicate(UObject* Object)
{
    if (FUnifiedScriptClassAssetOperations::IsAssetToolsMutationActive())
    {
        return;
    }
    UBlueprint* Blueprint = Cast<UBlueprint>(Object);
    if (!FUnifiedScriptClassAssetOperations::IsOrphanedGeneratedBlueprintDuplicate(
            Blueprint))
    {
        return;
    }
    PendingBlueprintDuplicates.AddUnique(Blueprint);
    UE_LOG(
        LogUnifiedScriptAssetDefinition,
        Warning,
        TEXT("Rejected Blueprint-only duplicate '%s'; use Duplicate UnifiedScript Class on the Script Asset."),
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
            return FUnifiedScriptClassAssetOperations::IsCompleteClassAsset(Object);
        });
}

void GuardBareDelete(
    const TArray<UObject*>& Objects,
    FCanDeleteAssetResult& OutCanDelete)
{
    if (!FUnifiedScriptClassAssetOperations::IsAssetToolsMutationActive() &&
        ContainsCompleteScriptClassAsset(Objects))
    {
        OutCanDelete.Set(false);
    }
}

void GuardBareDestructiveAction(FOnPreDestructiveAssetActionArgs& Args)
{
    if (FUnifiedScriptClassAssetOperations::IsAssetToolsMutationActive() ||
        !ContainsCompleteScriptClassAsset(Args.Objects))
    {
        return;
    }
    Args.Result.bSuccess = false;
    Args.Result.ErrorMessage =
        TEXT("Use the UnifiedScript Class actions on its Script Asset; operating on only the generated Blueprint or sidecar would break the complete class identity.");
}

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

FString GetBlueprintPackagePath(const UUnifiedScriptAsset& ScriptAsset)
{
    const UClass* GeneratedClass = ScriptAsset.GeneratedClassPath.TryLoadClass<UObject>();
    return GeneratedClass != nullptr && GeneratedClass->ClassGeneratedBy != nullptr
        ? GeneratedClass->ClassGeneratedBy->GetOutermost()->GetName()
        : FString();
}

bool PromptForTargetBlueprintPath(
    const UUnifiedScriptAsset& ScriptAsset,
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
            ? LOCTEXT("DuplicateClassDialog", "Duplicate UnifiedScript Class")
            : LOCTEXT("RenameClassDialog", "Rename UnifiedScript Class"))
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
    for (UUnifiedScriptAsset* Asset : LoadSelectedScriptAssets(Context))
    {
        FString TargetPackagePath;
        if (!PromptForTargetBlueprintPath(*Asset, bDuplicate, TargetPackagePath))
        {
            continue;
        }
        FUnifiedScriptClassAssetOperationResult Result;
        const bool bSucceeded = bDuplicate
            ? FUnifiedScriptClassAssetOperations::Duplicate(
                Asset,
                TargetPackagePath,
                Result)
            : FUnifiedScriptClassAssetOperations::Rename(
                Asset,
                TargetPackagePath,
                Result);
        if (!bSucceeded)
        {
            FMessageDialog::Open(
                EAppMsgType::Ok,
                FText::FromString(Result.Error),
                LOCTEXT("ClassOperationFailed", "UnifiedScript Class operation failed"));
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
    for (UUnifiedScriptAsset* Asset : LoadSelectedScriptAssets(Context))
    {
        const EAppReturnType::Type Confirmation = FMessageDialog::Open(
            EAppMsgType::YesNo,
            FText::Format(
                LOCTEXT(
                    "DeleteClassConfirmation",
                    "Delete UnifiedScript Class '{0}'? This removes its generated Blueprint, Script Asset, managed source, deployment entry, and matching first-Play request."),
                FText::FromString(Asset->ScriptTypeName)),
            LOCTEXT("DeleteClassDialog", "Delete UnifiedScript Class"));
        if (Confirmation != EAppReturnType::Yes)
        {
            continue;
        }
        FUnifiedScriptClassAssetOperationResult Result;
        if (!FUnifiedScriptClassAssetOperations::Delete(Asset, Result))
        {
            FMessageDialog::Open(
                EAppMsgType::Ok,
                FText::FromString(Result.Error),
                LOCTEXT(
                    "DeleteClassFailed",
                    "UnifiedScript Class deletion failed"));
        }
    }
}
}

FText UAssetDefinition_UnifiedScriptAsset::GetAssetDisplayName() const
{
    return LOCTEXT("DisplayName", "UnifiedScript Script Asset");
}

FText UAssetDefinition_UnifiedScriptBlueprint::GetAssetDisplayName() const
{
    return LOCTEXT("BlueprintDisplayName", "UnifiedScript Generated Blueprint");
}

TSoftClassPtr<UObject> UAssetDefinition_UnifiedScriptBlueprint::GetAssetClass() const
{
    return UUnifiedScriptBlueprint::StaticClass();
}

FAssetSupportResponse UAssetDefinition_UnifiedScriptBlueprint::CanRename(
    const FAssetData& InAsset) const
{
    if (FUnifiedScriptClassAssetOperations::IsAssetToolsMutationActive())
    {
        return FAssetSupportResponse::Supported();
    }
    return FAssetSupportResponse::Error(LOCTEXT(
        "BlueprintRenameBlocked",
        "Use Rename UnifiedScript Class on the Script Asset; renaming only the generated Blueprint would break the complete class identity."));
}

FAssetSupportResponse UAssetDefinition_UnifiedScriptBlueprint::CanDuplicate(
    const FAssetData& InAsset) const
{
    if (FUnifiedScriptClassAssetOperations::IsAssetToolsMutationActive())
    {
        return FAssetSupportResponse::Supported();
    }
    return FAssetSupportResponse::Error(LOCTEXT(
        "BlueprintDuplicateBlocked",
        "Use Duplicate UnifiedScript Class on the Script Asset; duplicating only the generated Blueprint would omit its source, sidecar, deployment, and semantic identity."));
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
    if (FUnifiedScriptClassAssetOperations::IsAssetToolsMutationActive())
    {
        return FAssetSupportResponse::Supported();
    }
    return FAssetSupportResponse::Error(LOCTEXT(
        "RenameBlocked",
        "Use Rename UnifiedScript Class; renaming only the Script Asset would break its source, proxy, deployment, and semantic identity."));
}

FAssetSupportResponse UAssetDefinition_UnifiedScriptAsset::CanDuplicate(
    const FAssetData& InAsset) const
{
    if (FUnifiedScriptClassAssetOperations::IsAssetToolsMutationActive())
    {
        return FAssetSupportResponse::Supported();
    }
    return FAssetSupportResponse::Error(LOCTEXT(
        "DuplicateBlocked",
        "Use Duplicate UnifiedScript Class; duplicating only the Script Asset would reuse the original source, proxy, deployment, and semantic identity."));
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

    FToolUIAction RenameClassAction;
    RenameClassAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::UnifiedScript::Editor::Private::RenameClass);
    Section.AddMenuEntry(
        TEXT("UnifiedScript.RenameClass"),
        LOCTEXT("RenameClass", "Rename UnifiedScript Class..."),
        LOCTEXT("RenameClassTooltip", "Rename the Blueprint proxy, Script Asset, C# source, deployment entry, and first-Play identity as one rollback-safe transaction."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Edit")),
        RenameClassAction);

    FToolUIAction DuplicateClassAction;
    DuplicateClassAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::UnifiedScript::Editor::Private::DuplicateClass);
    Section.AddMenuEntry(
        TEXT("UnifiedScript.DuplicateClass"),
        LOCTEXT("DuplicateClass", "Duplicate UnifiedScript Class..."),
        LOCTEXT("DuplicateClassTooltip", "Duplicate the complete Script Class with a fresh stable ClassId and an independent source/deployment identity."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Plus")),
        DuplicateClassAction);

    FToolUIAction DeleteClassAction;
    DeleteClassAction.ExecuteAction = FToolMenuExecuteAction::CreateStatic(
        &UE::UnifiedScript::Editor::Private::DeleteClass);
    Section.AddMenuEntry(
        TEXT("UnifiedScript.DeleteClass"),
        LOCTEXT("DeleteClass", "Delete UnifiedScript Class..."),
        LOCTEXT("DeleteClassTooltip", "Delete the generated Blueprint, Script Asset, managed source, deployment entry, and matching first-Play request as one guarded transaction."),
        FSlateIcon(FAppStyle::GetAppStyleSetName(), TEXT("Icons.Delete")),
        DeleteClassAction);
}

void UE::UnifiedScript::Editor::RegisterUnifiedScriptAssetOperationGuards()
{
    using namespace UE::UnifiedScript::Editor::Private;
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

void UE::UnifiedScript::Editor::UnregisterUnifiedScriptAssetOperationGuards()
{
    using namespace UE::UnifiedScript::Editor::Private;
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

int32 UE::UnifiedScript::Editor::ProcessPendingUnifiedScriptBlueprintDuplicateCleanup(
    TArray<FString>& OutErrors)
{
    using namespace UE::UnifiedScript::Editor::Private;
    OutErrors.Reset();
    TArray<TWeakObjectPtr<UBlueprint>> Pending =
        MoveTemp(PendingBlueprintDuplicates);
    PendingBlueprintDuplicates.Reset();
    int32 DeletedCount = 0;
    for (const TWeakObjectPtr<UBlueprint>& WeakBlueprint : Pending)
    {
        UBlueprint* Blueprint = WeakBlueprint.Get();
        if (Blueprint == nullptr ||
            !FUnifiedScriptClassAssetOperations::IsOrphanedGeneratedBlueprintDuplicate(
                Blueprint))
        {
            continue;
        }
        const FString AssetPath = Blueprint->GetPathName();
        FString Error;
        if (FUnifiedScriptClassAssetOperations::DeleteOrphanedGeneratedBlueprintDuplicate(
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
