#pragma once

#include "AssetDefinitionDefault.h"
#include "Script/AssetDefinition_Blueprint.h"

#include "UnifiedScriptAssetDefinition.generated.h"

/** Content Browser contract for one UnifiedScript program asset. */
UCLASS()
class UAssetDefinition_UnifiedScriptAsset final : public UAssetDefinitionDefault
{
    GENERATED_BODY()

public:
    virtual FText GetAssetDisplayName() const override;
    virtual FLinearColor GetAssetColor() const override;
    virtual TSoftClassPtr<UObject> GetAssetClass() const override;
    virtual TConstArrayView<FAssetCategoryPath> GetAssetCategories() const override;
    virtual EAssetCommandResult OpenAssets(const FAssetOpenArgs& OpenArgs) const override;
    virtual FAssetSupportResponse CanRename(const FAssetData& InAsset) const override;
    virtual FAssetSupportResponse CanDuplicate(const FAssetData& InAsset) const override;
    virtual EAssetCommandResult GetSourceFiles(
        const FAssetSourceFilesArgs& InArgs,
        TFunctionRef<bool(const FAssetSourceFilesResult& InSourceFile)> SourceFileFunc) const override;
};

/** Content Browser contract for the generated half of a Script Class. */
UCLASS()
class UAssetDefinition_UnifiedScriptBlueprint final
    : public UAssetDefinition_Blueprint
{
    GENERATED_BODY()

public:
    virtual FText GetAssetDisplayName() const override;
    virtual TSoftClassPtr<UObject> GetAssetClass() const override;
    virtual FAssetSupportResponse CanRename(const FAssetData& InAsset) const override;
    virtual FAssetSupportResponse CanDuplicate(const FAssetData& InAsset) const override;
};

namespace UE::UnifiedScript::Editor
{
/** Adds exact Script Asset actions to its class-specific Content Browser menu. */
void RegisterUnifiedScriptAssetContextMenu();

/** Rejects bare destructive operations against either half of a Script Class. */
void RegisterUnifiedScriptAssetOperationGuards();
void UnregisterUnifiedScriptAssetOperationGuards();

/** Runs deferred cleanup explicitly for deterministic Automation coverage. */
int32 ProcessPendingUnifiedScriptBlueprintDuplicateCleanup(
    TArray<FString>& OutErrors);
}
