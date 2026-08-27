#include "UnifiedScriptDebugWorkbench.h"

#include "Dom/JsonObject.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#include "UnifiedScriptAuthoringWorkbench.h"
#include "Misc/Paths.h"

namespace UE::UnifiedScript::DebugWorkbench::Private
{
constexpr TCHAR SessionSchema[] = TEXT("unifiedscript.debug-session/v0.2");
constexpr TCHAR PauseSchema[] = TEXT("unifiedscript.debug-pause/v0.2");
constexpr TCHAR EvaluationSchema[] = TEXT("unifiedscript.debug-evaluation/v0.1");
constexpr TCHAR LiveWorldPublishSchema[] =
    TEXT("unifiedscript.live-world-debug-publish/v0.1");

FString BuildUrl(const FString& ServerUrl, const TCHAR* Path)
{
    const FString Normalized = FUnifiedScriptAuthoringWorkbench::NormalizeServerUrl(ServerUrl);
    return Normalized.IsEmpty() ? FString() : Normalized + Path;
}

bool ReadRequiredString(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    FString& OutValue,
    FString& OutError)
{
    if (!Object.IsValid() || !Object->TryGetStringField(Field, OutValue) || OutValue.IsEmpty())
    {
        OutError = FString::Printf(TEXT("Debug response field '%s' must be a non-empty string."), Field);
        return false;
    }
    return true;
}

bool ReadInteger(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    int64& OutValue,
    FString& OutError,
    bool bPositive)
{
    double Number = 0.0;
    if (!Object.IsValid() || !Object->TryGetNumberField(Field, Number) ||
        Number != FMath::FloorToDouble(Number) || (bPositive ? Number < 1.0 : Number < 0.0))
    {
        OutError = FString::Printf(
            TEXT("Debug response field '%s' must be a %s integer."),
            Field,
            bPositive ? TEXT("positive") : TEXT("non-negative"));
        return false;
    }
    OutValue = static_cast<int64>(Number);
    return true;
}

bool ReadSignedInteger(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    int64& OutValue,
    FString& OutError)
{
    double Number = 0.0;
    if (!Object.IsValid() || !Object->TryGetNumberField(Field, Number) ||
        Number != FMath::FloorToDouble(Number))
    {
        OutError = FString::Printf(
            TEXT("Debug response field '%s' must be an integer."), Field);
        return false;
    }
    OutValue = static_cast<int64>(Number);
    return true;
}

bool ReadSpan(
    const TSharedPtr<FJsonObject>& Object,
    FUnifiedScriptAuthoringSpan& OutSpan,
    FString& OutError)
{
    const TSharedPtr<FJsonObject>* Start = nullptr;
    const TSharedPtr<FJsonObject>* End = nullptr;
    double StartLine = 0.0;
    double StartColumn = 0.0;
    double EndLine = 0.0;
    double EndColumn = 0.0;
    if (!ReadRequiredString(Object, TEXT("filePath"), OutSpan.FilePath, OutError) ||
        !Object->TryGetObjectField(TEXT("start"), Start) || Start == nullptr ||
        !Object->TryGetObjectField(TEXT("end"), End) || End == nullptr ||
        !(*Start)->TryGetNumberField(TEXT("line"), StartLine) ||
        !(*Start)->TryGetNumberField(TEXT("column"), StartColumn) ||
        !(*End)->TryGetNumberField(TEXT("line"), EndLine) ||
        !(*End)->TryGetNumberField(TEXT("column"), EndColumn))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("Debug source span is incomplete.");
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
        OutError = TEXT("Debug source span is invalid.");
        return false;
    }
    return true;
}

bool ReadGeneration(
    const TSharedPtr<FJsonObject>& Object,
    FUnifiedScriptDebugGeneration& OutGeneration,
    FString& OutError)
{
    int64 Number = 0;
    if (!ReadInteger(Object, TEXT("number"), Number, OutError, true) ||
        !ReadRequiredString(Object, TEXT("generationId"), OutGeneration.GenerationId, OutError) ||
        !ReadRequiredString(
            Object, TEXT("sourceCompilationId"), OutGeneration.SourceCompilationId, OutError) ||
        !ReadRequiredString(
            Object,
            TEXT("semanticConfigurationId"),
            OutGeneration.SemanticConfigurationId,
            OutError))
    {
        return false;
    }
    OutGeneration.Number = Number;
    Object->TryGetStringField(TEXT("domainId"), OutGeneration.DomainId);
    Object->TryGetStringField(TEXT("imageKey"), OutGeneration.ImageKey);
    return true;
}

bool ReadDebugValue(
    const TSharedPtr<FJsonObject>& Object,
    FUnifiedScriptDebugValue& OutValue,
    FString& OutError,
    int32 Depth = 0)
{
    if (Depth > 4 ||
        !ReadRequiredString(Object, TEXT("name"), OutValue.Name, OutError) ||
        !ReadRequiredString(Object, TEXT("typeMetadataName"), OutValue.TypeMetadataName, OutError) ||
        !Object->TryGetStringField(TEXT("display"), OutValue.Display) ||
        !ReadRequiredString(Object, TEXT("kind"), OutValue.Kind, OutError) ||
        !Object->TryGetBoolField(TEXT("truncated"), OutValue.bTruncated))
    {
        if (OutError.IsEmpty())
        {
            OutError = Depth > 4
                ? TEXT("Debug value exceeds the native snapshot depth limit.")
                : TEXT("Debug value is incomplete.");
        }
        return false;
    }
    const TArray<TSharedPtr<FJsonValue>>* Children = nullptr;
    if (!Object->TryGetArrayField(TEXT("children"), Children) || Children == nullptr ||
        Children->Num() > 32)
    {
        OutError = TEXT("Debug value children are missing or exceed the native bound.");
        return false;
    }
    for (const TSharedPtr<FJsonValue>& ChildValue : *Children)
    {
        FUnifiedScriptDebugValue Child;
        if (!ReadDebugValue(
                ChildValue.IsValid() ? ChildValue->AsObject() : nullptr,
                Child,
                OutError,
                Depth + 1))
        {
            return false;
        }
        OutValue.Children.Add(MoveTemp(Child));
    }
    return true;
}

