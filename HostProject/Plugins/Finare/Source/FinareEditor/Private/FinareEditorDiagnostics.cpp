#include "FinareEditorDiagnostics.h"

#include "HAL/FileManager.h"
#include "HAL/PlatformApplicationMisc.h"
#include "HAL/PlatformProcess.h"
#include "Logging/MessageLog.h"
#include "Logging/TokenizedMessage.h"
#include "MessageLogInitializationOptions.h"
#include "MessageLogModule.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Modules/ModuleManager.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "SourceCodeNavigation.h"
#include "FinareAuthoringWorkbench.h"
#include "FinareEditorModule.h"
#include "FinareRuntimeDiagnostic.h"

#define LOCTEXT_NAMESPACE "FinareEditorDiagnostics"

const FName FFinareEditorDiagnostics::LogName(TEXT("FinareDiagnostics"));

namespace UE::Finare::EditorDiagnostics::Private
{
	constexpr TCHAR SnapshotSchema[] = TEXT("unifiedscript.editor-diagnostics/v0.2");
	TArray<FFinareEditorDiagnostic> RetainedDiagnostics;

	struct FReportContext
	{
		FString Stage;
		FString PackagePath;
		uint64 WorldHostId = 0;
		uint64 InstanceId = 0;
		int32 Generation = 0;
	};

	FString ReadString(
		const TSharedPtr<FJsonObject>& Object,
		std::initializer_list<const TCHAR*> Names)
	{
		if (!Object.IsValid())
		{
			return FString();
		}
		FString Value;
		for (const TCHAR* Name : Names)
		{
			if (Object->TryGetStringField(Name, Value) && !Value.IsEmpty())
			{
				return Value.Left(4096);
			}
		}
		return FString();
	}

	int32 ReadPositiveInteger(
		const TSharedPtr<FJsonObject>& Object,
		std::initializer_list<const TCHAR*> Names)
	{
		if (!Object.IsValid())
		{
			return 0;
		}
		double Value = 0.0;
		for (const TCHAR* Name : Names)
		{
			if (Object->TryGetNumberField(Name, Value) && Value >= 1.0 && Value <= MAX_int32)
			{
				return static_cast<int32>(Value);
			}
		}
		return 0;
	}

	int32 ReadNonNegativeInteger(
		const TSharedPtr<FJsonObject>& Object,
		std::initializer_list<const TCHAR*> Names,
		int32 Fallback = 0)
	{
		if (!Object.IsValid())
		{
			return Fallback;
		}
		double Value = 0.0;
		for (const TCHAR* Name : Names)
		{
			if (Object->TryGetNumberField(Name, Value) && Value >= 0.0 && Value <= MAX_int32)
			{
				return static_cast<int32>(Value);
			}
		}
		return Fallback;
	}

	uint64 ReadUnsignedIdentity(
		const TSharedPtr<FJsonObject>& Object,
		std::initializer_list<const TCHAR*> Names,
		uint64 Fallback = 0)
	{
		if (!Object.IsValid())
		{
			return Fallback;
		}
		for (const TCHAR* Name : Names)
		{
			FString StringValue;
			if (Object->TryGetStringField(Name, StringValue))
			{
				uint64 Parsed = 0;
				if (LexTryParseString(Parsed, *StringValue))
				{
					return Parsed;
				}
			}
			double NumberValue = 0.0;
			if (Object->TryGetNumberField(Name, NumberValue) &&
				NumberValue >= 0.0 && NumberValue <= static_cast<double>(MAX_uint64))
			{
				return static_cast<uint64>(NumberValue);
			}
		}
		return Fallback;
	}

	FString NormalizeSeverity(FString Severity)
	{
		Severity.ToLowerInline();
		return Severity == TEXT("warning") || Severity == TEXT("info")
			       ? Severity
			       : TEXT("error");
	}

