#include "UnifiedScriptComponent.h"

#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformProperties.h"
#include "HAL/PlatformTime.h"
#include "Dom/JsonObject.h"
#include "GameFramework/Actor.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptDeclarationMaterializer.h"
#include "UnifiedScriptObjectRegistry.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/SoftObjectPath.h"
#include "UObject/UnrealType.h"

#include <atomic>

namespace UE::UnifiedScript::Component::Private
{
constexpr int32 ActorBeginOverlapEvent = 1;
constexpr int32 ActorEndOverlapEvent = 2;

FString GetAbsoluteProjectDir()
{
    FString ProjectDir = FPlatformProperties::RequiresCookedData()
        ? FPaths::Combine(FPlatformProcess::BaseDir(), TEXT("../.."))
        : FPaths::ProjectDir();
    ProjectDir = FPaths::ConvertRelativePathToFull(ProjectDir);
    FPaths::NormalizeDirectoryName(ProjectDir);
    return ProjectDir;
}

uint64 AllocateScriptInstanceId()
{
    static std::atomic<uint64> NextInstanceId{1};
    for (;;)
    {
        const uint64 Result = NextInstanceId.fetch_add(1, std::memory_order_relaxed);
        if (Result != 0)
        {
            return Result;
        }
    }
}

const TCHAR* DescribeWorldType(EWorldType::Type WorldType)
{
    switch (WorldType)
    {
        case EWorldType::None: return TEXT("None");
        case EWorldType::Game: return TEXT("Game");
        case EWorldType::Editor: return TEXT("Editor");
        case EWorldType::PIE: return TEXT("PIE");
        case EWorldType::EditorPreview: return TEXT("EditorPreview");
        case EWorldType::GamePreview: return TEXT("GamePreview");
        case EWorldType::GameRPC: return TEXT("GameRPC");
        case EWorldType::Inactive: return TEXT("Inactive");
        default: return TEXT("Unknown");
    }
}
}

UUnifiedScriptComponent::UUnifiedScriptComponent()
{
    PrimaryComponentTick.bCanEverTick = true;
    PrimaryComponentTick.bStartWithTickEnabled = false;
}

void UUnifiedScriptComponent::BeginPlay()
{
    Super::BeginPlay();
    if (bStartAutomatically)
    {
        StartScript();
    }
}

void UUnifiedScriptComponent::EndPlay(const EEndPlayReason::Type EndPlayReason)
{
    StopScript();
    Super::EndPlay(EndPlayReason);
}

void UUnifiedScriptComponent::TickComponent(
    float DeltaTime,
    ELevelTick TickType,
    FActorComponentTickFunction* ThisTickFunction)
{
    Super::TickComponent(DeltaTime, TickType, ThisTickFunction);
    if (!bScriptRunning)
    {
        SetComponentTickEnabled(false);
        return;
    }

    UUnifiedScriptWorldSubsystem* Host =
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    if (bSemanticDebugEnabled && Host != nullptr)
    {
        Host->PumpManagedBridgeCalls();
    }

    if (ConsumeReloadRequest() && !ReloadScript())
    {
        return;
    }

    bool bSemanticExecutionCompletedThisFrame = false;
    if (bSemanticDebugEnabled && bSemanticDebugTickActive)
    {
        FString DebugError;
        bool bFaulted = false;
        if (!RefreshSemanticDebugState(DebugError, bFaulted))
        {
            SetScriptFailure(MoveTemp(DebugError));
            StopScript();
            return;
        }
        bSemanticExecutionCompletedThisFrame = !bSemanticDebugTickActive;
        if (bFaulted)
        {
            StopScript();
            return;
        }
    }

    if (bDebugPausedAtFrameBoundary && !bDebugStepFrameRequested)
    {
        SetComponentTickEnabled(true);
        return;
    }
    const bool bStepManagedFrame = bDebugStepFrameRequested;
    bDebugStepFrameRequested = false;

    if (bTickAutomatically || bStepManagedFrame)
    {
        FString Error;
        if (bSemanticDebugEnabled)
        {
            if (!bSemanticExecutionCompletedThisFrame && !bSemanticDebugTickActive &&
                (Host == nullptr || !Host->BeginManagedScriptDebugTick(
                    ManagedScriptInstanceId,
                    DeltaTime,
                    Error)))
            {
                SetScriptFailure(
                    Host == nullptr
                    ? TEXT("The UnifiedScript World host is unavailable during semantic Tick.")
                    : MoveTemp(Error));
                StopScript();
                return;
            }
            if (!bSemanticExecutionCompletedThisFrame)
            {
                bSemanticDebugTickActive = true;
                bSemanticDebugPaused = false;
                SemanticDebugStateJson.Reset();
            }
        }
        else if (Host == nullptr ||
                 !Host->TickManagedScript(ManagedScriptInstanceId, DeltaTime, Error))
        {
            SetScriptFailure(
                Host == nullptr
                ? TEXT("The UnifiedScript World host is unavailable during Tick.")
                : MoveTemp(Error));
            StopScript();
            return;
        }
    }

    SetComponentTickEnabled(
        bTickAutomatically || bReloadRequested || bDebugPausedAtFrameBoundary ||
        bSemanticDebugEnabled);
}