bool SerializeObject(
    const TSharedRef<FJsonObject>& Object,
    FString& OutJson,
    FString& OutError)
{
    const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&OutJson);
    if (!FJsonSerializer::Serialize(Object, Writer))
    {
        OutError = TEXT("Could not serialize the debug request.");
        OutJson.Reset();
        return false;
    }
    OutError.Reset();
    return true;
}

TSharedPtr<FJsonValue> MakeDefaultArgument(const FString& TypeMetadataName)
{
    if (TypeMetadataName == TEXT("System.Boolean"))
    {
        return MakeShared<FJsonValueBoolean>(true);
    }
    if (TypeMetadataName == TEXT("System.String"))
    {
        return MakeShared<FJsonValueString>(FString());
    }
    if (TypeMetadataName.EndsWith(TEXT("[]")))
    {
        return MakeShared<FJsonValueArray>(TArray<TSharedPtr<FJsonValue>>());
    }
    if (TypeMetadataName == TEXT("System.Char"))
    {
        return MakeShared<FJsonValueString>(TEXT("A"));
    }
    if (TypeMetadataName.StartsWith(TEXT("System.Int")) ||
        TypeMetadataName.StartsWith(TEXT("System.UInt")) ||
        TypeMetadataName == TEXT("System.Byte") || TypeMetadataName == TEXT("System.SByte") ||
        TypeMetadataName == TEXT("System.Single") || TypeMetadataName == TEXT("System.Double") ||
        TypeMetadataName == TEXT("System.Decimal"))
    {
        return MakeShared<FJsonValueNumber>(1.0);
    }
    return MakeShared<FJsonValueNull>();
}
}

FString FUnifiedScriptDebugWorkbench::BuildStartUrl(const FString& ServerUrl)
{
    return UE::UnifiedScript::DebugWorkbench::Private::BuildUrl(
        ServerUrl, TEXT("/api/debug/start"));
}

FString FUnifiedScriptDebugWorkbench::BuildWaitUrl(const FString& ServerUrl)
{
    return UE::UnifiedScript::DebugWorkbench::Private::BuildUrl(
        ServerUrl, TEXT("/api/debug/wait"));
}

FString FUnifiedScriptDebugWorkbench::BuildPauseUrl(const FString& ServerUrl)
{
    return UE::UnifiedScript::DebugWorkbench::Private::BuildUrl(
        ServerUrl, TEXT("/api/debug/pause"));
}

FString FUnifiedScriptDebugWorkbench::BuildContinueUrl(const FString& ServerUrl)
{
    return UE::UnifiedScript::DebugWorkbench::Private::BuildUrl(
        ServerUrl, TEXT("/api/debug/continue"));
}

FString FUnifiedScriptDebugWorkbench::BuildStepInUrl(const FString& ServerUrl)
{
    return UE::UnifiedScript::DebugWorkbench::Private::BuildUrl(
        ServerUrl, TEXT("/api/debug/step-in"));
}

FString FUnifiedScriptDebugWorkbench::BuildStepOverUrl(const FString& ServerUrl)
{
    return UE::UnifiedScript::DebugWorkbench::Private::BuildUrl(
        ServerUrl, TEXT("/api/debug/step-over"));
}

FString FUnifiedScriptDebugWorkbench::BuildStepOutUrl(const FString& ServerUrl)
{
    return UE::UnifiedScript::DebugWorkbench::Private::BuildUrl(
        ServerUrl, TEXT("/api/debug/step-out"));
}

FString FUnifiedScriptDebugWorkbench::BuildEvaluateUrl(const FString& ServerUrl)
{
    return UE::UnifiedScript::DebugWorkbench::Private::BuildUrl(
        ServerUrl, TEXT("/api/debug/evaluate"));
}

FString FUnifiedScriptDebugWorkbench::BuildLiveWorldPublishUrl(const FString& ServerUrl)
{
    return UE::UnifiedScript::DebugWorkbench::Private::BuildUrl(
        ServerUrl, TEXT("/api/debug/publish-live-world"));
}

bool FUnifiedScriptDebugWorkbench::BuildStartRequest(
    const FString& SourceCompilationId,
    const FString& SemanticConfigurationId,
    const FUnifiedScriptAuthoringFunction& Function,
    const FUnifiedScriptAuthoringAsset& Asset,
    const TSet<FString>& BreakpointNodeIds,
    FString& OutJson,
    FString& OutError)
{
    if (SourceCompilationId.IsEmpty() || SemanticConfigurationId.IsEmpty() ||
        Function.Id.IsEmpty() || (!Function.bStatic && Asset.Id.IsEmpty()))
    {
        OutError = TEXT("A debug start requires the current method and receiver asset identity.");
        return false;
    }
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("methodId"), Function.Id);
    Root->SetStringField(
        TEXT("expectedSourceCompilationId"), SourceCompilationId);
    Root->SetStringField(
        TEXT("expectedSemanticConfigurationId"), SemanticConfigurationId);
    TArray<TSharedPtr<FJsonValue>> Arguments;
    for (const FUnifiedScriptAuthoringParameter& Parameter : Function.Parameters)
    {
        Arguments.Add(UE::UnifiedScript::DebugWorkbench::Private::MakeDefaultArgument(
            Parameter.TypeMetadataName));
    }
    Root->SetArrayField(TEXT("arguments"), MoveTemp(Arguments));
    if (!Function.bStatic)
    {
        const TSharedRef<FJsonObject> Receiver = MakeShared<FJsonObject>();
        Receiver->SetStringField(TEXT("assetId"), Asset.Id);
        Receiver->SetArrayField(
            TEXT("stateOverrides"), TArray<TSharedPtr<FJsonValue>>());
        Root->SetObjectField(TEXT("receiver"), Receiver);
    }
    TArray<FString> SortedBreakpoints = BreakpointNodeIds.Array();
    SortedBreakpoints.Sort();
    TArray<TSharedPtr<FJsonValue>> Breakpoints;
    for (const FString& Breakpoint : SortedBreakpoints)
    {
        if (!Breakpoint.IsEmpty())
        {
            Breakpoints.Add(MakeShared<FJsonValueString>(Breakpoint));
        }
    }
    Root->SetArrayField(TEXT("breakpointNodeIds"), MoveTemp(Breakpoints));
    return UE::UnifiedScript::DebugWorkbench::Private::SerializeObject(
        Root, OutJson, OutError);
}