	FString Classify(const FString& Code, const FString& Schema, const FString& Message)
	{
		const FString UpperCode = Code.ToUpper();
		const FString LowerSchema = Schema.ToLower();
		const FString LowerMessage = Message.ToLower();
		if (UpperCode.Contains(TEXT("HANDLE")) || UpperCode.Contains(TEXT("LIFETIME")) ||
			LowerMessage.Contains(TEXT("stale handle")) ||
			LowerMessage.Contains(TEXT("object lifetime")) ||
			LowerMessage.Contains(TEXT("world teardown")))
		{
			return TEXT("lifetime");
		}
		if (LowerSchema.Contains(TEXT("package")) || LowerSchema.Contains(TEXT("cook")) ||
			LowerMessage.Contains(TEXT("package omission")) ||
			LowerMessage.Contains(TEXT("missing cooked")))
		{
			return TEXT("package");
		}
		if (UpperCode.StartsWith(TEXT("CS")) || LowerSchema.Contains(TEXT("compile")))
		{
			return TEXT("compile");
		}
		if (UpperCode.Contains(TEXT("STALE")) || LowerMessage.Contains(TEXT("stale")))
		{
			return TEXT("stale-surface");
		}
		if (LowerSchema.Contains(TEXT("coverage")) || UpperCode.StartsWith(TEXT("USUE")))
		{
			return TEXT("coverage");
		}
		if (LowerSchema.Contains(TEXT("onboarding")) || LowerSchema.Contains(TEXT("generation")))
		{
			return TEXT("generation");
		}
		return TEXT("authoring");
	}

	FString RecoveryForDomain(const FString& Domain)
	{
		if (Domain == TEXT("stale-surface") || Domain == TEXT("compile") ||
			Domain == TEXT("runtime"))
		{
			return TEXT("retry");
		}
		if (Domain == TEXT("package"))
		{
			return TEXT("inspectPackage");
		}
		return TEXT("repair");
	}

	FString NormalizeRecovery(FString Action, const FString& Domain)
	{
		Action.TrimStartAndEndInline();
		if (Action.Equals(TEXT("restartEditor"), ESearchCase::IgnoreCase))
		{
			return TEXT("restartEditor");
		}
		if (Action.Equals(TEXT("inspectPackage"), ESearchCase::IgnoreCase) ||
			Action.Equals(TEXT("package"), ESearchCase::IgnoreCase))
		{
			return TEXT("inspectPackage");
		}
		if (Action.Equals(TEXT("repair"), ESearchCase::IgnoreCase))
		{
			return TEXT("repair");
		}
		if (Action.Equals(TEXT("retry"), ESearchCase::IgnoreCase))
		{
			return TEXT("retry");
		}
		return RecoveryForDomain(Domain);
	}

	FString RecoveryLabel(const FString& Action)
	{
		if (Action == TEXT("restartEditor"))
		{
			return TEXT("Restart Editor");
		}
		if (Action == TEXT("repair"))
		{
			return TEXT("Repair");
		}
		if (Action == TEXT("retry"))
		{
			return TEXT("Retry");
		}
		if (Action == TEXT("inspectPackage"))
		{
			return TEXT("Inspect Package");
		}
		return TEXT("Inspect");
	}

	EMessageSeverity::Type ToMessageSeverity(const FString& Severity)
	{
		return Severity == TEXT("warning")
			       ? EMessageSeverity::Warning
			       : Severity == TEXT("info")
			       ? EMessageSeverity::Info
			       : EMessageSeverity::Error;
	}

