/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 19:48:12
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 10:17:32
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptEditor\Private\Graph\UnifiedScriptGraphPresentation.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"
#include "UnifiedScriptAuthoringSurface.h"

/**
 * Read-only presentation policy over the lossless Authoring Surface graph.
 * It may compact visual nodes and labels, but never changes semantic IDs,
 * ports, edges, or the source-owned edit contract.
 */
struct FUnifiedScriptGraphPresentation
{
    static bool ShouldInlineAction(
        const FUnifiedScriptAuthoringAction& Action,
        const FUnifiedScriptAuthoringFunction& Function);

    static int32 CountVisibleActions(const FUnifiedScriptAuthoringFunction& Function);
    static int32 CountVisibleEdges(const FUnifiedScriptAuthoringFunction& Function);

    static FString GetNodeTitle(const FUnifiedScriptAuthoringAction& Action);
    static bool ShouldShowSummary(const FUnifiedScriptAuthoringAction& Action);
    static bool IsCompactNode(const FUnifiedScriptAuthoringAction& Action);

    static FString GetPinLabel(
        const FUnifiedScriptAuthoringAction& Action,
        const FUnifiedScriptAuthoringPort& Port,
        const FUnifiedScriptAuthoringFunction& Function);
    static FString GetPinTooltip(
        const FUnifiedScriptAuthoringAction& Action,
        const FUnifiedScriptAuthoringPort& Port,
        const FUnifiedScriptAuthoringFunction& Function);
    static bool ShouldHidePin(
        const FUnifiedScriptAuthoringAction& Action,
        const FUnifiedScriptAuthoringPort& Port,
        const FUnifiedScriptAuthoringFunction& Function);

private:
    static const FUnifiedScriptAuthoringAction* FindInlineSource(
        const FUnifiedScriptAuthoringAction& Action,
        const FUnifiedScriptAuthoringPort& Port,
        const FUnifiedScriptAuthoringFunction& Function);
};