#if WITH_EDITOR
void UUnifiedScriptComponent::PostEditChangeChainProperty(
    FPropertyChangedChainEvent& PropertyChangedEvent)
{
    Super::PostEditChangeChainProperty(PropertyChangedEvent);

    const FName ScriptStateName =
        GET_MEMBER_NAME_CHECKED(UUnifiedScriptComponent, ScriptState);
    if (PropertyChangedEvent.GetMemberPropertyName() != ScriptStateName)
    {
        return;
    }

    const FName ChangedName = PropertyChangedEvent.GetPropertyName();
    if (ChangedName != ScriptStateName)
    {
        if (const FPropertyBagPropertyDesc* Desc =
                ScriptState.FindPropertyDescByName(ChangedName))
        {
            MarkScriptStateOverridden(Desc->ID);
            return;
        }
    }

    // A whole-bag event does not identify the nested field. FixedLayout prevents
    // structural edits, so conservatively retain all values as explicit overrides.
    if (const UPropertyBag* BagStruct = ScriptState.GetPropertyBagStruct())
    {
        for (const FPropertyBagPropertyDesc& Desc : BagStruct->GetPropertyDescs())
        {
            MarkScriptStateOverridden(Desc.ID);
        }
    }
}
#endif

bool UUnifiedScriptComponent::StartScript()
{
    check(IsInGameThread());
    if (bScriptRunning)
    {
        return true;
    }

    UWorld* World = GetWorld();
    UUnifiedScriptWorldSubsystem* Host =
        World != nullptr
        ? World->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    if (Host == nullptr || !Host->IsManagedWorldReady())
    {
        SetScriptFailure(TEXT("The UnifiedScript managed World host is unavailable."));
        return false;
    }

    FString ResolvedAssemblyPath;
    FString ResolvedTypeName;
    UUnifiedScriptAsset* ResolvedAsset = nullptr;
    FString ResolutionError;
    if (!ResolveScriptConfiguration(
            ResolvedAssemblyPath,
            ResolvedTypeName,
            ResolvedAsset,
            ResolutionError))
    {
        SetScriptFailure(MoveTemp(ResolutionError));
        return false;
    }
    if (!IFileManager::Get().FileExists(*ResolvedAssemblyPath))
    {
        SetScriptFailure(FString::Printf(
            TEXT("The managed script assembly does not exist: '%s'."),
            *ResolvedAssemblyPath));
        return false;
    }

    FInstancedPropertyBag CandidateScriptState;
    TArray<FGuid> CandidateOverrideIds;
    FUnifiedScriptStateMigrationReport CandidateMigration;
    FString CandidateDeclarationAssetPath;
    if (!BuildScriptState(
            ResolvedAsset,
            ScriptState,
            GetEffectiveScriptStateOverrideIds(),
            CandidateScriptState,
            CandidateOverrideIds,
            CandidateMigration,
            CandidateDeclarationAssetPath,
            ResolutionError))
    {
        SetScriptFailure(MoveTemp(ResolutionError));
        return false;
    }

    FUnifiedScriptObjectHandleResult HandleResult;
    const FUnifiedScriptObjectHandle HostHandle = Host->RegisterObject(this, HandleResult);
    if (!HandleResult.IsSuccess())
    {
        SetScriptFailure(HandleResult.Message);
        return false;
    }

    UObject* ScriptTarget = GetScriptTarget();
    FUnifiedScriptObjectHandle TargetHandle = HostHandle;
    if (ScriptTarget != this)
    {
        TargetHandle = Host->RegisterObject(ScriptTarget, HandleResult);
        if (!HandleResult.IsSuccess())
        {
            FUnifiedScriptObjectHandleResult ReleaseResult;
            Host->ReleaseObject(HostHandle, ReleaseResult);
            SetScriptFailure(HandleResult.Message);
            return false;
        }
    }

    const uint64 InstanceId =
        UE::UnifiedScript::Component::Private::AllocateScriptInstanceId();
    FInstancedPropertyBag PreviousScriptState = ScriptState;
    TArray<FGuid> PreviousOverrideIds = ScriptStateOverrideIds;
    const bool bPreviousOverrideBaselineInitialized =
        bScriptStateOverrideBaselineInitialized;
    const int32 PreviousOverrideCount = MaterializedStateOverrideCount;
    ScriptState = MoveTemp(CandidateScriptState);
    ScriptStateOverrideIds = MoveTemp(CandidateOverrideIds);
    bScriptStateOverrideBaselineInitialized = true;
    FString Error;
    if (!Host->CreateManagedScript(
            InstanceId,
            HostHandle,
            TargetHandle,
            ResolvedAssemblyPath,
            ResolvedTypeName,
            Error))
    {
        ScriptState = MoveTemp(PreviousScriptState);
        ScriptStateOverrideIds = MoveTemp(PreviousOverrideIds);
        bScriptStateOverrideBaselineInitialized =
            bPreviousOverrideBaselineInitialized;
        MaterializedStateOverrideCount = PreviousOverrideCount;
        FUnifiedScriptObjectHandleResult ReleaseResult;
        if (TargetHandle != HostHandle)
        {
            Host->ReleaseObject(TargetHandle, ReleaseResult);
        }
        Host->ReleaseObject(HostHandle, ReleaseResult);
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    ManagedScriptInstanceId = InstanceId;
    ManagedHostHandle = HostHandle.Value;
    ManagedTargetHandle = TargetHandle.Value;
    bScriptRunning = true;
    ActiveGeneration = TEXT("1");
    ActiveScriptAsset = ResolvedAsset;
    ActiveScriptAssetPath = ResolvedAsset != nullptr
        ? ResolvedAsset->GetPathName()
        : FString();
    ActiveDeclarationAssetPath = MoveTemp(CandidateDeclarationAssetPath);
    ApplyStateMigrationDiagnostics(CandidateMigration);
    ActiveResolvedAssemblyPath = ResolvedAssemblyPath;
    ActiveResolvedTypeName = ResolvedTypeName;
    LastScriptError.Reset();
    LastRuntimeDiagnostic = {};
    bDebugPausedAtFrameBoundary = false;
    bDebugStepFrameRequested = false;
    bSemanticDebugEnabled = false;
    bSemanticDebugTickActive = false;
    bSemanticDebugPaused = false;
    SemanticDebugStateJson.Reset();
    BindManagedActorEvents();
    SetComponentTickEnabled(bTickAutomatically || bReloadRequested);
    UE_LOG(
        LogTemp,
        Display,
        TEXT("UnifiedScript started '%s' from '%s' (asset='%s')."),
        *ResolvedTypeName,
        *ResolvedAssemblyPath,
        ActiveScriptAssetPath.IsEmpty() ? TEXT("legacy-direct") : *ActiveScriptAssetPath);
    return true;
}

void UUnifiedScriptComponent::StopScript()
{
    check(IsInGameThread());
    if (!bScriptRunning && ManagedScriptInstanceId == 0 &&
        ManagedHostHandle == 0 && ManagedTargetHandle == 0)
    {
        SetComponentTickEnabled(false);
        return;
    }

    UUnifiedScriptWorldSubsystem* Host =
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    UnbindManagedActorEvents();
    FString StopError;
    if (Host != nullptr && Host->IsManagedWorldReady() && ManagedScriptInstanceId != 0)
    {
        FString DebugStopError;
        if (!DisableSemanticDebugAndDrain(DebugStopError))
        {
            StopError = MoveTemp(DebugStopError);
        }
        FString DestroyError;
        if (!Host->DestroyManagedScript(ManagedScriptInstanceId, DestroyError) &&
            StopError.IsEmpty())
        {
            StopError = MoveTemp(DestroyError);
        }
    }

    if (Host != nullptr && Host->IsNativeRuntimeReady())
    {
        FUnifiedScriptObjectHandleResult ReleaseResult;
        if (ManagedTargetHandle != 0 && ManagedTargetHandle != ManagedHostHandle)
        {
            Host->ReleaseObject({ManagedTargetHandle}, ReleaseResult);
            if (!ReleaseResult.IsSuccess() && StopError.IsEmpty())
            {
                StopError = ReleaseResult.Message;
            }
        }
        if (ManagedHostHandle != 0)
        {
            Host->ReleaseObject({ManagedHostHandle}, ReleaseResult);
            if (!ReleaseResult.IsSuccess() && StopError.IsEmpty())
            {
                StopError = ReleaseResult.Message;
            }
        }
    }

    ManagedScriptInstanceId = 0;
    ManagedHostHandle = 0;
    ManagedTargetHandle = 0;
    bScriptRunning = false;
    bReloadRequested = false;
    bDebugPausedAtFrameBoundary = false;
    bDebugStepFrameRequested = false;
    bSemanticDebugEnabled = false;
    bSemanticDebugTickActive = false;
    bSemanticDebugPaused = false;
    SemanticDebugStateJson.Reset();
    ActiveScriptAsset = nullptr;
    ActiveScriptAssetPath.Reset();
    ActiveDeclarationAssetPath.Reset();
    ActiveResolvedAssemblyPath.Reset();
    ActiveResolvedTypeName.Reset();
    SetComponentTickEnabled(false);
    if (!StopError.IsEmpty())
    {
        SetScriptFailure(MoveTemp(StopError));
    }
}

bool UUnifiedScriptComponent::ReloadScript()
{
    check(IsInGameThread());
    if (!bScriptRunning)
    {
        return StartScript();
    }

    UUnifiedScriptWorldSubsystem* Host =
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    if (Host == nullptr)
    {
        SetScriptFailure(TEXT("The UnifiedScript World host is unavailable during Reload."));
        return false;
    }

    FString DebugDrainError;
    if (!DisableSemanticDebugAndDrain(DebugDrainError))
    {
        SetScriptFailure(MoveTemp(DebugDrainError));
        return false;
    }

    FString Error;
    FString ResolvedAssemblyPath;
    FString ResolvedTypeName;
    UUnifiedScriptAsset* ResolvedAsset = nullptr;
    if (!ResolveScriptConfiguration(
            ResolvedAssemblyPath,
            ResolvedTypeName,
            ResolvedAsset,
            Error))
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }
    if (!IFileManager::Get().FileExists(*ResolvedAssemblyPath))
    {
        SetScriptFailure(FString::Printf(
            TEXT("The managed script assembly does not exist: '%s'."),
            *ResolvedAssemblyPath));
        return false;
    }

    FInstancedPropertyBag CandidateScriptState;
    TArray<FGuid> CandidateOverrideIds;
    FUnifiedScriptStateMigrationReport CandidateMigration;
    FString CandidateDeclarationAssetPath;
    if (!BuildScriptState(
            ResolvedAsset,
            ScriptState,
            GetEffectiveScriptStateOverrideIds(),
            CandidateScriptState,
            CandidateOverrideIds,
            CandidateMigration,
            CandidateDeclarationAssetPath,
            Error))
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    FInstancedPropertyBag PreviousScriptState = ScriptState;
    TArray<FGuid> PreviousOverrideIds = ScriptStateOverrideIds;
    const bool bPreviousOverrideBaselineInitialized =
        bScriptStateOverrideBaselineInitialized;
    const int32 PreviousOverrideCount = MaterializedStateOverrideCount;
    ScriptState = MoveTemp(CandidateScriptState);
    ScriptStateOverrideIds = MoveTemp(CandidateOverrideIds);
    bScriptStateOverrideBaselineInitialized = true;
    int32 Generation = 0;
    if (!Host->ReloadManagedScript(
            ManagedScriptInstanceId,
            ResolvedAssemblyPath,
            ResolvedTypeName,
            Generation,
            Error))
    {
        ScriptState = MoveTemp(PreviousScriptState);
        ScriptStateOverrideIds = MoveTemp(PreviousOverrideIds);
        bScriptStateOverrideBaselineInitialized =
            bPreviousOverrideBaselineInitialized;
        MaterializedStateOverrideCount = PreviousOverrideCount;
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    ActiveGeneration = FString::FromInt(Generation);
    ActiveScriptAsset = ResolvedAsset;
    ActiveScriptAssetPath = ResolvedAsset != nullptr
        ? ResolvedAsset->GetPathName()
        : FString();
    ActiveDeclarationAssetPath = MoveTemp(CandidateDeclarationAssetPath);
    ApplyStateMigrationDiagnostics(CandidateMigration);
    ActiveResolvedAssemblyPath = MoveTemp(ResolvedAssemblyPath);
    ActiveResolvedTypeName = MoveTemp(ResolvedTypeName);
    LastScriptError.Reset();
    LastRuntimeDiagnostic = {};
    return true;
}

bool UUnifiedScriptComponent::GetRuntimeDebugContext(
    FUnifiedScriptRuntimeDebugContext& OutContext) const
{
    const UWorld* World = GetWorld();
    const UUnifiedScriptWorldSubsystem* Host =
        World != nullptr ? World->GetSubsystem<UUnifiedScriptWorldSubsystem>() : nullptr;
    if (Host == nullptr || (ManagedScriptInstanceId == 0 && !LastRuntimeDiagnostic.IsValid()))
    {
        return false;
    }
    OutContext = {};
    OutContext.WorldHostId = ManagedScriptInstanceId != 0
        ? Host->GetWorldHostId()
        : LastRuntimeDiagnostic.WorldHostId;
    OutContext.WorldName = World->GetName();
    OutContext.WorldType = UE::UnifiedScript::Component::Private::DescribeWorldType(
        World->WorldType);
    OutContext.InstanceId = ManagedScriptInstanceId != 0
        ? ManagedScriptInstanceId
        : LastRuntimeDiagnostic.InstanceId;
    OutContext.Generation = ManagedScriptInstanceId != 0
        ? FCString::Atoi(*ActiveGeneration)
        : LastRuntimeDiagnostic.Generation;
    OutContext.HostHandle = ManagedHostHandle != 0
        ? ManagedHostHandle
        : LastRuntimeDiagnostic.HostHandle;
    OutContext.TargetHandle = ManagedTargetHandle != 0
        ? ManagedTargetHandle
        : LastRuntimeDiagnostic.TargetHandle;
    OutContext.AssemblyPath = ActiveResolvedAssemblyPath.IsEmpty()
        ? LastRuntimeDiagnostic.AssemblyPath
        : ActiveResolvedAssemblyPath;
    OutContext.TypeName = ActiveResolvedTypeName.IsEmpty()
        ? LastRuntimeDiagnostic.TypeName
        : ActiveResolvedTypeName;
    OutContext.bRunning = bScriptRunning;
    OutContext.bPausedAtFrameBoundary = bDebugPausedAtFrameBoundary;
    OutContext.bSemanticDebugEnabled = bSemanticDebugEnabled;
    OutContext.bSemanticDebugActive = bSemanticDebugTickActive;
    OutContext.bSemanticDebugPaused = bSemanticDebugPaused;
    OutContext.SemanticDebugStateJson = SemanticDebugStateJson;
    OutContext.LastDiagnostic = LastRuntimeDiagnostic;
    return OutContext.IsValid();
}

bool UUnifiedScriptComponent::PauseAtDebugFrameBoundary()
{
    if (!bScriptRunning)
    {
        return false;
    }
    bDebugPausedAtFrameBoundary = true;
    bDebugStepFrameRequested = false;
    SetComponentTickEnabled(true);
    return true;
}

bool UUnifiedScriptComponent::ContinueFromDebugFrameBoundary()
{
    if (!bScriptRunning || !bDebugPausedAtFrameBoundary)
    {
        return false;
    }
    bDebugPausedAtFrameBoundary = false;
    bDebugStepFrameRequested = false;
    SetComponentTickEnabled(bTickAutomatically || bReloadRequested);
    return true;
}

bool UUnifiedScriptComponent::StepOneDebugFrame()
{
    if (!bScriptRunning || !bDebugPausedAtFrameBoundary)
    {
        return false;
    }
    bDebugStepFrameRequested = true;
    SetComponentTickEnabled(true);
    return true;
}

bool UUnifiedScriptComponent::ConfigureSemanticDebug(
    const FString& ConfigurationJson,
    FString& OutError)
{
    check(IsInGameThread());
    if (!bScriptRunning || ManagedScriptInstanceId == 0)
    {
        OutError = TEXT("A live managed script is required before semantic debug attach.");
        return false;
    }
    UUnifiedScriptWorldSubsystem* Host =
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    if (Host == nullptr || !Host->ConfigureManagedScriptDebug(
            ManagedScriptInstanceId,
            ConfigurationJson,
            OutError))
    {
        if (Host == nullptr)
        {
            OutError = TEXT("The UnifiedScript World host is unavailable during debug attach.");
        }
        return false;
    }
    bSemanticDebugEnabled = true;
    bSemanticDebugTickActive = false;
    bSemanticDebugPaused = false;
    SemanticDebugStateJson.Reset();
    bDebugPausedAtFrameBoundary = false;
    bDebugStepFrameRequested = false;
    SetComponentTickEnabled(true);
    OutError.Reset();
    return true;
}

bool UUnifiedScriptComponent::ControlSemanticDebug(
    EUnifiedScriptSemanticDebugControl Control,
    const FString& PauseId,
    FString& OutError)
{
    check(IsInGameThread());
    if (!bScriptRunning || !bSemanticDebugEnabled || ManagedScriptInstanceId == 0)
    {
        OutError = TEXT("The component has no attached live semantic debug session.");
        return false;
    }
    if (Control != EUnifiedScriptSemanticDebugControl::Pause && PauseId.IsEmpty())
    {
        OutError = TEXT("Continue and step commands require the exact current pause ID.");
        return false;
    }
    UUnifiedScriptWorldSubsystem* Host =
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    if (Host == nullptr || !Host->ControlManagedScriptDebug(
            ManagedScriptInstanceId,
            static_cast<int32>(Control),
            PauseId,
            OutError))
    {
        if (Host == nullptr)
        {
            OutError = TEXT("The UnifiedScript World host is unavailable during debug control.");
        }
        return false;
    }
    if (Control != EUnifiedScriptSemanticDebugControl::Pause)
    {
        bSemanticDebugPaused = false;
    }
    SetComponentTickEnabled(true);
    OutError.Reset();
    return true;
}

bool UUnifiedScriptComponent::DisableSemanticDebug(FString& OutError)
{
    check(IsInGameThread());
    return DisableSemanticDebugAndDrain(OutError);
}

bool UUnifiedScriptComponent::RefreshSemanticDebugState(
    FString& OutError,
    bool& bOutFaulted)
{
    check(IsInGameThread());
    bOutFaulted = false;
    UUnifiedScriptWorldSubsystem* Host =
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    FString Json;
    if (Host == nullptr || !Host->GetManagedScriptDebugState(
            ManagedScriptInstanceId,
            Json,
            OutError))
    {
        if (Host == nullptr)
        {
            OutError = TEXT("The UnifiedScript World host is unavailable during debug polling.");
        }
        return false;
    }

    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    FString Schema;
    FString State;
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid() ||
        !Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
        Schema != TEXT("unifiedscript.debug-session/v0.2") ||
        !Root->TryGetStringField(TEXT("state"), State))
    {
        OutError = TEXT("The managed live World debugger returned invalid state JSON.");
        return false;
    }

    SemanticDebugStateJson = MoveTemp(Json);
    if (State == TEXT("running"))
    {
        bSemanticDebugTickActive = true;
        bSemanticDebugPaused = false;
    }
    else if (State == TEXT("paused"))
    {
        bSemanticDebugTickActive = true;
        bSemanticDebugPaused = true;
    }
    else if (State == TEXT("completed"))
    {
        bSemanticDebugTickActive = false;
        bSemanticDebugPaused = false;
        FString RuntimeDiagnostic;
        if (Root->TryGetStringField(TEXT("runtimeDiagnostic"), RuntimeDiagnostic) &&
            !RuntimeDiagnostic.IsEmpty())
        {
            SetScriptFailure(MoveTemp(RuntimeDiagnostic));
            bOutFaulted = true;
        }
        else
        {
            const TSharedPtr<FJsonObject>* Result = nullptr;
            const TSharedPtr<FJsonObject>* Fault = nullptr;
            FString Outcome;
            FString Message;
            if (Root->TryGetObjectField(TEXT("result"), Result) && Result != nullptr &&
                (*Result)->TryGetStringField(TEXT("outcome"), Outcome) &&
                Outcome == TEXT("faulted") &&
                (*Result)->TryGetObjectField(TEXT("fault"), Fault) && Fault != nullptr &&
                (*Fault)->TryGetStringField(TEXT("message"), Message))
            {
                SetScriptFailure(FString::Printf(
                    TEXT("Live semantic debug execution failed: %s"),
                    *Message));
                bOutFaulted = true;
            }
        }
    }
    else
    {
        OutError = FString::Printf(
            TEXT("The managed live World debugger returned unsupported state '%s'."),
            *State);
        return false;
    }
    OutError.Reset();
    return true;
}

