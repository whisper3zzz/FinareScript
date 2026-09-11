#include "FinareAuthoringWorkbench.h"

#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareBlueprintClassMaterializer.h"
#include "FinareClassOperationJournal.h"

#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "Misc/Base64.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"

#include <openssl/sha.h>

namespace UE::Finare::AuthoringWorkbench::Private
{
	constexpr TCHAR WorkspaceSchema[] = TEXT("unifiedscript.authoring-workspace/v0.5");
	constexpr TCHAR ProfileReportSchema[] = TEXT("unifiedscript.visual-csharp-profile-report/v0.1");
	constexpr TCHAR VisualProfileSchema[] = TEXT("unifiedscript.visual-csharp-profile/v0.3");
	constexpr TCHAR PreviewSchema[] = TEXT("unifiedscript.authoring-preview/v0.3");
	constexpr TCHAR RoundTripSchema[] = TEXT("unifiedscript.csharp-graph-round-trip/v0.1");
	constexpr TCHAR AiProposalSchema[] = TEXT("unifiedscript.ai-semantic-patch-proposal/v0.1");
	constexpr TCHAR AiPreviewSchema[] = TEXT("unifiedscript.ai-semantic-proposal-preview/v0.1");
	constexpr TCHAR AiCommitSchema[] = TEXT("unifiedscript.ai-semantic-proposal-commit/v0.1");
	constexpr TCHAR SemanticDiffSchema[] = TEXT("unifiedscript.semantic-graph-diff/v0.1");
	constexpr TCHAR GraphSchema[] = TEXT("unifiedscript.graph/v0.13");
	constexpr TCHAR SourceTextPatchSchema[] = TEXT("unifiedscript.csharp-source-text-patch/v0.1");
	constexpr int32 MaximumSourceCharacters = 512 * 1024;

	FString ComputeSha256Digest(const TArray<uint8>& Bytes)
	{
		uint8 Digest[SHA256_DIGEST_LENGTH]{};
		// OpenSSL accepts a zero-length input, but keep the pointer non-null for
		// platforms whose wrapper still validates the input address.
		const uint8 EmptyByte = 0;
		const uint8* Data = Bytes.IsEmpty() ? &EmptyByte : Bytes.GetData();
		if (SHA256(
			reinterpret_cast<const unsigned char*>(Data),
			static_cast<size_t>(Bytes.Num()),
			Digest) == nullptr)
		{
			return FString();
		}
		return TEXT("sha256:") + BytesToHex(Digest, SHA256_DIGEST_LENGTH).ToLower();
	}

	bool IsValidNativeTransactionId(const FString& Value)
	{
		if (Value.IsEmpty())
		{
			return false;
		}
		for (const TCHAR Character : Value)
		{
			if (!(FChar::IsAlnum(Character) || Character == TEXT('-') || Character == TEXT('_')))
			{
				return false;
			}
		}
		return true;
	}

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

	bool ReadOptionalClassBinding(
		const TSharedPtr<FJsonObject>& Object,
		const TCHAR* Field,
		TOptional<FFinareClassBindingDeclaration>& OutBinding,
		FString& OutError)
	{
		OutBinding.Reset();
		if (!Object.IsValid())
		{
			OutError = FString::Printf(TEXT("Native publication binding field '%s' has no object."), Field);
			return false;
		}
		const TSharedPtr<FJsonValue>* Value = Object->Values.Find(Field);
		if (Value == nullptr || !Value->IsValid() || (*Value)->IsNull())
		{
			return true;
		}
		const TSharedPtr<FJsonObject> BindingObject = (*Value)->AsObject();
		if (!BindingObject.IsValid())
		{
			OutError = FString::Printf(TEXT("Native publication binding field '%s' is not an object or null."), Field);
			return false;
		}
		const TSharedRef<FJsonObject> Asset = MakeShared<FJsonObject>();
		Asset->SetObjectField(TEXT("classBinding"), BindingObject.ToSharedRef());
		if (!FFinareClassBindingDeclaration::ReadJson(Asset, OutBinding, OutError) || !OutBinding.IsSet())
		{
			if (OutError.IsEmpty())
			{
				OutError = FString::Printf(TEXT("Native publication binding field '%s' is invalid."), Field);
			}
			return false;
		}
		return true;
	}

	bool SerializeJsonObject(
		const TSharedPtr<FJsonObject>& Object,
		FString& OutJson)
	{
		if (!Object.IsValid())
		{
			return false;
		}
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutJson);
		return FJsonSerializer::Serialize(Object.ToSharedRef(), Writer);
	}

	bool ReadSpan(
		const TSharedPtr<FJsonObject>& Object,
		FFinareAuthoringSpan& OutSpan,
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
		TArray<FFinareAuthoringDiagnostic>& OutDiagnostics,
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
			FFinareAuthoringDiagnostic Diagnostic;
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
			if (!Object->TryGetStringField(TEXT("graphNodeId"), Diagnostic.GraphNodeId))
			{
				Object->TryGetStringField(TEXT("nodeId"), Diagnostic.GraphNodeId);
			}
			OutDiagnostics.Add(MoveTemp(Diagnostic));
		}
		return true;
	}
}

FString FFinareAuthoringDiagnostic::ToDisplayString() const
{
	return FString::Printf(
		TEXT("%s %s: %s (%s)"),
		*Severity.ToUpper(),
		*Code,
		*Message,
		*Span.ToDisplayString());
}

FString FFinareAuthoringErrorResult::ToDisplayString() const
{
	return FString::Printf(TEXT("Workbench %d %s: %s"), StatusCode, *Code, *Message);
}

FString FFinareReloadTransactionResult::ToDisplayString() const
{
	TArray<FString> PhaseStates;
	PhaseStates.Reserve(Phases.Num());
	for (const FFinareReloadPhaseResult& Phase : Phases)
	{
		PhaseStates.Add(FString::Printf(TEXT("%s:%s"), *Phase.Name, *Phase.State));
	}
	FString Result = FString::Printf(
		TEXT("Reload %s · %s · %s"),
		*TransactionId,
		*State,
		*FString::Join(PhaseStates, TEXT(" -> ")));
	if (RequiredAction == TEXT("restartEditor"))
	{
		Result += TEXT(" · Restart Editor required");
	}
	if (!Failure.IsEmpty())
	{
		Result += TEXT("\n");
		Result += Failure;
	}
	return Result;
}

bool FFinareAuthoringWorkbench::ParseReloadTransaction(
	const FString& Json,
	FFinareReloadTransactionResult& OutTransaction,
	FString& OutError)
{
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	FString Schema;
	FFinareReloadTransactionResult Candidate;
	const TArray<TSharedPtr<FJsonValue>>* Phases = nullptr;
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid() ||
		!Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
		Schema != TEXT("unifiedscript.reload-transaction/v0.1") ||
		!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
			Root, TEXT("transactionId"), Candidate.TransactionId, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
			Root, TEXT("source"), Candidate.Source, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
			Root, TEXT("state"), Candidate.State, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			Root, TEXT("fromGeneration"), Candidate.FromGeneration, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			Root, TEXT("candidateGeneration"), Candidate.CandidateGeneration, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
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
		FFinareReloadPhaseResult Phase;
		if (!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
				PhaseObject, TEXT("name"), Phase.Name, OutError) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
				PhaseObject, TEXT("state"), Phase.State, OutError) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
				PhaseObject,
				TEXT("durationMicroseconds"),
				Phase.DurationMicroseconds,
				OutError) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
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
	Root->TryGetStringField(TEXT("requiredAction"), Candidate.RequiredAction);
	Root->TryGetStringField(TEXT("failure"), Candidate.Failure);
	Candidate.Json = Json;
	OutTransaction = MoveTemp(Candidate);
	OutError.Reset();
	return true;
}