	void AppendReportObject(
		const TSharedPtr<FJsonObject>& Object,
		const FString& Schema,
		const FString& ReportPath,
		const FReportContext& InheritedContext,
		int32 Depth,
		TArray<FFinareEditorDiagnostic>& OutDiagnostics)
	{
		if (!Object.IsValid() || Depth > 24 ||
			OutDiagnostics.Num() >= FFinareEditorDiagnostics::MaximumRetainedDiagnosticCount)
		{
			return;
		}

		FReportContext Context = InheritedContext;
		const FString Stage = ReadString(
			Object,
			{TEXT("stage"), TEXT("phase"), TEXT("currentPhase"), TEXT("operation")});
		if (!Stage.IsEmpty())
		{
			Context.Stage = Stage.Left(256);
		}
		const FString PackagePath = ReadString(
			Object,
			{TEXT("packagePath"), TEXT("assetPath"), TEXT("cookedPackage"), TEXT("package")});
		if (!PackagePath.IsEmpty())
		{
			Context.PackagePath = PackagePath.Left(4096);
		}
		Context.WorldHostId = ReadUnsignedIdentity(
			Object,
			{TEXT("worldHostId"), TEXT("worldId")},
			Context.WorldHostId);
		Context.InstanceId = ReadUnsignedIdentity(
			Object,
			{TEXT("instanceId"), TEXT("scriptInstanceId")},
			Context.InstanceId);
		Context.Generation = ReadNonNegativeInteger(
			Object,
			{TEXT("generation"), TEXT("runtimeGeneration")},
			Context.Generation);

		const FString Code = ReadString(Object, {TEXT("code"), TEXT("diagnosticCode")});
		const FString Message = ReadString(
			Object,
			{TEXT("message"), TEXT("error"), TEXT("reason"), TEXT("summary")});
		FString SourcePath = ReadString(
			Object,
			{TEXT("sourcePath"), TEXT("filePath"), TEXT("source"), TEXT("file")});
		FString Member = ReadString(
			Object,
			{
				TEXT("memberPath"), TEXT("memberName"), TEXT("member"), TEXT("symbol"),
				TEXT("semanticId")
			});
		const FString GraphNode = ReadString(
			Object,
			{TEXT("graphNodeId"), TEXT("nodeId"), TEXT("semanticNodeId")});
		int32 Line = ReadPositiveInteger(Object, {TEXT("line"), TEXT("startLine")});
		int32 Column = ReadPositiveInteger(Object, {TEXT("column"), TEXT("startColumn")});

		for (const TCHAR* NestedName : {TEXT("span"), TEXT("location"), TEXT("sourceLocation")})
		{
			const TSharedPtr<FJsonObject>* Nested = nullptr;
			if (Object->TryGetObjectField(NestedName, Nested) && Nested != nullptr)
			{
				if (SourcePath.IsEmpty())
				{
					SourcePath = ReadString(
						*Nested,
						{TEXT("sourcePath"), TEXT("filePath"), TEXT("path"), TEXT("file")});
				}
				Line = Line > 0 ? Line : ReadPositiveInteger(*Nested, {TEXT("line"), TEXT("startLine")});
				Column = Column > 0
					         ? Column
					         : ReadPositiveInteger(*Nested, {TEXT("column"), TEXT("startColumn")});
			}
		}

		if ((!Code.IsEmpty() && !Message.IsEmpty()) ||
			(!Message.IsEmpty() && (!SourcePath.IsEmpty() || !Member.IsEmpty())))
		{
			FFinareEditorDiagnostic Diagnostic;
			Diagnostic.Stage = Context.Stage;
			Diagnostic.Code = Code.IsEmpty() ? TEXT("USREPORT") : Code.Left(128);
			Diagnostic.Severity = NormalizeSeverity(ReadString(Object, {TEXT("severity"), TEXT("level")}));
			Diagnostic.Summary = Message.Left(4096);
			Diagnostic.Detail = ReadString(Object, {TEXT("detail"), TEXT("description")});
			Diagnostic.SourcePath = SourcePath;
			Diagnostic.Line = Line;
			Diagnostic.Column = Column;
			Diagnostic.Member = Member.Left(512);
			Diagnostic.GraphNode = GraphNode.Left(512);
			Diagnostic.PackagePath = Context.PackagePath;
			Diagnostic.WorldHostId = Context.WorldHostId;
			Diagnostic.InstanceId = Context.InstanceId;
			Diagnostic.Generation = Context.Generation;
			Diagnostic.Domain = ReadString(Object, {TEXT("domain")}).ToLower();
			if (Diagnostic.Domain.IsEmpty())
			{
				Diagnostic.Domain = Classify(Diagnostic.Code, Schema, Diagnostic.Summary);
			}
			Diagnostic.RecoveryAction = NormalizeRecovery(
				ReadString(Object, {TEXT("recoveryAction"), TEXT("requiredAction"), TEXT("action")}),
				Diagnostic.Domain);
			Diagnostic.EvidencePath = ReportPath;

			const bool bDuplicate = OutDiagnostics.ContainsByPredicate(
				[&Diagnostic](const FFinareEditorDiagnostic& Existing)
				{
					return Existing.Code == Diagnostic.Code &&
						Existing.Summary == Diagnostic.Summary &&
						Existing.SourcePath == Diagnostic.SourcePath &&
						Existing.Line == Diagnostic.Line &&
						Existing.Member == Diagnostic.Member &&
						Existing.GraphNode == Diagnostic.GraphNode &&
						Existing.Stage == Diagnostic.Stage &&
						Existing.WorldHostId == Diagnostic.WorldHostId &&
						Existing.Generation == Diagnostic.Generation;
				});
			if (!bDuplicate)
			{
				OutDiagnostics.Add(MoveTemp(Diagnostic));
			}
		}

		for (const TPair<FString, TSharedPtr<FJsonValue>>& Field : Object->Values)
		{
			if (!Field.Value.IsValid())
			{
				continue;
			}
			if (Field.Value->Type == EJson::Object)
			{
				AppendReportObject(
					Field.Value->AsObject(),
					Schema,
					ReportPath,
					Context,
					Depth + 1,
					OutDiagnostics);
			}
			else if (Field.Value->Type == EJson::Array)
			{
				for (const TSharedPtr<FJsonValue>& Item : Field.Value->AsArray())
				{
					if (Item.IsValid() && Item->Type == EJson::Object)
					{
						AppendReportObject(
							Item->AsObject(),
							Schema,
							ReportPath,
							Context,
							Depth + 1,
							OutDiagnostics);
					}
				}
			}
		}
	}