bool UUnifiedScriptComponent::DisableSemanticDebugAndDrain(FString& OutError)
{
    check(IsInGameThread());
    if ((!bSemanticDebugEnabled && !bSemanticDebugTickActive) ||
        ManagedScriptInstanceId == 0)
    {
        bSemanticDebugEnabled = false;
        bSemanticDebugTickActive = false;
        bSemanticDebugPaused = false;
        OutError.Reset();
        return true;
    }

    UUnifiedScriptWorldSubsystem* Host =
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    if (Host == nullptr || !Host->DisableManagedScriptDebug(
            ManagedScriptInstanceId,
            OutError))
    {
        if (Host == nullptr)
        {
            OutError = TEXT("The UnifiedScript World host is unavailable during debug detach.");
        }
        return false;
    }

    const double Deadline = FPlatformTime::Seconds() + 2.0;
    for (;;)
    {
        Host->PumpManagedBridgeCalls();
        bool bActive = false;
        if (!Host->IsManagedScriptDebugActive(
                ManagedScriptInstanceId,
                bActive,
                OutError))
        {
            return false;
        }
        if (!bActive)
        {
            break;
        }
        if (FPlatformTime::Seconds() >= Deadline)
        {
            OutError = TEXT(
                "Timed out draining the live semantic debug execution before lifecycle mutation.");
            return false;
        }
        FPlatformProcess::SleepNoStats(0.0f);
    }

    bSemanticDebugEnabled = false;
    bSemanticDebugTickActive = false;
    bSemanticDebugPaused = false;
    SemanticDebugStateJson.Reset();
    SetComponentTickEnabled(
        bTickAutomatically || bReloadRequested || bDebugPausedAtFrameBoundary);
    OutError.Reset();
    return true;
}