bool FUnifiedScriptDebugWorkbench::BuildExecutionRequest(
    const FUnifiedScriptDebugSession& Session,
    FString& OutJson,
    FString& OutError)
{
    if (Session.Generation.GenerationId.IsEmpty() || Session.ExecutionId < 1)
    {
        OutError = TEXT("A debug execution request requires the current generation and execution.");
        return false;
    }
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("generationId"), Session.Generation.GenerationId);
    Root->SetNumberField(TEXT("executionId"), Session.ExecutionId);
    return UE::UnifiedScript::DebugWorkbench::Private::SerializeObject(
        Root, OutJson, OutError);
}

bool FUnifiedScriptDebugWorkbench::BuildResumeRequest(
    const FUnifiedScriptDebugSession& Session,
    FString& OutJson,
    FString& OutError)
{
    if (!Session.IsPaused() || !Session.Pause.IsSet() || Session.Pause->PauseId.IsEmpty())
    {
        OutError = TEXT("A debug resume requires the exact current pause.");
        return false;
    }
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("generationId"), Session.Generation.GenerationId);
    Root->SetNumberField(TEXT("executionId"), Session.ExecutionId);
    Root->SetStringField(TEXT("pauseId"), Session.Pause->PauseId);
    return UE::UnifiedScript::DebugWorkbench::Private::SerializeObject(
        Root, OutJson, OutError);
}

bool FUnifiedScriptDebugWorkbench::BuildEvaluateRequest(
    const FUnifiedScriptDebugSession& Session,
    int64 FrameId,
    const FString& Expression,
    FString& OutJson,
    FString& OutError)
{
    const FString TrimmedExpression = Expression.TrimStartAndEnd();
    if (!Session.IsPaused() || !Session.Pause.IsSet() || FrameId < 1 ||
        TrimmedExpression.IsEmpty() || TrimmedExpression.Len() > 256)
    {
        OutError = TEXT("Watch evaluation requires the exact pause/frame and 1-256 characters.");
        return false;
    }
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("generationId"), Session.Generation.GenerationId);
    Root->SetNumberField(TEXT("executionId"), Session.ExecutionId);
    Root->SetStringField(TEXT("pauseId"), Session.Pause->PauseId);
    Root->SetNumberField(TEXT("frameId"), FrameId);
    Root->SetStringField(TEXT("expression"), TrimmedExpression);
    return UE::UnifiedScript::DebugWorkbench::Private::SerializeObject(
        Root, OutJson, OutError);
}

bool FUnifiedScriptDebugWorkbench::BuildLiveWorldPublishRequest(
    const FString& SourceCompilationId,
    const FString& SemanticConfigurationId,
    FString& OutJson,
    FString& OutError)
{
    if (SourceCompilationId.IsEmpty() || SemanticConfigurationId.IsEmpty())
    {
        OutError = TEXT("Live World debug publish requires current compiler identities.");
        return false;
    }
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("expectedSourceCompilationId"), SourceCompilationId);
    Root->SetStringField(
        TEXT("expectedSemanticConfigurationId"), SemanticConfigurationId);
    return UE::UnifiedScript::DebugWorkbench::Private::SerializeObject(
        Root, OutJson, OutError);
}

bool FUnifiedScriptDebugWorkbench::ParseLiveWorldPublishResponse(
    const FString& Json,
    FUnifiedScriptLiveWorldDebugPublish& OutPublish,
    FString& OutError)
{
    using namespace UE::UnifiedScript::DebugWorkbench::Private;
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    const TSharedPtr<FJsonObject>* Generation = nullptr;
    const TSharedPtr<FJsonObject>* ReloadTransaction = nullptr;
    FString Schema;
    FUnifiedScriptLiveWorldDebugPublish Candidate;
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid() ||
        !Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
        Schema != LiveWorldPublishSchema ||
        !Root->TryGetObjectField(TEXT("generation"), Generation) || Generation == nullptr ||
        !ReadGeneration(*Generation, Candidate.Generation, OutError) ||
        Candidate.Generation.DomainId.IsEmpty() || Candidate.Generation.ImageKey.IsEmpty() ||
        !ReadRequiredString(Root, TEXT("assemblyPath"), Candidate.AssemblyPath, OutError) ||
        !ReadRequiredString(Root, TEXT("portablePdbPath"), Candidate.PortablePdbPath, OutError) ||
        !Root->TryGetObjectField(TEXT("reloadTransaction"), ReloadTransaction) ||
        ReloadTransaction == nullptr)
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("The Workbench returned an invalid live World debug publish response.");
        }
        return false;
    }
    FString ReloadSchema;
    FString ReloadTransactionId;
    const TArray<TSharedPtr<FJsonValue>>* ReloadPhases = nullptr;
    if (!(*ReloadTransaction)->TryGetStringField(TEXT("schemaVersion"), ReloadSchema) ||
        ReloadSchema != TEXT("unifiedscript.reload-transaction/v0.1") ||
        !(*ReloadTransaction)->TryGetStringField(TEXT("transactionId"), ReloadTransactionId) ||
        ReloadTransactionId.IsEmpty() ||
        !(*ReloadTransaction)->TryGetArrayField(TEXT("phases"), ReloadPhases) ||
        ReloadPhases == nullptr)
    {
        OutError = TEXT("The live World publication has no valid reload transaction.");
        return false;
    }
    const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> ReloadWriter =
        TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(
            &Candidate.ReloadTransactionJson);
    if (!FJsonSerializer::Serialize(ReloadTransaction->ToSharedRef(), ReloadWriter))
    {
        OutError = TEXT("The live World reload transaction could not be decoded.");
        return false;
    }
    Candidate.AssemblyPath = FPaths::ConvertRelativePathToFull(Candidate.AssemblyPath);
    Candidate.PortablePdbPath = FPaths::ConvertRelativePathToFull(Candidate.PortablePdbPath);
    OutPublish = MoveTemp(Candidate);
    OutError.Reset();
    return true;
}

