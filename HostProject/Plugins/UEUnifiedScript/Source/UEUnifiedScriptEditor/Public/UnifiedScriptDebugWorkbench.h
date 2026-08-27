#pragma once

#include "CoreMinimal.h"
#include "UnifiedScriptAuthoringSurface.h"
#include "UnifiedScriptRuntimeDiagnostic.h"

enum class EUnifiedScriptDebugSessionState : uint8
{
    Running,
    Paused,
    Completed,
};

struct FUnifiedScriptDebugGeneration
{
    int64 Number = 0;
    FString DomainId;
    FString GenerationId;
    FString ImageKey;
    FString SourceCompilationId;
    FString SemanticConfigurationId;
};

struct FUnifiedScriptLiveWorldDebugPublish
{
    FUnifiedScriptDebugGeneration Generation;
    FString AssemblyPath;
    FString PortablePdbPath;
};

struct FUnifiedScriptDebugValue
{
    FString Name;
    FString TypeMetadataName;
    FString Display;
    FString Kind;
    TArray<FUnifiedScriptDebugValue> Children;
    bool bTruncated = false;
};

struct FUnifiedScriptDebugStackFrame
{
    int64 FrameId = 0;
    int32 Index = 0;
    FString MethodId;
    FString NodeId;
    FString MethodMetadataName;
    int32 MetadataToken = 0;
    int32 IlOffset = 0;
    TOptional<FUnifiedScriptAuthoringSpan> SourceSpan;
    bool bHasLocals = false;
};

struct FUnifiedScriptDebugFrameLocals
{
    int64 FrameId = 0;
    TArray<FUnifiedScriptDebugValue> Locals;
};

struct FUnifiedScriptDebugBreakpointBinding
{
    FString BreakpointId;
    FString NodeId;
    FString State;
    FString MethodId;
    FString Message;
};

struct FUnifiedScriptDebugPause
{
    FString PauseId;
    FString Reason;
    FString StepKind;
    FString MethodId;
    FString NodeId;
};

struct FUnifiedScriptDebugResult
{
    FString Outcome;
    FString ReturnTypeMetadataName;
    FString ReturnJson;
    FString FaultTypeMetadataName;
    FString FaultMessage;
};

struct FUnifiedScriptDebugSession
{
    EUnifiedScriptDebugSessionState State = EUnifiedScriptDebugSessionState::Running;
    FUnifiedScriptDebugGeneration Generation;
    int64 ExecutionId = 0;
    TOptional<FUnifiedScriptDebugPause> Pause;
    TArray<FUnifiedScriptDebugStackFrame> Frames;
    TArray<FUnifiedScriptDebugFrameLocals> FrameLocals;
    TArray<FUnifiedScriptDebugBreakpointBinding> Breakpoints;
    TOptional<FUnifiedScriptDebugResult> Result;

    bool IsRunning() const { return State == EUnifiedScriptDebugSessionState::Running; }
    bool IsPaused() const { return State == EUnifiedScriptDebugSessionState::Paused; }
    bool IsCompleted() const { return State == EUnifiedScriptDebugSessionState::Completed; }
};

struct FUnifiedScriptDebugEvaluation
{
    FUnifiedScriptDebugGeneration Generation;
    FString PauseId;
    int64 FrameId = 0;
    FString Expression;
    FUnifiedScriptDebugValue Value;
};

/** Strict native client contract for the loopback CoreCLR debugger. */
class UEUNIFIEDSCRIPTEDITOR_API FUnifiedScriptDebugWorkbench
{
public:
    static FString BuildStartUrl(const FString& ServerUrl);
    static FString BuildWaitUrl(const FString& ServerUrl);
    static FString BuildPauseUrl(const FString& ServerUrl);
    static FString BuildContinueUrl(const FString& ServerUrl);
    static FString BuildStepInUrl(const FString& ServerUrl);
    static FString BuildStepOverUrl(const FString& ServerUrl);
    static FString BuildStepOutUrl(const FString& ServerUrl);
    static FString BuildEvaluateUrl(const FString& ServerUrl);
    static FString BuildLiveWorldPublishUrl(const FString& ServerUrl);

    static bool BuildStartRequest(
        const FString& SourceCompilationId,
        const FString& SemanticConfigurationId,
        const FUnifiedScriptAuthoringFunction& Function,
        const FUnifiedScriptAuthoringAsset& Asset,
        const TSet<FString>& BreakpointNodeIds,
        FString& OutJson,
        FString& OutError);

    static bool BuildExecutionRequest(
        const FUnifiedScriptDebugSession& Session,
        FString& OutJson,
        FString& OutError);

    static bool BuildResumeRequest(
        const FUnifiedScriptDebugSession& Session,
        FString& OutJson,
        FString& OutError);

    static bool BuildEvaluateRequest(
        const FUnifiedScriptDebugSession& Session,
        int64 FrameId,
        const FString& Expression,
        FString& OutJson,
        FString& OutError);

    static bool BuildLiveWorldPublishRequest(
        const FString& SourceCompilationId,
        const FString& SemanticConfigurationId,
        FString& OutJson,
        FString& OutError);

    static bool ParseLiveWorldPublishResponse(
        const FString& Json,
        FUnifiedScriptLiveWorldDebugPublish& OutPublish,
        FString& OutError);

    static bool BuildLiveWorldConfiguration(
        const FUnifiedScriptLiveWorldDebugPublish& Publish,
        int32 RuntimeGeneration,
        const TSet<FString>& BreakpointNodeIds,
        FString& OutJson,
        FString& OutError);

    static bool ParseSessionResponse(
        const FString& Json,
        FUnifiedScriptDebugSession& OutSession,
        FString& OutError);

    static bool ParseEvaluationResponse(
        const FString& Json,
        FUnifiedScriptDebugEvaluation& OutEvaluation,
        FString& OutError);

    /** Resolves the first runtime stack source owned by a semantic graph action. */
    static bool ResolveRuntimeDiagnosticNode(
        const FUnifiedScriptAuthoringAsset& Asset,
        TConstArrayView<FUnifiedScriptRuntimeSourceLocation> SourceFrames,
        FString& OutMethodId,
        FString& OutNodeId);
};