void UUnifiedScriptComponent::RequestReload()
{
    bReloadRequested = true;
    if (bScriptRunning)
    {
        SetComponentTickEnabled(true);
    }
}

bool UUnifiedScriptComponent::RefreshScriptState()
{
    FString ResolvedAssemblyPath;
    FString ResolvedTypeName;
    UUnifiedScriptAsset* ResolvedAsset = nullptr;
    FString Error;
    if (!ResolveScriptConfiguration(
            ResolvedAssemblyPath,
            ResolvedTypeName,
            ResolvedAsset,
            Error))
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    FInstancedPropertyBag Candidate;
    TArray<FGuid> CandidateOverrideIds;
    FUnifiedScriptStateMigrationReport CandidateMigration;
    FString CandidateAssetPath;
    if (!BuildScriptState(
            ResolvedAsset,
            ScriptState,
            GetEffectiveScriptStateOverrideIds(),
            Candidate,
            CandidateOverrideIds,
            CandidateMigration,
            CandidateAssetPath,
            Error))
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    ScriptState = MoveTemp(Candidate);
    ScriptStateOverrideIds = MoveTemp(CandidateOverrideIds);
    bScriptStateOverrideBaselineInitialized = true;
    ActiveDeclarationAssetPath = MoveTemp(CandidateAssetPath);
    ApplyStateMigrationDiagnostics(CandidateMigration);
    LastScriptError.Reset();
    return true;
}

