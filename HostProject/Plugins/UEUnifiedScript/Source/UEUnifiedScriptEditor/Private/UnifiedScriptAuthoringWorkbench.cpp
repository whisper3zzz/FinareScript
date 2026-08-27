/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-24 18:41:46
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 14:22:40
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptEditor\Private\UnifiedScriptAuthoringWorkbench.cpp
 * @Description:
 */
#include "UnifiedScriptAuthoringWorkbench.h"

#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

namespace UE::UnifiedScript::AuthoringWorkbench::Private
{
constexpr TCHAR WorkspaceSchema[] = TEXT("unifiedscript.authoring-workspace/v0.4");
constexpr TCHAR PreviewSchema[] = TEXT("unifiedscript.authoring-preview/v0.1");

bool ReadPositiveInteger(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    int64& OutValue,
    FString& OutError)
{
    double Number = 0.0;
    if (!Object.IsValid() || !Object->TryGetNumberField(Field, Number) ||
        Number < 1.0 || Number != FMath::FloorToDouble(Number))
    {
        OutError = FString::Printf(TEXT("Authoring workspace field '%s' must be a positive integer."), Field);
        return false;
    }
    OutValue = static_cast<int64>(Number);
    return true;
}

bool ReadNonNegativeInteger(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    int64& OutValue,
    FString& OutError)
{
    double Number = 0.0;
    if (!Object.IsValid() || !Object->TryGetNumberField(Field, Number) ||
        Number < 0.0 || Number != FMath::FloorToDouble(Number))
    {
        OutError = FString::Printf(TEXT("Authoring workspace field '%s' must be a non-negative integer."), Field);
        return false;
    }
    OutValue = static_cast<int64>(Number);
    return true;
}

bool ReadRequiredString(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    FString& OutValue,
    FString& OutError)
{
    if (!Object.IsValid() || !Object->TryGetStringField(Field, OutValue) || OutValue.IsEmpty())
    {
        OutError = FString::Printf(TEXT("Authoring response field '%s' must be a non-empty string."), Field);
        return false;
    }
    return true;
}

bool ReadSpan(
    const TSharedPtr<FJsonObject>& Object,
    FUnifiedScriptAuthoringSpan& OutSpan,
    FString& OutError)
{
    const TSharedPtr<FJsonObject>* Span = nullptr;
    const TSharedPtr<FJsonObject>* Start = nullptr;
    const TSharedPtr<FJsonObject>* End = nullptr;
    double StartLine = 0.0;
    double StartColumn = 0.0;
    double EndLine = 0.0;
    double EndColumn = 0.0;
    if (!Object.IsValid() || !Object->TryGetObjectField(TEXT("span"), Span) ||
        Span == nullptr || !ReadRequiredString(*Span, TEXT("filePath"), OutSpan.FilePath, OutError) ||
        !(*Span)->TryGetObjectField(TEXT("start"), Start) || Start == nullptr ||
        !(*Span)->TryGetObjectField(TEXT("end"), End) || End == nullptr ||
        !(*Start)->TryGetNumberField(TEXT("line"), StartLine) ||
        !(*Start)->TryGetNumberField(TEXT("column"), StartColumn) ||
        !(*End)->TryGetNumberField(TEXT("line"), EndLine) ||
        !(*End)->TryGetNumberField(TEXT("column"), EndColumn))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("Authoring diagnostic requires one exact source span.");
        }
        return false;
    }

    OutSpan.StartLine = static_cast<int32>(StartLine);
    OutSpan.StartColumn = static_cast<int32>(StartColumn);
    OutSpan.EndLine = static_cast<int32>(EndLine);
    OutSpan.EndColumn = static_cast<int32>(EndColumn);
    if (StartLine != OutSpan.StartLine || StartColumn != OutSpan.StartColumn ||
        EndLine != OutSpan.EndLine || EndColumn != OutSpan.EndColumn || !OutSpan.IsValid())
    {
        OutError = TEXT("Authoring diagnostic contains an invalid source span.");
        return false;
    }
    return true;
}