FString FFinareAuthoringWorkbench::NormalizeServerUrl(const FString& ServerUrl)
{
	FString Result = ServerUrl;
	Result.TrimStartAndEndInline();
	while (Result.EndsWith(TEXT("/")))
	{
		Result.LeftChopInline(1);
	}
	return Result;
}

FString FFinareAuthoringWorkbench::BuildSemanticPatchUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/semantic-patch");
}

FString FFinareAuthoringWorkbench::BuildDeclarationPatchUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/declaration-patch");
}

FString FFinareAuthoringWorkbench::BuildSemanticPatchPlanUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/semantic-patch-plan");
}

FString FFinareAuthoringWorkbench::BuildAiSemanticProposalUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/ai-semantic-proposal");
}

FString FFinareAuthoringWorkbench::BuildSourceTextUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/source-text");
}

FString FFinareAuthoringWorkbench::BuildPreviewUrl(const FString& TransactionUrl)
{
	const FString Normalized = NormalizeServerUrl(TransactionUrl);
	static const TMap<FString, FString> PreviewPaths = {
		{TEXT("/api/authoring/declaration-patch"), TEXT("/api/authoring/preview/declaration-patch")},
		{TEXT("/api/authoring/semantic-patch"), TEXT("/api/authoring/preview/semantic-patch")},
		{TEXT("/api/authoring/semantic-patch-plan"), TEXT("/api/authoring/preview/semantic-patch-plan")},
		{TEXT("/api/authoring/source-text"), TEXT("/api/authoring/preview/source-text")},
		{TEXT("/api/authoring/ai-semantic-proposal"), TEXT("/api/authoring/preview/ai-semantic-proposal")},
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

FString FFinareAuthoringWorkbench::BuildWorkspaceUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/workspace");
}

FString FFinareAuthoringWorkbench::BuildRefreshUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty() ? FString() : Normalized + TEXT("/api/authoring/refresh");
}

FString FFinareAuthoringWorkbench::BuildUndoUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/undo");
}

FString FFinareAuthoringWorkbench::BuildRedoUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/redo");
}

FString FFinareAuthoringWorkbench::BuildGroupBeginUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/group/begin");
}

FString FFinareAuthoringWorkbench::BuildGroupCommitUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/group/commit");
}

FString FFinareAuthoringWorkbench::BuildGroupCancelUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/group/cancel");
}

FString FFinareAuthoringWorkbench::BuildNativePrepareUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/native/prepare");
}

FString FFinareAuthoringWorkbench::BuildNativeFinishUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/native/finish");
}

FString FFinareAuthoringWorkbench::BuildNativeCancelUrl(const FString& ServerUrl)
{
	const FString Normalized = NormalizeServerUrl(ServerUrl);
	return Normalized.IsEmpty()
		       ? FString()
		       : Normalized + TEXT("/api/authoring/native/cancel");
}

bool FFinareAuthoringWorkbench::BuildSourceTextPatch(
	const FString& SourcePath,
	const FString& BaselineSourceText,
	const FString& SourceText,
	FString& OutJson,
	FString& OutError)
{
	OutJson.Reset();
	OutError.Reset();
	if (SourcePath.IsEmpty())
	{
		OutError = TEXT("A canonical Workbench source path is required before editing C#.");
		return false;
	}
	if (SourceText.Len() >
		UE::Finare::AuthoringWorkbench::Private::MaximumSourceCharacters)
	{
		OutError = FString::Printf(
			TEXT("C# source cannot exceed %d characters in one authoring transaction."),
			UE::Finare::AuthoringWorkbench::Private::MaximumSourceCharacters);
		return false;
	}

	const FTCHARToUTF8 BaselineUtf8(*BaselineSourceText);
	uint8 Digest[SHA256_DIGEST_LENGTH]{};
	if (SHA256(
		reinterpret_cast<const unsigned char*>(BaselineUtf8.Get()),
		static_cast<size_t>(BaselineUtf8.Length()),
		Digest) == nullptr)
	{
		OutError = TEXT("Cannot compute the canonical C# baseline digest.");
		return false;
	}

	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(
		TEXT("schemaVersion"),
		UE::Finare::AuthoringWorkbench::Private::SourceTextPatchSchema);
	Root->SetStringField(TEXT("sourcePath"), SourcePath.Replace(TEXT("\\"), TEXT("/")));
	Root->SetStringField(
		TEXT("baselineContentDigest"),
		TEXT("sha256:") + BytesToHex(Digest, SHA256_DIGEST_LENGTH).ToLower());
	Root->SetStringField(TEXT("sourceText"), SourceText);
	const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutJson);
	if (!FJsonSerializer::Serialize(Root, Writer))
	{
		OutError = TEXT("Cannot serialize the C# source-text authoring transaction.");
		return false;
	}
	OutError.Reset();
	return true;
}