int32 UUnifiedScriptComponent::GetScriptStateInt32(
    const FString& Name,
    int32 Fallback) const
{
    const auto Result = ScriptState.GetValueInt32(FName(Name));
    return Result.IsValid() ? Result.GetValue() : Fallback;
}

bool UUnifiedScriptComponent::SetScriptStateInt32(
    const FString& Name,
    int32 Value)
{
    if (Name.IsEmpty())
    {
        return false;
    }

    const FPropertyBagPropertyDesc* Desc =
        ScriptState.FindPropertyDescByName(FName(Name));
    if (Desc == nullptr ||
        ScriptState.SetValueInt32(Desc->Name, Value) != EPropertyBagResult::Success)
    {
        return false;
    }

    MarkScriptStateOverridden(Desc->ID);
    return true;
}

bool UUnifiedScriptComponent::IsScriptStateOverridden(const FString& Name) const
{
    const FPropertyBagPropertyDesc* Desc =
        ScriptState.FindPropertyDescByName(FName(Name));
    return Desc != nullptr && GetEffectiveScriptStateOverrideIds().Contains(Desc->ID);
}

bool UUnifiedScriptComponent::ResetScriptStateOverride(const FString& Name)
{
    const FPropertyBagPropertyDesc* Desc =
        ScriptState.FindPropertyDescByName(FName(Name));
    if (Desc == nullptr)
    {
        SetScriptFailure(FString::Printf(
            TEXT("Script State does not contain '%s'."),
            *Name));
        return false;
    }

    const TArray<FGuid> PreviousOverrideIds = ScriptStateOverrideIds;
    const bool bPreviousOverrideBaselineInitialized =
        bScriptStateOverrideBaselineInitialized;
    ScriptStateOverrideIds = GetEffectiveScriptStateOverrideIds();
    bScriptStateOverrideBaselineInitialized = true;
    ScriptStateOverrideIds.Remove(Desc->ID);
    if (RefreshScriptState())
    {
        return true;
    }

    ScriptStateOverrideIds = PreviousOverrideIds;
    bScriptStateOverrideBaselineInitialized =
        bPreviousOverrideBaselineInitialized;
    return false;
}

