#include "Graph/FinareNodePalette.h"

namespace UE::Finare::Editor::Palette::Private
{
	FString Normalize(const FString& Value)
	{
		FString Result = Value;
		Result.TrimStartAndEndInline();
		Result.ToLowerInline();
		return Result;
	}

	int32 ScoreMatch(
		const FFinareGraphPaletteAction& Action,
		const FString& NormalizedQuery,
		const TArray<FString>& Tokens)
	{
		const FString Title = Normalize(Action.Title);
		const FString Category = Normalize(Action.Category);
		const FString Tooltip = Normalize(Action.Tooltip);
		const FString Keywords = Normalize(
			Action.Keywords + TEXT(" ") + Action.ActionId + TEXT(" ") +
			Action.RestrictionSummary + TEXT(" ") + Action.UnavailableReason);
		const FString Searchable = Title + TEXT(" ") + Category + TEXT(" ") + Tooltip +
			TEXT(" ") + Keywords;
		for (const FString& Token : Tokens)
		{
			if (!Searchable.Contains(Token, ESearchCase::CaseSensitive))
			{
				return INDEX_NONE;
			}
		}

		int32 Score = 0;
		if (Title == NormalizedQuery)
		{
			Score += 1000;
		}
		else if (Title.StartsWith(NormalizedQuery, ESearchCase::CaseSensitive))
		{
			Score += 700;
		}
		else if (Title.Contains(NormalizedQuery, ESearchCase::CaseSensitive))
		{
			Score += 500;
		}
		else if (Category.Contains(NormalizedQuery, ESearchCase::CaseSensitive))
		{
			Score += 300;
		}
		else if (Keywords.Contains(NormalizedQuery, ESearchCase::CaseSensitive))
		{
			Score += 200;
		}
		for (const FString& Token : Tokens)
		{
			Score += Title.Contains(Token, ESearchCase::CaseSensitive) ? 40 : 0;
			Score += Category.Contains(Token, ESearchCase::CaseSensitive) ? 20 : 0;
			Score += Keywords.Contains(Token, ESearchCase::CaseSensitive) ? 10 : 0;
		}
		return Score;
	}

	bool LessText(const FString& Left, const FString& Right)
	{
		return Left.Compare(Right, ESearchCase::IgnoreCase) < 0;
	}
}

TArray<FFinareNodePaletteItem> FFinareNodePaletteModel::Build(
	const TArray<FFinareGraphPaletteAction>& Actions,
	const FString& Query,
	const TArray<FString>& FavoriteActionIds,
	const TArray<FString>& RecentActionIds,
	bool bHasActiveBlock)
{
	using namespace UE::Finare::Editor::Palette::Private;

	const FString NormalizedQuery = Normalize(Query);
	TArray<FString> Tokens;
	NormalizedQuery.ParseIntoArrayWS(Tokens);
	TArray<FFinareNodePaletteItem> Result;
	Result.Reserve(Actions.Num());
	for (const FFinareGraphPaletteAction& Action : Actions)
	{
		if (Action.bRequiresBlock && !bHasActiveBlock)
		{
			continue;
		}
		const int32 MatchScore = NormalizedQuery.IsEmpty()
			                         ? 0
			                         : ScoreMatch(Action, NormalizedQuery, Tokens);
		if (MatchScore == INDEX_NONE)
		{
			continue;
		}

		FFinareNodePaletteItem& Item = Result.AddDefaulted_GetRef();
		Item.Action = Action;
		Item.bFavorite = FavoriteActionIds.Contains(Action.ActionId);
		Item.RecentRank = RecentActionIds.IndexOfByKey(Action.ActionId);
		Item.MatchScore = MatchScore;
	}

	Result.Sort([bSearching = !NormalizedQuery.IsEmpty()](
		const FFinareNodePaletteItem& Left,
		const FFinareNodePaletteItem& Right)
		{
			if (bSearching && Left.MatchScore != Right.MatchScore)
			{
				return Left.MatchScore > Right.MatchScore;
			}
			if (Left.bFavorite != Right.bFavorite)
			{
				return Left.bFavorite;
			}
			const bool bLeftRecent = Left.RecentRank != INDEX_NONE;
			const bool bRightRecent = Right.RecentRank != INDEX_NONE;
			if (bLeftRecent != bRightRecent)
			{
				return bLeftRecent;
			}
			if (bLeftRecent && Left.RecentRank != Right.RecentRank)
			{
				return Left.RecentRank < Right.RecentRank;
			}
			if (!Left.Action.Category.Equals(Right.Action.Category, ESearchCase::IgnoreCase))
			{
				return LessText(Left.Action.Category, Right.Action.Category);
			}
			if (!Left.Action.Title.Equals(Right.Action.Title, ESearchCase::IgnoreCase))
			{
				return LessText(Left.Action.Title, Right.Action.Title);
			}
			return LessText(Left.Action.ActionId, Right.Action.ActionId);
		});
	return Result;
}

bool FFinareNodePaletteModel::ToggleFavorite(
	TArray<FString>& FavoriteActionIds,
	const FString& ActionId)
{
	if (ActionId.IsEmpty())
	{
		return false;
	}
	const int32 ExistingIndex = FavoriteActionIds.IndexOfByKey(ActionId);
	if (ExistingIndex != INDEX_NONE)
	{
		FavoriteActionIds.RemoveAt(ExistingIndex);
		return false;
	}
	FavoriteActionIds.Add(ActionId);
	return true;
}

void FFinareNodePaletteModel::RecordRecent(
	TArray<FString>& RecentActionIds,
	const FString& ActionId)
{
	if (ActionId.IsEmpty())
	{
		return;
	}
	RecentActionIds.Remove(ActionId);
	RecentActionIds.Insert(ActionId, 0);
	if (RecentActionIds.Num() > MaxRecentActions)
	{
		RecentActionIds.SetNum(MaxRecentActions);
	}
}