bool FFinareAuthoringWorkbench::ParseWorkspaceResponse(
	const FString& Json,
	FFinareAuthoringWorkspaceResult& OutWorkspace,
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
	if (Root->TryGetStringField(TEXT("schemaVersion"), Schema) &&
		Schema == UE::Finare::AuthoringWorkbench::Private::AiCommitSchema)
	{
		FString ProposalId;
		const TSharedPtr<FJsonObject>* Workspace = nullptr;
		if (!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
				Root, TEXT("proposalId"), ProposalId, OutError) ||
			!Root->TryGetObjectField(TEXT("workspace"), Workspace) ||
			Workspace == nullptr || !Workspace->IsValid())
		{
			if (OutError.IsEmpty())
			{
				OutError = TEXT("The AI proposal commit has invalid identity or workspace data.");
			}
			return false;
		}
		Root = *Workspace;
		Schema.Reset();
	}
	if (!Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
		Schema != UE::Finare::AuthoringWorkbench::Private::WorkspaceSchema)
	{
		OutError = FString::Printf(TEXT("Unsupported authoring workspace schema '%s'."), *Schema);
		return false;
	}

	FFinareAuthoringWorkspaceResult Candidate;
	if (!UE::Finare::AuthoringWorkbench::Private::ReadPositiveInteger(
		Root, TEXT("revision"), Candidate.Revision, OutError))
	{
		return false;
	}
	if (!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
			Root, TEXT("sourcePath"), Candidate.SourcePath, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
			Root, TEXT("sourceText"), Candidate.SourceText, OutError))
	{
		return false;
	}

	const TSharedPtr<FJsonObject>* ScriptAssets = nullptr;
	if (Root->TryGetObjectField(TEXT("scriptAssets"), ScriptAssets) &&
		ScriptAssets != nullptr && ScriptAssets->IsValid())
	{
		FString ScriptAssetsSchema;
		const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
		if (!(*ScriptAssets)->TryGetStringField(TEXT("schemaVersion"), ScriptAssetsSchema) ||
			ScriptAssetsSchema.IsEmpty() ||
			!(*ScriptAssets)->TryGetArrayField(TEXT("assets"), Assets) ||
			Assets == nullptr ||
			!UE::Finare::AuthoringWorkbench::Private::SerializeJsonObject(
				*ScriptAssets,
				Candidate.ScriptAssetsJson))
		{
			OutError = TEXT("The Workbench response has an invalid ScriptAssets candidate document.");
			return false;
		}
	}

	const TSharedPtr<FJsonObject>* History = nullptr;
	if (!Root->TryGetObjectField(TEXT("history"), History) || !History->IsValid() ||
		!(*History)->TryGetBoolField(TEXT("canUndo"), Candidate.History.bCanUndo) ||
		!(*History)->TryGetBoolField(TEXT("canRedo"), Candidate.History.bCanRedo) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*History, TEXT("undoDepth"), Candidate.History.UndoDepth, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
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
			!UE::Finare::AuthoringWorkbench::Private::ReadPositiveInteger(
				*Group, TEXT("baselineRevision"), Candidate.Group.BaselineRevision, OutError) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
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
		ProfileReportSchema != UE::Finare::AuthoringWorkbench::Private::ProfileReportSchema ||
		!(*ProfileReport)->TryGetObjectField(TEXT("profile"), Profile) || !Profile->IsValid() ||
		!(*Profile)->TryGetStringField(
			TEXT("schemaVersion"), Candidate.Profile.SchemaVersion) ||
		Candidate.Profile.SchemaVersion !=
		UE::Finare::AuthoringWorkbench::Private::VisualProfileSchema ||
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
		!UE::Finare::AuthoringWorkbench::Private::ReadPositiveInteger(
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
		!FFinareAuthoringSurfaceReader::ParseJson(
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

bool FFinareAuthoringWorkbench::ParsePreviewResponse(
	const FString& Json,
	FFinareAuthoringPreviewResult& OutPreview,
	FString& OutError)
{
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		OutError = TEXT("The Workbench returned invalid authoring preview JSON.");
		return false;
	}

	FFinareAuthoringPreviewResult Candidate;
	FString Schema;
	if (Root->TryGetStringField(TEXT("schemaVersion"), Schema) &&
		Schema == UE::Finare::AuthoringWorkbench::Private::AiPreviewSchema)
	{
		const TSharedPtr<FJsonObject>* Proposal = nullptr;
		const TSharedPtr<FJsonObject>* Preview = nullptr;
		FString ProposalSchema;
		if (!Root->TryGetObjectField(TEXT("proposal"), Proposal) || Proposal == nullptr ||
			!Proposal->IsValid() ||
			!(*Proposal)->TryGetStringField(TEXT("schemaVersion"), ProposalSchema) ||
			ProposalSchema != UE::Finare::AuthoringWorkbench::Private::AiProposalSchema ||
			!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
				*Proposal, TEXT("proposalId"), Candidate.ProposalId, OutError) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
				*Proposal, TEXT("title"), Candidate.ProposalTitle, OutError) ||
			!Root->TryGetObjectField(TEXT("preview"), Preview) || Preview == nullptr ||
			!Preview->IsValid())
		{
			if (OutError.IsEmpty())
			{
				OutError = TEXT("The AI proposal preview has invalid identity or preview data.");
			}
			return false;
		}
		Root = *Preview;
		Schema.Reset();
	}
	if (!Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
		Schema != UE::Finare::AuthoringWorkbench::Private::PreviewSchema ||
		!UE::Finare::AuthoringWorkbench::Private::ReadPositiveInteger(
			Root, TEXT("baselineRevision"), Candidate.BaselineRevision, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadPositiveInteger(
			Root, TEXT("candidateRevision"), Candidate.CandidateRevision, OutError) ||
		Candidate.CandidateRevision <= Candidate.BaselineRevision ||
		!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
			Root, TEXT("transactionId"), Candidate.TransactionId, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
			Root, TEXT("description"), Candidate.Description, OutError) ||
		!Root->TryGetBoolField(TEXT("publishesRuntime"), Candidate.bPublishesRuntime) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
			Root, TEXT("previewToken"), Candidate.PreviewToken, OutError))
	{
		if (OutError.IsEmpty())
		{
			OutError = Schema == UE::Finare::AuthoringWorkbench::Private::PreviewSchema
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

	const TSharedPtr<FJsonObject>* ScriptAssets = nullptr;
	if (Root->TryGetObjectField(TEXT("scriptAssets"), ScriptAssets) &&
		ScriptAssets != nullptr && ScriptAssets->IsValid())
	{
		FString ScriptAssetsSchema;
		const TArray<TSharedPtr<FJsonValue>>* Assets = nullptr;
		if (!(*ScriptAssets)->TryGetStringField(TEXT("schemaVersion"), ScriptAssetsSchema) ||
			ScriptAssetsSchema.IsEmpty() ||
			!(*ScriptAssets)->TryGetArrayField(TEXT("assets"), Assets) ||
			Assets == nullptr ||
			!UE::Finare::AuthoringWorkbench::Private::SerializeJsonObject(
				*ScriptAssets,
				Candidate.CandidateScriptAssetsJson))
		{
			OutError = TEXT("The authoring preview has an invalid ScriptAssets candidate document.");
			return false;
		}
	}
	const TArray<TSharedPtr<FJsonValue>>* NativeBindings = nullptr;
	if (Root->TryGetArrayField(TEXT("nativeBindingChanges"), NativeBindings) &&
		NativeBindings != nullptr)
	{
		for (const TSharedPtr<FJsonValue>& Value : *NativeBindings)
		{
			const TSharedPtr<FJsonObject> Change = Value.IsValid() ? Value->AsObject() : nullptr;
			FFinareNativeBindingChange Binding;
			if (!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
					Change, TEXT("assetPath"), Binding.AssetPath, OutError) ||
				!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
					Change, TEXT("assetId"), Binding.AssetId, OutError) ||
				!UE::Finare::AuthoringWorkbench::Private::ReadOptionalClassBinding(
					Change, TEXT("before"), Binding.Before, OutError) ||
				!UE::Finare::AuthoringWorkbench::Private::ReadOptionalClassBinding(
					Change, TEXT("after"), Binding.After, OutError) ||
				(!Binding.Before.IsSet() && !Binding.After.IsSet()))
			{
				if (OutError.IsEmpty())
				{
					OutError = TEXT("The authoring preview has an empty native binding change.");
				}
				return false;
			}
			Candidate.NativeBindingChanges.Add(MoveTemp(Binding));
		}
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
		SemanticSchema != UE::Finare::AuthoringWorkbench::Private::SemanticDiffSchema ||
		!(*SemanticDiff)->TryGetStringField(TEXT("graphSchemaVersion"), GraphSchema) ||
		GraphSchema != UE::Finare::AuthoringWorkbench::Private::GraphSchema ||
		!(*SemanticDiff)->TryGetObjectField(TEXT("summary"), SemanticSummary) ||
		SemanticSummary == nullptr ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*SemanticSummary, TEXT("addedFunctions"), AddedFunctions, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*SemanticSummary, TEXT("removedFunctions"), RemovedFunctions, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*SemanticSummary, TEXT("modifiedFunctions"), ModifiedFunctions, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
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
		if (!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
				Function, TEXT("functionId"), FunctionId, OutError) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
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
			FFinareAuthoringSemanticChange CandidateChange;
			CandidateChange.FunctionName = QualifiedName;
			if (!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
					Change, TEXT("entityKind"), CandidateChange.EntityKind, OutError) ||
				!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
					Change, TEXT("category"), CandidateChange.Category, OutError) ||
				!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
					Change, TEXT("facet"), CandidateChange.Facet, OutError) ||
				!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
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

	const TSharedPtr<FJsonObject>* RoundTrip = nullptr;
	FString RoundTripSchema;
	if (!Root->TryGetObjectField(TEXT("roundTrip"), RoundTrip) ||
		RoundTrip == nullptr || !RoundTrip->IsValid() ||
		!(*RoundTrip)->TryGetStringField(TEXT("schemaVersion"), RoundTripSchema) ||
		RoundTripSchema != UE::Finare::AuthoringWorkbench::Private::RoundTripSchema ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*RoundTrip,
			TEXT("preservedSemanticIds"),
			Candidate.RoundTrip.PreservedSemanticIds,
			OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*RoundTrip,
			TEXT("introducedSemanticIds"),
			Candidate.RoundTrip.IntroducedSemanticIds,
			OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*RoundTrip,
			TEXT("retiredSemanticIds"),
			Candidate.RoundTrip.RetiredSemanticIds,
			OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*RoundTrip,
			TEXT("preservedSourceComments"),
			Candidate.RoundTrip.PreservedSourceComments,
			OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*RoundTrip,
			TEXT("addedSourceComments"),
			Candidate.RoundTrip.AddedSourceComments,
			OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*RoundTrip,
			TEXT("removedSourceComments"),
			Candidate.RoundTrip.RemovedSourceComments,
			OutError) ||
		!(*RoundTrip)->TryGetBoolField(
			TEXT("layoutChecked"),
			Candidate.RoundTrip.bLayoutChecked) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*RoundTrip,
			TEXT("preservedNodeLayouts"),
			Candidate.RoundTrip.PreservedNodeLayouts,
			OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*RoundTrip,
			TEXT("preservedGraphComments"),
			Candidate.RoundTrip.PreservedGraphComments,
			OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*RoundTrip,
			TEXT("preservedRoutingHints"),
			Candidate.RoundTrip.PreservedRoutingHints,
			OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadNonNegativeInteger(
			*RoundTrip,
			TEXT("newUnplacedNodes"),
			Candidate.RoundTrip.NewUnplacedNodes,
			OutError))
	{
		if (OutError.IsEmpty())
		{
			OutError = TEXT("The authoring preview requires valid C#/Graph round-trip evidence.");
		}
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
		FFinareAuthoringPreviewFile CandidateFile;
		if (!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
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
	if (!UE::Finare::AuthoringWorkbench::Private::ReadDiagnostics(
		Root, Candidate.Diagnostics, OutError, true))
	{
		return false;
	}

	OutPreview = MoveTemp(Candidate);
	OutError.Reset();
	return true;
}

bool FFinareAuthoringWorkbench::ParseNativePublicationPrepareResponse(
	const FString& Json,
	FFinareNativePublicationPrepareResult& OutPrepare,
	FString& OutError)
{
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		OutError = TEXT("The Workbench returned invalid native publication JSON.");
		return false;
	}
	FFinareNativePublicationPrepareResult Candidate;
	if (!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
			Root, TEXT("schemaVersion"), Candidate.SchemaVersion, OutError) ||
		Candidate.SchemaVersion != TEXT("unifiedscript.native-publication-prepare/v0.1") ||
		!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
			Root, TEXT("transactionId"), Candidate.TransactionId, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
			Root, TEXT("previewToken"), Candidate.PreviewToken, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadPositiveInteger(
			Root, TEXT("baselineRevision"), Candidate.BaselineRevision, OutError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadPositiveInteger(
			Root, TEXT("candidateRevision"), Candidate.CandidateRevision, OutError) ||
		Candidate.CandidateRevision <= Candidate.BaselineRevision ||
		!Root->TryGetBoolField(TEXT("publishesRuntime"), Candidate.bPublishesRuntime))
	{
		if (OutError.IsEmpty())
		{
			OutError = TEXT("The native publication response has invalid transaction metadata.");
		}
		return false;
	}
	if (Candidate.PreviewToken.Len() != 64)
	{
		OutError = TEXT("The native publication response preview token must be a 64-character SHA-256 token.");
		return false;
	}
	for (const TCHAR Character : Candidate.PreviewToken)
	{
		if (!FChar::IsHexDigit(Character))
		{
			OutError = TEXT("The native publication response preview token is not hexadecimal.");
			return false;
		}
	}
	Root->TryGetStringField(
		TEXT("historyEntryTransactionId"),
		Candidate.HistoryEntryTransactionId);
	Root->TryGetStringField(TEXT("historyDirection"), Candidate.HistoryDirection);
	const TSharedPtr<FJsonObject>* ScriptAssets = nullptr;
	const TArray<TSharedPtr<FJsonValue>>* BindingChanges = nullptr;
	const TArray<TSharedPtr<FJsonValue>>* Outputs = nullptr;
	if (!Root->TryGetObjectField(TEXT("scriptAssets"), ScriptAssets) || ScriptAssets == nullptr ||
		!ScriptAssets->IsValid() ||
		!Root->TryGetArrayField(TEXT("bindingChanges"), BindingChanges) || BindingChanges == nullptr ||
		!Root->TryGetArrayField(TEXT("outputs"), Outputs) || Outputs == nullptr ||
		!UE::Finare::AuthoringWorkbench::Private::SerializeJsonObject(
			*ScriptAssets, Candidate.ScriptAssetsJson))
	{
		OutError = TEXT("The native publication response has no complete ScriptAssets/output manifest.");
		return false;
	}
	for (const TSharedPtr<FJsonValue>& Value : *BindingChanges)
	{
		const TSharedPtr<FJsonObject> Change = Value.IsValid() ? Value->AsObject() : nullptr;
		FFinareNativeBindingChange Binding;
		if (!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
				Change, TEXT("assetPath"), Binding.AssetPath, OutError) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
				Change, TEXT("assetId"), Binding.AssetId, OutError) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadOptionalClassBinding(
				Change, TEXT("before"), Binding.Before, OutError) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadOptionalClassBinding(
				Change, TEXT("after"), Binding.After, OutError) ||
			(!Binding.Before.IsSet() && !Binding.After.IsSet()))
		{
			if (OutError.IsEmpty())
			{
				OutError = TEXT("The native publication response contains an empty binding change.");
			}
			return false;
		}
		Candidate.BindingChanges.Add(MoveTemp(Binding));
	}
	for (const TSharedPtr<FJsonValue>& Value : *Outputs)
	{
		const TSharedPtr<FJsonObject> Output = Value.IsValid() ? Value->AsObject() : nullptr;
		FFinareNativePublicationOutput CandidateOutput;
		FString ContentBase64;
		if (!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
				Output, TEXT("path"), CandidateOutput.Path, OutError) ||
			!Output->TryGetBoolField(TEXT("baselineExists"), CandidateOutput.bBaselineExists) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
				Output, TEXT("baselineDigest"), CandidateOutput.BaselineDigest, OutError) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
				Output, TEXT("candidateDigest"), CandidateOutput.CandidateDigest, OutError) ||
			!Output->TryGetStringField(TEXT("content"), ContentBase64) ||
			!FBase64::Decode(ContentBase64, CandidateOutput.Content))
		{
			if (OutError.IsEmpty())
			{
				OutError = TEXT("The native publication response contains an invalid output entry.");
			}
			return false;
		}
		Candidate.Outputs.Add(MoveTemp(CandidateOutput));
	}
	if (Candidate.Outputs.IsEmpty())
	{
		OutError = TEXT("The native publication response must contain at least one output.");
		return false;
	}
	OutPrepare = MoveTemp(Candidate);
	OutError.Reset();
	return true;
}