bool UUnifiedScriptComponent::ResetAllScriptStateOverrides()
{
    const TArray<FGuid> PreviousOverrideIds = ScriptStateOverrideIds;
    const bool bPreviousOverrideBaselineInitialized =
        bScriptStateOverrideBaselineInitialized;
    ScriptStateOverrideIds.Reset();
    bScriptStateOverrideBaselineInitialized = true;
    if (RefreshScriptState())
    {
        return true;
    }

    ScriptStateOverrideIds = PreviousOverrideIds;
    bScriptStateOverrideBaselineInitialized =
        bPreviousOverrideBaselineInitialized;
    return false;
}

UObject* UUnifiedScriptComponent::GetScriptTarget()
{
    if (AActor* Owner = GetOwner())
    {
        return Owner;
    }
    return this;
}

void UUnifiedScriptComponent::BindManagedActorEvents()
{
    AActor* Owner = GetOwner();
    if (bActorEventsBound || Owner == nullptr)
    {
        return;
    }
    Owner->OnActorBeginOverlap.AddUniqueDynamic(
        this,
        &UUnifiedScriptComponent::HandleActorBeginOverlap);
    Owner->OnActorEndOverlap.AddUniqueDynamic(
        this,
        &UUnifiedScriptComponent::HandleActorEndOverlap);
    bActorEventsBound = true;
}