	void PersistSnapshot()
	{
		const FString Path = FFinareEditorDiagnostics::GetSnapshotPath();
		IFileManager::Get().MakeDirectory(*FPaths::GetPath(Path), true);
		const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
		Root->SetStringField(TEXT("schemaVersion"), SnapshotSchema);
		Root->SetNumberField(TEXT("retainedCount"), RetainedDiagnostics.Num());
		TArray<TSharedPtr<FJsonValue>> Values;
		Values.Reserve(RetainedDiagnostics.Num());
		for (int32 Index = 0; Index < RetainedDiagnostics.Num(); ++Index)
		{
			const FFinareEditorDiagnostic& Diagnostic = RetainedDiagnostics[Index];
			const TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
			Json->SetNumberField(TEXT("sequence"), Index + 1);
			Json->SetStringField(TEXT("domain"), Diagnostic.Domain);
			Json->SetStringField(TEXT("stage"), Diagnostic.Stage);
			Json->SetStringField(TEXT("code"), Diagnostic.Code);
			Json->SetStringField(TEXT("severity"), Diagnostic.Severity);
			Json->SetStringField(TEXT("message"), Diagnostic.Summary.Left(4096));
			Json->SetStringField(TEXT("detail"), Diagnostic.Detail.Left(4096));
			Json->SetStringField(TEXT("sourcePath"), Diagnostic.SourcePath);
			Json->SetNumberField(TEXT("line"), Diagnostic.Line);
			Json->SetNumberField(TEXT("column"), Diagnostic.Column);
			Json->SetStringField(TEXT("member"), Diagnostic.Member);
			Json->SetStringField(TEXT("graphNodeId"), Diagnostic.GraphNode);
			Json->SetStringField(TEXT("packagePath"), Diagnostic.PackagePath);
			Json->SetStringField(TEXT("worldHostId"), LexToString(Diagnostic.WorldHostId));
			Json->SetStringField(TEXT("instanceId"), LexToString(Diagnostic.InstanceId));
			Json->SetNumberField(TEXT("generation"), Diagnostic.Generation);
			Json->SetStringField(TEXT("action"), Diagnostic.RecoveryAction);
			Json->SetStringField(TEXT("evidencePath"), Diagnostic.EvidencePath);
			Values.Add(MakeShared<FJsonValueObject>(Json));
		}
		Root->SetArrayField(TEXT("diagnostics"), Values);
		FString JsonText;
		const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&JsonText);
		if (!FJsonSerializer::Serialize(Root, Writer))
		{
			return;
		}
		JsonText.ReplaceInline(TEXT("\r\n"), TEXT("\n"));
		JsonText.AppendChar(TEXT('\n'));
		const FString TemporaryPath = Path + TEXT(".tmp");
		if (FFileHelper::SaveStringToFile(
			JsonText,
			*TemporaryPath,
			FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM))
		{
			IFileManager::Get().Move(*Path, *TemporaryPath, true, true);
		}
	}
}

