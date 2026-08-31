#include "UnifiedScriptEditorDiagnostics.h"

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
#include "UnifiedScriptAuthoringWorkbench.h"
#include "UEUnifiedScriptEditorModule.h"
#include "UnifiedScriptRuntimeDiagnostic.h"

#define LOCTEXT_NAMESPACE "UnifiedScriptEditorDiagnostics"

const FName FUnifiedScriptEditorDiagnostics::LogName(TEXT("UnifiedScriptDiagnostics"));

namespace UE::UnifiedScript::EditorDiagnostics::Private
{
constexpr TCHAR SnapshotSchema[] = TEXT("unifiedscript.editor-diagnostics/v0.1");
TArray<FUnifiedScriptEditorDiagnostic> RetainedDiagnostics;

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
    return TEXT("repair");
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
    int32 Depth,
    TArray<FUnifiedScriptEditorDiagnostic>& OutDiagnostics)
{
    if (!Object.IsValid() || Depth > 24 ||
        OutDiagnostics.Num() >= FUnifiedScriptEditorDiagnostics::MaximumRetainedDiagnosticCount)
    {
        return;
    }

    const FString Code = ReadString(Object, {TEXT("code"), TEXT("diagnosticCode")});
    const FString Message = ReadString(
        Object,
        {TEXT("message"), TEXT("error"), TEXT("reason"), TEXT("summary")});
    FString SourcePath = ReadString(
        Object,
        {TEXT("sourcePath"), TEXT("filePath"), TEXT("source"), TEXT("file")});
    FString Member = ReadString(
        Object,
        {TEXT("memberPath"), TEXT("memberName"), TEXT("member"), TEXT("symbol")});
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
        FUnifiedScriptEditorDiagnostic Diagnostic;
        Diagnostic.Code = Code.IsEmpty() ? TEXT("USREPORT") : Code.Left(128);
        Diagnostic.Severity = NormalizeSeverity(ReadString(Object, {TEXT("severity"), TEXT("level")}));
        Diagnostic.Summary = Message.Left(4096);
        Diagnostic.Detail = ReadString(Object, {TEXT("detail"), TEXT("description")});
        Diagnostic.SourcePath = SourcePath;
        Diagnostic.Line = Line;
        Diagnostic.Column = Column;
        Diagnostic.Member = Member.Left(512);
        Diagnostic.Domain = Classify(Diagnostic.Code, Schema, Diagnostic.Summary);
        Diagnostic.RecoveryAction = RecoveryForDomain(Diagnostic.Domain);
        Diagnostic.EvidencePath = ReportPath;

        const bool bDuplicate = OutDiagnostics.ContainsByPredicate(
            [&Diagnostic](const FUnifiedScriptEditorDiagnostic& Existing)
            {
                return Existing.Code == Diagnostic.Code &&
                    Existing.Summary == Diagnostic.Summary &&
                    Existing.SourcePath == Diagnostic.SourcePath &&
                    Existing.Line == Diagnostic.Line &&
                    Existing.Member == Diagnostic.Member;
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
                Field.Value->AsObject(), Schema, ReportPath, Depth + 1, OutDiagnostics);
        }
        else if (Field.Value->Type == EJson::Array)
        {
            for (const TSharedPtr<FJsonValue>& Item : Field.Value->AsArray())
            {
                if (Item.IsValid() && Item->Type == EJson::Object)
                {
                    AppendReportObject(
                        Item->AsObject(), Schema, ReportPath, Depth + 1, OutDiagnostics);
                }
            }
        }
    }
}