bool FFinareAuthoringWorkbench::ApplyNativePublication(
	const FNativePublicationContext& Context,
	const FFinareNativePublicationPrepareResult& Prepare,
	const FString& ExpectedPreviewToken,
	FString& OutError)
{
	OutError.Reset();
	UFinareAsset* ScriptAsset = Context.ScriptAsset.Get();
	UFinareBlueprint* Blueprint = Context.Blueprint.Get();
	UClass* GeneratedClass = Context.GeneratedClass.Get();
	if (!IsValid(ScriptAsset) || !IsValid(Blueprint) ||
		!IsValid(GeneratedClass) || ExpectedPreviewToken.IsEmpty() ||
		Prepare.PreviewToken != ExpectedPreviewToken ||
		Prepare.TransactionId.IsEmpty() || Prepare.CandidateRevision <= Prepare.BaselineRevision)
	{
		OutError = TEXT("Native publication context or preview token is stale; no UE package was changed.");
		return false;
	}
	if (!UE::Finare::AuthoringWorkbench::Private::IsValidNativeTransactionId(Prepare.TransactionId) ||
		(!Prepare.HistoryEntryTransactionId.IsEmpty() &&
			!UE::Finare::AuthoringWorkbench::Private::IsValidNativeTransactionId(Prepare.HistoryEntryTransactionId)))
	{
		OutError = TEXT(
			"Native publication contains an invalid transaction identity; use the exact durable transaction id.");
		return false;
	}
	if (Context.bHasExpectedPreviewRevisions &&
		(Prepare.BaselineRevision != Context.ExpectedBaselineRevision ||
			Prepare.CandidateRevision != Context.ExpectedCandidateRevision))
	{
		OutError = FString::Printf(
			TEXT(
				"Native publication revisions do not match the accepted preview (expected %lld -> %lld, received %lld -> %lld)."),
			Context.ExpectedBaselineRevision,
			Context.ExpectedCandidateRevision,
			Prepare.BaselineRevision,
			Prepare.CandidateRevision);
		return false;
	}

	// Check durable completion before comparing the candidate's old parent
	// against the now-advanced Blueprint.  A fresh retry of a completed
	// transaction must resolve as already committed, rather than looking like
	// a stale ordinary prepare and potentially taking another materialize
	// path.
	const FString EarlyCompletionMarkerPath = FPaths::Combine(
		FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectSavedDir(), TEXT("Finare/ClassOperations/NativeHistory"))),
		Prepare.TransactionId + TEXT(".completed.json"));
	if (IFileManager::Get().FileExists(*EarlyCompletionMarkerPath))
	{
		OutError = FString::Printf(
			TEXT(
				"Native publication completion marker '%s' already exists; a completed transaction cannot be materialized again."),
			*EarlyCompletionMarkerPath);
		return false;
	}

	const FString CurrentGeneratedClassPath = ScriptAsset->GeneratedClassPath.ToString();
	const FString CurrentOwnerPath = Blueprint->GetPathName();
	const FString CurrentParentPath = Blueprint->ParentClass != nullptr
		                                  ? Blueprint->ParentClass->GetPathName()
		                                  : FString();
	UClass* ResolvedGeneratedClass = ScriptAsset->GeneratedClassPath.TryLoadClass<UObject>();
	if (ResolvedGeneratedClass != GeneratedClass ||
		Blueprint->GeneratedClass != GeneratedClass ||
		Blueprint->ScriptAsset != ScriptAsset ||
		GeneratedClass->ClassGeneratedBy != Blueprint ||
		Context.ScriptAssetObjectPath != ScriptAsset->GetPathName() ||
		Context.ScriptClassId != ScriptAsset->ScriptClassId ||
		Context.ScriptTypeName != ScriptAsset->ScriptTypeName ||
		Context.GeneratedClassPath != CurrentGeneratedClassPath ||
		Context.GeneratedOwnerPath != CurrentOwnerPath ||
		Context.NativeParentClassPath != CurrentParentPath)
	{
		OutError = TEXT(
			"Native publication owner, generated class, and Script Asset no longer match the captured context.");
		return false;
	}
	if (Prepare.BindingChanges.Num() != 1)
	{
		OutError = TEXT("Native publication is limited to exactly one selected Script Asset owner.");
		return false;
	}

	const FFinareNativeBindingChange& BindingChange = Prepare.BindingChanges[0];
	const FString ExpectedAssetId = TEXT("us_") +
		ScriptAsset->ScriptClassId.ToString(EGuidFormats::Digits).ToLower();
	if (BindingChange.AssetPath != ScriptAsset->DeclarationAssetPath ||
		BindingChange.AssetId != ExpectedAssetId || !BindingChange.Before.IsSet() ||
		!BindingChange.After.IsSet() ||
		BindingChange.Before->Kind != BindingChange.After->Kind ||
		(BindingChange.Before->GeneratedClassPath.IsSet() &&
			BindingChange.Before->GeneratedClassPath.GetValue() != Context.GeneratedClassPath) ||
		(BindingChange.After->GeneratedClassPath.IsSet() &&
			BindingChange.After->GeneratedClassPath.GetValue() != Context.GeneratedClassPath))
	{
		OutError = TEXT(
			"Native publication binding does not identify the selected owner or is an unsupported identity/carrier change.");
		return false;
	}
	if (BindingChange.Before->BaseClassPath.IsSet() &&
		BindingChange.Before->BaseClassPath.GetValue() != CurrentParentPath)
	{
		OutError = FString::Printf(
			TEXT("Native publication expected current parent '%s', but the loaded Blueprint uses '%s'."),
			*BindingChange.Before->BaseClassPath.GetValue(),
			*CurrentParentPath);
		return false;
	}

	// The native binding change and the ScriptAssets declaration are two
	// representations of the same candidate.  Reject a split candidate
	// before the materializer can use its history override to hide an
	// explicit source parent.
	TSharedPtr<FJsonObject> ScriptAssetsRoot;
	const TSharedRef<TJsonReader<>> ScriptAssetsReader =
		TJsonReaderFactory<>::Create(Prepare.ScriptAssetsJson);
	const TArray<TSharedPtr<FJsonValue>>* ScriptAssets = nullptr;
	TOptional<FFinareClassBindingDeclaration> SelectedClassBinding;
	bool bFoundSelectedAsset = false;
	if (!FJsonSerializer::Deserialize(ScriptAssetsReader, ScriptAssetsRoot) ||
		!ScriptAssetsRoot.IsValid() ||
		!ScriptAssetsRoot->TryGetArrayField(TEXT("assets"), ScriptAssets) ||
		ScriptAssets == nullptr)
	{
		OutError = TEXT("Native publication ScriptAssets candidate is invalid or has no assets array.");
		return false;
	}
	for (const TSharedPtr<FJsonValue>& AssetValue : *ScriptAssets)
	{
		const TSharedPtr<FJsonObject> Asset = AssetValue.IsValid()
			                                      ? AssetValue->AsObject()
			                                      : nullptr;
		FString AssetPath;
		FString AssetId;
		if (!Asset.IsValid() ||
			!Asset->TryGetStringField(TEXT("assetPath"), AssetPath) ||
			!Asset->TryGetStringField(TEXT("id"), AssetId))
		{
			continue;
		}
		if (AssetPath == BindingChange.AssetPath && AssetId == BindingChange.AssetId)
		{
			FString BindingError;
			if (!FFinareClassBindingDeclaration::ReadJson(
				Asset,
				SelectedClassBinding,
				BindingError))
			{
				OutError = TEXT("Native publication selected ScriptAssets classBinding is invalid: ") + BindingError;
				return false;
			}
			bFoundSelectedAsset = true;
			break;
		}
	}
	if (!bFoundSelectedAsset || !SelectedClassBinding.IsSet() ||
		SelectedClassBinding->Kind != BindingChange.After->Kind ||
		(SelectedClassBinding->GeneratedClassPath.IsSet() &&
			SelectedClassBinding->GeneratedClassPath.GetValue() != Context.GeneratedClassPath))
	{
		OutError = TEXT(
			"Native publication ScriptAssets classBinding does not identify the selected owner or generated class.");
		return false;
	}
	if (BindingChange.After->BaseClassPath.IsSet() != SelectedClassBinding->BaseClassPath.IsSet() ||
		(BindingChange.After->BaseClassPath.IsSet() &&
			BindingChange.After->BaseClassPath.GetValue() != SelectedClassBinding->BaseClassPath.GetValue()))
	{
		OutError = TEXT(
			"Native publication binding and ScriptAssets classBinding disagree about the candidate parent.");
		return false;
	}

	FString HistoryId = Prepare.HistoryEntryTransactionId.IsEmpty()
		                    ? Prepare.TransactionId
		                    : Prepare.HistoryEntryTransactionId;
	if (HistoryId.IsEmpty())
	{
		OutError = TEXT("Native publication has no durable history transaction identity.");
		return false;
	}
	const FString SafeHistoryId = HistoryId;
	const FString SafeTransactionId = Prepare.TransactionId;
	const FString HistoryDirectory = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectSavedDir(), TEXT("Finare/ClassOperations/NativeHistory")));
	const FString NativeHistoryPath = FPaths::Combine(
		HistoryDirectory, SafeHistoryId + TEXT(".json"));
	const FString CompletionMarkerPath = FPaths::Combine(
		HistoryDirectory, SafeTransactionId + TEXT(".completed.json"));
	const FString GeneratedOwnerPath = Blueprint->GetPathName();
	const FString ClassId = ExpectedAssetId;
	FString BeforeParent;
	FString AfterParent;
	FString ForcedParentClassPath;

	FString ExistingHistoryJson;
	const bool bHasExistingHistory = FFileHelper::LoadFileToString(
		ExistingHistoryJson, *NativeHistoryPath);
	if (IFileManager::Get().FileExists(*CompletionMarkerPath))
	{
		OutError = FString::Printf(
			TEXT(
				"Native publication completion marker '%s' already exists; a completed transaction cannot be materialized again."),
			*CompletionMarkerPath);
		return false;
	}
	if (Prepare.HistoryDirection.Equals(TEXT("commit"), ESearchCase::IgnoreCase))
	{
		if (bHasExistingHistory)
		{
			OutError = FString::Printf(
				TEXT(
					"Native publication history or completion marker '%s' already exists; recover the pending transaction before retrying."),
				*NativeHistoryPath);
			return false;
		}
		BeforeParent = CurrentParentPath;
		AfterParent = BindingChange.After->BaseClassPath.IsSet()
			              ? BindingChange.After->BaseClassPath.GetValue()
			              : BeforeParent;
		ForcedParentClassPath = AfterParent;
	}
	else
	{
		if (!bHasExistingHistory)
		{
			OutError = FString::Printf(
				TEXT("Native history sidecar '%s' is required for %s; undo/redo cannot guess a parent."),
				*NativeHistoryPath,
				*Prepare.HistoryDirection);
			return false;
		}
		TSharedPtr<FJsonObject> HistoryObject;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(ExistingHistoryJson);
		FString Schema;
		FString RecordedTransaction;
		FString RecordedClassId;
		FString RecordedOwner;
		FString RecordedAssetPath;
		FString ExpectedCurrentParent;
		if (!FJsonSerializer::Deserialize(Reader, HistoryObject) || !HistoryObject.IsValid() ||
			!HistoryObject->TryGetStringField(TEXT("schemaVersion"), Schema) ||
			Schema != TEXT("unifiedscript.native-parent-history/v0.1") ||
			!HistoryObject->TryGetStringField(TEXT("transactionId"), RecordedTransaction) ||
			!HistoryObject->TryGetStringField(TEXT("classId"), RecordedClassId) ||
			!HistoryObject->TryGetStringField(TEXT("generatedOwnerPath"), RecordedOwner) ||
			!HistoryObject->TryGetStringField(TEXT("assetPath"), RecordedAssetPath) ||
			!HistoryObject->TryGetStringField(TEXT("beforeParentClassPath"), BeforeParent) ||
			!HistoryObject->TryGetStringField(TEXT("afterParentClassPath"), AfterParent) ||
			!HistoryObject->TryGetStringField(TEXT("expectedCurrentParentClassPath"), ExpectedCurrentParent))
		{
			OutError = FString::Printf(
				TEXT("Native history sidecar '%s' is invalid or incomplete."), *NativeHistoryPath);
			return false;
		}
		if (RecordedTransaction != HistoryId || RecordedClassId != ClassId ||
			RecordedOwner != GeneratedOwnerPath || RecordedAssetPath != BindingChange.AssetPath)
		{
			OutError = FString::Printf(
				TEXT("Native history sidecar '%s' does not belong to the selected owner and transaction."),
				*NativeHistoryPath);
			return false;
		}
		if (ExpectedCurrentParent != CurrentParentPath)
		{
			OutError = FString::Printf(
				TEXT("Native history sidecar expected current parent '%s', but the loaded Blueprint uses '%s'."),
				*ExpectedCurrentParent,
				*CurrentParentPath);
			return false;
		}
		if (Prepare.HistoryDirection.Equals(TEXT("undo"), ESearchCase::IgnoreCase))
		{
			ForcedParentClassPath = BeforeParent;
		}
		else if (Prepare.HistoryDirection.Equals(TEXT("redo"), ESearchCase::IgnoreCase))
		{
			ForcedParentClassPath = AfterParent;
		}
		else
		{
			OutError = FString::Printf(
				TEXT("Native history sidecar '%s' was requested with unsupported direction '%s'."), *NativeHistoryPath,
				*Prepare.HistoryDirection);
			return false;
		}
	}

	if (!ForcedParentClassPath.IsEmpty() && LoadObject<UClass>(nullptr, *ForcedParentClassPath) == nullptr)
	{
		OutError = FString::Printf(
			TEXT("Native parent history resolved missing parent class '%s'."), *ForcedParentClassPath);
		return false;
	}
	if (BindingChange.After->BaseClassPath.IsSet() &&
		BindingChange.After->BaseClassPath.GetValue() != ForcedParentClassPath)
	{
		OutError = FString::Printf(
			TEXT("Native publication source parent '%s' conflicts with the durable %s parent '%s'."),
			*BindingChange.After->BaseClassPath.GetValue(),
			Prepare.HistoryDirection.IsEmpty() ? TEXT("commit") : *Prepare.HistoryDirection,
			*ForcedParentClassPath);
		return false;
	}

	FString NativeHistoryJson;
	if (bHasExistingHistory)
	{
		// Undo/Redo must carry the existing authoring history through the
		// shared journal.  Replacing it with an empty external file would
		// make the first successful Undo impossible to Redo.
		TSharedPtr<FJsonObject> HistoryObject;
		const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(ExistingHistoryJson);
		if (!FJsonSerializer::Deserialize(Reader, HistoryObject) || !HistoryObject.IsValid())
		{
			OutError = FString::Printf(
				TEXT("Native history sidecar '%s' could not be reserialized."), *NativeHistoryPath);
			return false;
		}
		// The sidecar is a durable state record, so its expected parent moves
		// with each successful Undo/Redo.  The preflight above still checks
		// the old value against the currently loaded Blueprint.
		HistoryObject->SetStringField(TEXT("expectedCurrentParentClassPath"), ForcedParentClassPath);
		HistoryObject->SetStringField(TEXT("direction"), Prepare.HistoryDirection);
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&NativeHistoryJson);
		if (!FJsonSerializer::Serialize(HistoryObject.ToSharedRef(), Writer))
		{
			OutError = FString::Printf(TEXT("Native history sidecar '%s' could not be updated."), *NativeHistoryPath);
			return false;
		}
	}
	else
	{
		TSharedRef<FJsonObject> HistoryObject = MakeShared<FJsonObject>();
		HistoryObject->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.native-parent-history/v0.1"));
		HistoryObject->SetStringField(TEXT("transactionId"), HistoryId);
		HistoryObject->SetStringField(TEXT("classId"), ClassId);
		HistoryObject->SetStringField(TEXT("generatedOwnerPath"), GeneratedOwnerPath);
		HistoryObject->SetStringField(TEXT("assetPath"), BindingChange.AssetPath);
		HistoryObject->SetStringField(TEXT("beforeParentClassPath"), BeforeParent);
		HistoryObject->SetStringField(TEXT("afterParentClassPath"), AfterParent);
		HistoryObject->SetStringField(TEXT("expectedCurrentParentClassPath"), AfterParent);
		HistoryObject->SetStringField(TEXT("direction"), Prepare.HistoryDirection);
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&NativeHistoryJson);
		if (!FJsonSerializer::Serialize(HistoryObject, Writer))
		{
			OutError = TEXT("Could not serialize the native parent history sidecar.");
			return false;
		}
	}

	TArray<FString> OutputPaths;
	TSet<FString> UniquePaths;
	FFinareBlueprintClassMaterializerOptions Options;
	Options.JournalTransactionId = Prepare.TransactionId;
	Options.FailAfterExternalPublicationWrite = Context.FailAfterExternalPublicationWriteForTesting;
	Options.bOverrideParentClassPath = !ForcedParentClassPath.IsEmpty();
	Options.ParentClassPathOverride = ForcedParentClassPath;
	for (const FFinareNativePublicationOutput& Output : Prepare.Outputs)
	{
		FString Path = FPaths::ConvertRelativePathToFull(Output.Path);
		FPaths::NormalizeFilename(Path);
		const FString PathKey = Path.ToLower();
		if (FPaths::IsRelative(Output.Path) || UniquePaths.Contains(PathKey))
		{
			OutError = TEXT("Native publication output paths must be absolute and unique.");
			return false;
		}
		const bool bExists = IFileManager::Get().FileExists(*Path);
		TArray<uint8> BaselineBytes;
		if (bExists && !FFileHelper::LoadFileToArray(BaselineBytes, *Path))
		{
			OutError = FString::Printf(
				TEXT("Native publication could not read the baseline output '%s'."),
				*Path);
			return false;
		}
		const FString BaselineDigest =
			UE::Finare::AuthoringWorkbench::Private::ComputeSha256Digest(BaselineBytes);
		const FString CandidateDigest =
			UE::Finare::AuthoringWorkbench::Private::ComputeSha256Digest(Output.Content);
		if (BaselineDigest.IsEmpty() || CandidateDigest.IsEmpty() ||
			bExists != Output.bBaselineExists ||
			!BaselineDigest.Equals(Output.BaselineDigest, ESearchCase::IgnoreCase) ||
			!CandidateDigest.Equals(Output.CandidateDigest, ESearchCase::IgnoreCase))
		{
			OutError = FString::Printf(
				TEXT("Native publication output baseline or candidate digest changed for '%s'; prepare must be rerun."),
				*Path);
			return false;
		}
		UniquePaths.Add(PathKey);
		FFinareExternalPublicationFile& File = Options.ExternalPublicationFiles.AddDefaulted_GetRef();
		File.Path = Path;
		File.Content = Output.Content;
	}
	const FString HistoryPathKey = NativeHistoryPath.ToLower();
	const FString CompletionPathKey = CompletionMarkerPath.ToLower();
	if (UniquePaths.Contains(HistoryPathKey) || UniquePaths.Contains(CompletionPathKey))
	{
		OutError = TEXT("Native publication outputs collide with the reserved history or completion marker path.");
		return false;
	}
	FFinareExternalPublicationFile& HistoryFile = Options.ExternalPublicationFiles.AddDefaulted_GetRef();
	HistoryFile.Path = NativeHistoryPath;
	FTCHARToUTF8 HistoryUtf8(*NativeHistoryJson);
	HistoryFile.Content.Append(reinterpret_cast<const uint8*>(HistoryUtf8.Get()), HistoryUtf8.Length());

	TSharedRef<FJsonObject> MarkerObject = MakeShared<FJsonObject>();
	MarkerObject->SetStringField(TEXT("schemaVersion"), TEXT("unifiedscript.native-publication-completion/v0.1"));
	MarkerObject->SetStringField(TEXT("transactionId"), Prepare.TransactionId);
	MarkerObject->SetStringField(TEXT("previewToken"), Prepare.PreviewToken);
	MarkerObject->SetStringField(TEXT("classId"), ClassId);
	MarkerObject->SetStringField(TEXT("generatedOwnerPath"), GeneratedOwnerPath);
	MarkerObject->SetStringField(TEXT("assetPath"), BindingChange.AssetPath);
	MarkerObject->SetStringField(TEXT("generatedClassPath"), CurrentGeneratedClassPath);
	MarkerObject->SetStringField(TEXT("parentClassPath"), ForcedParentClassPath);
	const FString ExpectedGeneratedSuperClassPath = !ForcedParentClassPath.IsEmpty()
		                                                ? ForcedParentClassPath
		                                                : (Blueprint->GeneratedClass != nullptr && Blueprint->
		                                                   GeneratedClass->GetSuperClass() != nullptr
			                                                   ? Blueprint->GeneratedClass->GetSuperClass()->
			                                                                GetPathName()
			                                                   : FString());
	MarkerObject->SetStringField(TEXT("generatedSuperClassPath"), ExpectedGeneratedSuperClassPath);
	MarkerObject->SetStringField(TEXT("state"), TEXT("nativeCommittedAwaitingWorkbenchAdoption"));
	FString MarkerJson;
	const TSharedRef<TJsonWriter<>> MarkerWriter = TJsonWriterFactory<>::Create(&MarkerJson);
	if (!FJsonSerializer::Serialize(MarkerObject, MarkerWriter))
	{
		OutError = TEXT("Could not serialize the native publication completion marker.");
		return false;
	}
	FFinareExternalPublicationFile& MarkerFile = Options.ExternalPublicationFiles.AddDefaulted_GetRef();
	MarkerFile.Path = CompletionMarkerPath;
	FTCHARToUTF8 MarkerUtf8(*MarkerJson);
	MarkerFile.Content.Append(reinterpret_cast<const uint8*>(MarkerUtf8.Get()), MarkerUtf8.Length());

	FFinareBlueprintClassSyncResult Result;
	return FFinareBlueprintClassMaterializer::SynchronizeJson(
		*ScriptAsset,
		*Blueprint,
		Prepare.ScriptAssetsJson,
		false,
		Result,
		OutError,
		Options);
}