FString FFinareEditorDiagnostic::ToClipboardText() const
{
	FString Result = FString::Printf(
		TEXT("[%s/%s] %s: %s\nRecovery: %s"),
		*Domain,
		Stage.IsEmpty() ? TEXT("unspecified") : *Stage,
		*Code,
		*Summary,
		*UE::Finare::EditorDiagnostics::Private::RecoveryLabel(RecoveryAction));
	if (!Member.IsEmpty())
	{
		Result += FString::Printf(TEXT("\nMember: %s"), *Member);
	}
	if (!GraphNode.IsEmpty())
	{
		Result += FString::Printf(TEXT("\nGraph node: %s"), *GraphNode);
	}
	if (WorldHostId != 0 || InstanceId != 0 || Generation != 0)
	{
		Result += FString::Printf(
			TEXT("\nRuntime: World=%llu Instance=%llu Generation=%d"),
			WorldHostId,
			InstanceId,
			Generation);
	}
	if (!PackagePath.IsEmpty())
	{
		Result += FString::Printf(TEXT("\nPackage: %s"), *PackagePath);
	}
	if (HasSource())
	{
		Result += FString::Printf(
			TEXT("\nSource: %s(%d,%d)"), *SourcePath, Line, FMath::Max(1, Column));
	}
	if (!EvidencePath.IsEmpty())
	{
		Result += FString::Printf(TEXT("\nEvidence: %s"), *EvidencePath);
	}
	if (!Detail.IsEmpty())
	{
		Result += FString::Printf(TEXT("\nDetail: %s"), *Detail);
	}
	return Result;
}

void FFinareEditorDiagnostics::Register()
{
	FMessageLogModule& Module = FModuleManager::LoadModuleChecked<FMessageLogModule>(
		TEXT("MessageLog"));
	if (!Module.IsRegisteredLogListing(LogName))
	{
		FMessageLogInitializationOptions Options;
		Options.bShowFilters = true;
		Options.bShowPages = true;
		Options.bAllowClear = true;
		Options.bDiscardDuplicates = true;
		Options.bScrollToBottom = true;
		Module.RegisterLogListing(LogName, LOCTEXT("LogLabel", "Finare Diagnostics"), Options);
	}
}

void FFinareEditorDiagnostics::Unregister()
{
	if (FModuleManager::Get().IsModuleLoaded(TEXT("MessageLog")))
	{
		FMessageLogModule& Module = FModuleManager::GetModuleChecked<FMessageLogModule>(
			TEXT("MessageLog"));
		if (Module.IsRegisteredLogListing(LogName))
		{
			Module.UnregisterLogListing(LogName);
		}
	}
}

TArray<FFinareEditorDiagnostic> FFinareEditorDiagnostics::FromAuthoringFailure(
	const FFinareAuthoringErrorResult& Failure)
{
	TArray<FFinareEditorDiagnostic> Result;
	for (const FFinareAuthoringDiagnostic& Source : Failure.Diagnostics)
	{
		FFinareEditorDiagnostic Diagnostic;
		Diagnostic.Stage = Source.Code.StartsWith(TEXT("CS"))
			                   ? TEXT("compile")
			                   : TEXT("graph-analysis");
		Diagnostic.Code = Source.Code;
		Diagnostic.Severity = UE::Finare::EditorDiagnostics::Private::NormalizeSeverity(
			Source.Severity);
		Diagnostic.Summary = Source.Message;
		Diagnostic.SourcePath = Source.Span.FilePath == TEXT("<unknown>")
			                        ? FString()
			                        : Source.Span.FilePath;
		Diagnostic.Line = Source.Span.StartLine;
		Diagnostic.Column = Source.Span.StartColumn;
		Diagnostic.Member = Source.SemanticId;
		Diagnostic.GraphNode = Source.GraphNodeId;
		Diagnostic.Domain = UE::Finare::EditorDiagnostics::Private::Classify(
			Source.Code, TEXT("authoring"), Source.Message);
		Diagnostic.RecoveryAction = UE::Finare::EditorDiagnostics::Private::RecoveryForDomain(
			Diagnostic.Domain);
		Result.Add(MoveTemp(Diagnostic));
	}
	if (Result.IsEmpty())
	{
		FFinareEditorDiagnostic Diagnostic;
		Diagnostic.Stage = TEXT("authoring");
		Diagnostic.Domain = UE::Finare::EditorDiagnostics::Private::Classify(
			Failure.Code, TEXT("authoring"), Failure.Message);
		Diagnostic.Code = Failure.Code.IsEmpty() ? TEXT("USAUTHORING") : Failure.Code;
		Diagnostic.Severity = TEXT("error");
		Diagnostic.Summary = Failure.Message;
		Diagnostic.RecoveryAction = UE::Finare::EditorDiagnostics::Private::RecoveryForDomain(
			Diagnostic.Domain);
		Result.Add(MoveTemp(Diagnostic));
	}
	if (Failure.ReloadTransaction.IsValid())
	{
		Result.Add(FromReloadTransaction(Failure.ReloadTransaction, FString()));
	}
	return Result;
}

