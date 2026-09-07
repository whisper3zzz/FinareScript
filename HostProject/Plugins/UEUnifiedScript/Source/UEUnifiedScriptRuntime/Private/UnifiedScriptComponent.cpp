#include "UnifiedScriptComponent.h"

#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "HAL/PlatformProperties.h"
#include "HAL/PlatformTime.h"
#include "Dom/JsonObject.h"
#include "EnhancedInputComponent.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "GameFramework/PlayerController.h"
#include "InputAction.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/CommandLine.h"
#include "Misc/Parse.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptBlueprint.h"
#include "UnifiedScriptCoreClrHost.h"
#include "UnifiedScriptDeclarationMaterializer.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptObjectRegistry.h"
#include "UnifiedScriptRuntimeDiagnostic.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/SoftObjectPath.h"
#include "UObject/UnrealType.h"
#include "UObject/GCObject.h"
#include "UObject/StructOnScope.h"

#include <atomic>

// Own the parameter values, including string/object previous values, across
// initial replication and BeginPlay. Never retain a network stack address.
struct FUnifiedScriptInitialRepNotify final : FGCObject
{
    TWeakObjectPtr<UObject> Target;
    TObjectPtr<UFunction> Function;
    FStructOnScope Parameters;

    FUnifiedScriptInitialRepNotify(UObject* InTarget, UFunction* InFunction, const void* Source)
        : Target(InTarget), Function(InFunction), Parameters(InFunction)
    {
        for (TFieldIterator<FProperty> It(Function); It; ++It)
            if (It->HasAnyPropertyFlags(CPF_Parm))
                It->CopyCompleteValue_InContainer(Parameters.GetStructMemory(), Source);
    }
    virtual void AddReferencedObjects(FReferenceCollector& Collector) override
    {
        Collector.AddReferencedObject(Function);
        // FStructOnScope::AddReferencedObjects assumes UScriptStruct, whereas
        // this storage belongs to a UFunction. Traverse its properties directly.
        Collector.AddPropertyReferences(Function, Parameters.GetStructMemory());
    }
    virtual FString GetReferencerName() const override { return TEXT("UnifiedScriptInitialRepNotify"); }
};

