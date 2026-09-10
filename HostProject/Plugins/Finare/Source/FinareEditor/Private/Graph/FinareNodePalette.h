#pragma once

#include "CoreMinimal.h"
#include "FinareEdGraph.h"

struct FFinareNodePaletteItem
{
    FFinareGraphPaletteAction Action;
    bool bFavorite = false;
    int32 RecentRank = INDEX_NONE;
    int32 MatchScore = 0;
};

/** Deterministic, editor-only search and preference model for Blueprint-style node discovery. */
class FFinareNodePaletteModel final
{
public:
    static constexpr int32 MaxRecentActions = 8;

    static TArray<FFinareNodePaletteItem> Build(
        const TArray<FFinareGraphPaletteAction>& Actions,
        const FString& Query,
        const TArray<FString>& FavoriteActionIds,
        const TArray<FString>& RecentActionIds,
        bool bHasActiveBlock);

    static bool ToggleFavorite(TArray<FString>& FavoriteActionIds, const FString& ActionId);
    static void RecordRecent(TArray<FString>& RecentActionIds, const FString& ActionId);
};