FFinareEditorDiagnostic FFinareEditorDiagnostics::FromReloadTransaction(
	const FFinareReloadTransactionResult& Transaction,
	const FString& SourcePath)
{
	FFinareEditorDiagnostic Diagnostic;
	Diagnostic.Domain = TEXT("reload");
	Diagnostic.Stage = TEXT("hot-reload");
	Diagnostic.Code = Transaction.State == TEXT("restartRequired")
		                  ? TEXT("USRELOAD_RESTART_REQUIRED")
		                  : TEXT("USRELOAD_FAILED");
	Diagnostic.Severity = TEXT("error");
	Diagnostic.Summary = !Transaction.Failure.IsEmpty()
		                     ? Transaction.Failure
		                     : Transaction.ToDisplayString();
	Diagnostic.Detail = Transaction.ToDisplayString();
	Diagnostic.SourcePath = SourcePath;
	Diagnostic.Line = SourcePath.IsEmpty() ? 0 : 1;
	Diagnostic.RecoveryAction = Transaction.RequiredAction == TEXT("restartEditor") ||
	                            Transaction.State == TEXT("restartRequired")
		                            ? TEXT("restartEditor")
		                            : TEXT("retry");
	return Diagnostic;
}

FFinareEditorDiagnostic FFinareEditorDiagnostics::FromRuntime(
	const FFinareRuntimeDiagnostic& Runtime)
{
	FFinareEditorDiagnostic Diagnostic;
	Diagnostic.Domain = Runtime.Bridge.HandleError.IsEmpty()
		                    ? TEXT("runtime")
		                    : TEXT("lifetime");
	Diagnostic.Stage = Runtime.Operation;
	Diagnostic.Code = Runtime.Kind.IsEmpty() ? TEXT("USRUNTIME") : Runtime.Kind;
	Diagnostic.Severity = TEXT("error");
	Diagnostic.Summary = Runtime.Message;
	Diagnostic.Detail = Runtime.Operation;
	Diagnostic.SourcePath = Runtime.Source.FilePath;
	Diagnostic.Line = Runtime.Source.Line;
	Diagnostic.Column = Runtime.Source.Column;
	Diagnostic.Member = Runtime.Bridge.MemberPath.IsEmpty()
		                    ? Runtime.Source.MethodName
		                    : Runtime.Bridge.MemberPath;
	Diagnostic.WorldHostId = Runtime.WorldHostId;
	Diagnostic.InstanceId = Runtime.InstanceId;
	Diagnostic.Generation = Runtime.Generation;
	Diagnostic.RecoveryAction = TEXT("retry");
	return Diagnostic;
}

FFinareEditorDiagnostic FFinareEditorDiagnostics::FromSetupFailure(
	const FString& Operation,
	int32 ExitCode,
	const FString& ReportPath)
{
	FFinareEditorDiagnostic Diagnostic;
	Diagnostic.Domain = TEXT("setup");
	Diagnostic.Stage = Operation;
	Diagnostic.Code = TEXT("USSETUP_FAILED");
	Diagnostic.Severity = TEXT("error");
	Diagnostic.Summary = FString::Printf(
		TEXT("%s failed with exit code %d."), *Operation, ExitCode);
	Diagnostic.RecoveryAction = TEXT("repair");
	Diagnostic.EvidencePath = ReportPath;
	return Diagnostic;
}

TArray<FFinareEditorDiagnostic> FFinareEditorDiagnostics::ReadReport(
	const FString& ReportPath)
{
	TArray<FFinareEditorDiagnostic> Result;
	FString Json;
	if (!FFileHelper::LoadFileToString(Json, *ReportPath) || Json.Len() > 16 * 1024 * 1024)
	{
		return Result;
	}
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		return Result;
	}
	FString Schema;
	Root->TryGetStringField(TEXT("schemaVersion"), Schema);
	UE::Finare::EditorDiagnostics::Private::FReportContext Context;
	UE::Finare::EditorDiagnostics::Private::AppendReportObject(
		Root, Schema, ReportPath, Context, 0, Result);
	return Result;
}