bool FUnifiedScriptDebugWorkbench::BuildLiveWorldConfiguration(
    const FUnifiedScriptLiveWorldDebugPublish& Publish,
    int32 RuntimeGeneration,
    const TSet<FString>& BreakpointNodeIds,
    FString& OutJson,
    FString& OutError)
{
    const FUnifiedScriptDebugGeneration& Generation = Publish.Generation;
    if (RuntimeGeneration < 1 || Generation.Number < 1 ||
        Generation.DomainId.IsEmpty() || Generation.GenerationId.IsEmpty() ||
        Generation.ImageKey.IsEmpty() || Generation.SourceCompilationId.IsEmpty() ||
        Generation.SemanticConfigurationId.IsEmpty() || BreakpointNodeIds.Num() > 4096)
    {
        OutError = TEXT("A live World debug configuration requires exact runtime/compiler identities.");
        return false;
    }

    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(
        TEXT("schemaVersion"), TEXT("unifiedscript.unreal-debug-config/v0.1"));
    Root->SetNumberField(TEXT("runtimeGeneration"), RuntimeGeneration);
    const TSharedRef<FJsonObject> GenerationJson = MakeShared<FJsonObject>();
    GenerationJson->SetStringField(TEXT("domainId"), Generation.DomainId);
    GenerationJson->SetNumberField(TEXT("number"), Generation.Number);
    GenerationJson->SetStringField(TEXT("generationId"), Generation.GenerationId);
    GenerationJson->SetStringField(TEXT("imageKey"), Generation.ImageKey);
    GenerationJson->SetStringField(
        TEXT("sourceCompilationId"), Generation.SourceCompilationId);
    GenerationJson->SetStringField(
        TEXT("semanticConfigurationId"), Generation.SemanticConfigurationId);
    Root->SetObjectField(TEXT("generation"), GenerationJson);
    TArray<FString> SortedBreakpoints = BreakpointNodeIds.Array();
    SortedBreakpoints.Sort();
    TArray<TSharedPtr<FJsonValue>> Breakpoints;
    for (const FString& NodeId : SortedBreakpoints)
    {
        if (NodeId.IsEmpty())
        {
            OutError = TEXT("A live World breakpoint node ID cannot be empty.");
            return false;
        }
        Breakpoints.Add(MakeShared<FJsonValueString>(NodeId));
    }
    Root->SetArrayField(TEXT("breakpointNodeIds"), MoveTemp(Breakpoints));
    return UE::UnifiedScript::DebugWorkbench::Private::SerializeObject(
        Root, OutJson, OutError);
}