bool FFinareAuthoringWorkbench::CanCancelNativePublication(
	const FFinareNativePublicationPrepareResult& Prepare,
	FString& OutError)
{
	OutError.Reset();
	if (!UE::Finare::AuthoringWorkbench::Private::IsValidNativeTransactionId(Prepare.TransactionId))
	{
		OutError = TEXT("Native publication rollback cannot be cancelled because its transaction identity is invalid.");
		return false;
	}

	const FString HistoryDirectory = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectSavedDir(), TEXT("Finare/ClassOperations/NativeHistory")));
	const FString PendingReceiptPath = FPaths::Combine(
		UE::Finare::ClassOperationJournal::GetPendingRoot(),
		Prepare.TransactionId + TEXT(".json"));
	if (IFileManager::Get().FileExists(*PendingReceiptPath))
	{
		OutError = FString::Printf(
			TEXT("Native publication rollback is uncertain; recovery receipt '%s' is still pending."),
			*PendingReceiptPath);
		return false;
	}

	const FString CompletionMarkerPath = FPaths::Combine(
		HistoryDirectory,
		Prepare.TransactionId + TEXT(".completed.json"));
	if (IFileManager::Get().FileExists(*CompletionMarkerPath))
	{
		OutError = FString::Printf(
			TEXT("Native publication rollback cannot be cancelled because completion marker '%s' exists."),
			*CompletionMarkerPath);
		return false;
	}

	for (const FFinareNativePublicationOutput& Output : Prepare.Outputs)
	{
		if (FPaths::IsRelative(Output.Path))
		{
			OutError = TEXT("Native publication rollback cannot verify a relative output path.");
			return false;
		}
		FString Path = FPaths::ConvertRelativePathToFull(Output.Path);
		FPaths::NormalizeFilename(Path);
		const bool bExists = IFileManager::Get().FileExists(*Path);
		if (bExists != Output.bBaselineExists)
		{
			OutError = FString::Printf(
				TEXT("Native publication rollback could not prove baseline existence for '%s'."),
				*Path);
			return false;
		}
		if (bExists)
		{
			TArray<uint8> Bytes;
			if (!FFileHelper::LoadFileToArray(Bytes, *Path))
			{
				OutError = FString::Printf(
					TEXT("Native publication rollback could not read baseline output '%s'."),
					*Path);
				return false;
			}
			const FString Digest = UE::Finare::AuthoringWorkbench::Private::ComputeSha256Digest(Bytes);
			if (!Digest.Equals(Output.BaselineDigest, ESearchCase::IgnoreCase))
			{
				OutError = FString::Printf(
					TEXT("Native publication rollback did not restore baseline output '%s'."),
					*Path);
				return false;
			}
		}
	}
	return true;
}