void FFinareEditorDiagnostics::Publish(
	const FText& PageLabel,
	const TArray<FFinareEditorDiagnostic>& Diagnostics,
	bool bOpenLog)
{
	TArray<FFinareEditorDiagnostic> Valid;
	for (const FFinareEditorDiagnostic& Diagnostic : Diagnostics)
	{
		if (Diagnostic.IsValid())
		{
			Valid.Add(Diagnostic);
		}
	}
	if (Valid.IsEmpty())
	{
		return;
	}

	Register();
	FMessageLog Log(LogName);
	Log.SuppressLoggingToOutputLog(true);
	Log.NewPage(PageLabel);
	for (const FFinareEditorDiagnostic& Diagnostic : Valid)
	{
		TSharedRef<FTokenizedMessage> Message = FTokenizedMessage::Create(
			UE::Finare::EditorDiagnostics::Private::ToMessageSeverity(
				Diagnostic.Severity));
		Message->AddText(FText::FromString(FString::Printf(
			TEXT("[%s/%s] %s: %s"),
			*Diagnostic.Domain,
			Diagnostic.Stage.IsEmpty() ? TEXT("unspecified") : *Diagnostic.Stage,
			*Diagnostic.Code,
			*Diagnostic.Summary)));
		if (!Diagnostic.Member.IsEmpty())
		{
			const FString Member = Diagnostic.Member;
			Message->AddToken(FActionToken::Create(
				FText::FromString(FString::Printf(TEXT("Member: %s"), *Member)),
				LOCTEXT(
					"OpenMemberTooltip",
					"Open the matching semantic member in the Finare Authoring Surface."),
				FOnActionTokenExecuted::CreateLambda(
					[Member]()
					{
						IFinareEditorModule::Get().OpenAuthoringMember(Member);
					})));
		}
		if (!Diagnostic.GraphNode.IsEmpty() && Diagnostic.GraphNode != Diagnostic.Member)
		{
			const FString GraphNode = Diagnostic.GraphNode;
			Message->AddToken(FActionToken::Create(
				FText::FromString(FString::Printf(TEXT("Graph: %s"), *GraphNode)),
				LOCTEXT(
					"OpenGraphNodeTooltip",
					"Open and focus the matching semantic Graph node."),
				FOnActionTokenExecuted::CreateLambda(
					[GraphNode]()
					{
						IFinareEditorModule::Get().OpenAuthoringMember(GraphNode);
					})));
		}
		const FString ResolvedSource = ResolveSourcePath(Diagnostic.SourcePath);
		if (!ResolvedSource.IsEmpty())
		{
			const int32 Line = Diagnostic.Line;
			const int32 Column = Diagnostic.Column;
			Message->AddToken(FActionToken::Create(
				LOCTEXT("OpenSourceAction", "Open Source"),
				FText::FromString(FString::Printf(
					TEXT("Open %s(%d,%d)"), *ResolvedSource, Line, Column)),
				FOnActionTokenExecuted::CreateLambda(
					[ResolvedSource, Line, Column]()
					{
						OpenSource(ResolvedSource, Line, Column);
					})));
		}
		if (!Diagnostic.EvidencePath.IsEmpty())
		{
			const FString EvidencePath = Diagnostic.EvidencePath;
			Message->AddToken(FActionToken::Create(
				LOCTEXT("OpenEvidenceAction", "Open Evidence"),
				FText::FromString(EvidencePath),
				FOnActionTokenExecuted::CreateLambda(
					[EvidencePath]()
					{
						FPlatformProcess::ExploreFolder(*EvidencePath);
					})));
		}
		const FString WorldHostText = Diagnostic.WorldHostId == 0
			                              ? TEXT("-")
			                              : LexToString(Diagnostic.WorldHostId);
		const FString InstanceText = Diagnostic.InstanceId == 0
			                             ? TEXT("-")
			                             : LexToString(Diagnostic.InstanceId);
		Message->AddText(FText::FromString(FString::Printf(
			TEXT("  World: %s  Instance: %s  Generation: %d"),
			*WorldHostText,
			*InstanceText,
			Diagnostic.Generation)));
		const FFinareEditorDiagnostic RecoveryDiagnostic = Diagnostic;
		Message->AddToken(FActionToken::Create(
			FText::FromString(FString::Printf(
				TEXT("Resolve: %s"),
				*UE::Finare::EditorDiagnostics::Private::RecoveryLabel(
					Diagnostic.RecoveryAction))),
			LOCTEXT(
				"RunRecoveryTooltip",
				"Open the exact safe surface for this diagnostic's prescribed recovery."),
			FOnActionTokenExecuted::CreateLambda(
				[RecoveryDiagnostic]()
				{
					FFinareEditorDiagnostics::ExecuteRecovery(RecoveryDiagnostic);
				})));
		Message->AddText(FText::FromString(FString::Printf(
			TEXT("  Package: %s"),
			Diagnostic.PackagePath.IsEmpty() ? TEXT("-") : *Diagnostic.PackagePath)));
		const FString ClipboardText = Diagnostic.ToClipboardText();
		Message->AddToken(FActionToken::Create(
			LOCTEXT("CopyDiagnosticAction", "Copy"),
			LOCTEXT("CopyDiagnosticTooltip", "Copy this normalized diagnostic."),
			FOnActionTokenExecuted::CreateLambda(
				[ClipboardText]()
				{
					FPlatformApplicationMisc::ClipboardCopy(*ClipboardText);
				})));
		Log.AddMessage(Message);
	}
	Log.Flush();
	if (bOpenLog)
	{
		Log.Open(EMessageSeverity::Info, true);
	}

	UE::Finare::EditorDiagnostics::Private::RetainedDiagnostics.Append(Valid);
	const int32 Overflow =
		UE::Finare::EditorDiagnostics::Private::RetainedDiagnostics.Num() -
		MaximumRetainedDiagnosticCount;
	if (Overflow > 0)
	{
		UE::Finare::EditorDiagnostics::Private::RetainedDiagnostics.RemoveAt(
			0, Overflow, EAllowShrinking::No);
	}
	UE::Finare::EditorDiagnostics::Private::PersistSnapshot();
}