void UUnifiedScriptComponent::UnbindManagedActorEvents()
{
    AActor* Owner = GetOwner();
    if (!bActorEventsBound)
    {
        return;
    }
    if (Owner != nullptr)
    {
        Owner->OnActorBeginOverlap.RemoveDynamic(
            this,
            &UUnifiedScriptComponent::HandleActorBeginOverlap);
        Owner->OnActorEndOverlap.RemoveDynamic(
            this,
            &UUnifiedScriptComponent::HandleActorEndOverlap);
    }
    bActorEventsBound = false;
}

void UUnifiedScriptComponent::HandleActorBeginOverlap(
    AActor* OverlappedActor,
    AActor* OtherActor)
{
    if (OverlappedActor == GetOwner())
    {
        DispatchManagedActorEvent(
            UE::UnifiedScript::Component::Private::ActorBeginOverlapEvent,
            OtherActor);
    }
}

void UUnifiedScriptComponent::HandleActorEndOverlap(
    AActor* OverlappedActor,
    AActor* OtherActor)
{
    if (OverlappedActor == GetOwner())
    {
        DispatchManagedActorEvent(
            UE::UnifiedScript::Component::Private::ActorEndOverlapEvent,
            OtherActor);
    }
}

void UUnifiedScriptComponent::DispatchManagedActorEvent(
    int32 EventKind,
    AActor* OtherActor)
{
    if (!bScriptRunning || ManagedScriptInstanceId == 0 ||
        ManagedTargetHandle == 0 || OtherActor == nullptr)
    {
        return;
    }
    UUnifiedScriptWorldSubsystem* Host =
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    if (Host == nullptr || !Host->IsManagedWorldReady())
    {
        SetScriptFailure(TEXT("The UnifiedScript World host is unavailable during event dispatch."));
        return;
    }

    FUnifiedScriptObjectHandleResult HandleResult;
    const FUnifiedScriptObjectHandle Argument =
        Host->RegisterObject(OtherActor, HandleResult);
    if (!HandleResult.IsSuccess())
    {
        SetScriptFailure(HandleResult.Message);
        return;
    }

    FString Error;
    Host->DispatchManagedEvent(
        ManagedScriptInstanceId,
        EventKind,
        {ManagedTargetHandle},
        Argument,
        Error);
    FUnifiedScriptObjectHandleResult ReleaseResult;
    Host->ReleaseObject(Argument, ReleaseResult);
    if (!Error.IsEmpty())
    {
        SetScriptFailure(MoveTemp(Error));
    }
    else if (!ReleaseResult.IsSuccess())
    {
        SetScriptFailure(MoveTemp(ReleaseResult.Message));
    }
}

int32 UUnifiedScriptComponent::Add(int32 Left, int32 Right) const
{
    return Left + Right;
}

void UUnifiedScriptComponent::BroadcastManagedSignal(int32 Value, UObject* Context)
{
    OnManagedSignal.Broadcast(Value, Context);
}

bool UUnifiedScriptComponent::ConsumeReloadRequest()
{
    const bool bWasRequested = bReloadRequested;
    bReloadRequested = false;
    return bWasRequested;
}

bool UUnifiedScriptComponent::ResolveScriptConfiguration(
    FString& OutAssemblyPath,
    FString& OutTypeName,
    UUnifiedScriptAsset*& OutAsset,
    FString& OutError) const
{
    OutAsset = ScriptAsset.Get();
    if (OutAsset == nullptr && !ScriptAssetPath.IsEmpty())
    {
        const FSoftObjectPath AssetReference(ScriptAssetPath);
        if (!AssetReference.IsValid())
        {
            OutError = FString::Printf(
                TEXT("ScriptAssetPath '%s' is not a valid Unreal asset path."),
                *ScriptAssetPath);
            return false;
        }

        UObject* LoadedObject = AssetReference.ResolveObject();
        if (LoadedObject == nullptr)
        {
            LoadedObject = AssetReference.TryLoad();
        }
        OutAsset = Cast<UUnifiedScriptAsset>(LoadedObject);
        if (OutAsset == nullptr)
        {
            OutError = FString::Printf(
                TEXT("ScriptAssetPath '%s' did not resolve to a UnifiedScript Asset."),
                *ScriptAssetPath);
            return false;
        }
    }

    FString ConfiguredAssemblyPath;
    if (OutAsset != nullptr)
    {
        if (!OutAsset->ResolveConfiguration(
                ConfiguredAssemblyPath,
                OutTypeName,
                OutError))
        {
            return false;
        }
        const UObject* Target = GetOwner() != nullptr
            ? static_cast<const UObject*>(GetOwner())
            : static_cast<const UObject*>(this);
        if (!OutAsset->ValidateTarget(Target, OutError))
        {
            return false;
        }
    }
    else
    {
        ConfiguredAssemblyPath = ScriptAssemblyPath;
        OutTypeName = ScriptTypeName;
        ConfiguredAssemblyPath.TrimStartAndEndInline();
        OutTypeName.TrimStartAndEndInline();
        if (ConfiguredAssemblyPath.IsEmpty() || OutTypeName.IsEmpty())
        {
            OutError = TEXT(
                "Assign ScriptAsset, set ScriptAssetPath, or configure both legacy "
                "ScriptAssemblyPath and ScriptTypeName.");
            return false;
        }
    }

    OutAssemblyPath = ResolveConfiguredPath(ConfiguredAssemblyPath);
    OutError.Reset();
    return true;
}

