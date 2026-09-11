#pragma once

#include "CoreMinimal.h"

struct FINARERUNTIME_API FFinareRuntimeSourceLocation
{
	FString FilePath;
	int32 Line = 0;
	int32 Column = 0;
	FString MethodName;

	bool IsValid() const { return !FilePath.IsEmpty() && Line > 0; }
	FString ToDisplayString() const;
};

struct FINARERUNTIME_API FFinareRuntimeBridgeFailure
{
	FString Error;
	FString HandleError;
	uint64 TargetHandle = 0;
	uint64 MemberId = 0;
	FString MemberPath;

	bool IsSet() const { return !Error.IsEmpty(); }
};

/** Detached v0.1 failure record emitted by the managed UE World boundary. */
struct FINARERUNTIME_API FFinareRuntimeDiagnostic
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
	FFinareRuntimeSourceLocation Source;
	TArray<FFinareRuntimeSourceLocation> SourceFrames;
	FFinareRuntimeBridgeFailure Bridge;
	FString RawJson;

	bool IsValid() const;
	FString ToDisplayString() const;

	/** Extracts the structured JSON record from a native boundary error wrapper. */
	static bool TryParseBoundaryError(
		const FString& BoundaryError,
		FFinareRuntimeDiagnostic& OutDiagnostic,
		FString& OutError);
};

/**
 * Detached, size-bounded process breadcrumb retained independently of any
 * UWorld or collectible managed generation.
 */
struct FINARERUNTIME_API FFinareRuntimeBreadcrumb
{
	uint64 Sequence = 0;
	FString TimestampUtc;
	FString Kind;
	FString Operation;
	FString Message;
	uint64 WorldHostId = 0;
	uint64 InstanceId = 0;
	int32 Generation = 0;
};

struct FINARERUNTIME_API FFinareRuntimeBreadcrumbSnapshot
{
	uint64 DroppedCount = 0;
	TArray<FFinareRuntimeBreadcrumb> Entries;

	FString ToJson() const;
};

/**
 * Process-wide crash breadcrumbs. Entries retain no UObject, managed object,
 * source text, or raw pointer and are safe after World teardown.
 */
class FINARERUNTIME_API FFinareRuntimeBreadcrumbs final
{
public:
	static constexpr int32 MaximumEntryCount = 64;
	static constexpr int32 MaximumKindLength = 64;
	static constexpr int32 MaximumOperationLength = 96;
	static constexpr int32 MaximumMessageLength = 512;
	static constexpr int32 MaximumCrashContextEntryCount = 8;

	static void RecordDiagnostic(const FFinareRuntimeDiagnostic& Diagnostic);
	static void RecordLifecycle(
		const FString& Operation,
		uint64 WorldHostId,
		const FString& Message = FString());
	static void RecordFailure(
		const FString& Operation,
		const FString& Message,
		uint64 WorldHostId,
		uint64 InstanceId,
		int32 Generation);

	static FFinareRuntimeBreadcrumbSnapshot Capture();
	static FString ExportJson();

#if WITH_DEV_AUTOMATION_TESTS
	static void ResetForTests();
#endif

private:
	static void Record(FFinareRuntimeBreadcrumb Breadcrumb);
};

enum class EFinareSemanticDebugControl : uint8
{
	Pause = 1,
	Continue = 2,
	StepInto = 3,
	StepOver = 4,
	StepOut = 5,
};

/** Current native identity for one managed script hosted by a real UWorld. */
struct FINARERUNTIME_API FFinareRuntimeDebugContext
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
	FFinareRuntimeDiagnostic LastDiagnostic;

	bool IsValid() const { return WorldHostId != 0 && InstanceId != 0; }
};