void FFinareEditorDiagnostics::ExecuteRecovery(
	const FFinareEditorDiagnostic& Diagnostic)
{
	if (Diagnostic.RecoveryAction == TEXT("retry"))
	{
		if (!Diagnostic.GraphNode.IsEmpty() &&
			IFinareEditorModule::Get().OpenAuthoringMember(Diagnostic.GraphNode))
		{
			return;
		}
		if (!Diagnostic.Member.IsEmpty() &&
			IFinareEditorModule::Get().OpenAuthoringMember(Diagnostic.Member))
		{
			return;
		}
		if (OpenSource(Diagnostic.SourcePath, Diagnostic.Line, Diagnostic.Column))
		{
			return;
		}
	}
	if (Diagnostic.RecoveryAction == TEXT("inspectPackage") &&
		!Diagnostic.PackagePath.IsEmpty())
	{
		const FString ResolvedPackage = ResolveSourcePath(Diagnostic.PackagePath);
		if (!ResolvedPackage.IsEmpty())
		{
			FPlatformProcess::ExploreFolder(*ResolvedPackage);
			return;
		}
	}
	IFinareEditorModule::Get().OpenProjectSetup();
}

bool FFinareEditorDiagnostics::OpenSource(
	const FString& SourcePath,
	int32 Line,
	int32 Column)
{
	const FString Resolved = ResolveSourcePath(SourcePath);
	return !Resolved.IsEmpty() && FSourceCodeNavigation::OpenSourceFile(
		Resolved, FMath::Max(1, Line), FMath::Max(1, Column));
}

FString FFinareEditorDiagnostics::ResolveSourcePath(const FString& SourcePath)
{
	if (SourcePath.IsEmpty() || SourcePath == TEXT("<unknown>"))
	{
		return FString();
	}
	TArray<FString> Candidates;
	if (FPaths::IsRelative(SourcePath))
	{
		Candidates.Add(FPaths::Combine(FPaths::ProjectDir(), SourcePath));
		Candidates.Add(FPaths::Combine(FPaths::ProjectDir(), TEXT(".."), SourcePath));
	}
	else
	{
		Candidates.Add(SourcePath);
	}
	for (FString Candidate : Candidates)
	{
		Candidate = FPaths::ConvertRelativePathToFull(Candidate);
		FPaths::NormalizeFilename(Candidate);
		if (IFileManager::Get().FileExists(*Candidate))
		{
			return Candidate;
		}
	}
	return FString();
}

FString FFinareEditorDiagnostics::GetSnapshotPath()
{
	return FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectSavedDir(),
		TEXT("Finare/EditorDiagnostics/latest.json")));
}

#undef LOCTEXT_NAMESPACE
