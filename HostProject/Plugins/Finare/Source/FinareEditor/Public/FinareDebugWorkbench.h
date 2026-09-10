#pragma once

#include "CoreMinimal.h"
#include "FinareAuthoringSurface.h"
#include "FinareRuntimeDiagnostic.h"

enum class EFinareDebugSessionState : uint8
{
    Running,
    Paused,
    Completed,
};

struct FFinareDebugGeneration
{
    int64 Number = 0;
    FString DomainId;
    FString GenerationId;
    FString ImageKey;
    FString SourceCompilationId;
    FString SemanticConfigurationId;
};

struct FFinareLiveWorldDebugPublish
{
    FFinareDebugGeneration Generation;
    FString AssemblyPath;
    FString PortablePdbPath;
    FString ReloadTransactionJson;
};

struct FFinareDebugValue
{
    FString Name;
    FString TypeMetadataName;
    FString Display;
    FString Kind;
    TArray<FFinareDebugValue> Children;
    bool bTruncated = false;
};

struct FFinareDebugStackFrame
{
    int64 FrameId = 0;
    int32 Index = 0;
    FString MethodId;
    FString NodeId;
    FString MethodMetadataName;
    int32 MetadataToken = 0;
    int32 IlOffset = 0;
    TOptional<FFinareAuthoringSpan> SourceSpan;
    bool bHasLocals = false;
};

struct FFinareDebugFrameLocals
{
    int64 FrameId = 0;
    TArray<FFinareDebugValue> Locals;
};

struct FFinareDebugBreakpointBinding
{
    FString BreakpointId;
    FString NodeId;
    FString State;
    FString MethodId;
    FString Message;
};

struct FFinareDebugPause
{
    FString PauseId;
    FString Reason;
    FString StepKind;
    FString MethodId;
    FString NodeId;
};

struct FFinareDebugResult
{
    FString Outcome;
    FString ReturnTypeMetadataName;
    FString ReturnJson;
    FString FaultTypeMetadataName;
    FString FaultMessage;
};

struct FFinareDebugSession
{
    EFinareDebugSessionState State = EFinareDebugSessionState::Running;
    FFinareDebugGeneration Generation;
    int64 ExecutionId = 0;
    TOptional<FFinareDebugPause> Pause;
    TArray<FFinareDebugStackFrame> Frames;
    TArray<FFinareDebugFrameLocals> FrameLocals;
    TArray<FFinareDebugBreakpointBinding> Breakpoints;
    TOptional<FFinareDebugResult> Result;

    bool IsRunning() const { return State == EFinareDebugSessionState::Running; }
    bool IsPaused() const { return State == EFinareDebugSessionState::Paused; }
    bool IsCompleted() const { return State == EFinareDebugSessionState::Completed; }
};

struct FFinareDebugEvaluation
{
    FFinareDebugGeneration Generation;
    FString PauseId;
    int64 FrameId = 0;
    FString Expression;
    FFinareDebugValue Value;
};

struct FFinareGameplayProfileBoundary
{
    FString Name;
    int64 Count = 0;
    int64 TotalNanoseconds = 0;
};

struct FFinareGameplayProfileNode
{
    FString MethodId;
    FString NodeId;
    int64 HitCount = 0;
    int64 TotalNanoseconds = 0;
    TArray<FFinareGameplayProfileBoundary> Boundaries;
};

struct FFinareGameplayProfileBufferPressure
{
    int64 CommandOverflowCount = 0;
    int64 EventOverflowCount = 0;
    int64 TraceOverflowCount = 0;
    int64 OverlapOverflowCount = 0;

    int64 TotalOverflowCount() const
    {
        int64 Result = 0;
        for (const int64 Value : {
                 CommandOverflowCount,
                 EventOverflowCount,
                 TraceOverflowCount,
                 OverlapOverflowCount})
        {
            Result = Value > MAX_int64 - Result ? MAX_int64 : Result + Value;
        }
        return Result;
    }
};

struct FFinareGameplayProfileFrame
{
    int64 Sequence = 0;
    FString WorldFrameNumber;
    int32 Generation = 0;
    FString ScriptType;
    double DeltaSeconds = 0.0;
    FString WorldPhase;
    int64 DurationNanoseconds = 0;
    int64 AllocatedBytes = 0;
    TArray<FFinareGameplayProfileBoundary> WorldPhases;
    TArray<FFinareGameplayProfileBoundary> Boundaries;
    TArray<FFinareGameplayProfileNode> Nodes;
    FFinareGameplayProfileBufferPressure BufferPressure;
};

struct FFinareGameplayProfileResult
{
    FString TraceId;
    FString WorldHostId;
    FString InstanceId;
    int32 FrameCapacity = 0;
    int64 DroppedFrameCount = 0;
    TArray<FFinareGameplayProfileFrame> Frames;
    int64 TotalNanoseconds = 0;
    int64 AllocatedBytes = 0;
    TArray<FFinareGameplayProfileBoundary> WorldPhases;
    TArray<FFinareGameplayProfileBoundary> Boundaries;
    TArray<FFinareGameplayProfileNode> Nodes;
    FFinareGameplayProfileBufferPressure BufferPressure;

    FString ToDisplayString() const;
};

/** Strict native client contract for the loopback CoreCLR debugger. */
class FINAREEDITOR_API FFinareDebugWorkbench
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
        const FFinareAuthoringFunction& Function,
        const FFinareAuthoringAsset& Asset,
        const TSet<FString>& BreakpointNodeIds,
        FString& OutJson,
        FString& OutError);

    static bool BuildExecutionRequest(
        const FFinareDebugSession& Session,
        FString& OutJson,
        FString& OutError);

    static bool BuildResumeRequest(
        const FFinareDebugSession& Session,
        FString& OutJson,
        FString& OutError);

    static bool BuildEvaluateRequest(
        const FFinareDebugSession& Session,
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
        FFinareLiveWorldDebugPublish& OutPublish,
        FString& OutError);

    static bool BuildLiveWorldConfiguration(
        const FFinareLiveWorldDebugPublish& Publish,
        int32 RuntimeGeneration,
        const TSet<FString>& BreakpointNodeIds,
        FString& OutJson,
        FString& OutError);

    static bool ParseSessionResponse(
        const FString& Json,
        FFinareDebugSession& OutSession,
        FString& OutError);

    static bool ParseEvaluationResponse(
        const FString& Json,
        FFinareDebugEvaluation& OutEvaluation,
        FString& OutError);

    static bool ParseGameplayProfileTrace(
        const FString& Json,
        FFinareGameplayProfileResult& OutProfile,
        FString& OutError);

    /** Resolves the first runtime stack source owned by a semantic graph action. */
    static bool ResolveRuntimeDiagnosticNode(
        const FFinareAuthoringAsset& Asset,
        TConstArrayView<FFinareRuntimeSourceLocation> SourceFrames,
        FString& OutMethodId,
        FString& OutNodeId);
};