bool FFinareAuthoringWorkbench::ValidateNativeCompletionMarker(
	const FNativePublicationContext& Context,
	const FFinareNativePublicationPrepareResult& Prepare,
	FString& OutError)
{
	OutError.Reset();
	UFinareAsset* ScriptAsset = Context.ScriptAsset.Get();
	UFinareBlueprint* Blueprint = Context.Blueprint.Get();
	if (!IsValid(ScriptAsset) || !IsValid(Blueprint) || Prepare.TransactionId.IsEmpty() ||
		Prepare.PreviewToken.IsEmpty())
	{
		OutError = TEXT("Native publication completion has no valid captured owner or transaction identity.");
		return false;
	}

	const FString PendingReceiptPath = FPaths::Combine(
		UE::Finare::ClassOperationJournal::GetPendingRoot(),
		Prepare.TransactionId + TEXT(".json"));
	if (IFileManager::Get().FileExists(*PendingReceiptPath))
	{
		OutError = FString::Printf(
			TEXT("Native publication completion is blocked by pending recovery receipt '%s'."),
			*PendingReceiptPath);
		return false;
	}

	if (!UE::Finare::AuthoringWorkbench::Private::IsValidNativeTransactionId(Prepare.TransactionId))
	{
		OutError = TEXT(
			"Native publication completion has an invalid transaction identity; the marker path cannot be resolved safely.");
		return false;
	}
	const FString MarkerPath = FPaths::Combine(
		FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectSavedDir(), TEXT("Finare/ClassOperations/NativeHistory"))),
		Prepare.TransactionId + TEXT(".completed.json"));
	FString MarkerJson;
	if (!FFileHelper::LoadFileToString(MarkerJson, *MarkerPath))
	{
		OutError = FString::Printf(
			TEXT("Native publication completion marker '%s' is missing; keep the reservation for recovery."),
			*MarkerPath);
		return false;
	}

	TSharedPtr<FJsonObject> Marker;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(MarkerJson);
	FString Schema;
	FString TransactionId;
	FString PreviewToken;
	FString ClassId;
	FString GeneratedOwnerPath;
	FString AssetPath;
	FString GeneratedClassPath;
	FString ParentClassPath;
	FString GeneratedSuperClassPath;
	FString State;
	if (!FJsonSerializer::Deserialize(Reader, Marker) || !Marker.IsValid() ||
		!Marker->TryGetStringField(TEXT("schemaVersion"), Schema) ||
		!Marker->TryGetStringField(TEXT("transactionId"), TransactionId) ||
		!Marker->TryGetStringField(TEXT("previewToken"), PreviewToken) ||
		!Marker->TryGetStringField(TEXT("classId"), ClassId) ||
		!Marker->TryGetStringField(TEXT("generatedOwnerPath"), GeneratedOwnerPath) ||
		!Marker->TryGetStringField(TEXT("assetPath"), AssetPath) ||
		!Marker->TryGetStringField(TEXT("generatedClassPath"), GeneratedClassPath) ||
		!Marker->TryGetStringField(TEXT("parentClassPath"), ParentClassPath) ||
		!Marker->TryGetStringField(TEXT("generatedSuperClassPath"), GeneratedSuperClassPath) ||
		!Marker->TryGetStringField(TEXT("state"), State) ||
		Schema != TEXT("unifiedscript.native-publication-completion/v0.1") ||
		State != TEXT("nativeCommittedAwaitingWorkbenchAdoption"))
	{
		OutError = FString::Printf(
			TEXT("Native publication completion marker '%s' is invalid or incomplete."),
			*MarkerPath);
		return false;
	}
	const FString ExpectedClassId = TEXT("us_") +
		ScriptAsset->ScriptClassId.ToString(EGuidFormats::Digits).ToLower();
	if (TransactionId != Prepare.TransactionId || PreviewToken != Prepare.PreviewToken ||
		ClassId != ExpectedClassId || GeneratedOwnerPath != Blueprint->GetPathName() ||
		AssetPath != ScriptAsset->DeclarationAssetPath ||
		GeneratedClassPath != ScriptAsset->GeneratedClassPath.ToString() ||
		Context.ScriptAssetObjectPath != ScriptAsset->GetPathName() ||
		Context.ScriptClassId != ScriptAsset->ScriptClassId ||
		Context.ScriptTypeName != ScriptAsset->ScriptTypeName ||
		Context.GeneratedOwnerPath != Blueprint->GetPathName() ||
		Context.GeneratedClassPath != ScriptAsset->GeneratedClassPath.ToString() ||
		ParentClassPath != (Blueprint->ParentClass != nullptr
			                    ? Blueprint->ParentClass->GetPathName()
			                    : FString()) ||
		GeneratedSuperClassPath != (Blueprint->GeneratedClass != nullptr &&
		                            Blueprint->GeneratedClass->GetSuperClass() != nullptr
			                            ? Blueprint->GeneratedClass->GetSuperClass()->GetPathName()
			                            : FString()))
	{
		OutError = FString::Printf(
			TEXT(
				"Native publication completion marker '%s' does not match the captured owner or exact transaction token."),
			*MarkerPath);
		return false;
	}
	return true;
}