bool FUnifiedScriptDebugWorkbench::ParseSessionResponse(
    const FString& Json,
    FUnifiedScriptDebugSession& OutSession,
    FString& OutError)
{
    using namespace UE::UnifiedScript::DebugWorkbench::Private;
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = TEXT("The Workbench returned invalid debug-session JSON.");
        return false;
    }
    FString Schema;
    FString State;
    const TSharedPtr<FJsonObject>* Generation = nullptr;
    FUnifiedScriptDebugSession Candidate;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), Schema) || Schema != SessionSchema ||
        !ReadRequiredString(Root, TEXT("state"), State, OutError) ||
        !Root->TryGetObjectField(TEXT("generation"), Generation) || Generation == nullptr ||
        !ReadGeneration(*Generation, Candidate.Generation, OutError) ||
        !ReadInteger(Root, TEXT("executionId"), Candidate.ExecutionId, OutError, true))
    {
        if (OutError.IsEmpty())
        {
            OutError = FString::Printf(TEXT("Unsupported debug-session schema '%s'."), *Schema);
        }
        return false;
    }
    if (State == TEXT("running"))
    {
        Candidate.State = EUnifiedScriptDebugSessionState::Running;
    }
    else if (State == TEXT("paused"))
    {
        Candidate.State = EUnifiedScriptDebugSessionState::Paused;
    }
    else if (State == TEXT("completed"))
    {
        Candidate.State = EUnifiedScriptDebugSessionState::Completed;
    }
    else
    {
        OutError = FString::Printf(TEXT("Unsupported debug-session state '%s'."), *State);
        return false;
    }

    const TArray<TSharedPtr<FJsonValue>>* Breakpoints = nullptr;
    if (!Root->TryGetArrayField(TEXT("breakpoints"), Breakpoints) || Breakpoints == nullptr)
    {
        OutError = TEXT("Debug session requires a breakpoint binding array.");
        return false;
    }
    for (const TSharedPtr<FJsonValue>& Value : *Breakpoints)
    {
        const TSharedPtr<FJsonObject> Binding = Value.IsValid() ? Value->AsObject() : nullptr;
        FUnifiedScriptDebugBreakpointBinding CandidateBinding;
        if (!ReadRequiredString(Binding, TEXT("breakpointId"), CandidateBinding.BreakpointId, OutError) ||
            !ReadRequiredString(Binding, TEXT("nodeId"), CandidateBinding.NodeId, OutError) ||
            !ReadRequiredString(Binding, TEXT("state"), CandidateBinding.State, OutError))
        {
            return false;
        }
        Binding->TryGetStringField(TEXT("methodId"), CandidateBinding.MethodId);
        Binding->TryGetStringField(TEXT("message"), CandidateBinding.Message);
        Candidate.Breakpoints.Add(MoveTemp(CandidateBinding));
    }

    if (Candidate.IsPaused())
    {
        const TSharedPtr<FJsonObject>* Pause = nullptr;
        FString PauseSchemaValue;
        FUnifiedScriptDebugPause CandidatePause;
        if (!Root->TryGetObjectField(TEXT("pause"), Pause) || Pause == nullptr ||
            !(*Pause)->TryGetStringField(TEXT("schemaVersion"), PauseSchemaValue) ||
            PauseSchemaValue != PauseSchema ||
            !ReadRequiredString(*Pause, TEXT("pauseId"), CandidatePause.PauseId, OutError) ||
            !ReadRequiredString(*Pause, TEXT("reason"), CandidatePause.Reason, OutError) ||
            !ReadRequiredString(*Pause, TEXT("methodId"), CandidatePause.MethodId, OutError) ||
            !ReadRequiredString(*Pause, TEXT("nodeId"), CandidatePause.NodeId, OutError))
        {
            if (OutError.IsEmpty())
            {
                OutError = TEXT("Paused debug session has no valid pause identity.");
            }
            return false;
        }
        (*Pause)->TryGetStringField(TEXT("stepKind"), CandidatePause.StepKind);
        Candidate.Pause = MoveTemp(CandidatePause);
    }

    const TArray<TSharedPtr<FJsonValue>>* Frames = nullptr;
    const TArray<TSharedPtr<FJsonValue>>* FrameLocals = nullptr;
    if (!Root->TryGetArrayField(TEXT("frames"), Frames) || Frames == nullptr ||
        !Root->TryGetArrayField(TEXT("frameLocals"), FrameLocals) || FrameLocals == nullptr)
    {
        OutError = TEXT("Debug session requires frame and frame-locals arrays.");
        return false;
    }
    for (const TSharedPtr<FJsonValue>& Value : *Frames)
    {
        const TSharedPtr<FJsonObject> Frame = Value.IsValid() ? Value->AsObject() : nullptr;
        FUnifiedScriptDebugStackFrame CandidateFrame;
        int64 FrameId = 0;
        int64 Index = 0;
        int64 MetadataToken = 0;
        int64 IlOffset = 0;
        if (!ReadInteger(Frame, TEXT("frameId"), FrameId, OutError, true) ||
            !ReadInteger(Frame, TEXT("index"), Index, OutError, false) ||
            !ReadRequiredString(
                Frame, TEXT("methodMetadataName"), CandidateFrame.MethodMetadataName, OutError) ||
            !ReadInteger(Frame, TEXT("metadataToken"), MetadataToken, OutError, false) ||
            !ReadSignedInteger(Frame, TEXT("ilOffset"), IlOffset, OutError) ||
            !Frame->TryGetBoolField(TEXT("hasLocals"), CandidateFrame.bHasLocals))
        {
            return false;
        }
        CandidateFrame.FrameId = FrameId;
        CandidateFrame.Index = static_cast<int32>(Index);
        CandidateFrame.MetadataToken = static_cast<int32>(MetadataToken);
        CandidateFrame.IlOffset = static_cast<int32>(IlOffset);
        Frame->TryGetStringField(TEXT("methodId"), CandidateFrame.MethodId);
        Frame->TryGetStringField(TEXT("nodeId"), CandidateFrame.NodeId);
        const TSharedPtr<FJsonObject>* Span = nullptr;
        if (Frame->TryGetObjectField(TEXT("sourceSpan"), Span) && Span != nullptr)
        {
            FUnifiedScriptAuthoringSpan CandidateSpan;
            if (!ReadSpan(*Span, CandidateSpan, OutError))
            {
                return false;
            }
            CandidateFrame.SourceSpan = MoveTemp(CandidateSpan);
        }
        Candidate.Frames.Add(MoveTemp(CandidateFrame));
    }
    for (const TSharedPtr<FJsonValue>& Value : *FrameLocals)
    {
        const TSharedPtr<FJsonObject> LocalsObject = Value.IsValid() ? Value->AsObject() : nullptr;
        FUnifiedScriptDebugFrameLocals CandidateLocals;
        const TArray<TSharedPtr<FJsonValue>>* Locals = nullptr;
        if (!ReadInteger(LocalsObject, TEXT("frameId"), CandidateLocals.FrameId, OutError, true) ||
            !LocalsObject->TryGetArrayField(TEXT("locals"), Locals) || Locals == nullptr)
        {
            if (OutError.IsEmpty())
            {
                OutError = TEXT("Debug frame locals are incomplete.");
            }
            return false;
        }
        for (const TSharedPtr<FJsonValue>& LocalValue : *Locals)
        {
            FUnifiedScriptDebugValue Local;
            if (!ReadDebugValue(
                    LocalValue.IsValid() ? LocalValue->AsObject() : nullptr,
                    Local,
                    OutError))
            {
                return false;
            }
            CandidateLocals.Locals.Add(MoveTemp(Local));
        }
        Candidate.FrameLocals.Add(MoveTemp(CandidateLocals));
    }

    if (Candidate.IsPaused() && Candidate.Frames.IsEmpty())
    {
        OutError = TEXT("Paused debug session must contain at least one stack frame.");
        return false;
    }
    if (Candidate.IsCompleted())
    {
        const TSharedPtr<FJsonObject>* Result = nullptr;
        FUnifiedScriptDebugResult CandidateResult;
        if (!Root->TryGetObjectField(TEXT("result"), Result) || Result == nullptr ||
            !ReadRequiredString(*Result, TEXT("outcome"), CandidateResult.Outcome, OutError))
        {
            return false;
        }
        const TSharedPtr<FJsonObject>* ReturnValue = nullptr;
        if ((*Result)->TryGetObjectField(TEXT("returnValue"), ReturnValue) && ReturnValue != nullptr)
        {
            if (!ReadRequiredString(
                    *ReturnValue,
                    TEXT("typeMetadataName"),
                    CandidateResult.ReturnTypeMetadataName,
                    OutError) ||
                !(*ReturnValue)->TryGetStringField(TEXT("json"), CandidateResult.ReturnJson))
            {
                return false;
            }
        }
        const TSharedPtr<FJsonObject>* Fault = nullptr;
        if ((*Result)->TryGetObjectField(TEXT("fault"), Fault) && Fault != nullptr)
        {
            if (!ReadRequiredString(
                    *Fault,
                    TEXT("typeMetadataName"),
                    CandidateResult.FaultTypeMetadataName,
                    OutError) ||
                !ReadRequiredString(*Fault, TEXT("message"), CandidateResult.FaultMessage, OutError))
            {
                return false;
            }
        }
        Candidate.Result = MoveTemp(CandidateResult);
    }

    OutSession = MoveTemp(Candidate);
    OutError.Reset();
    return true;
}

