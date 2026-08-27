#pragma once

#include "CoreMinimal.h"

struct UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptRuntimeSourceLocation
{
    FString FilePath;
    int32 Line = 0;
    int32 Column = 0;
    FString MethodName;

    bool IsValid() const { return !FilePath.IsEmpty() && Line > 0; }
    FString ToDisplayString() const;
};

struct UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptRuntimeBridgeFailure
{
    FString Error;
    FString HandleError;
    uint64 TargetHandle = 0;
    uint64 MemberId = 0;
    FString MemberPath;

    bool IsSet() const { return !Error.IsEmpty(); }
};

/** Detached v0.1 failure record emitted by the managed UE World boundary. */
struct UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptRuntimeDiagnostic
{
    FString SchemaVersion;
    int64 Sequence = 0;
    FString TimestampUtc;
    FString Kind;
    FString Operation;
    FString Message;
    FString ExceptionType;
    FString StackTrace;
    uint64 WorldHostId = 0;
    uint64 InstanceId = 0;
    int32 Generation = 0;
    FString AssemblyPath;
    FString TypeName;
    uint64 HostHandle = 0;
    uint64 TargetHandle = 0;
    FUnifiedScriptRuntimeSourceLocation Source;
    TArray<FUnifiedScriptRuntimeSourceLocation> SourceFrames;
    FUnifiedScriptRuntimeBridgeFailure Bridge;
    FString RawJson;

    bool IsValid() const;
    FString ToDisplayString() const;

    /** Extracts the structured JSON record from a native boundary error wrapper. */
    static bool TryParseBoundaryError(
        const FString& BoundaryError,
        FUnifiedScriptRuntimeDiagnostic& OutDiagnostic,
        FString& OutError);
};

enum class EUnifiedScriptSemanticDebugControl : uint8
{
    Pause = 1,
    Continue = 2,
    StepInto = 3,
    StepOver = 4,
    StepOut = 5,
};

/** Current native identity for one managed script hosted by a real UWorld. */
struct UEUNIFIEDSCRIPTRUNTIME_API FUnifiedScriptRuntimeDebugContext
{
    uint64 WorldHostId = 0;
    FString WorldName;
    FString WorldType;
    uint64 InstanceId = 0;
    int32 Generation = 0;
    uint64 HostHandle = 0;
    uint64 TargetHandle = 0;
    FString AssemblyPath;
    FString TypeName;
    bool bRunning = false;
    bool bPausedAtFrameBoundary = false;
    bool bSemanticDebugEnabled = false;
    bool bSemanticDebugActive = false;
    bool bSemanticDebugPaused = false;
    FString SemanticDebugStateJson;
    FUnifiedScriptRuntimeDiagnostic LastDiagnostic;

    bool IsValid() const { return WorldHostId != 0 && InstanceId != 0; }
};