bool UUnifiedScriptComponent::BuildScriptState(
    const UUnifiedScriptAsset* ResolvedAsset,
    const FInstancedPropertyBag& ExistingState,
    TConstArrayView<FGuid> ExistingOverrideIds,
    FInstancedPropertyBag& OutState,
    TArray<FGuid>& OutOverrideIds,
    FUnifiedScriptStateMigrationReport& OutMigration,
    FString& OutDeclarationAssetPath,
    FString& OutError) const
{
    OutDeclarationAssetPath.Reset();
    OutOverrideIds.Reset();
    OutMigration = {};
    if (ResolvedAsset == nullptr)
    {
        OutState.Reset();
        OutMigration.DroppedOverrideCount = ExistingOverrideIds.Num();
        OutError.Reset();
        return true;
    }

    FString DeclarationPath;
    FString RequestedAssetPath;
    ResolvedAsset->ResolveDeclarationConfiguration(
        DeclarationPath,
        RequestedAssetPath);
    if (DeclarationPath.IsEmpty())
    {
        OutState.Reset();
        OutMigration.DroppedOverrideCount = ExistingOverrideIds.Num();
        OutError.Reset();
        return true;
    }

    return FUnifiedScriptDeclarationMaterializer::MaterializeFileWithOverrides(
        ResolveConfiguredPath(DeclarationPath),
        RequestedAssetPath,
        ExistingState,
        ExistingOverrideIds,
        OutState,
        OutOverrideIds,
        OutMigration,
        OutDeclarationAssetPath,
        OutError);
}

TArray<FGuid> UUnifiedScriptComponent::GetEffectiveScriptStateOverrideIds() const
{
    if (bScriptStateOverrideBaselineInitialized)
    {
        return ScriptStateOverrideIds;
    }

    // Components saved before explicit override tracking contained only one bag.
    // Preserve every compatible legacy value once, then establish the new baseline.
    TArray<FGuid> LegacyOverrideIds;
    if (const UPropertyBag* BagStruct = ScriptState.GetPropertyBagStruct())
    {
        LegacyOverrideIds.Reserve(BagStruct->GetPropertyDescs().Num());
        for (const FPropertyBagPropertyDesc& Desc : BagStruct->GetPropertyDescs())
        {
            LegacyOverrideIds.Add(Desc.ID);
        }
    }
    return LegacyOverrideIds;
}

void UUnifiedScriptComponent::MarkScriptStateOverridden(const FGuid& PropertyId)
{
    if (!PropertyId.IsValid())
    {
        return;
    }

    if (!bScriptStateOverrideBaselineInitialized)
    {
        ScriptStateOverrideIds = GetEffectiveScriptStateOverrideIds();
        bScriptStateOverrideBaselineInitialized = true;
    }
    ScriptStateOverrideIds.AddUnique(PropertyId);
    MaterializedStateOverrideCount = ScriptStateOverrideIds.Num();
}

void UUnifiedScriptComponent::ApplyStateMigrationDiagnostics(
    const FUnifiedScriptStateMigrationReport& Migration)
{
    MaterializedStateCount = ScriptState.GetNumPropertiesInBag();
    MaterializedStateOverrideCount = ScriptStateOverrideIds.Num();
    LastStateMigration = FString::Printf(
        TEXT("Declared=%d PreservedOverrides=%d Defaults=%d DroppedOverrides=%d"),
        Migration.DeclaredStateCount,
        Migration.PreservedOverrideCount,
        Migration.DefaultedStateCount,
        Migration.DroppedOverrideCount);
}

FString UUnifiedScriptComponent::ResolveConfiguredPath(
    const FString& ConfiguredPath) const
{
    FString Result = ConfiguredPath;
    const FString ProjectDir =
        UE::UnifiedScript::Component::Private::GetAbsoluteProjectDir();
    const TSharedPtr<IPlugin> Plugin =
        IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (Plugin.IsValid())
    {
        const FString PluginBaseDir =
            FPaths::ConvertRelativePathToFull(Plugin->GetBaseDir());
        Result.ReplaceInline(
            TEXT("{PluginDir}"),
            *PluginBaseDir,
            ESearchCase::IgnoreCase);
    }
    Result.ReplaceInline(
        TEXT("{ProjectDir}"),
        *ProjectDir,
        ESearchCase::IgnoreCase);
    if (FPaths::IsRelative(Result))
    {
        Result = FPaths::Combine(ProjectDir, Result);
    }
    Result = FPaths::ConvertRelativePathToFull(Result);
    FPaths::NormalizeFilename(Result);
    return Result;
}

void UUnifiedScriptComponent::SetScriptFailure(FString Error)
{
    FUnifiedScriptRuntimeDiagnostic Diagnostic;
    FString ParseError;
    if (FUnifiedScriptRuntimeDiagnostic::TryParseBoundaryError(
            Error,
            Diagnostic,
            ParseError))
    {
        LastRuntimeDiagnostic = MoveTemp(Diagnostic);
        LastScriptError = LastRuntimeDiagnostic.ToDisplayString();
    }
    else
    {
        LastScriptError = MoveTemp(Error);
    }
    UE_LOG(LogTemp, Error, TEXT("UnifiedScript component: %s"), *LastScriptError);
}