bool ReadDiagnostics(
    const TSharedPtr<FJsonObject>& Root,
    TArray<FUnifiedScriptAuthoringDiagnostic>& OutDiagnostics,
    FString& OutError,
    bool bRequired)
{
    const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
    if (!Root.IsValid() || !Root->TryGetArrayField(TEXT("diagnostics"), Values) || Values == nullptr)
    {
        if (bRequired)
        {
            OutError = TEXT("Authoring response requires a diagnostics array.");
            return false;
        }
        return true;
    }

    for (const TSharedPtr<FJsonValue>& Value : *Values)
    {
        const TSharedPtr<FJsonObject> Object = Value.IsValid() ? Value->AsObject() : nullptr;
        FUnifiedScriptAuthoringDiagnostic Diagnostic;
        if (!ReadRequiredString(Object, TEXT("code"), Diagnostic.Code, OutError) ||
            !ReadRequiredString(Object, TEXT("severity"), Diagnostic.Severity, OutError) ||
            !ReadRequiredString(Object, TEXT("message"), Diagnostic.Message, OutError) ||
            !ReadSpan(Object, Diagnostic.Span, OutError) ||
            !(Diagnostic.Severity == TEXT("info") ||
              Diagnostic.Severity == TEXT("warning") ||
              Diagnostic.Severity == TEXT("error")))
        {
            if (OutError.IsEmpty())
            {
                OutError = TEXT("Authoring diagnostic has an unsupported severity.");
            }
            return false;
        }
        Object->TryGetStringField(TEXT("semanticId"), Diagnostic.SemanticId);
        OutDiagnostics.Add(MoveTemp(Diagnostic));
    }
    return true;
}
}

FString FUnifiedScriptAuthoringDiagnostic::ToDisplayString() const
{
    return FString::Printf(
        TEXT("%s %s: %s (%s)"),
        *Severity.ToUpper(),
        *Code,
        *Message,
        *Span.ToDisplayString());
}

FString FUnifiedScriptAuthoringErrorResult::ToDisplayString() const
{
    return FString::Printf(TEXT("Workbench %d %s: %s"), StatusCode, *Code, *Message);
}

FString FUnifiedScriptAuthoringWorkbench::NormalizeServerUrl(const FString& ServerUrl)
{
    FString Result = ServerUrl;
    Result.TrimStartAndEndInline();
    while (Result.EndsWith(TEXT("/")))
    {
        Result.LeftChopInline(1);
    }
    return Result;
}

FString FUnifiedScriptAuthoringWorkbench::BuildSemanticPatchUrl(const FString& ServerUrl)
{
    const FString Normalized = NormalizeServerUrl(ServerUrl);
    return Normalized.IsEmpty()
        ? FString()
        : Normalized + TEXT("/api/authoring/semantic-patch");
}

FString FUnifiedScriptAuthoringWorkbench::BuildDeclarationPatchUrl(const FString& ServerUrl)
{
    const FString Normalized = NormalizeServerUrl(ServerUrl);
    return Normalized.IsEmpty()
        ? FString()
        : Normalized + TEXT("/api/authoring/declaration-patch");
}

FString FUnifiedScriptAuthoringWorkbench::BuildSemanticPatchPlanUrl(const FString& ServerUrl)
{
    const FString Normalized = NormalizeServerUrl(ServerUrl);
    return Normalized.IsEmpty()
        ? FString()
        : Normalized + TEXT("/api/authoring/semantic-patch-plan");
}

FString FUnifiedScriptAuthoringWorkbench::BuildPreviewUrl(const FString& TransactionUrl)
{
    const FString Normalized = NormalizeServerUrl(TransactionUrl);
    static const TMap<FString, FString> PreviewPaths = {
        {TEXT("/api/authoring/declaration-patch"), TEXT("/api/authoring/preview/declaration-patch")},
        {TEXT("/api/authoring/semantic-patch"), TEXT("/api/authoring/preview/semantic-patch")},
        {TEXT("/api/authoring/semantic-patch-plan"), TEXT("/api/authoring/preview/semantic-patch-plan")},
        {TEXT("/api/authoring/group/commit"), TEXT("/api/authoring/preview/group/commit")},
        {TEXT("/api/authoring/undo"), TEXT("/api/authoring/preview/undo")},
        {TEXT("/api/authoring/redo"), TEXT("/api/authoring/preview/redo")},
    };
    for (const TPair<FString, FString>& Entry : PreviewPaths)
    {
        if (Normalized.EndsWith(Entry.Key))
        {
            return Normalized.LeftChop(Entry.Key.Len()) + Entry.Value;
        }
    }
    return FString();
}

FString FUnifiedScriptAuthoringWorkbench::BuildWorkspaceUrl(const FString& ServerUrl)
{
    const FString Normalized = NormalizeServerUrl(ServerUrl);
    return Normalized.IsEmpty()
        ? FString()
        : Normalized + TEXT("/api/authoring/workspace");
}

