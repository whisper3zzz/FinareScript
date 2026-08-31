#pragma once

#include "AssetDefinitionDefault.h"

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

namespace UE::UnifiedScript::Editor
{
/** Adds exact Script Asset actions to its class-specific Content Browser menu. */
void RegisterUnifiedScriptAssetContextMenu();
}
