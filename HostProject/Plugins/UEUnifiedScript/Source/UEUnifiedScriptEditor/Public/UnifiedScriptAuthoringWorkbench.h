/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 18:41:46
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 14:22:40
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptEditor\Public\UnifiedScriptAuthoringWorkbench.h
 * @Description:
 */
#pragma once

#include "CoreMinimal.h"
#include "UnifiedScriptAuthoringSurface.h"

struct FUnifiedScriptAuthoringHistoryResult
{
    bool bCanUndo = false;
    bool bCanRedo = false;
    int64 UndoDepth = 0;
    int64 RedoDepth = 0;
    FString UndoTransactionId;
    FString UndoDescription;
    FString RedoTransactionId;
    FString RedoDescription;
    FString LastOperationId;
};

struct FUnifiedScriptAuthoringWorkspaceResult
{
    int64 Revision = 0;
    int64 Generation = 0;
    FString SourcePath;
    FUnifiedScriptAuthoringSurfaceDocument Surface;
    FUnifiedScriptAuthoringHistoryResult History;
};

class UEUNIFIEDSCRIPTEDITOR_API FUnifiedScriptAuthoringWorkbench
{
public:
    static FString NormalizeServerUrl(const FString& ServerUrl);

    static FString BuildSemanticPatchUrl(const FString& ServerUrl);

    static FString BuildDeclarationPatchUrl(const FString& ServerUrl);

    static FString BuildSemanticPatchPlanUrl(const FString& ServerUrl);

    static FString BuildUndoUrl(const FString& ServerUrl);

    static FString BuildRedoUrl(const FString& ServerUrl);

    static bool ParseWorkspaceResponse(
        const FString& Json,
        FUnifiedScriptAuthoringWorkspaceResult& OutWorkspace,
        FString& OutError);

    static FString ParseErrorResponse(const FString& Json, int32 StatusCode);
};
