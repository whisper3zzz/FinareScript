#pragma once

#include "CoreMinimal.h"
#include "UnifiedScriptEdGraph.h"

struct FUnifiedScriptNodePaletteItem
{
    FUnifiedScriptGraphPaletteAction Action;
    bool bFavorite = false;
    int32 RecentRank = INDEX_NONE;
    int32 MatchScore = 0;
};

/** Deterministic, editor-only search and preference model for Blueprint-style node discovery. */
class FUnifiedScriptNodePaletteModel final
{
public:
    static constexpr int32 MaxRecentActions = 8;

    static TArray<FUnifiedScriptNodePaletteItem> Build(
        const TArray<FUnifiedScriptGraphPaletteAction>& Actions,
        const FString& Query,
        const TArray<FString>& FavoriteActionIds,
        const TArray<FString>& RecentActionIds,
        bool bHasActiveBlock);

    static bool ToggleFavorite(TArray<FString>& FavoriteActionIds, const FString& ActionId);
    static void RecordRecent(TArray<FString>& RecentActionIds, const FString& ActionId);
};