bool FFinareAuthoringWorkbench::ParseErrorResponse(
	const FString& Json,
	int32 StatusCode,
	FFinareAuthoringErrorResult& OutFailure,
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
	FFinareAuthoringErrorResult Candidate;
	Candidate.StatusCode = StatusCode;
	if (!Root->TryGetObjectField(TEXT("error"), Error) || Error == nullptr ||
		!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
			*Error, TEXT("code"), Candidate.Code, OutParseError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
			*Error, TEXT("message"), Candidate.Message, OutParseError) ||
		!UE::Finare::AuthoringWorkbench::Private::ReadDiagnostics(
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
			FFinareAuthoringQuickFix CandidateFix;
			if (!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
					Fix, TEXT("id"), CandidateFix.Id, OutParseError) ||
				!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
					Fix, TEXT("label"), CandidateFix.Label, OutParseError) ||
				!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
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
		if (!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
				*Rollback, TEXT("state"), Candidate.Rollback.State, OutParseError) ||
			!(Candidate.Rollback.State == TEXT("unchanged") ||
				Candidate.Rollback.State == TEXT("restored") ||
				Candidate.Rollback.State == TEXT("failed")) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadPositiveInteger(
				*Rollback, TEXT("revision"), Candidate.Rollback.Revision, OutParseError) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadPositiveInteger(
				*Rollback, TEXT("generation"), Candidate.Rollback.Generation, OutParseError) ||
			!UE::Finare::AuthoringWorkbench::Private::ReadRequiredString(
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

FString FFinareAuthoringWorkbench::ParseErrorResponse(
	const FString& Json,
	int32 StatusCode)
{
	FFinareAuthoringErrorResult Failure;
	FString ParseError;
	if (ParseErrorResponse(Json, StatusCode, Failure, ParseError))
	{
		return Failure.ToDisplayString();
	}
	return FString::Printf(TEXT("Workbench request failed with HTTP %d."), StatusCode);
}
