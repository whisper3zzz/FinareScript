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
constexpr TCHAR WorkspaceSchema[] = TEXT("unifiedscript.authoring-workspace/v0.5");
constexpr TCHAR ProfileReportSchema[] = TEXT("unifiedscript.visual-csharp-profile-report/v0.1");
constexpr TCHAR VisualProfileSchema[] = TEXT("unifiedscript.visual-csharp-profile/v0.3");
constexpr TCHAR PreviewSchema[] = TEXT("unifiedscript.authoring-preview/v0.2");
constexpr TCHAR SemanticDiffSchema[] = TEXT("unifiedscript.semantic-graph-diff/v0.1");
constexpr TCHAR GraphSchema[] = TEXT("unifiedscript.graph/v0.13");

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

FString FUnifiedScriptReloadTransactionResult::ToDisplayString() const
{
    TArray<FString> PhaseStates;
    PhaseStates.Reserve(Phases.Num());
    for (const FUnifiedScriptReloadPhaseResult& Phase : Phases)
    {
        PhaseStates.Add(FString::Printf(TEXT("%s:%s"), *Phase.Name, *Phase.State));
    }
    return FString::Printf(
        TEXT("Reload %s · %s · %s"),
        *TransactionId,
        *State,
        *FString::Join(PhaseStates, TEXT(" -> ")));
}

