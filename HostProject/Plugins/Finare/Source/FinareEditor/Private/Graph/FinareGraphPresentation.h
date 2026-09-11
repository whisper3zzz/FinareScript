#pragma once

#include "CoreMinimal.h"
#include "FinareAuthoringSurface.h"

/**
 * Read-only presentation policy over the lossless Authoring Surface graph.
 * It may compact visual nodes and labels, but never changes semantic IDs,
 * ports, edges, or the source-owned edit contract.
 */
struct FFinareGraphPresentation
{
	static bool ShouldInlineAction(
		const FFinareAuthoringAction& Action,
		const FFinareAuthoringFunction& Function);

	static int32 CountVisibleActions(const FFinareAuthoringFunction& Function);
	static int32 CountVisibleEdges(const FFinareAuthoringFunction& Function);

	static FString GetNodeTitle(const FFinareAuthoringAction& Action);
	static FString GetNodeSubtitle(const FFinareAuthoringAction& Action);
	static FString GetNodeTooltip(const FFinareAuthoringAction& Action);
	static bool ShouldShowSummary(const FFinareAuthoringAction& Action);
	static bool IsCompactNode(const FFinareAuthoringAction& Action);

	static FString GetPinLabel(
		const FFinareAuthoringAction& Action,
		const FFinareAuthoringPort& Port,
		const FFinareAuthoringFunction& Function);
	static FString GetPinTooltip(
		const FFinareAuthoringAction& Action,
		const FFinareAuthoringPort& Port,
		const FFinareAuthoringFunction& Function);
	static bool ShouldHidePin(
		const FFinareAuthoringAction& Action,
		const FFinareAuthoringPort& Port,
		const FFinareAuthoringFunction& Function);

private:
	static const FFinareAuthoringAction* FindInlineSource(
		const FFinareAuthoringAction& Action,
		const FFinareAuthoringPort& Port,
		const FFinareAuthoringFunction& Function);
};
