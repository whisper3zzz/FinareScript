#pragma once

#include "CoreMinimal.h"

struct FUnifiedScriptAuthoringErrorResult;
struct FUnifiedScriptReloadTransactionResult;
struct FUnifiedScriptRuntimeDiagnostic;

/** One normalized, navigable UE-facing diagnostic from any UnifiedScript phase. */
struct UEUNIFIEDSCRIPTEDITOR_API FUnifiedScriptEditorDiagnostic
{
    FString Domain;
    FString Code;
    FString Severity;
    FString Summary;
    FString Detail;
    FString SourcePath;
    int32 Line = 0;
    int32 Column = 0;
    FString Member;
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
class UEUNIFIEDSCRIPTEDITOR_API FUnifiedScriptEditorDiagnostics final
{
public:
    static constexpr int32 MaximumRetainedDiagnosticCount = 128;
    static const FName LogName;

    static void Register();
    static void Unregister();

    static TArray<FUnifiedScriptEditorDiagnostic> FromAuthoringFailure(
        const FUnifiedScriptAuthoringErrorResult& Failure);
    static FUnifiedScriptEditorDiagnostic FromReloadTransaction(
        const FUnifiedScriptReloadTransactionResult& Transaction,
        const FString& SourcePath);
    static FUnifiedScriptEditorDiagnostic FromRuntime(
        const FUnifiedScriptRuntimeDiagnostic& Runtime);
    static FUnifiedScriptEditorDiagnostic FromSetupFailure(
        const FString& Operation,
        int32 ExitCode,
        const FString& ReportPath);
    static TArray<FUnifiedScriptEditorDiagnostic> ReadReport(
        const FString& ReportPath);

    static void Publish(
        const FText& PageLabel,
        const TArray<FUnifiedScriptEditorDiagnostic>& Diagnostics,
        bool bOpenLog = true);
    static bool OpenSource(const FString& SourcePath, int32 Line, int32 Column);
    static FString ResolveSourcePath(const FString& SourcePath);
    static FString GetSnapshotPath();
};