FString FUnifiedScriptAuthoringWorkbench::BuildUndoUrl(const FString& ServerUrl)
{
    const FString Normalized = NormalizeServerUrl(ServerUrl);
    return Normalized.IsEmpty()
        ? FString()
        : Normalized + TEXT("/api/authoring/undo");
}

FString FUnifiedScriptAuthoringWorkbench::BuildRedoUrl(const FString& ServerUrl)
{
    const FString Normalized = NormalizeServerUrl(ServerUrl);
    return Normalized.IsEmpty()
        ? FString()
        : Normalized + TEXT("/api/authoring/redo");
}

FString FUnifiedScriptAuthoringWorkbench::BuildGroupBeginUrl(const FString& ServerUrl)
{
    const FString Normalized = NormalizeServerUrl(ServerUrl);
    return Normalized.IsEmpty()
        ? FString()
        : Normalized + TEXT("/api/authoring/group/begin");
}

FString FUnifiedScriptAuthoringWorkbench::BuildGroupCommitUrl(const FString& ServerUrl)
{
    const FString Normalized = NormalizeServerUrl(ServerUrl);
    return Normalized.IsEmpty()
        ? FString()
        : Normalized + TEXT("/api/authoring/group/commit");
}

FString FUnifiedScriptAuthoringWorkbench::BuildGroupCancelUrl(const FString& ServerUrl)
{
    const FString Normalized = NormalizeServerUrl(ServerUrl);
    return Normalized.IsEmpty()
        ? FString()
        : Normalized + TEXT("/api/authoring/group/cancel");
}

bool FUnifiedScriptAuthoringWorkbench::ParseWorkspaceResponse(
    const FString& Json,
    FUnifiedScriptAuthoringWorkspaceResult& OutWorkspace,
    FString& OutError)
{
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = TEXT("The Workbench returned invalid workspace JSON.");
        return false;
    }

    FString Schema;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
        Schema != UE::UnifiedScript::AuthoringWorkbench::Private::WorkspaceSchema)
    {
        OutError = FString::Printf(TEXT("Unsupported authoring workspace schema '%s'."), *Schema);
        return false;
    }

    FUnifiedScriptAuthoringWorkspaceResult Candidate;
    if (!UE::UnifiedScript::AuthoringWorkbench::Private::ReadPositiveInteger(
            Root, TEXT("revision"), Candidate.Revision, OutError))
    {
        return false;
    }
    Root->TryGetStringField(TEXT("sourcePath"), Candidate.SourcePath);

    const TSharedPtr<FJsonObject>* History = nullptr;
    if (!Root->TryGetObjectField(TEXT("history"), History) || !History->IsValid() ||
        !(*History)->TryGetBoolField(TEXT("canUndo"), Candidate.History.bCanUndo) ||
        !(*History)->TryGetBoolField(TEXT("canRedo"), Candidate.History.bCanRedo) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadNonNegativeInteger(
            *History, TEXT("undoDepth"), Candidate.History.UndoDepth, OutError) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadNonNegativeInteger(
            *History, TEXT("redoDepth"), Candidate.History.RedoDepth, OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("The Workbench response has no valid transaction history.");
        }
        return false;
    }
    (*History)->TryGetStringField(TEXT("undoTransactionId"), Candidate.History.UndoTransactionId);
    (*History)->TryGetStringField(TEXT("undoDescription"), Candidate.History.UndoDescription);
    (*History)->TryGetStringField(TEXT("redoTransactionId"), Candidate.History.RedoTransactionId);
    (*History)->TryGetStringField(TEXT("redoDescription"), Candidate.History.RedoDescription);
    (*History)->TryGetStringField(TEXT("lastOperationId"), Candidate.History.LastOperationId);
    if (!(*History)->TryGetStringField(
            TEXT("persistenceState"), Candidate.History.PersistenceState) ||
        !(Candidate.History.PersistenceState == TEXT("disabled") ||
          Candidate.History.PersistenceState == TEXT("empty") ||
          Candidate.History.PersistenceState == TEXT("restored") ||
          Candidate.History.PersistenceState == TEXT("rejected") ||
          Candidate.History.PersistenceState == TEXT("persisted")))
    {
        OutError = TEXT("The Workbench response has no valid history persistence state.");
        return false;
    }
    (*History)->TryGetStringField(
        TEXT("persistenceMessage"), Candidate.History.PersistenceMessage);

    const TSharedPtr<FJsonObject>* Group = nullptr;
    if (Root->TryGetObjectField(TEXT("group"), Group) && Group->IsValid())
    {
        if (!(*Group)->TryGetStringField(TEXT("id"), Candidate.Group.Id) ||
            Candidate.Group.Id.IsEmpty() ||
            !(*Group)->TryGetStringField(TEXT("description"), Candidate.Group.Description) ||
            Candidate.Group.Description.IsEmpty() ||
            !UE::UnifiedScript::AuthoringWorkbench::Private::ReadPositiveInteger(
                *Group, TEXT("baselineRevision"), Candidate.Group.BaselineRevision, OutError) ||
            !UE::UnifiedScript::AuthoringWorkbench::Private::ReadNonNegativeInteger(
                *Group, TEXT("stagedOperationCount"), Candidate.Group.StagedOperationCount, OutError))
        {
            if (OutError.IsEmpty())
            {
                OutError = TEXT("The Workbench response has invalid grouped transaction state.");
            }
            return false;
        }
        Candidate.Group.bActive = true;
    }

    const TSharedPtr<FJsonObject>* Generation = nullptr;
    if (!Root->TryGetObjectField(TEXT("generation"), Generation) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadPositiveInteger(
            *Generation, TEXT("number"), Candidate.Generation, OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("The Workbench response has no runtime generation.");
        }
        return false;
    }

    const TSharedPtr<FJsonObject>* Surface = nullptr;
    if (!Root->TryGetObjectField(TEXT("surface"), Surface) || !Surface->IsValid())
    {
        OutError = TEXT("The Workbench response has no Authoring Surface.");
        return false;
    }
    FString SurfaceJson;
    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&SurfaceJson);
    if (!FJsonSerializer::Serialize(Surface->ToSharedRef(), Writer) ||
        !FUnifiedScriptAuthoringSurfaceReader::ParseJson(
            SurfaceJson,
            Candidate.Surface,
            OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("The Workbench Authoring Surface could not be decoded.");
        }
        return false;
    }

    OutWorkspace = MoveTemp(Candidate);
    OutError.Reset();
    return true;
}

