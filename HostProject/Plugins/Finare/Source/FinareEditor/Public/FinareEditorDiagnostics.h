#pragma once

#include "CoreMinimal.h"

struct FFinareAuthoringErrorResult;
struct FFinareReloadTransactionResult;
struct FFinareRuntimeDiagnostic;

/** One normalized, navigable UE-facing diagnostic from any Finare phase. */
struct FINAREEDITOR_API FFinareEditorDiagnostic
{
	FString Domain;
	FString Stage;
	FString Code;
	FString Severity;
	FString Summary;
	FString Detail;
	FString SourcePath;
	int32 Line = 0;
	int32 Column = 0;
	FString Member;
	FString GraphNode;
	FString PackagePath;
	uint64 WorldHostId = 0;
	uint64 InstanceId = 0;
	int32 Generation = 0;
	FString RecoveryAction;
	FString EvidencePath;

	bool IsValid() const { return !Code.IsEmpty() && !Summary.IsEmpty(); }
	bool HasSource() const { return !SourcePath.IsEmpty() && Line > 0; }
	FString ToClipboardText() const;
};

/**
 * Bridges compile, generation, coverage, stale-surface, reload, runtime, and
 * setup failures into one Message Log listing and one bounded bundle input.
 */
class FINAREEDITOR_API FFinareEditorDiagnostics final
{
public:
	static constexpr int32 MaximumRetainedDiagnosticCount = 128;
	static const FName LogName;

	static void Register();
	static void Unregister();

	static TArray<FFinareEditorDiagnostic> FromAuthoringFailure(
		const FFinareAuthoringErrorResult& Failure);
	static FFinareEditorDiagnostic FromReloadTransaction(
		const FFinareReloadTransactionResult& Transaction,
		const FString& SourcePath);
	static FFinareEditorDiagnostic FromRuntime(
		const FFinareRuntimeDiagnostic& Runtime);
	static FFinareEditorDiagnostic FromSetupFailure(
		const FString& Operation,
		int32 ExitCode,
		const FString& ReportPath);
	static TArray<FFinareEditorDiagnostic> ReadReport(
		const FString& ReportPath);

	static void Publish(
		const FText& PageLabel,
		const TArray<FFinareEditorDiagnostic>& Diagnostics,
		bool bOpenLog = true);
	static void ExecuteRecovery(const FFinareEditorDiagnostic& Diagnostic);
	static bool OpenSource(const FString& SourcePath, int32 Line, int32 Column);
	static FString ResolveSourcePath(const FString& SourcePath);
	static FString GetSnapshotPath();
};