bool FUnifiedScriptDebugWorkbench::ParseEvaluationResponse(
    const FString& Json,
    FUnifiedScriptDebugEvaluation& OutEvaluation,
    FString& OutError)
{
    using namespace UE::UnifiedScript::DebugWorkbench::Private;
    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = TEXT("The Workbench returned invalid debug-evaluation JSON.");
        return false;
    }
    FString Schema;
    const TSharedPtr<FJsonObject>* Generation = nullptr;
    const TSharedPtr<FJsonObject>* Value = nullptr;
    FUnifiedScriptDebugEvaluation Candidate;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), Schema) || Schema != EvaluationSchema ||
        !Root->TryGetObjectField(TEXT("generation"), Generation) || Generation == nullptr ||
        !ReadGeneration(*Generation, Candidate.Generation, OutError) ||
        !ReadRequiredString(Root, TEXT("pauseId"), Candidate.PauseId, OutError) ||
        !ReadInteger(Root, TEXT("frameId"), Candidate.FrameId, OutError, true) ||
        !ReadRequiredString(Root, TEXT("expression"), Candidate.Expression, OutError) ||
        !Root->TryGetObjectField(TEXT("value"), Value) || Value == nullptr ||
        !ReadDebugValue(*Value, Candidate.Value, OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = FString::Printf(TEXT("Unsupported debug-evaluation schema '%s'."), *Schema);
        }
        return false;
    }
    OutEvaluation = MoveTemp(Candidate);
    OutError.Reset();
    return true;
}

FString FUnifiedScriptGameplayProfileResult::ToDisplayString() const
{
    const auto CountBoundary = [this](const TCHAR* Name)
    {
        const FUnifiedScriptGameplayProfileBoundary* Boundary =
            Boundaries.FindByPredicate(
                [Name](const FUnifiedScriptGameplayProfileBoundary& Candidate)
                {
                    return Candidate.Name == Name;
        });
        return Boundary != nullptr ? Boundary->Count : int64{0};
    };
    const auto PhaseNanoseconds = [this](const TCHAR* Name)
    {
        const FUnifiedScriptGameplayProfileBoundary* Phase =
            WorldPhases.FindByPredicate(
                [Name](const FUnifiedScriptGameplayProfileBoundary& Candidate)
                {
                    return Candidate.Name == Name;
                });
        return Phase != nullptr ? Phase->TotalNanoseconds : int64{0};
    };
    const double WorldMilliseconds =
        (static_cast<double>(PhaseNanoseconds(TEXT("NativePreActorTick"))) +
         static_cast<double>(PhaseNanoseconds(TEXT("ManagedTick"))) +
         static_cast<double>(PhaseNanoseconds(TEXT("NativePostActorTick")))) /
        1000000.0;
    return FString::Printf(
        TEXT("%d frame%s · Script %.3f ms · World %.3f ms · %lld B · Direct %lld · CallFrame %lld · Reflection %lld · Batch %lld · Pressure %lld"),
        Frames.Num(),
        Frames.Num() == 1 ? TEXT("") : TEXT("s"),
        static_cast<double>(TotalNanoseconds) / 1000000.0,
        WorldMilliseconds,
        static_cast<long long>(AllocatedBytes),
        static_cast<long long>(CountBoundary(TEXT("directThunk"))),
        static_cast<long long>(CountBoundary(TEXT("callFrame"))),
        static_cast<long long>(CountBoundary(TEXT("reflectionFallback"))),
        static_cast<long long>(
            CountBoundary(TEXT("batchTrace")) +
            CountBoundary(TEXT("batchOverlap"))),
        static_cast<long long>(BufferPressure.TotalOverflowCount()));
}