namespace UE::UnifiedScript::Component::Private
{
constexpr int32 ActorBeginOverlapEvent = 1;
constexpr int32 ActorEndOverlapEvent = 2;

bool ShouldPreferCompiledChunks()
{
    return !FParse::Param(
        FCommandLine::Get(),
        TEXT("UnifiedScriptDisableCompiledChunks"));
}

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

ETriggerEvent ToEnhancedInputTriggerEvent(
    EUnifiedScriptManagedInputTriggerEvent TriggerEvent)
{
    switch (TriggerEvent)
    {
        case EUnifiedScriptManagedInputTriggerEvent::Started:
            return ETriggerEvent::Started;
        case EUnifiedScriptManagedInputTriggerEvent::Ongoing:
            return ETriggerEvent::Ongoing;
        case EUnifiedScriptManagedInputTriggerEvent::Triggered:
            return ETriggerEvent::Triggered;
        case EUnifiedScriptManagedInputTriggerEvent::Completed:
            return ETriggerEvent::Completed;
        case EUnifiedScriptManagedInputTriggerEvent::Canceled:
            return ETriggerEvent::Canceled;
        default:
            return ETriggerEvent::None;
    }
}

bool InputValueTypeMatches(
    EUnifiedScriptManagedInputValueType Declared,
    EInputActionValueType Reflected)
{
    switch (Declared)
    {
        case EUnifiedScriptManagedInputValueType::Boolean:
            return Reflected == EInputActionValueType::Boolean;
        case EUnifiedScriptManagedInputValueType::Axis1D:
            return Reflected == EInputActionValueType::Axis1D;
        case EUnifiedScriptManagedInputValueType::Axis2D:
            return Reflected == EInputActionValueType::Axis2D;
        case EUnifiedScriptManagedInputValueType::Axis3D:
            return Reflected == EInputActionValueType::Axis3D;
        default:
            return false;
    }
}

void StoreFloat32(float Value, uint64& OutPayload)
{
    OutPayload = 0;
    FMemory::Memcpy(&OutPayload, &Value, sizeof(Value));
}

void StoreFloat64(double Value, uint64& OutPayload)
{
    FMemory::Memcpy(&OutPayload, &Value, sizeof(Value));
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

class FReloadTransactionBuilder
{
public:
    bool Initialize(
        const FString& PublicationJson,
        int32 FromGeneration,
        FString& OutError)
    {
        Root = MakeShared<FJsonObject>();
        if (!PublicationJson.IsEmpty())
        {
            const TSharedRef<TJsonReader<>> Reader =
                TJsonReaderFactory<>::Create(PublicationJson);
            if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
            {
                OutError = TEXT("The reload publication transaction is invalid JSON.");
                return false;
            }
            FString Schema;
            if (!Root->TryGetStringField(TEXT("schemaVersion"), Schema) ||
                Schema != TEXT("unifiedscript.reload-transaction/v0.1"))
            {
                OutError = FString::Printf(
                    TEXT("Unsupported reload transaction schema '%s'."),
                    *Schema);
                return false;
            }
            const TArray<TSharedPtr<FJsonValue>>* ExistingPhases = nullptr;
            if (Root->TryGetArrayField(TEXT("phases"), ExistingPhases) &&
                ExistingPhases != nullptr)
            {
                Phases = *ExistingPhases;
            }
        }
        else
        {
            Root->SetStringField(
                TEXT("schemaVersion"),
                TEXT("unifiedscript.reload-transaction/v0.1"));
            Root->SetStringField(
                TEXT("transactionId"),
                FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower));
            Root->SetStringField(TEXT("source"), TEXT("runtime"));
            AddPhase(
                TEXT("compile"),
                TEXT("notApplicable"),
                0.0,
                TEXT("Direct runtime reload uses the currently published assembly."));
            AddPhase(
                TEXT("artifactPublication"),
                TEXT("notApplicable"),
                0.0,
                TEXT("Direct runtime reload performs no artifact write."));
        }
        if (!Root->HasField(TEXT("fromGeneration")))
        {
            Root->SetNumberField(TEXT("fromGeneration"), FromGeneration);
        }
        Root->SetNumberField(TEXT("worldFromGeneration"), FromGeneration);
        Root->SetStringField(TEXT("state"), TEXT("running"));
        OutError.Reset();
        return true;
    }

    void AddPhase(
        const FString& Name,
        const FString& State,
        double StartedSeconds,
        const FString& Detail)
    {
        const double DurationMicroseconds = StartedSeconds > 0.0
            ? FMath::Max(0.0, (FPlatformTime::Seconds() - StartedSeconds) * 1000000.0)
            : 0.0;
        const TSharedRef<FJsonObject> Phase = MakeShared<FJsonObject>();
        Phase->SetStringField(TEXT("name"), Name);
        Phase->SetStringField(TEXT("state"), State);
        Phase->SetNumberField(TEXT("durationMicroseconds"), DurationMicroseconds);
        Phase->SetStringField(TEXT("detail"), Detail.Left(2048));
        Phases.Add(MakeShared<FJsonValueObject>(Phase));
    }

    void AppendManagedPhases(const FString& ManagedJson)
    {
        TSharedPtr<FJsonObject> Managed;
        const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(ManagedJson);
        FString Schema;
        const TArray<TSharedPtr<FJsonValue>>* ManagedPhases = nullptr;
        if (!FJsonSerializer::Deserialize(Reader, Managed) || !Managed.IsValid() ||
            !Managed->TryGetStringField(TEXT("schemaVersion"), Schema) ||
            Schema != TEXT("unifiedscript.managed-reload-transaction/v0.1") ||
            !Managed->TryGetArrayField(TEXT("phases"), ManagedPhases) ||
            ManagedPhases == nullptr)
        {
            AddPhase(
                TEXT("managedTransactionRead"),
                TEXT("failed"),
                0.0,
                TEXT("The managed reload phase report was missing or invalid."));
            return;
        }
        for (const TSharedPtr<FJsonValue>& ManagedPhaseValue : *ManagedPhases)
        {
            const TSharedPtr<FJsonObject>* ManagedPhase = nullptr;
            if (!ManagedPhaseValue.IsValid() ||
                !ManagedPhaseValue->TryGetObject(ManagedPhase) ||
                ManagedPhase == nullptr || !ManagedPhase->IsValid())
            {
                continue;
            }
            FString ManagedName;
            FString ManagedStateValue;
            FString ManagedDetail;
            (*ManagedPhase)->TryGetStringField(TEXT("name"), ManagedName);
            (*ManagedPhase)->TryGetStringField(TEXT("state"), ManagedStateValue);
            (*ManagedPhase)->TryGetStringField(TEXT("detail"), ManagedDetail);
            const bool bAlreadyPresent = Phases.ContainsByPredicate(
                [&ManagedName, &ManagedStateValue, &ManagedDetail](
                    const TSharedPtr<FJsonValue>& ExistingValue)
                {
                    const TSharedPtr<FJsonObject>* Existing = nullptr;
                    if (!ExistingValue.IsValid() ||
                        !ExistingValue->TryGetObject(Existing) ||
                        Existing == nullptr || !Existing->IsValid())
                    {
                        return false;
                    }
                    FString ExistingName;
                    FString ExistingState;
                    FString ExistingDetail;
                    (*Existing)->TryGetStringField(TEXT("name"), ExistingName);
                    (*Existing)->TryGetStringField(TEXT("state"), ExistingState);
                    (*Existing)->TryGetStringField(TEXT("detail"), ExistingDetail);
                    return ExistingName == ManagedName &&
                        ExistingState == ManagedStateValue &&
                        ExistingDetail == ManagedDetail;
                });
            if (!bAlreadyPresent)
            {
                Phases.Add(ManagedPhaseValue);
            }
        }
        FString ManagedState;
        if (Managed->TryGetStringField(TEXT("state"), ManagedState))
        {
            Root->SetStringField(TEXT("managedState"), ManagedState);
        }
        const TSharedPtr<FJsonObject>* ProgramLoaderPrewarm = nullptr;
        if (Managed->TryGetObjectField(
                TEXT("programLoaderPrewarm"),
                ProgramLoaderPrewarm) &&
            ProgramLoaderPrewarm != nullptr && ProgramLoaderPrewarm->IsValid())
        {
            Root->SetObjectField(
                TEXT("programLoaderPrewarm"),
                *ProgramLoaderPrewarm);
        }
    }

    bool IsAuthoringPublication() const
    {
        FString Source;
        return Root.IsValid() &&
            Root->TryGetStringField(TEXT("source"), Source) &&
            Source == TEXT("authoring");
    }

    FString Finish(
        const FString& State,
        int32 CandidateGeneration,
        int32 ActiveGeneration,
        const UUnifiedScriptWorldSubsystem* Host,
        bool bSemanticDebugActive,
        const FString& Failure = FString(),
        int32 RetiredLoadContextsAlive = INDEX_NONE)
    {
        Root->SetStringField(TEXT("state"), State);
        if (!Root->HasField(TEXT("candidateGeneration")))
        {
            Root->SetNumberField(TEXT("candidateGeneration"), CandidateGeneration);
        }
        if (!Root->HasField(TEXT("activeGeneration")))
        {
            Root->SetNumberField(TEXT("activeGeneration"), ActiveGeneration);
        }
        Root->SetNumberField(TEXT("worldCandidateGeneration"), CandidateGeneration);
        Root->SetNumberField(TEXT("worldActiveGeneration"), ActiveGeneration);
        Root->SetArrayField(TEXT("phases"), Phases);
        if (State == TEXT("restartRequired"))
        {
            Root->SetBoolField(TEXT("restartRequired"), true);
            Root->SetStringField(TEXT("requiredAction"), TEXT("restartEditor"));
        }
        if (!Failure.IsEmpty())
        {
            Root->SetStringField(TEXT("failure"), Failure.Left(4096));
        }
        const TSharedRef<FJsonObject> Resources = MakeShared<FJsonObject>();
        Resources->SetNumberField(
            TEXT("registeredObjects"),
            Host != nullptr ? Host->NumRegisteredObjects() : 0);
        Resources->SetNumberField(
            TEXT("eventSubscriptions"),
            Host != nullptr ? Host->GetEventSubscriptionCount() : 0);
        Resources->SetNumberField(
            TEXT("pendingAsyncRequests"),
            Host != nullptr ? Host->GetPendingAsyncRequestCount() : 0);
        Resources->SetNumberField(
            TEXT("queuedAsyncCompletions"),
            Host != nullptr ? Host->GetQueuedAsyncCompletionCount() : 0);
        Resources->SetNumberField(
            TEXT("ownedAsyncResources"),
            Host != nullptr ? Host->GetOwnedAsyncResourceCount() : 0);
        Resources->SetBoolField(TEXT("semanticDebugActive"), bSemanticDebugActive);
        if (RetiredLoadContextsAlive != INDEX_NONE)
        {
            Resources->SetNumberField(
                TEXT("retiredLoadContextsAlive"),
                RetiredLoadContextsAlive);
        }
        Root->SetObjectField(TEXT("resources"), Resources);

        FString Json;
        const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
            TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Json);
        return FJsonSerializer::Serialize(Root.ToSharedRef(), Writer) ? Json : FString();
    }

private:
    TSharedPtr<FJsonObject> Root;
    TArray<TSharedPtr<FJsonValue>> Phases;
};
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
    if (bStopRequestedByInputFailure)
    {
        StopScript();
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
            if (!bSemanticExecutionCompletedThisFrame && !bSemanticDebugTickActive)
            {
                if (Host == nullptr || !Host->BeginManagedScriptDebugTick(
                        ManagedScriptInstanceId,
                        DeltaTime,
                        Error))
                {
                    SetScriptFailure(
                        Host == nullptr
                        ? TEXT("The UnifiedScript World host is unavailable during semantic Tick.")
                        : MoveTemp(Error));
                    StopScript();
                    return;
                }
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
    bManagedStartAttempted = true;
    ON_SCOPE_EXIT { if (!bScriptRunning) InitialRepNotifies.Reset(); };
    const double StartSeconds = FPlatformTime::Seconds();
    LastStartMilliseconds = 0.0;

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
    const double HostValidationMilliseconds =
        (FPlatformTime::Seconds() - StartSeconds) * 1000.0;

    double PhaseStartSeconds = FPlatformTime::Seconds();
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
    const double ConfigurationMilliseconds =
        (FPlatformTime::Seconds() - PhaseStartSeconds) * 1000.0;

    PhaseStartSeconds = FPlatformTime::Seconds();
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
    const double StateMaterializationMilliseconds =
        (FPlatformTime::Seconds() - PhaseStartSeconds) * 1000.0;

    PhaseStartSeconds = FPlatformTime::Seconds();
    UObject* ScriptTarget = GetScriptTarget();
    FString Error;
    if (ResolvedAsset != nullptr &&
        !ResolvedAsset->ValidateGeneratedClassIdentity(this, ScriptTarget, Error))
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }
    FString BlueprintOverrideActivationContract =
        TEXT("{\"schema\":\"unifiedscript.blueprint-override-contract/v0.1\",\"overrides\":[]}");
    if (ResolvedAsset != nullptr &&
        ResolvedAsset->ScriptClassKind != EUnifiedScriptClassKind::None)
    {
        UClass* ResolvedGeneratedClass =
            ResolvedAsset->GeneratedClassPath.TryLoadClass<UObject>();
        UUnifiedScriptBlueprintGeneratedClass* GeneratedClass =
            Cast<UUnifiedScriptBlueprintGeneratedClass>(ResolvedGeneratedClass);
        if (GeneratedClass == nullptr ||
            !GeneratedClass->BuildBlueprintOverrideActivationContract(
                BlueprintOverrideActivationContract,
                Error))
        {
            if (Error.IsEmpty())
            {
                Error = FString::Printf(
                    TEXT("UnifiedScript Class '%s' has no exact generated class carrier at '%s' for activation contract construction."),
                    *ResolvedAsset->GetPathName(),
                    *ResolvedAsset->GeneratedClassPath.ToString());
            }
            SetScriptFailure(MoveTemp(Error));
            return false;
        }
    }
    const FName ResolvedOwnershipKey = LifecycleOwnershipKey.IsNone()
        ? FName(*ResolvedTypeName)
        : LifecycleOwnershipKey;
    if (!Host->ClaimGameplayLifecycle(
            this,
            ScriptTarget,
            ResolvedOwnershipKey,
            EUnifiedScriptRuntimeOwner::UnifiedScript,
            Error))
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }
    auto ReleaseCandidateOwnership = [&]()
    {
        FString ReleaseError;
        if (!Host->ReleaseGameplayLifecycle(
                this,
                ScriptTarget,
                ResolvedOwnershipKey,
                EUnifiedScriptRuntimeOwner::UnifiedScript,
                ReleaseError))
        {
            Error = Error.IsEmpty()
                ? MoveTemp(ReleaseError)
                : Error + TEXT(" Ownership rollback also failed: ") + ReleaseError;
        }
    };
    const double OwnershipMilliseconds =
        (FPlatformTime::Seconds() - PhaseStartSeconds) * 1000.0;

    PhaseStartSeconds = FPlatformTime::Seconds();
    FUnifiedScriptObjectHandleResult HandleResult;
    const FUnifiedScriptObjectHandle HostHandle = Host->RegisterObject(this, HandleResult);
    if (!HandleResult.IsSuccess())
    {
        Error = HandleResult.Message;
        ReleaseCandidateOwnership();
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    FUnifiedScriptObjectHandle TargetHandle = HostHandle;
    if (ScriptTarget != this)
    {
        TargetHandle = Host->RegisterObject(ScriptTarget, HandleResult);
        if (!HandleResult.IsSuccess())
        {
            FUnifiedScriptObjectHandleResult ReleaseResult;
            Host->ReleaseObject(HostHandle, ReleaseResult);
            Error = HandleResult.Message;
            ReleaseCandidateOwnership();
            SetScriptFailure(MoveTemp(Error));
            return false;
        }
    }
    const double HandleRegistrationMilliseconds =
        (FPlatformTime::Seconds() - PhaseStartSeconds) * 1000.0;

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
    PhaseStartSeconds = FPlatformTime::Seconds();
    if (!Host->CreateManagedScriptWithBlueprintContract(
            InstanceId,
            HostHandle,
            TargetHandle,
            ResolvedAssemblyPath,
            ResolvedTypeName,
            BlueprintOverrideActivationContract,
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
        ReleaseCandidateOwnership();
        SetScriptFailure(MoveTemp(Error));
        return false;
    }
    const double ManagedActivationMilliseconds =
        (FPlatformTime::Seconds() - PhaseStartSeconds) * 1000.0;

    PhaseStartSeconds = FPlatformTime::Seconds();
    ManagedScriptInstanceId = InstanceId;
    ManagedHostHandle = HostHandle.Value;
    ManagedTargetHandle = TargetHandle.Value;
    ActiveLifecycleOwnershipKey = ResolvedOwnershipKey;
    ActiveLifecycleHostObject = ScriptTarget;
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
    bGameplayProfilerEnabled = false;
    LastGameplayProfileTrace.Reset();
    bStopRequestedByInputFailure = false;
    BindManagedActorEvents();
    FString InputBindingError;
    if (!BindManagedInputActions(InputBindingError))
    {
        SetScriptFailure(MoveTemp(InputBindingError));
        StopScript();
        return false;
    }
    SetComponentTickEnabled(bTickAutomatically || bReloadRequested);
    const double FinalizationMilliseconds =
        (FPlatformTime::Seconds() - PhaseStartSeconds) * 1000.0;
    LastStartMilliseconds = FMath::Max(
        0.001,
        (FPlatformTime::Seconds() - StartSeconds) * 1000.0);
    UE_LOG(
        LogTemp,
        Display,
        TEXT("UnifiedScript started '%s' from '%s' (asset='%s', startupMs=%.6f)."),
        *ResolvedTypeName,
        *ResolvedAssemblyPath,
        ActiveScriptAssetPath.IsEmpty() ? TEXT("legacy-direct") : *ActiveScriptAssetPath,
        LastStartMilliseconds);
    UE_LOG(
        LogTemp,
        Display,
        TEXT("UnifiedScript startup phases: Type=%s, Mode=%s, HostMs=%.6f, ConfigurationMs=%.6f, StateMs=%.6f, OwnershipMs=%.6f, HandlesMs=%.6f, ManagedMs=%.6f, FinalizationMs=%.6f"),
        *ResolvedTypeName,
        ResolvedAssemblyPath.EndsWith(TEXT(".uschunk"), ESearchCase::IgnoreCase)
            ? TEXT("chunk")
            : TEXT("loose"),
        HostValidationMilliseconds,
        ConfigurationMilliseconds,
        StateMaterializationMilliseconds,
        OwnershipMilliseconds,
        HandleRegistrationMilliseconds,
        ManagedActivationMilliseconds,
        FinalizationMilliseconds);
    auto Pending = MoveTemp(InitialRepNotifies);
    InitialRepNotifies.Reset();
    for (const auto& Notify : Pending)
        if (bScriptRunning && Notify->Target.IsValid() &&
            Notify->Target->FindFunction(Notify->Function->GetFName()) == Notify->Function.Get())
            Notify->Target->ProcessEvent(Notify->Function, Notify->Parameters.GetStructMemory());
    return true;
}

bool UUnifiedScriptComponent::QueueInitialRepNotify(UObject* Target, UFunction* Function, const void* Parameters)
{
    check(IsInGameThread());
    if (bScriptRunning || bManagedStartAttempted || !bStartAutomatically || HasBegunPlay() || !Target || !Function ||
        Function->NumParms > 1 || Function->GetReturnProperty() != nullptr ||
        (Function->NumParms != 0 && Parameters == nullptr)) return false;
    const auto* Class = Cast<UUnifiedScriptBlueprintGeneratedClass>(Function->GetOuter());
    const auto* Binding = Class ? Class->ManagedFunctionBindings.Find(Function->GetFName()) : nullptr;
    if (!Binding || Binding->RepNotifyProperty.IsNone()) return false;
    // Multiple pre-start updates retain the first old value; hydration will
    // expose the newest replicated state, matching UE's coalesced notification.
    if (InitialRepNotifies.ContainsByPredicate([&](const auto& N) { return N->Function == Function; })) return true;
    if (InitialRepNotifies.Num() >= 256) return false;
    InitialRepNotifies.Add(MakeShared<FUnifiedScriptInitialRepNotify>(Target, Function, Parameters));
    return true;
}

void UUnifiedScriptComponent::StopScript()
{
    check(IsInGameThread());
    InitialRepNotifies.Reset();
    UnbindManagedInputActions();
    bStopRequestedByInputFailure = false;
    if (!bScriptRunning && ManagedScriptInstanceId == 0 &&
        ManagedHostHandle == 0 && ManagedTargetHandle == 0 &&
        ActiveLifecycleOwnershipKey.IsNone())
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
        if (bGameplayProfilerEnabled)
        {
            FString ProfileError;
            if (!Host->GetManagedScriptProfilerTrace(
                    ManagedScriptInstanceId,
                    LastGameplayProfileTrace,
                    ProfileError) &&
                StopError.IsEmpty())
            {
                StopError = MoveTemp(ProfileError);
            }
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
        if (!ActiveLifecycleOwnershipKey.IsNone() &&
            ActiveLifecycleHostObject.IsValid())
        {
            FString OwnershipError;
            if (!Host->ReleaseGameplayLifecycle(
                    this,
                    ActiveLifecycleHostObject.Get(),
                    ActiveLifecycleOwnershipKey,
                    EUnifiedScriptRuntimeOwner::UnifiedScript,
                    OwnershipError) &&
                StopError.IsEmpty())
            {
                StopError = MoveTemp(OwnershipError);
            }
        }
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

    PreparedScriptReload.Reset();
    ManagedScriptInstanceId = 0;
    ManagedHostHandle = 0;
    ManagedTargetHandle = 0;
    ActiveLifecycleOwnershipKey = NAME_None;
    ActiveLifecycleHostObject.Reset();
    bScriptRunning = false;
    bReloadRequested = false;
    bDebugPausedAtFrameBoundary = false;
    bDebugStepFrameRequested = false;
    bSemanticDebugEnabled = false;
    bSemanticDebugTickActive = false;
    bSemanticDebugPaused = false;
    bGameplayProfilerEnabled = false;
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
    return ReloadScriptWithTransaction(FString());
}

bool UUnifiedScriptComponent::InvokeManagedScriptFunction(
    const FString& FunctionName,
    TConstArrayView<FUnifiedScriptManagedFunctionValueV1> Arguments,
    FUnifiedScriptManagedFunctionValueV1& OutResult,
    FString& OutError)
{
    check(IsInGameThread());
    OutResult = {};
    if (!bScriptRunning || ManagedScriptInstanceId == 0)
    {
        OutError = TEXT("The UnifiedScript component has no running managed Script Class instance.");
        return false;
    }
    UWorld* World = GetWorld();
    UUnifiedScriptWorldSubsystem* Host = World != nullptr
        ? World->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    if (Host == nullptr)
    {
        OutError = TEXT("The UnifiedScript managed World host is unavailable.");
        return false;
    }
    return Host->InvokeManagedScriptFunction(
        ManagedScriptInstanceId,
        FunctionName,
        Arguments,
        OutResult,
        OutError);
}

bool UUnifiedScriptComponent::InvokeManagedScriptFunctionV2(
    const FString& FunctionName,
    FUnifiedScriptNativeCallFrameV3& Frame,
    FString& OutError)
{
    check(IsInGameThread());
    if (!bScriptRunning || ManagedScriptInstanceId == 0)
    {
        OutError = TEXT("The UnifiedScript component has no running managed Script Class instance.");
        return false;
    }
    UWorld* World = GetWorld();
    UUnifiedScriptWorldSubsystem* Host = World != nullptr
        ? World->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    if (Host == nullptr)
    {
        OutError = TEXT("The UnifiedScript managed World host is unavailable.");
        return false;
    }
    return Host->InvokeManagedScriptFunctionV2(
        ManagedScriptInstanceId,
        FString(),
        FString(),
        FunctionName,
        Frame,
        OutError);
}

bool UUnifiedScriptComponent::PreflightScriptReloadWithTransaction(
    const FString& PublicationTransactionJson,
    FString& OutPreflightTransactionJson) const
{
    check(IsInGameThread());
    OutPreflightTransactionJson.Reset();

    const int32 FromGeneration = FCString::Atoi(*ActiveGeneration);
    const int32 CandidateGeneration = bScriptRunning
        ? FMath::Max(1, FromGeneration + 1)
        : FromGeneration;
    UE::UnifiedScript::Component::Private::FReloadTransactionBuilder Transaction;
    FString Error;
    if (!Transaction.Initialize(
            PublicationTransactionJson,
            FromGeneration,
            Error))
    {
        UE::UnifiedScript::Component::Private::FReloadTransactionBuilder Failure;
        FString Ignored;
        Failure.Initialize(FString(), FromGeneration, Ignored);
        Failure.AddPhase(
            TEXT("batchPreflight"),
            TEXT("failed"),
            0.0,
            Error);
        OutPreflightTransactionJson = Failure.Finish(
            TEXT("failed"),
            CandidateGeneration,
            FromGeneration,
            nullptr,
            bSemanticDebugTickActive,
            Error);
        return false;
    }

    const UUnifiedScriptWorldSubsystem* Host = bScriptRunning && GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    const auto Reject = [&Transaction,
                         &OutPreflightTransactionJson,
                         CandidateGeneration,
                         FromGeneration,
                         Host,
                         this](
                            const FString& FailureState,
                            const FString& FailureMessage)
    {
        Transaction.AddPhase(
            TEXT("batchPreflight"),
            FailureState,
            0.0,
            FailureMessage);
        const bool bRestartRequired = FailureState == TEXT("restartRequired");
        OutPreflightTransactionJson = Transaction.Finish(
            bRestartRequired ? TEXT("restartRequired") : TEXT("rolledBack"),
            CandidateGeneration,
            FromGeneration,
            Host,
            bSemanticDebugTickActive,
            FailureMessage);
        return false;
    };

    if (bScriptRunning && Host == nullptr)
    {
        return Reject(
            TEXT("failed"),
            TEXT("The UnifiedScript World host is unavailable during reload preflight."));
    }

    FString ResolvedAssemblyPath;
    FString ResolvedTypeName;
    UUnifiedScriptAsset* ResolvedAsset = nullptr;
    if (!ResolveScriptConfiguration(
            ResolvedAssemblyPath,
            ResolvedTypeName,
            ResolvedAsset,
            Error))
    {
        return Reject(TEXT("failed"), Error);
    }

    if (bScriptRunning)
    {
        const FName CandidateOwnershipKey = LifecycleOwnershipKey.IsNone()
            ? ActiveLifecycleOwnershipKey
            : LifecycleOwnershipKey;
        if (ActiveLifecycleOwnershipKey.IsNone() ||
            CandidateOwnershipKey != ActiveLifecycleOwnershipKey)
        {
            Error = FString::Printf(
                TEXT("Reload cannot change lifecycle ownership from '%s' to '%s'; stop the active runtime before changing coexistence routes."),
                *ActiveLifecycleOwnershipKey.ToString(),
                *CandidateOwnershipKey.ToString());
            return Reject(TEXT("restartRequired"), Error);
        }
        if (!IFileManager::Get().FileExists(*ResolvedAssemblyPath))
        {
            Error = FString::Printf(
                TEXT("The managed script assembly does not exist: '%s'."),
                *ResolvedAssemblyPath);
            return Reject(TEXT("failed"), Error);
        }
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
        return Reject(TEXT("failed"), Error);
    }

    if (Transaction.IsAuthoringPublication() &&
        CandidateMigration.DroppedOverrideCount > 0)
    {
        Error = FString::Printf(
            TEXT("Restart required: this edit would discard %d explicit instance state override(s). No matching instance was reloaded."),
            CandidateMigration.DroppedOverrideCount);
        return Reject(TEXT("restartRequired"), Error);
    }

    Transaction.AddPhase(
        TEXT("batchPreflight"),
        TEXT("succeeded"),
        0.0,
        FString::Printf(
            TEXT("Validated the %s instance candidate and preserved %d explicit state override(s) before the batch reload barrier."),
            bScriptRunning ? TEXT("running") : TEXT("stopped/editor"),
            CandidateOverrideIds.Num()));
    OutPreflightTransactionJson = Transaction.Finish(
        TEXT("preflightPassed"),
        CandidateGeneration,
        FromGeneration,
        Host,
        bSemanticDebugTickActive);
    return true;
}

bool UUnifiedScriptComponent::ReloadScriptWithTransaction(
    const FString& PublicationTransactionJson)
{
    check(IsInGameThread());
    if (!bScriptRunning)
    {
        return StartScript();
    }

    const int32 FromGeneration = FCString::Atoi(*ActiveGeneration);
    const int32 CandidateGeneration = FMath::Max(1, FromGeneration + 1);
    UE::UnifiedScript::Component::Private::FReloadTransactionBuilder Transaction;
    FString TransactionError;
    if (!Transaction.Initialize(
            PublicationTransactionJson,
            FromGeneration,
            TransactionError))
    {
        UE::UnifiedScript::Component::Private::FReloadTransactionBuilder Failure;
        FString Ignored;
        Failure.Initialize(FString(), FromGeneration, Ignored);
        Failure.AddPhase(
            TEXT("publicationProvenance"),
            TEXT("failed"),
            0.0,
            TransactionError);
        LastReloadTransaction = Failure.Finish(
            TEXT("failed"),
            CandidateGeneration,
            FromGeneration,
            nullptr,
            bSemanticDebugTickActive,
            TransactionError);
        SetScriptFailure(MoveTemp(TransactionError));
        return false;
    }

    UUnifiedScriptWorldSubsystem* Host =
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    if (Host == nullptr)
    {
        FString Error = TEXT("The UnifiedScript World host is unavailable during Reload.");
        Transaction.AddPhase(TEXT("worldValidation"), TEXT("failed"), 0.0, Error);
        LastReloadTransaction = Transaction.Finish(
            TEXT("failed"),
            CandidateGeneration,
            FromGeneration,
            nullptr,
            bSemanticDebugTickActive,
            Error);
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    double PhaseStart = FPlatformTime::Seconds();
    bDebugPausedAtFrameBoundary = false;
    bDebugStepFrameRequested = false;
    FString DebugDrainError;
    if (!DisableSemanticDebugAndDrain(DebugDrainError))
    {
        Transaction.AddPhase(
            TEXT("debugDrain"),
            TEXT("failed"),
            PhaseStart,
            DebugDrainError);
        LastReloadTransaction = Transaction.Finish(
            TEXT("rolledBack"),
            CandidateGeneration,
            FromGeneration,
            Host,
            bSemanticDebugTickActive,
            DebugDrainError);
        SetScriptFailure(MoveTemp(DebugDrainError));
        return false;
    }
    Transaction.AddPhase(
        TEXT("debugDrain"),
        TEXT("succeeded"),
        PhaseStart,
        TEXT("Cleared frame-boundary state, disabled semantic execution, and drained its worker before reload."));

    FString Error;
    FString ResolvedAssemblyPath;
    FString ResolvedTypeName;
    UUnifiedScriptAsset* ResolvedAsset = nullptr;
    PhaseStart = FPlatformTime::Seconds();
    if (!ResolveScriptConfiguration(
            ResolvedAssemblyPath,
            ResolvedTypeName,
            ResolvedAsset,
            Error))
    {
        Transaction.AddPhase(
            TEXT("configurationResolution"), TEXT("failed"), PhaseStart, Error);
        LastReloadTransaction = Transaction.Finish(
            TEXT("rolledBack"),
            CandidateGeneration,
            FromGeneration,
            Host,
            false,
            Error);
        SetScriptFailure(MoveTemp(Error));
        return false;
    }
    Transaction.AddPhase(
        TEXT("configurationResolution"),
        TEXT("succeeded"),
        PhaseStart,
        TEXT("Resolved the Script Asset assembly, type, and declaration paths."));
    const FName CandidateOwnershipKey = LifecycleOwnershipKey.IsNone()
        ? ActiveLifecycleOwnershipKey
        : LifecycleOwnershipKey;
    if (ActiveLifecycleOwnershipKey.IsNone() ||
        CandidateOwnershipKey != ActiveLifecycleOwnershipKey)
    {
        Error = FString::Printf(
            TEXT("Reload cannot change lifecycle ownership from '%s' to '%s'; stop the active runtime before changing coexistence routes."),
            *ActiveLifecycleOwnershipKey.ToString(),
            *CandidateOwnershipKey.ToString());
        Transaction.AddPhase(
            TEXT("ownershipValidation"), TEXT("failed"), 0.0, Error);
        LastReloadTransaction = Transaction.Finish(
            TEXT("rolledBack"),
            CandidateGeneration,
            FromGeneration,
            Host,
            false,
            Error);
        SetScriptFailure(MoveTemp(Error));
        return false;
    }
    Transaction.AddPhase(
        TEXT("ownershipValidation"),
        TEXT("succeeded"),
        0.0,
        TEXT("Retained the active World lifecycle claim across the candidate generation."));
    if (!IFileManager::Get().FileExists(*ResolvedAssemblyPath))
    {
        Error = FString::Printf(
            TEXT("The managed script assembly does not exist: '%s'."),
            *ResolvedAssemblyPath);
        Transaction.AddPhase(
            TEXT("artifactValidation"), TEXT("failed"), 0.0, Error);
        LastReloadTransaction = Transaction.Finish(
            TEXT("rolledBack"),
            CandidateGeneration,
            FromGeneration,
            Host,
            false,
            Error);
        SetScriptFailure(MoveTemp(Error));
        return false;
    }
    Transaction.AddPhase(
        TEXT("artifactValidation"),
        TEXT("succeeded"),
        0.0,
        TEXT("Validated that the published managed assembly is readable."));

    FInstancedPropertyBag CandidateScriptState;
    TArray<FGuid> CandidateOverrideIds;
    FUnifiedScriptStateMigrationReport CandidateMigration;
    FString CandidateDeclarationAssetPath;
    PhaseStart = FPlatformTime::Seconds();
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
        Transaction.AddPhase(
            TEXT("stateMigration"), TEXT("failed"), PhaseStart, Error);
        LastReloadTransaction = Transaction.Finish(
            TEXT("rolledBack"),
            CandidateGeneration,
            FromGeneration,
            Host,
            false,
            Error);
        SetScriptFailure(MoveTemp(Error));
        return false;
    }
    Transaction.AddPhase(
        TEXT("stateMigration"),
        TEXT("succeeded"),
        PhaseStart,
        FString::Printf(
            TEXT("Prepared %d state value(s), preserving %d override(s)."),
            CandidateScriptState.GetNumPropertiesInBag(),
            CandidateOverrideIds.Num()));

    if (Transaction.IsAuthoringPublication())
    {
        if (CandidateMigration.DroppedOverrideCount > 0)
        {
            Error = FString::Printf(
                TEXT("Restart required: this edit would discard %d explicit instance state override(s). The active generation and its state were left unchanged."),
                CandidateMigration.DroppedOverrideCount);
            Transaction.AddPhase(
                TEXT("schemaCompatibility"),
                TEXT("restartRequired"),
                0.0,
                Error);
            LastReloadTransaction = Transaction.Finish(
                TEXT("restartRequired"),
                CandidateGeneration,
                FromGeneration,
                Host,
                false,
                Error);
            LastScriptError = Error;
            LastRuntimeDiagnostic = {};
            return false;
        }
        Transaction.AddPhase(
            TEXT("schemaCompatibility"),
            TEXT("succeeded"),
            0.0,
            TEXT("The authoring edit preserves every explicit instance state override and can swap generations transactionally."));
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
    const bool bManagedReloaded = Host->ReloadManagedScript(
            ManagedScriptInstanceId,
            ResolvedAssemblyPath,
            ResolvedTypeName,
            Generation,
            Error);
    FString ManagedTransactionJson;
    FString ManagedTransactionError;
    if (Host->GetManagedScriptReloadState(
            ManagedScriptInstanceId,
            ManagedTransactionJson,
            ManagedTransactionError))
    {
        Transaction.AppendManagedPhases(ManagedTransactionJson);
    }
    else
    {
        Transaction.AddPhase(
            TEXT("managedTransactionRead"),
            TEXT("failed"),
            0.0,
            ManagedTransactionError);
    }
    if (!bManagedReloaded)
    {
        ScriptState = MoveTemp(PreviousScriptState);
        ScriptStateOverrideIds = MoveTemp(PreviousOverrideIds);
        bScriptStateOverrideBaselineInitialized =
            bPreviousOverrideBaselineInitialized;
        MaterializedStateOverrideCount = PreviousOverrideCount;
        Transaction.AddPhase(
            TEXT("stateRollback"),
            TEXT("restored"),
            0.0,
            TEXT("Restored the previous property bag and explicit override identities."));
        PhaseStart = FPlatformTime::Seconds();
        const int32 RetiredLoadContextsAlive =
            FUnifiedScriptCoreClrHost::Get().CollectRetiredManagedLoadContexts();
        Transaction.AddPhase(
            TEXT("oldContextCollection"),
            RetiredLoadContextsAlive == 0 ? TEXT("succeeded") : TEXT("retained"),
            PhaseStart,
            FString::Printf(
                TEXT("Collected rejected candidate contexts; %d retired context(s) remain alive."),
                RetiredLoadContextsAlive));
        LastReloadTransaction = Transaction.Finish(
            TEXT("rolledBack"),
            CandidateGeneration,
            FromGeneration,
            Host,
            false,
            Error,
            RetiredLoadContextsAlive);
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
    Transaction.AddPhase(
        TEXT("stateCommit"),
        TEXT("succeeded"),
        0.0,
        TEXT("Committed the migrated property bag after the managed generation swap."));
    PhaseStart = FPlatformTime::Seconds();
    const int32 RetiredLoadContextsAlive =
        FUnifiedScriptCoreClrHost::Get().CollectRetiredManagedLoadContexts();
    Transaction.AddPhase(
        TEXT("oldContextCollection"),
        RetiredLoadContextsAlive == 0 ? TEXT("succeeded") : TEXT("retained"),
        PhaseStart,
        FString::Printf(
            TEXT("Forced collectible-context reclamation; %d retired context(s) remain alive."),
            RetiredLoadContextsAlive));
    LastReloadTransaction = Transaction.Finish(
        TEXT("committed"),
        CandidateGeneration,
        Generation,
        Host,
        false,
        FString(),
        RetiredLoadContextsAlive);
    LastScriptError.Reset();
    LastRuntimeDiagnostic = {};
    return true;
}

bool UUnifiedScriptComponent::PrepareScriptReloadWithTransaction(
    const FString& PreflightTransactionJson)
{
    check(IsInGameThread());
    if (PreparedScriptReload.IsValid())
    {
        SetScriptFailure(TEXT("A Script Class reload candidate is already prepared."));
        return false;
    }

    const int32 FromGeneration = FCString::Atoi(*ActiveGeneration);
    const int32 ExpectedCandidateGeneration = bScriptRunning
        ? FMath::Max(1, FromGeneration + 1)
        : FromGeneration;
    UE::UnifiedScript::Component::Private::FReloadTransactionBuilder Transaction;
    FString Error;
    if (!Transaction.Initialize(PreflightTransactionJson, FromGeneration, Error))
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    UUnifiedScriptWorldSubsystem* Host = bScriptRunning && GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    if (bScriptRunning && Host == nullptr)
    {
        Error = TEXT("The UnifiedScript World host is unavailable during reload prepare.");
        Transaction.AddPhase(TEXT("batchPrepare"), TEXT("failed"), 0.0, Error);
        LastReloadTransaction = Transaction.Finish(
            TEXT("rolledBack"),
            ExpectedCandidateGeneration,
            FromGeneration,
            nullptr,
            bSemanticDebugTickActive,
            Error);
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    if (bScriptRunning)
    {
        const double DebugDrainStarted = FPlatformTime::Seconds();
        bDebugPausedAtFrameBoundary = false;
        bDebugStepFrameRequested = false;
        if (!DisableSemanticDebugAndDrain(Error))
        {
            Transaction.AddPhase(
                TEXT("debugDrain"), TEXT("failed"), DebugDrainStarted, Error);
            LastReloadTransaction = Transaction.Finish(
                TEXT("rolledBack"),
                ExpectedCandidateGeneration,
                FromGeneration,
                Host,
                bSemanticDebugTickActive,
                Error);
            SetScriptFailure(MoveTemp(Error));
            return false;
        }
        Transaction.AddPhase(
            TEXT("debugDrain"),
            TEXT("succeeded"),
            DebugDrainStarted,
            TEXT("Disabled semantic execution before preparing the retained-generation swap."));
    }

    FString ResolvedAssemblyPath;
    FString ResolvedTypeName;
    UUnifiedScriptAsset* ResolvedAsset = nullptr;
    double PhaseStarted = FPlatformTime::Seconds();
    if (!ResolveScriptConfiguration(
            ResolvedAssemblyPath,
            ResolvedTypeName,
            ResolvedAsset,
            Error))
    {
        Transaction.AddPhase(
            TEXT("configurationResolution"), TEXT("failed"), PhaseStarted, Error);
        LastReloadTransaction = Transaction.Finish(
            TEXT("rolledBack"),
            ExpectedCandidateGeneration,
            FromGeneration,
            Host,
            false,
            Error);
        SetScriptFailure(MoveTemp(Error));
        return false;
    }
    Transaction.AddPhase(
        TEXT("configurationResolution"),
        TEXT("succeeded"),
        PhaseStarted,
        TEXT("Resolved the prepared Script Asset assembly, type, and declaration paths."));

    if (bScriptRunning)
    {
        const FName CandidateOwnershipKey = LifecycleOwnershipKey.IsNone()
            ? ActiveLifecycleOwnershipKey
            : LifecycleOwnershipKey;
        if (ActiveLifecycleOwnershipKey.IsNone() ||
            CandidateOwnershipKey != ActiveLifecycleOwnershipKey)
        {
            Error = FString::Printf(
                TEXT("Reload cannot change lifecycle ownership from '%s' to '%s'; stop the active runtime before changing coexistence routes."),
                *ActiveLifecycleOwnershipKey.ToString(),
                *CandidateOwnershipKey.ToString());
            Transaction.AddPhase(
                TEXT("ownershipValidation"), TEXT("failed"), 0.0, Error);
            LastReloadTransaction = Transaction.Finish(
                TEXT("restartRequired"),
                ExpectedCandidateGeneration,
                FromGeneration,
                Host,
                false,
                Error);
            LastScriptError = Error;
            LastRuntimeDiagnostic = {};
            return false;
        }
        if (!IFileManager::Get().FileExists(*ResolvedAssemblyPath))
        {
            Error = FString::Printf(
                TEXT("The managed script assembly does not exist: '%s'."),
                *ResolvedAssemblyPath);
            Transaction.AddPhase(
                TEXT("artifactValidation"), TEXT("failed"), 0.0, Error);
            LastReloadTransaction = Transaction.Finish(
                TEXT("rolledBack"),
                ExpectedCandidateGeneration,
                FromGeneration,
                Host,
                false,
                Error);
            SetScriptFailure(MoveTemp(Error));
            return false;
        }
    }

    FInstancedPropertyBag CandidateScriptState;
    TArray<FGuid> CandidateOverrideIds;
    FUnifiedScriptStateMigrationReport CandidateMigration;
    FString CandidateDeclarationAssetPath;
    PhaseStarted = FPlatformTime::Seconds();
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
        Transaction.AddPhase(TEXT("stateMigration"), TEXT("failed"), PhaseStarted, Error);
        LastReloadTransaction = Transaction.Finish(
            TEXT("rolledBack"),
            ExpectedCandidateGeneration,
            FromGeneration,
            Host,
            false,
            Error);
        SetScriptFailure(MoveTemp(Error));
        return false;
    }
    Transaction.AddPhase(
        TEXT("stateMigration"),
        TEXT("succeeded"),
        PhaseStarted,
        FString::Printf(
            TEXT("Prepared %d state value(s), preserving %d override(s)."),
            CandidateScriptState.GetNumPropertiesInBag(),
            CandidateOverrideIds.Num()));
    if (Transaction.IsAuthoringPublication() &&
        CandidateMigration.DroppedOverrideCount > 0)
    {
        Error = FString::Printf(
            TEXT("Restart required: this edit would discard %d explicit instance state override(s). No prepared candidate was published."),
            CandidateMigration.DroppedOverrideCount);
        Transaction.AddPhase(
            TEXT("schemaCompatibility"), TEXT("restartRequired"), 0.0, Error);
        LastReloadTransaction = Transaction.Finish(
            TEXT("restartRequired"),
            ExpectedCandidateGeneration,
            FromGeneration,
            Host,
            false,
            Error);
        LastScriptError = Error;
        LastRuntimeDiagnostic = {};
        return false;
    }

    TUniquePtr<FPreparedScriptReloadState> Prepared =
        MakeUnique<FPreparedScriptReloadState>();
    Prepared->bWasRunning = bScriptRunning;
    Prepared->FromGeneration = FromGeneration;
    Prepared->CandidateGeneration = ExpectedCandidateGeneration;
    Prepared->PreviousScriptState = ScriptState;
    Prepared->PreviousOverrideIds = ScriptStateOverrideIds;
    Prepared->bPreviousOverrideBaselineInitialized =
        bScriptStateOverrideBaselineInitialized;
    Prepared->PreviousMaterializedStateCount = MaterializedStateCount;
    Prepared->PreviousMaterializedStateOverrideCount =
        MaterializedStateOverrideCount;
    Prepared->PreviousLastStateMigration = LastStateMigration;
    Prepared->PreviousManagedState = ManagedState;
    Prepared->bPreviousStartAutomatically = bStartAutomatically;
    Prepared->bPreviousTickAutomatically = bTickAutomatically;
    Prepared->PreviousActiveAsset = ActiveScriptAsset;
    Prepared->PreviousActiveScriptAssetPath = ActiveScriptAssetPath;
    Prepared->PreviousActiveDeclarationAssetPath = ActiveDeclarationAssetPath;
    Prepared->PreviousResolvedAssemblyPath = ActiveResolvedAssemblyPath;
    Prepared->PreviousResolvedTypeName = ActiveResolvedTypeName;
    Prepared->CandidateAsset = ResolvedAsset;
    Prepared->CandidateDeclarationAssetPath = CandidateDeclarationAssetPath;
    Prepared->CandidateResolvedAssemblyPath = ResolvedAssemblyPath;
    Prepared->CandidateResolvedTypeName = ResolvedTypeName;

    ScriptState = MoveTemp(CandidateScriptState);
    ScriptStateOverrideIds = MoveTemp(CandidateOverrideIds);
    bScriptStateOverrideBaselineInitialized = true;
    ApplyStateMigrationDiagnostics(CandidateMigration);

    if (bScriptRunning)
    {
        int32 ManagedCandidateGeneration = 0;
        const bool bManagedPrepared = Host->PrepareManagedScriptReload(
            ManagedScriptInstanceId,
            ResolvedAssemblyPath,
            ResolvedTypeName,
            ManagedCandidateGeneration,
            Error);
        FString ManagedTransactionJson;
        FString ManagedTransactionError;
        if (Host->GetManagedScriptReloadState(
                ManagedScriptInstanceId,
                ManagedTransactionJson,
                ManagedTransactionError))
        {
            Transaction.AppendManagedPhases(ManagedTransactionJson);
        }
        else
        {
            Transaction.AddPhase(
                TEXT("managedTransactionRead"),
                TEXT("failed"),
                0.0,
                ManagedTransactionError);
        }
        if (!bManagedPrepared)
        {
            ScriptState = MoveTemp(Prepared->PreviousScriptState);
            ScriptStateOverrideIds = MoveTemp(Prepared->PreviousOverrideIds);
            bScriptStateOverrideBaselineInitialized =
                Prepared->bPreviousOverrideBaselineInitialized;
            MaterializedStateCount = Prepared->PreviousMaterializedStateCount;
            MaterializedStateOverrideCount =
                Prepared->PreviousMaterializedStateOverrideCount;
            LastStateMigration = Prepared->PreviousLastStateMigration;
            ManagedState = Prepared->PreviousManagedState;
            bStartAutomatically = Prepared->bPreviousStartAutomatically;
            bTickAutomatically = Prepared->bPreviousTickAutomatically;
            Transaction.AddPhase(
                TEXT("batchPrepare"),
                TEXT("failed"),
                0.0,
                TEXT("The managed candidate failed before the batch commit barrier; the retained generation was restored."));
            LastReloadTransaction = Transaction.Finish(
                TEXT("rolledBack"),
                ExpectedCandidateGeneration,
                FromGeneration,
                Host,
                false,
                Error);
            SetScriptFailure(MoveTemp(Error));
            return false;
        }
        Prepared->CandidateGeneration = ManagedCandidateGeneration;
    }

    Transaction.AddPhase(
        TEXT("batchPrepare"),
        TEXT("succeeded"),
        0.0,
        bScriptRunning
            ? TEXT("Loaded, started, and rebound the candidate while retaining the stopped previous generation for batch rollback.")
            : TEXT("Prepared compatible stopped/editor state without mutating its committed declaration identity."));
    Prepared->TransactionJson = Transaction.Finish(
        TEXT("prepared"),
        Prepared->CandidateGeneration,
        FromGeneration,
        Host,
        false);
    LastReloadTransaction = Prepared->TransactionJson;
    PreparedScriptReload = MoveTemp(Prepared);
    LastScriptError.Reset();
    LastRuntimeDiagnostic = {};
    return true;
}

bool UUnifiedScriptComponent::CommitPreparedScriptReload()
{
    check(IsInGameThread());
    if (!PreparedScriptReload.IsValid())
    {
        SetScriptFailure(TEXT("No Script Class reload candidate is prepared."));
        return false;
    }
    if (PreparedScriptReload->bCommitted)
    {
        return true;
    }

    UUnifiedScriptWorldSubsystem* Host = PreparedScriptReload->bWasRunning &&
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    UE::UnifiedScript::Component::Private::FReloadTransactionBuilder Transaction;
    FString Error;
    if (!Transaction.Initialize(
            PreparedScriptReload->TransactionJson,
            PreparedScriptReload->FromGeneration,
            Error))
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    int32 ActiveCandidateGeneration = PreparedScriptReload->CandidateGeneration;
    if (PreparedScriptReload->bWasRunning)
    {
        if (Host == nullptr ||
            !Host->CommitManagedScriptReload(
                ManagedScriptInstanceId,
                PreparedScriptReload->CandidateGeneration,
                ActiveCandidateGeneration,
                Error))
        {
            Transaction.AddPhase(TEXT("batchCommit"), TEXT("failed"), 0.0, Error);
            LastReloadTransaction = Transaction.Finish(
                TEXT("failed"),
                PreparedScriptReload->CandidateGeneration,
                PreparedScriptReload->FromGeneration,
                Host,
                false,
                Error);
            SetScriptFailure(MoveTemp(Error));
            return false;
        }
        FString ManagedJson;
        FString ManagedError;
        if (Host->GetManagedScriptReloadState(
                ManagedScriptInstanceId, ManagedJson, ManagedError))
        {
            Transaction.AppendManagedPhases(ManagedJson);
        }
    }

    if (PreparedScriptReload->bWasRunning)
    {
        ActiveGeneration = FString::FromInt(ActiveCandidateGeneration);
        ActiveScriptAsset = PreparedScriptReload->CandidateAsset.Get();
        ActiveScriptAssetPath = ActiveScriptAsset != nullptr
            ? ActiveScriptAsset->GetPathName()
            : FString();
        ActiveResolvedAssemblyPath = PreparedScriptReload->CandidateResolvedAssemblyPath;
        ActiveResolvedTypeName = PreparedScriptReload->CandidateResolvedTypeName;
    }
    ActiveDeclarationAssetPath =
        PreparedScriptReload->CandidateDeclarationAssetPath;
    Transaction.AddPhase(
        TEXT("stateCommit"),
        TEXT("succeeded"),
        0.0,
        PreparedScriptReload->bWasRunning
            ? TEXT("Committed migrated state with the prepared managed generation pointer swap.")
            : TEXT("Committed compatible declaration state without starting managed code."));
    Transaction.AddPhase(
        TEXT("batchCommit"),
        TEXT("succeeded"),
        0.0,
        TEXT("Published the prepared candidate without lifecycle callbacks or old-generation retirement."));
    PreparedScriptReload->bCommitted = true;
    LastReloadTransaction = Transaction.Finish(
        TEXT("commitPendingFinalize"),
        PreparedScriptReload->CandidateGeneration,
        PreparedScriptReload->bWasRunning
            ? ActiveCandidateGeneration
            : PreparedScriptReload->FromGeneration,
        Host,
        false);
    PreparedScriptReload->TransactionJson = LastReloadTransaction;
    LastScriptError.Reset();
    LastRuntimeDiagnostic = {};
    return true;
}

bool UUnifiedScriptComponent::AbortPreparedScriptReload(const FString& Reason)
{
    check(IsInGameThread());
    if (!PreparedScriptReload.IsValid())
    {
        return true;
    }

    UUnifiedScriptWorldSubsystem* Host = PreparedScriptReload->bWasRunning &&
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    UE::UnifiedScript::Component::Private::FReloadTransactionBuilder Transaction;
    FString Error;
    if (!Transaction.Initialize(
            PreparedScriptReload->TransactionJson,
            PreparedScriptReload->FromGeneration,
            Error))
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    bool bManagedRestored = true;
    FString ManagedError;
    if (PreparedScriptReload->bWasRunning)
    {
        bManagedRestored = Host != nullptr && Host->AbortManagedScriptReload(
            ManagedScriptInstanceId,
            PreparedScriptReload->CandidateGeneration,
            ManagedError);
        if (Host != nullptr)
        {
            FString ManagedJson;
            FString ManagedTransactionError;
            if (Host->GetManagedScriptReloadState(
                    ManagedScriptInstanceId,
                    ManagedJson,
                    ManagedTransactionError))
            {
                Transaction.AppendManagedPhases(ManagedJson);
            }
        }
    }

    ScriptState = MoveTemp(PreparedScriptReload->PreviousScriptState);
    ScriptStateOverrideIds = MoveTemp(PreparedScriptReload->PreviousOverrideIds);
    bScriptStateOverrideBaselineInitialized =
        PreparedScriptReload->bPreviousOverrideBaselineInitialized;
    MaterializedStateCount = PreparedScriptReload->PreviousMaterializedStateCount;
    MaterializedStateOverrideCount =
        PreparedScriptReload->PreviousMaterializedStateOverrideCount;
    LastStateMigration = PreparedScriptReload->PreviousLastStateMigration;
    ManagedState = PreparedScriptReload->PreviousManagedState;
    bStartAutomatically = PreparedScriptReload->bPreviousStartAutomatically;
    bTickAutomatically = PreparedScriptReload->bPreviousTickAutomatically;
    ActiveGeneration = FString::FromInt(PreparedScriptReload->FromGeneration);
    ActiveScriptAsset = PreparedScriptReload->PreviousActiveAsset.Get();
    ActiveScriptAssetPath = PreparedScriptReload->PreviousActiveScriptAssetPath;
    ActiveDeclarationAssetPath =
        PreparedScriptReload->PreviousActiveDeclarationAssetPath;
    ActiveResolvedAssemblyPath =
        PreparedScriptReload->PreviousResolvedAssemblyPath;
    ActiveResolvedTypeName = PreparedScriptReload->PreviousResolvedTypeName;
    Transaction.AddPhase(
        TEXT("stateRollback"),
        TEXT("restored"),
        0.0,
        TEXT("Restored the exact native state bag, overrides, active identity, and component lifecycle settings."));
    Transaction.AddPhase(
        TEXT("batchAbort"),
        bManagedRestored ? TEXT("restored") : TEXT("failed"),
        0.0,
        bManagedRestored
            ? TEXT("Discarded the prepared candidate and restarted the retained previous generation.")
            : ManagedError);
    const int32 CandidateGeneration = PreparedScriptReload->CandidateGeneration;
    const int32 FromGeneration = PreparedScriptReload->FromGeneration;
    PreparedScriptReload.Reset();
    const FString Failure = bManagedRestored
        ? Reason
        : FString::Printf(TEXT("%s %s"), *Reason, *ManagedError);
    LastReloadTransaction = Transaction.Finish(
        bManagedRestored ? TEXT("rolledBack") : TEXT("failed"),
        CandidateGeneration,
        FromGeneration,
        Host,
        false,
        Failure);
    if (!bManagedRestored)
    {
        SetScriptFailure(Failure);
        return false;
    }
    LastScriptError = Failure;
    LastRuntimeDiagnostic = {};
    return true;
}

bool UUnifiedScriptComponent::FinalizePreparedScriptReload()
{
    check(IsInGameThread());
    if (!PreparedScriptReload.IsValid() || !PreparedScriptReload->bCommitted)
    {
        SetScriptFailure(TEXT("A committed Script Class reload candidate is required before finalization."));
        return false;
    }

    UUnifiedScriptWorldSubsystem* Host = PreparedScriptReload->bWasRunning &&
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    UE::UnifiedScript::Component::Private::FReloadTransactionBuilder Transaction;
    FString Error;
    if (!Transaction.Initialize(
            PreparedScriptReload->TransactionJson,
            PreparedScriptReload->FromGeneration,
            Error))
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    bool bFinalized = true;
    if (PreparedScriptReload->bWasRunning)
    {
        bFinalized = Host != nullptr && Host->FinalizeManagedScriptReload(
            ManagedScriptInstanceId,
            PreparedScriptReload->CandidateGeneration,
            Error);
        if (Host != nullptr)
        {
            FString ManagedJson;
            FString ManagedTransactionError;
            if (Host->GetManagedScriptReloadState(
                    ManagedScriptInstanceId,
                    ManagedJson,
                    ManagedTransactionError))
            {
                Transaction.AppendManagedPhases(ManagedJson);
            }
        }
    }
    Transaction.AddPhase(
        TEXT("batchFinalize"),
        bFinalized ? TEXT("succeeded") : TEXT("failed"),
        0.0,
        bFinalized
            ? TEXT("Retired the retained previous generation after every batch member committed.")
            : Error);
    const int32 CandidateGeneration = PreparedScriptReload->CandidateGeneration;
    const int32 ActiveWorldGeneration = PreparedScriptReload->bWasRunning
        ? FCString::Atoi(*ActiveGeneration)
        : PreparedScriptReload->FromGeneration;
    PreparedScriptReload.Reset();
    const int32 RetiredLoadContextsAlive = bFinalized
        ? FUnifiedScriptCoreClrHost::Get().CollectRetiredManagedLoadContexts()
        : INDEX_NONE;
    LastReloadTransaction = Transaction.Finish(
        bFinalized ? TEXT("committed") : TEXT("failed"),
        CandidateGeneration,
        ActiveWorldGeneration,
        Host,
        false,
        bFinalized ? FString() : Error,
        RetiredLoadContextsAlive);
    if (!bFinalized)
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }
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

bool UUnifiedScriptComponent::SetGameplayProfilerEnabled(
    bool bEnabled,
    FString& OutError)
{
    check(IsInGameThread());
    if (!bScriptRunning || ManagedScriptInstanceId == 0)
    {
        OutError = TEXT("A live managed script is required before profiling.");
        return false;
    }
    UUnifiedScriptWorldSubsystem* Host =
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    if (Host == nullptr || !Host->SetManagedScriptProfilerEnabled(
            ManagedScriptInstanceId,
            bEnabled,
            OutError))
    {
        if (Host == nullptr)
        {
            OutError = TEXT("The UnifiedScript World host is unavailable during profiler control.");
        }
        return false;
    }
    bGameplayProfilerEnabled = bEnabled;
    LastGameplayProfileTrace.Reset();
    OutError.Reset();
    return true;
}

bool UUnifiedScriptComponent::CaptureGameplayProfileTrace(
    FString& OutJson,
    FString& OutError)
{
    check(IsInGameThread());
    UUnifiedScriptWorldSubsystem* Host =
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    if (!bScriptRunning || ManagedScriptInstanceId == 0 || Host == nullptr ||
        !Host->GetManagedScriptProfilerTrace(
            ManagedScriptInstanceId,
            OutJson,
            OutError))
    {
        if (!bScriptRunning || ManagedScriptInstanceId == 0)
        {
            OutError = TEXT("A live managed script is required before profile capture.");
        }
        else if (Host == nullptr)
        {
            OutError = TEXT("The UnifiedScript World host is unavailable during profile capture.");
        }
        OutJson.Reset();
        return false;
    }
    LastGameplayProfileTrace = OutJson;
    OutError.Reset();
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

bool UUnifiedScriptComponent::RefreshScriptStateWithTransaction(
    const FString& PublicationTransactionJson)
{
    check(IsInGameThread());
    if (PublicationTransactionJson.IsEmpty())
    {
        return RefreshScriptState();
    }

    const int32 Generation = FCString::Atoi(*ActiveGeneration);
    UE::UnifiedScript::Component::Private::FReloadTransactionBuilder Transaction;
    FString Error;
    if (!Transaction.Initialize(PublicationTransactionJson, Generation, Error))
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    FString ResolvedAssemblyPath;
    FString ResolvedTypeName;
    UUnifiedScriptAsset* ResolvedAsset = nullptr;
    double PhaseStart = FPlatformTime::Seconds();
    if (!ResolveScriptConfiguration(
            ResolvedAssemblyPath,
            ResolvedTypeName,
            ResolvedAsset,
            Error))
    {
        Transaction.AddPhase(
            TEXT("configurationResolution"),
            TEXT("failed"),
            PhaseStart,
            Error);
        LastReloadTransaction = Transaction.Finish(
            TEXT("rolledBack"), Generation, Generation, nullptr, false, Error);
        SetScriptFailure(MoveTemp(Error));
        return false;
    }
    Transaction.AddPhase(
        TEXT("configurationResolution"),
        TEXT("succeeded"),
        PhaseStart,
        TEXT("Resolved the stopped/editor instance Script Asset and declaration paths."));

    FInstancedPropertyBag Candidate;
    TArray<FGuid> CandidateOverrideIds;
    FUnifiedScriptStateMigrationReport CandidateMigration;
    FString CandidateAssetPath;
    PhaseStart = FPlatformTime::Seconds();
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
        Transaction.AddPhase(TEXT("stateMigration"), TEXT("failed"), PhaseStart, Error);
        LastReloadTransaction = Transaction.Finish(
            TEXT("rolledBack"), Generation, Generation, nullptr, false, Error);
        SetScriptFailure(MoveTemp(Error));
        return false;
    }
    Transaction.AddPhase(
        TEXT("stateMigration"),
        TEXT("succeeded"),
        PhaseStart,
        FString::Printf(
            TEXT("Prepared %d state value(s), preserving %d override(s)."),
            Candidate.GetNumPropertiesInBag(),
            CandidateOverrideIds.Num()));

    if (Transaction.IsAuthoringPublication() && CandidateMigration.DroppedOverrideCount > 0)
    {
        Error = FString::Printf(
            TEXT("Restart required: this edit would discard %d explicit instance state override(s). The stopped/editor instance state was left unchanged."),
            CandidateMigration.DroppedOverrideCount);
        Transaction.AddPhase(
            TEXT("schemaCompatibility"),
            TEXT("restartRequired"),
            0.0,
            Error);
        LastReloadTransaction = Transaction.Finish(
            TEXT("restartRequired"), Generation, Generation, nullptr, false, Error);
        LastScriptError = Error;
        LastRuntimeDiagnostic = {};
        return false;
    }

    Transaction.AddPhase(
        TEXT("schemaCompatibility"),
        TEXT("succeeded"),
        0.0,
        TEXT("The authoring edit preserves every explicit stopped/editor instance state override."));
    ScriptState = MoveTemp(Candidate);
    ScriptStateOverrideIds = MoveTemp(CandidateOverrideIds);
    bScriptStateOverrideBaselineInitialized = true;
    ActiveDeclarationAssetPath = MoveTemp(CandidateAssetPath);
    ApplyStateMigrationDiagnostics(CandidateMigration);
    Transaction.AddPhase(
        TEXT("stateCommit"),
        TEXT("succeeded"),
        0.0,
        TEXT("Committed the compatible declaration state without starting a managed generation."));
    LastReloadTransaction = Transaction.Finish(
        TEXT("committed"), Generation, Generation, nullptr, false);
    LastScriptError.Reset();
    LastRuntimeDiagnostic = {};
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

const UUnifiedScriptBlueprintGeneratedClass*
UUnifiedScriptComponent::GetActiveManagedGeneratedClass() const
{
    if (!bScriptRunning || ActiveScriptAsset == nullptr ||
        !ActiveScriptAsset->GeneratedClassPath.IsValid())
    {
        return nullptr;
    }
    UClass* ExpectedClass =
        ActiveScriptAsset->GeneratedClassPath.TryLoadClass<UObject>();
    if (ExpectedClass == nullptr)
    {
        return nullptr;
    }
    UObject* Receiver = GetParentInvocationReceiver();
    for (UClass* Candidate = Receiver != nullptr ? Receiver->GetClass() : nullptr;
         Candidate != nullptr;
         Candidate = Candidate->GetSuperClass())
    {
        if (Candidate == ExpectedClass)
        {
            return Cast<UUnifiedScriptBlueprintGeneratedClass>(Candidate);
        }
    }
    return nullptr;
}

UObject* UUnifiedScriptComponent::GetParentInvocationReceiver() const
{
    if (!bScriptRunning)
    {
        return nullptr;
    }
    if (ActiveScriptAsset != nullptr &&
        ActiveScriptAsset->ScriptClassKind == EUnifiedScriptClassKind::ActorComponent)
    {
        return const_cast<UUnifiedScriptComponent*>(this);
    }
    return const_cast<UUnifiedScriptComponent*>(this)->GetScriptTarget();
}

bool UUnifiedScriptComponent::OwnsActiveManagedOverride(
    const UUnifiedScriptBlueprintGeneratedClass* GeneratedClass) const
{
    return GeneratedClass != nullptr &&
        GetActiveManagedGeneratedClass() == GeneratedClass;
}

bool UUnifiedScriptComponent::BindManagedInputActions(FString& OutError)
{
    check(IsInGameThread());
    OutError.Reset();
    if (!ManagedInputBindingHandles.IsEmpty() ||
        ManagedEnhancedInputComponent.IsValid())
    {
        OutError = TEXT("Managed Enhanced Input bindings are already active.");
        return false;
    }

    const UUnifiedScriptBlueprintGeneratedClass* GeneratedClass = nullptr;
    for (const UClass* Candidate = GetClass();
         Candidate != nullptr && GeneratedClass == nullptr;
         Candidate = Candidate->GetSuperClass())
    {
        GeneratedClass = Cast<UUnifiedScriptBlueprintGeneratedClass>(Candidate);
    }
    if (GeneratedClass == nullptr)
    {
        const AActor* Owner = GetOwner();
        for (const UClass* Candidate = Owner != nullptr ? Owner->GetClass() : nullptr;
             Candidate != nullptr && GeneratedClass == nullptr;
             Candidate = Candidate->GetSuperClass())
        {
            GeneratedClass = Cast<UUnifiedScriptBlueprintGeneratedClass>(Candidate);
        }
    }
    if (GeneratedClass == nullptr ||
        GeneratedClass->ManagedInputActionBindings.IsEmpty())
    {
        return true;
    }

    struct FResolvedInputBinding
    {
        const FUnifiedScriptManagedInputActionBinding* Declaration = nullptr;
        UInputAction* Action = nullptr;
    };
    TArray<FResolvedInputBinding> ResolvedBindings;
    ResolvedBindings.Reserve(GeneratedClass->ManagedInputActionBindings.Num());
    for (const FUnifiedScriptManagedInputActionBinding& Declaration :
         GeneratedClass->ManagedInputActionBindings)
    {
        UInputAction* Action = LoadObject<UInputAction>(
            nullptr,
            *Declaration.ActionPath);
        if (Action == nullptr)
        {
            UClass* ActionClass = LoadObject<UClass>(
                nullptr,
                *Declaration.ActionPath);
            if (ActionClass != nullptr &&
                ActionClass->IsChildOf(UInputAction::StaticClass()))
            {
                Action = ActionClass->GetDefaultObject<UInputAction>();
            }
        }
        const ETriggerEvent TriggerEvent =
            UE::UnifiedScript::Component::Private::ToEnhancedInputTriggerEvent(
                Declaration.TriggerEvent);
        if (Action == nullptr || TriggerEvent == ETriggerEvent::None)
        {
            OutError = FString::Printf(
                TEXT("Managed input handler '%s' cannot resolve action '%s' or its trigger event."),
                *Declaration.ManagedName.ToString(),
                *Declaration.ActionPath);
            return false;
        }
        if (!UE::UnifiedScript::Component::Private::InputValueTypeMatches(
                Declaration.ValueType,
                Action->ValueType))
        {
            OutError = FString::Printf(
                TEXT("Managed input handler '%s' value contract does not match action '%s'."),
                *Declaration.ManagedName.ToString(),
                *Declaration.ActionPath);
            return false;
        }
        FResolvedInputBinding& Resolved =
            ResolvedBindings.AddDefaulted_GetRef();
        Resolved.Declaration = &Declaration;
        Resolved.Action = Action;
    }

    AActor* Owner = GetOwner();
    if (Owner == nullptr)
    {
        OutError = TEXT("Managed Enhanced Input bindings require an owning Actor.");
        return false;
    }
    bManagedCreatedInputComponent = Owner->InputComponent == nullptr;
    if (bManagedCreatedInputComponent)
    {
        Owner->CreateInputComponent(UEnhancedInputComponent::StaticClass());
    }
    UEnhancedInputComponent* EnhancedInput =
        Cast<UEnhancedInputComponent>(Owner->InputComponent);
    if (EnhancedInput == nullptr)
    {
        bManagedCreatedInputComponent = false;
        OutError = FString::Printf(
            TEXT("Actor '%s' uses input component '%s'; managed input bindings require UEnhancedInputComponent."),
            *Owner->GetPathName(),
            *GetPathNameSafe(Owner->InputComponent));
        return false;
    }

    ManagedEnhancedInputComponent = EnhancedInput;
    if (bManagedCreatedInputComponent)
    {
        if (UWorld* World = GetWorld())
        {
            if (APlayerController* PlayerController =
                    World->GetFirstPlayerController())
            {
                Owner->EnableInput(PlayerController);
                ManagedInputPlayerController = PlayerController;
            }
        }
    }

    ManagedInputActions.Reserve(ResolvedBindings.Num());
    ManagedInputBindingHandles.Reserve(ResolvedBindings.Num());
    for (const FResolvedInputBinding& Resolved : ResolvedBindings)
    {
        const FUnifiedScriptManagedInputActionBinding& Declaration =
            *Resolved.Declaration;
        FEnhancedInputActionEventBinding& RuntimeBinding =
            EnhancedInput->BindAction(
                Resolved.Action,
                UE::UnifiedScript::Component::Private::ToEnhancedInputTriggerEvent(
                    Declaration.TriggerEvent),
                this,
                &UUnifiedScriptComponent::HandleManagedInputAction,
                Declaration.ManagedName,
                Declaration.ValueType);
        RuntimeBinding.SetShouldConsume(Declaration.bConsumeInput);
        ManagedInputBindingHandles.Add(RuntimeBinding.GetHandle());
        ManagedInputActions.Add(Resolved.Action);
    }
    return true;
}

void UUnifiedScriptComponent::UnbindManagedInputActions()
{
    check(IsInGameThread());
    UEnhancedInputComponent* EnhancedInput =
        ManagedEnhancedInputComponent.Get();
    if (EnhancedInput != nullptr)
    {
        for (const uint32 Handle : ManagedInputBindingHandles)
        {
            EnhancedInput->RemoveBindingByHandle(Handle);
        }
    }
    ManagedInputBindingHandles.Reset();
    ManagedInputActions.Reset();

    if (bManagedCreatedInputComponent && EnhancedInput != nullptr)
    {
        if (AActor* Owner = GetOwner())
        {
            if (Owner->InputComponent == EnhancedInput)
            {
                Owner->DisableInput(ManagedInputPlayerController.Get());
                Owner->InputComponent = nullptr;
            }
        }
        EnhancedInput->DestroyComponent();
    }
    bManagedCreatedInputComponent = false;
    ManagedEnhancedInputComponent.Reset();
    ManagedInputPlayerController.Reset();
}

void UUnifiedScriptComponent::HandleManagedInputAction(
    const FInputActionInstance& ActionInstance,
    FName ManagedName,
    EUnifiedScriptManagedInputValueType ValueType)
{
    if (!bScriptRunning || ManagedScriptInstanceId == 0 || ManagedName.IsNone())
    {
        return;
    }

    const FInputActionValue InputValue = ActionInstance.GetValue();
    FUnifiedScriptNativeValueV3 Argument;
    switch (ValueType)
    {
        case EUnifiedScriptManagedInputValueType::Boolean:
            Argument.Kind = EUnifiedScriptNativeValueKindV3::Boolean;
            Argument.Payload0 = InputValue.Get<bool>() ? 1 : 0;
            break;
        case EUnifiedScriptManagedInputValueType::Axis1D:
            Argument.Kind = EUnifiedScriptNativeValueKindV3::Float32;
            UE::UnifiedScript::Component::Private::StoreFloat32(
                InputValue.Get<float>(),
                Argument.Payload0);
            break;
        case EUnifiedScriptManagedInputValueType::Axis2D:
        {
            const FVector2D Axis = InputValue.Get<FVector2D>();
            Argument.Kind = EUnifiedScriptNativeValueKindV3::Vector3;
            UE::UnifiedScript::Component::Private::StoreFloat64(
                Axis.X,
                Argument.Payload0);
            UE::UnifiedScript::Component::Private::StoreFloat64(
                Axis.Y,
                Argument.Payload1);
            UE::UnifiedScript::Component::Private::StoreFloat64(
                0.0,
                Argument.Payload2);
            break;
        }
        case EUnifiedScriptManagedInputValueType::Axis3D:
        {
            const FVector Axis = InputValue.Get<FVector>();
            Argument.Kind = EUnifiedScriptNativeValueKindV3::Vector3;
            UE::UnifiedScript::Component::Private::StoreFloat64(
                Axis.X,
                Argument.Payload0);
            UE::UnifiedScript::Component::Private::StoreFloat64(
                Axis.Y,
                Argument.Payload1);
            UE::UnifiedScript::Component::Private::StoreFloat64(
                Axis.Z,
                Argument.Payload2);
            break;
        }
        default:
            SetScriptFailure(FString::Printf(
                TEXT("Managed input handler '%s' has an unsupported value contract."),
                *ManagedName.ToString()));
            bStopRequestedByInputFailure = true;
            SetComponentTickEnabled(true);
            return;
    }

    uint8 Arena[512] = {};
    FUnifiedScriptNativeCallFrameV3 Frame;
    Frame.Arguments = &Argument;
    Frame.ArgumentCount = 1;
    Frame.ByteBuffer = Arena;
    Frame.ByteCapacity = UE_ARRAY_COUNT(Arena);
    FString Error;
    if (!InvokeManagedScriptFunctionV2(ManagedName.ToString(), Frame, Error) ||
        Frame.Status != EUnifiedScriptNativeCallStatusV3::Success ||
        Frame.ResultCount != 0)
    {
        if (Error.IsEmpty())
        {
            Error = FString::Printf(
                TEXT("Managed input handler '%s' failed ABI v2 dispatch (status=%u, results=%u)."),
                *ManagedName.ToString(),
                static_cast<uint32>(Frame.Status),
                Frame.ResultCount);
        }
        SetScriptFailure(MoveTemp(Error));
        bStopRequestedByInputFailure = true;
        SetComponentTickEnabled(true);
    }
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

FVector UUnifiedScriptComponent::AddBenchmarkVector(
    FVector Left,
    FVector Right) const
{
    return Left + Right;
}

TArray<int32> UUnifiedScriptComponent::EchoBenchmarkArray(
    const TArray<int32>& Values) const
{
    return Values;
}

TMap<int32, int32> UUnifiedScriptComponent::EchoBenchmarkMap(
    const TMap<int32, int32>& Values) const
{
    return Values;
}

void UUnifiedScriptComponent::ResetBenchmarkActorState()
{
    BenchmarkActorLocation = FVector::ZeroVector;
    BenchmarkActorRotation = FRotator::ZeroRotator;
    if (AActor* Owner = GetOwner())
    {
        Owner->SetActorLocationAndRotation(
            BenchmarkActorLocation,
            BenchmarkActorRotation,
            false,
            nullptr,
            ETeleportType::TeleportPhysics);
    }
}

FVector UUnifiedScriptComponent::ApplyBenchmarkActorInput(
    float MoveForward,
    float MoveRight,
    float LookHorizontal,
    float LookVertical)
{
    BenchmarkActorLocation.X += static_cast<double>(MoveForward);
    BenchmarkActorLocation.Y += static_cast<double>(MoveRight);
    BenchmarkActorRotation.Yaw += static_cast<double>(LookHorizontal);
    BenchmarkActorRotation.Pitch += static_cast<double>(LookVertical);
    if (AActor* Owner = GetOwner())
    {
        Owner->SetActorLocationAndRotation(
            BenchmarkActorLocation,
            BenchmarkActorRotation,
            false,
            nullptr,
            ETeleportType::TeleportPhysics);
    }
    return FVector(
        BenchmarkActorLocation.X,
        BenchmarkActorLocation.Y,
        BenchmarkActorRotation.Yaw);
}

void UUnifiedScriptComponent::BroadcastManagedSignal(int32 Value, UObject* Context)
{
    OnManagedSignal.Broadcast(Value, Context);
}

void UUnifiedScriptComponent::BroadcastBenchmarkSignal(
    int32 Value,
    float Weight,
    bool bEnabled,
    UObject* Context)
{
    OnBenchmarkSignal.Broadcast(Value, Weight, bEnabled, Context);
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
                UE::UnifiedScript::Component::Private::ShouldPreferCompiledChunks(),
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
    const FString CompiledChunkPath = ResolvedAsset->ResolveCompiledChunkPath();
    if (UE::UnifiedScript::Component::Private::ShouldPreferCompiledChunks() &&
        !CompiledChunkPath.IsEmpty())
    {
        const bool bMaterialized = FUnifiedScriptDeclarationMaterializer::MaterializeChunkFileWithOverrides(
            ResolveConfiguredPath(CompiledChunkPath),
            RequestedAssetPath,
            ExistingState,
            ExistingOverrideIds,
            OutState,
            OutOverrideIds,
            OutMigration,
            OutDeclarationAssetPath,
            OutError);
        return bMaterialized && ResolvedAsset->ValidateDeclarationIdentity(
            OutMigration.AssetId,
            OutMigration.AssetQualifiedName,
            OutError);
    }
    if (DeclarationPath.IsEmpty())
    {
        if (ResolvedAsset->ScriptClassId.IsValid())
        {
            OutError = TEXT("A persistent UnifiedScript Class requires declaration identity data.");
            return false;
        }
        OutState.Reset();
        OutMigration.DroppedOverrideCount = ExistingOverrideIds.Num();
        OutError.Reset();
        return true;
    }

    const bool bMaterialized = FUnifiedScriptDeclarationMaterializer::MaterializeFileWithOverrides(
        ResolveConfiguredPath(DeclarationPath),
        RequestedAssetPath,
        ExistingState,
        ExistingOverrideIds,
        OutState,
        OutOverrideIds,
        OutMigration,
        OutDeclarationAssetPath,
        OutError);
    return bMaterialized && ResolvedAsset->ValidateDeclarationIdentity(
        OutMigration.AssetId,
        OutMigration.AssetQualifiedName,
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
        FUnifiedScriptRuntimeBreadcrumbs::RecordDiagnostic(LastRuntimeDiagnostic);
    }
    else
    {
        LastScriptError = MoveTemp(Error);
        UUnifiedScriptWorldSubsystem* Host =
            GetWorld() != nullptr
            ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
            : nullptr;
        FUnifiedScriptRuntimeBreadcrumbs::RecordFailure(
            TEXT("ComponentBoundary"),
            LastScriptError,
            Host != nullptr ? Host->GetWorldHostId() : 0,
            ManagedScriptInstanceId,
            FCString::Atoi(*ActiveGeneration));
    }
    UE_LOG(LogTemp, Error, TEXT("UnifiedScript component: %s"), *LastScriptError);
}
