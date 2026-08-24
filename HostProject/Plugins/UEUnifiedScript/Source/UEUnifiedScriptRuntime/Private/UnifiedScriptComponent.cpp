#include "UnifiedScriptComponent.h"

#include "HAL/FileManager.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/Paths.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptDeclarationMaterializer.h"
#include "UnifiedScriptObjectRegistry.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/SoftObjectPath.h"

#include <atomic>

namespace UE::UnifiedScript::Component::Private
{
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

    if (ConsumeReloadRequest() && !ReloadScript())
    {
        return;
    }

    if (bTickAutomatically)
    {
        UUnifiedScriptWorldSubsystem* Host =
            GetWorld() != nullptr
            ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
            : nullptr;
        FString Error;
        if (Host == nullptr ||
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

    SetComponentTickEnabled(bTickAutomatically || bReloadRequested);
}

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
    FString CandidateDeclarationAssetPath;
    if (!BuildScriptState(
            ResolvedAsset,
            ScriptState,
            CandidateScriptState,
            CandidateDeclarationAssetPath,
            ResolutionError))
    {
        SetScriptFailure(MoveTemp(ResolutionError));
        return false;
    }

    FUnifiedScriptObjectHandleResult HandleResult;
    const FUnifiedScriptObjectHandle Handle = Host->RegisterObject(this, HandleResult);
    if (!HandleResult.IsSuccess())
    {
        SetScriptFailure(HandleResult.Message);
        return false;
    }

    const uint64 InstanceId =
        UE::UnifiedScript::Component::Private::AllocateScriptInstanceId();
    FInstancedPropertyBag PreviousScriptState = ScriptState;
    ScriptState = MoveTemp(CandidateScriptState);
    FString Error;
    if (!Host->CreateManagedScript(
            InstanceId,
            Handle,
            ResolvedAssemblyPath,
            ResolvedTypeName,
            Error))
    {
        ScriptState = MoveTemp(PreviousScriptState);
        FUnifiedScriptObjectHandleResult ReleaseResult;
        Host->ReleaseObject(Handle, ReleaseResult);
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    ManagedScriptInstanceId = InstanceId;
    ManagedObjectHandle = Handle.Value;
    bScriptRunning = true;
    ActiveGeneration = TEXT("1");
    ActiveScriptAsset = ResolvedAsset;
    ActiveScriptAssetPath = ResolvedAsset != nullptr
        ? ResolvedAsset->GetPathName()
        : FString();
    ActiveDeclarationAssetPath = MoveTemp(CandidateDeclarationAssetPath);
    MaterializedStateCount = ScriptState.GetNumPropertiesInBag();
    ActiveResolvedAssemblyPath = ResolvedAssemblyPath;
    ActiveResolvedTypeName = ResolvedTypeName;
    LastScriptError.Reset();
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
    if (!bScriptRunning && ManagedScriptInstanceId == 0 && ManagedObjectHandle == 0)
    {
        SetComponentTickEnabled(false);
        return;
    }

    UUnifiedScriptWorldSubsystem* Host =
        GetWorld() != nullptr
        ? GetWorld()->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
    FString StopError;
    if (Host != nullptr && Host->IsManagedWorldReady() && ManagedScriptInstanceId != 0)
    {
        Host->DestroyManagedScript(ManagedScriptInstanceId, StopError);
    }

    if (Host != nullptr && Host->IsNativeRuntimeReady() && ManagedObjectHandle != 0)
    {
        FUnifiedScriptObjectHandleResult ReleaseResult;
        Host->ReleaseObject({ManagedObjectHandle}, ReleaseResult);
        if (!ReleaseResult.IsSuccess() && StopError.IsEmpty())
        {
            StopError = MoveTemp(ReleaseResult.Message);
        }
    }

    ManagedScriptInstanceId = 0;
    ManagedObjectHandle = 0;
    bScriptRunning = false;
    bReloadRequested = false;
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
    FString CandidateDeclarationAssetPath;
    if (!BuildScriptState(
            ResolvedAsset,
            ScriptState,
            CandidateScriptState,
            CandidateDeclarationAssetPath,
            Error))
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    FInstancedPropertyBag PreviousScriptState = ScriptState;
    ScriptState = MoveTemp(CandidateScriptState);
    int32 Generation = 0;
    if (!Host->ReloadManagedScript(
            ManagedScriptInstanceId,
            ResolvedAssemblyPath,
            ResolvedTypeName,
            Generation,
            Error))
    {
        ScriptState = MoveTemp(PreviousScriptState);
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    ActiveGeneration = FString::FromInt(Generation);
    ActiveScriptAsset = ResolvedAsset;
    ActiveScriptAssetPath = ResolvedAsset != nullptr
        ? ResolvedAsset->GetPathName()
        : FString();
    ActiveDeclarationAssetPath = MoveTemp(CandidateDeclarationAssetPath);
    MaterializedStateCount = ScriptState.GetNumPropertiesInBag();
    ActiveResolvedAssemblyPath = MoveTemp(ResolvedAssemblyPath);
    ActiveResolvedTypeName = MoveTemp(ResolvedTypeName);
    LastScriptError.Reset();
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
    FString CandidateAssetPath;
    if (!BuildScriptState(
            ResolvedAsset,
            ScriptState,
            Candidate,
            CandidateAssetPath,
            Error))
    {
        SetScriptFailure(MoveTemp(Error));
        return false;
    }

    ScriptState = MoveTemp(Candidate);
    ActiveDeclarationAssetPath = MoveTemp(CandidateAssetPath);
    MaterializedStateCount = ScriptState.GetNumPropertiesInBag();
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
    return !Name.IsEmpty() &&
        ScriptState.SetValueInt32(FName(Name), Value) == EPropertyBagResult::Success;
}

int32 UUnifiedScriptComponent::Add(int32 Left, int32 Right) const
{
    return Left + Right;
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
    FInstancedPropertyBag& OutState,
    FString& OutDeclarationAssetPath,
    FString& OutError) const
{
    OutDeclarationAssetPath.Reset();
    if (ResolvedAsset == nullptr)
    {
        OutState.Reset();
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
        OutError.Reset();
        return true;
    }

    return FUnifiedScriptDeclarationMaterializer::MaterializeFile(
        ResolveConfiguredPath(DeclarationPath),
        RequestedAssetPath,
        ExistingState,
        OutState,
        OutDeclarationAssetPath,
        OutError);
}

FString UUnifiedScriptComponent::ResolveConfiguredPath(
    const FString& ConfiguredPath) const
{
    FString Result = ConfiguredPath;
    const TSharedPtr<IPlugin> Plugin =
        IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    if (Plugin.IsValid())
    {
        Result.ReplaceInline(
            TEXT("{PluginDir}"),
            *Plugin->GetBaseDir(),
            ESearchCase::IgnoreCase);
    }
    Result.ReplaceInline(
        TEXT("{ProjectDir}"),
        *FPaths::ProjectDir(),
        ESearchCase::IgnoreCase);
    if (FPaths::IsRelative(Result))
    {
        Result = FPaths::Combine(FPaths::ProjectDir(), Result);
    }
    Result = FPaths::ConvertRelativePathToFull(Result);
    FPaths::NormalizeFilename(Result);
    return Result;
}

void UUnifiedScriptComponent::SetScriptFailure(FString Error)
{
    LastScriptError = MoveTemp(Error);
    UE_LOG(LogTemp, Error, TEXT("UnifiedScript component: %s"), *LastScriptError);
}