bool FUnifiedScriptAuthoringWorkbench::ParseReloadTransaction(
    const FString& Json,
    FUnifiedScriptReloadTransactionResult& OutTransaction,
    FString& OutError)
{
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    FString Schema;
    FUnifiedScriptReloadTransactionResult Candidate;
    const TArray<TSharedPtr<FJsonValue>>* Phases = nullptr;
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid() ||
        !Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
        Schema != TEXT("unifiedscript.reload-transaction/v0.1") ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
            Root, TEXT("transactionId"), Candidate.TransactionId, OutError) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
            Root, TEXT("source"), Candidate.Source, OutError) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
            Root, TEXT("state"), Candidate.State, OutError) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadNonNegativeInteger(
            Root, TEXT("fromGeneration"), Candidate.FromGeneration, OutError) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadNonNegativeInteger(
            Root, TEXT("candidateGeneration"), Candidate.CandidateGeneration, OutError) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadNonNegativeInteger(
            Root, TEXT("activeGeneration"), Candidate.ActiveGeneration, OutError) ||
        !Root->TryGetArrayField(TEXT("phases"), Phases) || Phases == nullptr ||
        Phases->IsEmpty())
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("The reload transaction is missing its schema, identity, generations, or phases.");
        }
        return false;
    }

    for (const TSharedPtr<FJsonValue>& Value : *Phases)
    {
        const TSharedPtr<FJsonObject> PhaseObject =
            Value.IsValid() ? Value->AsObject() : nullptr;
        FUnifiedScriptReloadPhaseResult Phase;
        if (!UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                PhaseObject, TEXT("name"), Phase.Name, OutError) ||
            !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                PhaseObject, TEXT("state"), Phase.State, OutError) ||
            !UE::UnifiedScript::AuthoringWorkbench::Private::ReadNonNegativeInteger(
                PhaseObject,
                TEXT("durationMicroseconds"),
                Phase.DurationMicroseconds,
                OutError) ||
            !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                PhaseObject, TEXT("detail"), Phase.Detail, OutError))
        {
            if (OutError.IsEmpty())
            {
                OutError = TEXT("The reload transaction contains an invalid phase.");
            }
            return false;
        }
        Candidate.Phases.Add(MoveTemp(Phase));
    }
    Root->TryGetStringField(TEXT("failure"), Candidate.Failure);
    Candidate.Json = Json;
    OutTransaction = MoveTemp(Candidate);
    OutError.Reset();
    return true;
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

    const TSharedPtr<FJsonObject>* ProfileReport = nullptr;
    const TSharedPtr<FJsonObject>* Profile = nullptr;
    const TSharedPtr<FJsonObject>* OpaqueBoundary = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* ProfileMethods = nullptr;
    FString ProfileReportSchema;
    bool bUnmarkedMethodsAreSourceOnly = false;
    bool bRequiresGraphSafeSignature = false;
    bool bAllowsRefParameters = true;
    if (!Root->TryGetObjectField(TEXT("profile"), ProfileReport) ||
        !ProfileReport->IsValid() ||
        !(*ProfileReport)->TryGetStringField(TEXT("schemaVersion"), ProfileReportSchema) ||
        ProfileReportSchema != UE::UnifiedScript::AuthoringWorkbench::Private::ProfileReportSchema ||
        !(*ProfileReport)->TryGetObjectField(TEXT("profile"), Profile) || !Profile->IsValid() ||
        !(*Profile)->TryGetStringField(
            TEXT("schemaVersion"), Candidate.Profile.SchemaVersion) ||
        Candidate.Profile.SchemaVersion !=
            UE::UnifiedScript::AuthoringWorkbench::Private::VisualProfileSchema ||
        !(*Profile)->TryGetStringField(TEXT("displayName"), Candidate.Profile.DisplayName) ||
        Candidate.Profile.DisplayName.IsEmpty() ||
        !(*Profile)->TryGetStringField(TEXT("languageVersion"), Candidate.Profile.LanguageVersion) ||
        Candidate.Profile.LanguageVersion.IsEmpty() ||
        !(*Profile)->TryGetObjectField(TEXT("opaqueCodeBoundary"), OpaqueBoundary) ||
        !OpaqueBoundary->IsValid() ||
        !(*OpaqueBoundary)->TryGetBoolField(
            TEXT("unmarkedMethodsAreSourceOnly"), bUnmarkedMethodsAreSourceOnly) ||
        !(*OpaqueBoundary)->TryGetBoolField(
            TEXT("callsProjectAsSingleNodes"), Candidate.Profile.bOpaqueCallsProjectAsSingleNodes) ||
        !(*OpaqueBoundary)->TryGetBoolField(
            TEXT("bodiesAreGraphEditable"), Candidate.Profile.bOpaqueBodiesGraphEditable) ||
        !(*OpaqueBoundary)->TryGetBoolField(
            TEXT("requiresGraphSafeSignature"), bRequiresGraphSafeSignature) ||
        !(*OpaqueBoundary)->TryGetBoolField(TEXT("allowsRefParameters"), bAllowsRefParameters) ||
        !bUnmarkedMethodsAreSourceOnly ||
        !Candidate.Profile.bOpaqueCallsProjectAsSingleNodes ||
        Candidate.Profile.bOpaqueBodiesGraphEditable ||
        !bRequiresGraphSafeSignature || bAllowsRefParameters ||
        !(*ProfileReport)->TryGetArrayField(TEXT("methods"), ProfileMethods) ||
        ProfileMethods == nullptr)
    {
        OutError = TEXT("The Workbench response has no valid Visual C# profile contract.");
        return false;
    }

    for (const TSharedPtr<FJsonValue>& MethodValue : *ProfileMethods)
    {
        const TSharedPtr<FJsonObject> Method = MethodValue.IsValid()
            ? MethodValue->AsObject()
            : nullptr;
        FString Representation;
        FString AuthoringBoundary;
        bool bGraphEditable = false;
        if (!Method.IsValid() ||
            !Method->TryGetStringField(TEXT("representation"), Representation) ||
            !Method->TryGetStringField(TEXT("authoringBoundary"), AuthoringBoundary) ||
            !Method->TryGetBoolField(TEXT("graphEditable"), bGraphEditable))
        {
            OutError = TEXT("The Workbench Visual C# profile has an invalid method entry.");
            return false;
        }

        if (Representation == TEXT("visualGraph") && AuthoringBoundary == TEXT("visualGraph"))
        {
            ++Candidate.Profile.VisualMethodCount;
            Candidate.Profile.InvalidVisualMethodCount += bGraphEditable ? 0 : 1;
        }
        else if (Representation == TEXT("opaqueCode") &&
                 AuthoringBoundary == TEXT("sourceOnly") && !bGraphEditable)
        {
            ++Candidate.Profile.OpaqueMethodCount;
        }
        else
        {
            OutError = TEXT("The Workbench Visual C# profile has inconsistent method boundaries.");
            return false;
        }
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

    const TSharedPtr<FJsonObject>* ReloadTransaction = nullptr;
    if (Root->TryGetObjectField(TEXT("reloadTransaction"), ReloadTransaction) &&
        ReloadTransaction != nullptr)
    {
        FString ReloadSchema;
        FString ReloadTransactionId;
        const TArray<TSharedPtr<FJsonValue>>* ReloadPhases = nullptr;
        if (!(*ReloadTransaction)->TryGetStringField(
                TEXT("schemaVersion"), ReloadSchema) ||
            ReloadSchema != TEXT("unifiedscript.reload-transaction/v0.1") ||
            !(*ReloadTransaction)->TryGetStringField(
                TEXT("transactionId"), ReloadTransactionId) ||
            ReloadTransactionId.IsEmpty() ||
            !(*ReloadTransaction)->TryGetArrayField(TEXT("phases"), ReloadPhases) ||
            ReloadPhases == nullptr)
        {
            OutError = TEXT("The Workbench response has an invalid reload transaction.");
            return false;
        }
        const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> ReloadWriter =
            TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(
                &Candidate.ReloadTransactionJson);
        if (!FJsonSerializer::Serialize(ReloadTransaction->ToSharedRef(), ReloadWriter))
        {
            OutError = TEXT("The Workbench reload transaction could not be decoded.");
            return false;
        }
        if (!ParseReloadTransaction(
                Candidate.ReloadTransactionJson,
                Candidate.ReloadTransaction,
                OutError))
        {
            return false;
        }
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

    const TSharedPtr<FJsonObject>* SemanticDiff = nullptr;
    const TSharedPtr<FJsonObject>* SemanticSummary = nullptr;
    FString SemanticSchema;
    FString GraphSchema;
    int64 AddedFunctions = 0;
    int64 RemovedFunctions = 0;
    int64 ModifiedFunctions = 0;
    int64 ChangeCount = 0;
    if (!Root->TryGetObjectField(TEXT("semanticDiff"), SemanticDiff) ||
        SemanticDiff == nullptr ||
        !(*SemanticDiff)->TryGetStringField(TEXT("schemaVersion"), SemanticSchema) ||
        SemanticSchema != UE::UnifiedScript::AuthoringWorkbench::Private::SemanticDiffSchema ||
        !(*SemanticDiff)->TryGetStringField(TEXT("graphSchemaVersion"), GraphSchema) ||
        GraphSchema != UE::UnifiedScript::AuthoringWorkbench::Private::GraphSchema ||
        !(*SemanticDiff)->TryGetObjectField(TEXT("summary"), SemanticSummary) ||
        SemanticSummary == nullptr ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadNonNegativeInteger(
            *SemanticSummary, TEXT("addedFunctions"), AddedFunctions, OutError) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadNonNegativeInteger(
            *SemanticSummary, TEXT("removedFunctions"), RemovedFunctions, OutError) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadNonNegativeInteger(
            *SemanticSummary, TEXT("modifiedFunctions"), ModifiedFunctions, OutError) ||
        !UE::UnifiedScript::AuthoringWorkbench::Private::ReadNonNegativeInteger(
            *SemanticSummary, TEXT("changeCount"), ChangeCount, OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("The authoring preview requires a valid semantic graph diff.");
        }
        return false;
    }
    Candidate.SemanticSummary.AddedFunctions = AddedFunctions;
    Candidate.SemanticSummary.RemovedFunctions = RemovedFunctions;
    Candidate.SemanticSummary.ModifiedFunctions = ModifiedFunctions;
    Candidate.SemanticSummary.ChangeCount = ChangeCount;

    const TArray<TSharedPtr<FJsonValue>>* Functions = nullptr;
    if (!(*SemanticDiff)->TryGetArrayField(TEXT("functions"), Functions) || Functions == nullptr ||
        AddedFunctions + RemovedFunctions + ModifiedFunctions != Functions->Num())
    {
        OutError = TEXT("The semantic graph diff function summary is inconsistent.");
        return false;
    }
    for (const TSharedPtr<FJsonValue>& FunctionValue : *Functions)
    {
        const TSharedPtr<FJsonObject> Function =
            FunctionValue.IsValid() ? FunctionValue->AsObject() : nullptr;
        FString FunctionId;
        FString QualifiedName;
        const TArray<TSharedPtr<FJsonValue>>* Changes = nullptr;
        if (!UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                Function, TEXT("functionId"), FunctionId, OutError) ||
            !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                Function, TEXT("qualifiedName"), QualifiedName, OutError) ||
            !Function->TryGetArrayField(TEXT("changes"), Changes) || Changes == nullptr ||
            Changes->IsEmpty())
        {
            if (OutError.IsEmpty())
            {
                OutError = TEXT("The semantic graph diff contains an invalid function change.");
            }
            return false;
        }
        for (const TSharedPtr<FJsonValue>& ChangeValue : *Changes)
        {
            const TSharedPtr<FJsonObject> Change =
                ChangeValue.IsValid() ? ChangeValue->AsObject() : nullptr;
            FUnifiedScriptAuthoringSemanticChange CandidateChange;
            CandidateChange.FunctionName = QualifiedName;
            if (!UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                    Change, TEXT("entityKind"), CandidateChange.EntityKind, OutError) ||
                !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                    Change, TEXT("category"), CandidateChange.Category, OutError) ||
                !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                    Change, TEXT("facet"), CandidateChange.Facet, OutError) ||
                !UE::UnifiedScript::AuthoringWorkbench::Private::ReadRequiredString(
                    Change, TEXT("summary"), CandidateChange.Summary, OutError))
            {
                return false;
            }
            Candidate.SemanticChanges.Add(MoveTemp(CandidateChange));
        }
    }
    if (Candidate.SemanticChanges.Num() != ChangeCount)
    {
        OutError = TEXT("The semantic graph diff change count is inconsistent.");
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

    const TSharedPtr<FJsonObject>* ReloadTransaction = nullptr;
    if (Root->TryGetObjectField(TEXT("reloadTransaction"), ReloadTransaction) &&
        ReloadTransaction != nullptr)
    {
        FString ReloadJson;
        const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
            TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(
                &ReloadJson);
        if (!FJsonSerializer::Serialize(ReloadTransaction->ToSharedRef(), Writer) ||
            !ParseReloadTransaction(
                ReloadJson,
                Candidate.ReloadTransaction,
                OutParseError))
        {
            if (OutParseError.IsEmpty())
            {
                OutParseError = TEXT("The Workbench error response contains an invalid reload transaction.");
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