void PersistSnapshot()
{
    const FString Path = FUnifiedScriptEditorDiagnostics::GetSnapshotPath();
    IFileManager::Get().MakeDirectory(*FPaths::GetPath(Path), true);
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("schemaVersion"), SnapshotSchema);
    Root->SetNumberField(TEXT("retainedCount"), RetainedDiagnostics.Num());
    TArray<TSharedPtr<FJsonValue>> Values;
    Values.Reserve(RetainedDiagnostics.Num());
    for (int32 Index = 0; Index < RetainedDiagnostics.Num(); ++Index)
    {
        const FUnifiedScriptEditorDiagnostic& Diagnostic = RetainedDiagnostics[Index];
        const TSharedRef<FJsonObject> Json = MakeShared<FJsonObject>();
        Json->SetNumberField(TEXT("sequence"), Index + 1);
        Json->SetStringField(TEXT("domain"), Diagnostic.Domain);
        Json->SetStringField(TEXT("code"), Diagnostic.Code);
        Json->SetStringField(TEXT("severity"), Diagnostic.Severity);
        Json->SetStringField(TEXT("message"), Diagnostic.Summary.Left(4096));
        Json->SetStringField(TEXT("detail"), Diagnostic.Detail.Left(4096));
        Json->SetStringField(TEXT("sourcePath"), Diagnostic.SourcePath);
        Json->SetNumberField(TEXT("line"), Diagnostic.Line);
        Json->SetNumberField(TEXT("column"), Diagnostic.Column);
        Json->SetStringField(TEXT("member"), Diagnostic.Member);
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

FString FUnifiedScriptEditorDiagnostic::ToClipboardText() const
{
    FString Result = FString::Printf(
        TEXT("[%s] %s: %s\nRecovery: %s"),
        *Domain,
        *Code,
        *Summary,
        *UE::UnifiedScript::EditorDiagnostics::Private::RecoveryLabel(RecoveryAction));
    if (!Member.IsEmpty())
    {
        Result += FString::Printf(TEXT("\nMember: %s"), *Member);
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

void FUnifiedScriptEditorDiagnostics::Register()
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
        Module.RegisterLogListing(LogName, LOCTEXT("LogLabel", "UnifiedScript Diagnostics"), Options);
    }
}

void FUnifiedScriptEditorDiagnostics::Unregister()
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

TArray<FUnifiedScriptEditorDiagnostic> FUnifiedScriptEditorDiagnostics::FromAuthoringFailure(
    const FUnifiedScriptAuthoringErrorResult& Failure)
{
    TArray<FUnifiedScriptEditorDiagnostic> Result;
    for (const FUnifiedScriptAuthoringDiagnostic& Source : Failure.Diagnostics)
    {
        FUnifiedScriptEditorDiagnostic Diagnostic;
        Diagnostic.Code = Source.Code;
        Diagnostic.Severity = UE::UnifiedScript::EditorDiagnostics::Private::NormalizeSeverity(
            Source.Severity);
        Diagnostic.Summary = Source.Message;
        Diagnostic.SourcePath = Source.Span.FilePath == TEXT("<unknown>")
            ? FString()
            : Source.Span.FilePath;
        Diagnostic.Line = Source.Span.StartLine;
        Diagnostic.Column = Source.Span.StartColumn;
        Diagnostic.Member = Source.SemanticId;
        Diagnostic.Domain = UE::UnifiedScript::EditorDiagnostics::Private::Classify(
            Source.Code, TEXT("authoring"), Source.Message);
        Diagnostic.RecoveryAction = UE::UnifiedScript::EditorDiagnostics::Private::RecoveryForDomain(
            Diagnostic.Domain);
        Result.Add(MoveTemp(Diagnostic));
    }
    if (Result.IsEmpty())
    {
        FUnifiedScriptEditorDiagnostic Diagnostic;
        Diagnostic.Domain = UE::UnifiedScript::EditorDiagnostics::Private::Classify(
            Failure.Code, TEXT("authoring"), Failure.Message);
        Diagnostic.Code = Failure.Code.IsEmpty() ? TEXT("USAUTHORING") : Failure.Code;
        Diagnostic.Severity = TEXT("error");
        Diagnostic.Summary = Failure.Message;
        Diagnostic.RecoveryAction = UE::UnifiedScript::EditorDiagnostics::Private::RecoveryForDomain(
            Diagnostic.Domain);
        Result.Add(MoveTemp(Diagnostic));
    }
    if (Failure.ReloadTransaction.IsValid())
    {
        Result.Add(FromReloadTransaction(Failure.ReloadTransaction, FString()));
    }
    return Result;
}

FUnifiedScriptEditorDiagnostic FUnifiedScriptEditorDiagnostics::FromReloadTransaction(
    const FUnifiedScriptReloadTransactionResult& Transaction,
    const FString& SourcePath)
{
    FUnifiedScriptEditorDiagnostic Diagnostic;
    Diagnostic.Domain = TEXT("reload");
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

FUnifiedScriptEditorDiagnostic FUnifiedScriptEditorDiagnostics::FromRuntime(
    const FUnifiedScriptRuntimeDiagnostic& Runtime)
{
    FUnifiedScriptEditorDiagnostic Diagnostic;
    Diagnostic.Domain = TEXT("runtime");
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
    Diagnostic.RecoveryAction = TEXT("retry");
    return Diagnostic;
}

FUnifiedScriptEditorDiagnostic FUnifiedScriptEditorDiagnostics::FromSetupFailure(
    const FString& Operation,
    int32 ExitCode,
    const FString& ReportPath)
{
    FUnifiedScriptEditorDiagnostic Diagnostic;
    Diagnostic.Domain = TEXT("setup");
    Diagnostic.Code = TEXT("USSETUP_FAILED");
    Diagnostic.Severity = TEXT("error");
    Diagnostic.Summary = FString::Printf(
        TEXT("%s failed with exit code %d."), *Operation, ExitCode);
    Diagnostic.RecoveryAction = TEXT("repair");
    Diagnostic.EvidencePath = ReportPath;
    return Diagnostic;
}

TArray<FUnifiedScriptEditorDiagnostic> FUnifiedScriptEditorDiagnostics::ReadReport(
    const FString& ReportPath)
{
    TArray<FUnifiedScriptEditorDiagnostic> Result;
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
    UE::UnifiedScript::EditorDiagnostics::Private::AppendReportObject(
        Root, Schema, ReportPath, 0, Result);
    return Result;
}

void FUnifiedScriptEditorDiagnostics::Publish(
    const FText& PageLabel,
    const TArray<FUnifiedScriptEditorDiagnostic>& Diagnostics,
    bool bOpenLog)
{
    TArray<FUnifiedScriptEditorDiagnostic> Valid;
    for (const FUnifiedScriptEditorDiagnostic& Diagnostic : Diagnostics)
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
    for (const FUnifiedScriptEditorDiagnostic& Diagnostic : Valid)
    {
        TSharedRef<FTokenizedMessage> Message = FTokenizedMessage::Create(
            UE::UnifiedScript::EditorDiagnostics::Private::ToMessageSeverity(
                Diagnostic.Severity));
        Message->AddText(FText::FromString(FString::Printf(
            TEXT("[%s] %s: %s"),
            *Diagnostic.Domain,
            *Diagnostic.Code,
            *Diagnostic.Summary)));
        if (!Diagnostic.Member.IsEmpty())
        {
            const FString Member = Diagnostic.Member;
            Message->AddToken(FActionToken::Create(
                FText::FromString(FString::Printf(TEXT("Member: %s"), *Member)),
                LOCTEXT(
                    "OpenMemberTooltip",
                    "Open the matching semantic member in the UnifiedScript Authoring Surface."),
                FOnActionTokenExecuted::CreateLambda(
                    [Member]()
                    {
                        IUEUnifiedScriptEditorModule::Get().OpenAuthoringMember(Member);
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
        Message->AddText(FText::FromString(FString::Printf(
            TEXT("  Action: %s"),
            *UE::UnifiedScript::EditorDiagnostics::Private::RecoveryLabel(
                Diagnostic.RecoveryAction))));
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

    UE::UnifiedScript::EditorDiagnostics::Private::RetainedDiagnostics.Append(Valid);
    const int32 Overflow =
        UE::UnifiedScript::EditorDiagnostics::Private::RetainedDiagnostics.Num() -
        MaximumRetainedDiagnosticCount;
    if (Overflow > 0)
    {
        UE::UnifiedScript::EditorDiagnostics::Private::RetainedDiagnostics.RemoveAt(
            0, Overflow, EAllowShrinking::No);
    }
    UE::UnifiedScript::EditorDiagnostics::Private::PersistSnapshot();
}

bool FUnifiedScriptEditorDiagnostics::OpenSource(
    const FString& SourcePath,
    int32 Line,
    int32 Column)
{
    const FString Resolved = ResolveSourcePath(SourcePath);
    return !Resolved.IsEmpty() && FSourceCodeNavigation::OpenSourceFile(
        Resolved, FMath::Max(1, Line), FMath::Max(1, Column));
}

FString FUnifiedScriptEditorDiagnostics::ResolveSourcePath(const FString& SourcePath)
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

FString FUnifiedScriptEditorDiagnostics::GetSnapshotPath()
{
    return FPaths::ConvertRelativePathToFull(FPaths::Combine(
        FPaths::ProjectSavedDir(),
        TEXT("UnifiedScript/EditorDiagnostics/latest.json")));
}

#undef LOCTEXT_NAMESPACE