bool FUnifiedScriptAuthoringWorkbench::ParsePreviewResponse(
    const FString& Json,
    FUnifiedScriptAuthoringPreviewResult& OutPreview,
    FString& OutError)
{
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = TEXT("The Workbench returned invalid authoring preview JSON.");
        return false;
    }

    FString Schema;
    FUnifiedScriptAuthoringPreviewResult Candidate;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
        Schema != UE::UnifiedScript::AuthoringWorkbench::Private::PreviewSchema ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadPositiveInteger(
            Root, TEXT("baselineRevision"), Candidate.BaselineRevision, OutError) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadPositiveInteger(
            Root, TEXT("candidateRevision"), Candidate.CandidateRevision, OutError) ||
        Candidate.CandidateRevision <= Candidate.BaselineRevision ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
            Root, TEXT("transactionId"), Candidate.TransactionId, OutError) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
            Root, TEXT("description"), Candidate.Description, OutError) ||
        !Root->TryGetBoolField(TEXT("publishesRuntime"), Candidate.bPublishesRuntime) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
            Root, TEXT("previewToken"), Candidate.PreviewToken, OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = Schema == UE::UnifiedScript::AuthoringWorkbench::Private::PreviewSchema
                ? TEXT("The authoring preview has invalid transaction metadata.")
                : FString::Printf(TEXT("Unsupported authoring preview schema '%s'."), *Schema);
        }
        return false;
    }
    bool bValidPreviewToken = Candidate.PreviewToken.Len() == 64;
    for (TCHAR Character : Candidate.PreviewToken)
    {
        bValidPreviewToken = bValidPreviewToken && FChar::IsHexDigit(Character);
    }
    if (!bValidPreviewToken)
    {
        OutError = TEXT("The authoring preview has an invalid SHA-256 token.");
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* Files = nullptr;
    if (!Root->TryGetArrayField(TEXT("files"), Files) || Files == nullptr)
    {
        OutError = TEXT("The authoring preview requires a files array.");
        return false;
    }
    TSet<FString> Paths;
    for (const TSharedPtr<FJsonValue>& Value : *Files)
    {
        const TSharedPtr<FJsonObject> File = Value.IsValid() ? Value->AsObject() : nullptr;
        FUnifiedScriptAuthoringPreviewFile CandidateFile;
        if (!UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                File, TEXT("path"), CandidateFile.Path, OutError) ||
            !File->TryGetStringField(TEXT("beforeText"), CandidateFile.BeforeText) ||
            !File->TryGetStringField(TEXT("afterText"), CandidateFile.AfterText) ||
            CandidateFile.BeforeText == CandidateFile.AfterText ||
            Paths.Contains(CandidateFile.Path))
        {
            if (OutError.IsEmpty())
            {
                OutError = TEXT("The authoring preview contains an invalid or duplicate changed file.");
            }
            return false;
        }
        Paths.Add(CandidateFile.Path);
        Candidate.Files.Add(MoveTemp(CandidateFile));
    }
    if (!UE::UnifiedScript::AuthoringWorkbench::Private::ReadDiagnostics(
            Root, Candidate.Diagnostics, OutError, true))
    {
        return false;
    }

    OutPreview = MoveTemp(Candidate);
    OutError.Reset();
    return true;
}