bool FUnifiedScriptDebugWorkbench::ParseGameplayProfileTrace(
    const FString& Json,
    FUnifiedScriptGameplayProfileResult& OutProfile,
    FString& OutError)
{
    using namespace UE::UnifiedScript::DebugWorkbench::Private;
    const auto ReadBoundaryArray = [&OutError](
        const TSharedPtr<FJsonObject>& Object,
        const TCHAR* Field,
        int32 MaximumCount,
        TArray<FUnifiedScriptGameplayProfileBoundary>& OutBoundaries)
    {
        const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
        if (!Object.IsValid() || !Object->TryGetArrayField(Field, Values) ||
            Values == nullptr || Values->Num() > MaximumCount)
        {
            OutError = FString::Printf(
                TEXT("Gameplay profile field '%s' is missing or exceeds %d entries."),
                Field,
                MaximumCount);
            return false;
        }
        OutBoundaries.Reset(Values->Num());
        for (const TSharedPtr<FJsonValue>& Value : *Values)
        {
            const TSharedPtr<FJsonObject> Boundary =
                Value.IsValid() ? Value->AsObject() : nullptr;
            FUnifiedScriptGameplayProfileBoundary CandidateBoundary;
            if (!ReadRequiredString(
                    Boundary,
                    TEXT("name"),
                    CandidateBoundary.Name,
                    OutError) ||
                !ReadInteger(
                    Boundary,
                    TEXT("count"),
                    CandidateBoundary.Count,
                    OutError,
                    false) ||
                !ReadInteger(
                    Boundary,
                    TEXT("totalNanoseconds"),
                    CandidateBoundary.TotalNanoseconds,
                    OutError,
                    false))
            {
                return false;
            }
            OutBoundaries.Add(MoveTemp(CandidateBoundary));
        }
        return true;
    };
    const auto ReadNodeArray = [&OutError](
        const TSharedPtr<FJsonObject>& Object,
        const TCHAR* Field,
        TArray<FUnifiedScriptGameplayProfileNode>& OutNodes)
    {
        const TArray<TSharedPtr<FJsonValue>>* Values = nullptr;
        if (!Object.IsValid() || !Object->TryGetArrayField(Field, Values) ||
            Values == nullptr || Values->Num() > 4096)
        {
            OutError = FString::Printf(
                TEXT("Gameplay profile field '%s' is missing or exceeds 4096 entries."),
                Field);
            return false;
        }
        OutNodes.Reset(Values->Num());
        for (const TSharedPtr<FJsonValue>& Value : *Values)
        {
            const TSharedPtr<FJsonObject> Node =
                Value.IsValid() ? Value->AsObject() : nullptr;
            FUnifiedScriptGameplayProfileNode CandidateNode;
            if (!ReadRequiredString(
                    Node,
                    TEXT("methodId"),
                    CandidateNode.MethodId,
                    OutError) ||
                !ReadRequiredString(
                    Node,
                    TEXT("nodeId"),
                    CandidateNode.NodeId,
                    OutError) ||
                !ReadInteger(
                    Node,
                    TEXT("hitCount"),
                    CandidateNode.HitCount,
                    OutError,
                    true) ||
                !ReadInteger(
                    Node,
                    TEXT("totalNanoseconds"),
                    CandidateNode.TotalNanoseconds,
                    OutError,
                    false))
            {
                return false;
            }
            OutNodes.Add(MoveTemp(CandidateNode));
        }
        return true;
    };
    const auto ReadPressure = [&OutError](
        const TSharedPtr<FJsonObject>& Object,
        FUnifiedScriptGameplayProfileBufferPressure& OutPressure)
    {
        const TSharedPtr<FJsonObject>* Pressure = nullptr;
        return Object.IsValid() &&
            Object->TryGetObjectField(TEXT("bufferPressure"), Pressure) &&
            Pressure != nullptr &&
            ReadInteger(
                *Pressure,
                TEXT("commandOverflowCount"),
                OutPressure.CommandOverflowCount,
                OutError,
                false) &&
            ReadInteger(
                *Pressure,
                TEXT("eventOverflowCount"),
                OutPressure.EventOverflowCount,
                OutError,
                false) &&
            ReadInteger(
                *Pressure,
                TEXT("traceOverflowCount"),
                OutPressure.TraceOverflowCount,
                OutError,
                false) &&
            ReadInteger(
                *Pressure,
                TEXT("overlapOverflowCount"),
                OutPressure.OverlapOverflowCount,
                OutError,
                false);
    };

    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = TEXT("The managed World returned invalid gameplay-profile JSON.");
        return false;
    }
    FString Schema;
    FUnifiedScriptGameplayProfileResult Candidate;
    int64 FrameCapacity = 0;
    if (!Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
        Schema != TEXT("unifiedscript.gameplay-profile/v0.2") ||
        !ReadRequiredString(Root, TEXT("traceId"), Candidate.TraceId, OutError) ||
        !ReadRequiredString(Root, TEXT("worldHostId"), Candidate.WorldHostId, OutError) ||
        !ReadRequiredString(Root, TEXT("instanceId"), Candidate.InstanceId, OutError) ||
        !ReadInteger(Root, TEXT("frameCapacity"), FrameCapacity, OutError, true) ||
        FrameCapacity > 4096 ||
        !ReadInteger(
            Root,
            TEXT("droppedFrameCount"),
            Candidate.DroppedFrameCount,
            OutError,
            false))
    {
        if (OutError.IsEmpty())
        {
            OutError = FString::Printf(
                TEXT("Unsupported or unbounded gameplay-profile schema '%s'."),
                *Schema);
        }
        return false;
    }
    Candidate.FrameCapacity = static_cast<int32>(FrameCapacity);

    const TArray<TSharedPtr<FJsonValue>>* Frames = nullptr;
    const TSharedPtr<FJsonObject>* Aggregate = nullptr;
    if (!Root->TryGetArrayField(TEXT("frames"), Frames) || Frames == nullptr ||
        Frames->Num() > Candidate.FrameCapacity ||
        !Root->TryGetObjectField(TEXT("aggregate"), Aggregate) || Aggregate == nullptr)
    {
        OutError = TEXT("Gameplay profile requires a capacity-bounded frame timeline and aggregate.");
        return false;
    }
    for (const TSharedPtr<FJsonValue>& Value : *Frames)
    {
        const TSharedPtr<FJsonObject> Frame = Value.IsValid() ? Value->AsObject() : nullptr;
        FUnifiedScriptGameplayProfileFrame CandidateFrame;
        int64 Generation = 0;
        double DeltaSeconds = 0.0;
        if (!ReadInteger(Frame, TEXT("sequence"), CandidateFrame.Sequence, OutError, true) ||
            !ReadRequiredString(
                Frame, TEXT("worldFrameNumber"), CandidateFrame.WorldFrameNumber, OutError) ||
            !ReadInteger(Frame, TEXT("generation"), Generation, OutError, true) ||
            Generation > MAX_int32 ||
            !ReadRequiredString(Frame, TEXT("scriptType"), CandidateFrame.ScriptType, OutError) ||
            !Frame->TryGetNumberField(TEXT("deltaSeconds"), DeltaSeconds) ||
            !FMath::IsFinite(DeltaSeconds) || DeltaSeconds < 0.0 || DeltaSeconds > 3600.0 ||
            !ReadRequiredString(Frame, TEXT("worldPhase"), CandidateFrame.WorldPhase, OutError) ||
            !ReadInteger(
                Frame,
                TEXT("durationNanoseconds"),
                CandidateFrame.DurationNanoseconds,
                OutError,
                false) ||
            !ReadInteger(
                Frame,
                TEXT("allocatedBytes"),
                CandidateFrame.AllocatedBytes,
                OutError,
                false) ||
            !ReadBoundaryArray(
                Frame,
                TEXT("worldPhases"),
                8,
                CandidateFrame.WorldPhases) ||
            !ReadBoundaryArray(
                Frame,
                TEXT("boundaries"),
                32,
                CandidateFrame.Boundaries) ||
            !ReadNodeArray(Frame, TEXT("nodes"), CandidateFrame.Nodes) ||
            !ReadPressure(Frame, CandidateFrame.BufferPressure))
        {
            if (OutError.IsEmpty())
            {
                OutError = TEXT("Gameplay profile frame is incomplete.");
            }
            return false;
        }
        CandidateFrame.Generation = static_cast<int32>(Generation);
        CandidateFrame.DeltaSeconds = DeltaSeconds;
        Candidate.Frames.Add(MoveTemp(CandidateFrame));
    }

    int64 AggregateFrameCount = 0;
    if (!ReadInteger(
            *Aggregate,
            TEXT("frameCount"),
            AggregateFrameCount,
            OutError,
            false) ||
        AggregateFrameCount != Candidate.Frames.Num() ||
        !ReadInteger(
            *Aggregate,
            TEXT("totalNanoseconds"),
            Candidate.TotalNanoseconds,
            OutError,
            false) ||
        !ReadInteger(
            *Aggregate,
            TEXT("allocatedBytes"),
            Candidate.AllocatedBytes,
            OutError,
            false) ||
        !ReadBoundaryArray(
            *Aggregate,
            TEXT("worldPhases"),
            8,
            Candidate.WorldPhases) ||
        !ReadBoundaryArray(
            *Aggregate,
            TEXT("boundaries"),
            32,
            Candidate.Boundaries) ||
        !ReadNodeArray(*Aggregate, TEXT("nodes"), Candidate.Nodes) ||
        !ReadPressure(*Aggregate, Candidate.BufferPressure))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("Gameplay profile aggregate is incomplete or exceeds native bounds.");
        }
        return false;
    }
    OutProfile = MoveTemp(Candidate);
    OutError.Reset();
    return true;
}

