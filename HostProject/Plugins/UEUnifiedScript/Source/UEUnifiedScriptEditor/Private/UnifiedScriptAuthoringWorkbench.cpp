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
constexpr TCHAR WorkspaceSchema[] = TEXT("unifiedscript.authoring-workspace/v0.3");

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

FString FUnifiedScriptAuthoringWorkbench::ParseErrorResponse(
    const FString& Json,
    int32 StatusCode)
{
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid())
    {
        const TSharedPtr<FJsonObject>* Error = nullptr;
        if (Root->TryGetObjectField(TEXT("error"), Error) && Error->IsValid())
        {
            FString Code;
            FString Message;
            (*Error)->TryGetStringField(TEXT("code"), Code);
            (*Error)->TryGetStringField(TEXT("message"), Message);
            if (!Code.IsEmpty() || !Message.IsEmpty())
            {
                return FString::Printf(TEXT("Workbench %d %s: %s"), StatusCode, *Code, *Message);
            }
        }
    }
    return FString::Printf(TEXT("Workbench request failed with HTTP %d."), StatusCode);
}