bool FUnifiedScriptAuthoringWorkbench::ParseErrorResponse(
    const FString& Json,
    int32 StatusCode,
    FUnifiedScriptAuthoringErrorResult& OutFailure,
    FString& OutParseError)
{
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutParseError = TEXT("The Workbench returned invalid authoring error JSON.");
        return false;
    }

    const TSharedPtr<FJsonObject>* Error = nullptr;
    FUnifiedScriptAuthoringErrorResult Candidate;
    Candidate.StatusCode = StatusCode;
    if (!Root->TryGetObjectField(TEXT("error"), Error) || Error == nullptr ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
            *Error, TEXT("code"), Candidate.Code, OutParseError) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
            *Error, TEXT("message"), Candidate.Message, OutParseError) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadDiagnostics(
            Root, Candidate.Diagnostics, OutParseError, false))
    {
        if (OutParseError.IsEmpty())
        {
            OutParseError = TEXT("The Workbench error response has no protocol error.");
        }
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* QuickFixes = nullptr;
    if (Root->TryGetArrayField(TEXT("quickFixes"), QuickFixes) && QuickFixes != nullptr)
    {
        TSet<FString> FixIds;
        for (const TSharedPtr<FJsonValue>& Value : *QuickFixes)
        {
            const TSharedPtr<FJsonObject> Fix = Value.IsValid() ? Value->AsObject() : nullptr;
            FUnifiedScriptAuthoringQuickFix CandidateFix;
            if (!UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                    Fix, TEXT("id"), CandidateFix.Id, OutParseError) ||
                !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                    Fix, TEXT("label"), CandidateFix.Label, OutParseError) ||
                !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                    Fix, TEXT("kind"), CandidateFix.Kind, OutParseError) ||
                FixIds.Contains(CandidateFix.Id) ||
                !(CandidateFix.Kind == TEXT("openFirstDiagnostic") ||
                  CandidateFix.Kind == TEXT("refreshWorkspace")))
            {
                if (OutParseError.IsEmpty())
                {
                    OutParseError = TEXT("The Workbench error response contains an invalid quick fix.");
                }
                return false;
            }
            FixIds.Add(CandidateFix.Id);
            Candidate.QuickFixes.Add(MoveTemp(CandidateFix));
        }
    }

    const TSharedPtr<FJsonObject>* Rollback = nullptr;
    if (Root->TryGetObjectField(TEXT("rollback"), Rollback) && Rollback != nullptr)
    {
        if (!UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                *Rollback, TEXT("state"), Candidate.Rollback.State, OutParseError) ||
            !(Candidate.Rollback.State == TEXT("unchanged") ||
              Candidate.Rollback.State == TEXT("restored") ||
              Candidate.Rollback.State == TEXT("failed")) ||
            !UE::UnifiedScript::AuthoringWorkbench::Private::ReadPositiveInteger(
                *Rollback, TEXT("revision"), Candidate.Rollback.Revision, OutParseError) ||
            !UE::UnifiedScript::AuthoringWorkbench::Private::ReadPositiveInteger(
                *Rollback, TEXT("generation"), Candidate.Rollback.Generation, OutParseError) ||
            !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                *Rollback, TEXT("message"), Candidate.Rollback.Message, OutParseError))
        {
            if (OutParseError.IsEmpty())
            {
                OutParseError = TEXT("The Workbench error response contains invalid rollback metadata.");
            }
            return false;
        }
    }

    OutFailure = MoveTemp(Candidate);
    OutParseError.Reset();
    return true;
}

FString FUnifiedScriptAuthoringWorkbench::ParseErrorResponse(
    const FString& Json,
    int32 StatusCode)
{
    FUnifiedScriptAuthoringErrorResult Failure;
    FString ParseError;
    if (ParseErrorResponse(Json, StatusCode, Failure, ParseError))
    {
        return Failure.ToDisplayString();
    }
    return FString::Printf(TEXT("Workbench request failed with HTTP %d."), StatusCode);
}