bool FUnifiedScriptDebugWorkbench::ResolveRuntimeDiagnosticNode(
    const FUnifiedScriptAuthoringAsset& Asset,
    TConstArrayView<FUnifiedScriptRuntimeSourceLocation> SourceFrames,
    FString& OutMethodId,
    FString& OutNodeId)
{
    OutMethodId.Reset();
    OutNodeId.Reset();
    for (const FUnifiedScriptRuntimeSourceLocation& Source : SourceFrames)
    {
        if (!Source.IsValid())
        {
            continue;
        }
        FString RuntimePath = Source.FilePath;
        RuntimePath.ReplaceInline(TEXT("\\"), TEXT("/"));
        const int32 SourceLine = Source.Line - 1;
        const int32 SourceColumn = FMath::Max(Source.Column - 1, 0);
        int64 BestExtent = MAX_int64;
        for (const FUnifiedScriptAuthoringFunction& Function : Asset.Functions)
        {
            for (const FUnifiedScriptAuthoringAction& Action : Function.Actions)
            {
                FString SurfacePath = Action.Span.FilePath;
                SurfacePath.ReplaceInline(TEXT("\\"), TEXT("/"));
                const bool bPathMatches =
                    RuntimePath.Equals(SurfacePath, ESearchCase::IgnoreCase) ||
                    RuntimePath.EndsWith(
                        TEXT("/") + SurfacePath,
                        ESearchCase::IgnoreCase) ||
                    FPaths::GetCleanFilename(RuntimePath).Equals(
                        FPaths::GetCleanFilename(SurfacePath),
                        ESearchCase::IgnoreCase);
                const bool bPositionMatches = Action.Span.IsValid() &&
                    SourceLine >= Action.Span.StartLine &&
                    SourceLine <= Action.Span.EndLine &&
                    (SourceLine != Action.Span.StartLine ||
                     SourceColumn >= Action.Span.StartColumn) &&
                    (SourceLine != Action.Span.EndLine ||
                     SourceColumn <= Action.Span.EndColumn);
                if (Action.DebugTargetId.IsEmpty() || !bPathMatches || !bPositionMatches)
                {
                    continue;
                }
                const int64 Extent =
                    static_cast<int64>(Action.Span.EndLine - Action.Span.StartLine) *
                        1000000ll +
                    FMath::Max(Action.Span.EndColumn - Action.Span.StartColumn, 0);
                if (Extent < BestExtent)
                {
                    BestExtent = Extent;
                    OutMethodId = Function.Id;
                    OutNodeId = Action.DebugTargetId;
                }
            }
        }
        if (!OutNodeId.IsEmpty())
        {
            return true;
        }
    }
    return false;
}
