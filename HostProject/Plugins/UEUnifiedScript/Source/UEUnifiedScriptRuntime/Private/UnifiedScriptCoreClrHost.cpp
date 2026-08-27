#include "UnifiedScriptCoreClrHost.h"

#include "Containers/StringConv.h"
#include "HAL/FileManager.h"
#include "Interfaces/IPluginManager.h"
#include "Misc/Paths.h"
#include "Misc/ScopeLock.h"

#if PLATFORM_WINDOWS
#define UNIFIEDSCRIPT_HOSTFXR_CALLTYPE __cdecl
#define UNIFIEDSCRIPT_CORECLR_DELEGATE_CALLTYPE __stdcall
using FUnifiedScriptHostChar = wchar_t;
#else
#define UNIFIEDSCRIPT_HOSTFXR_CALLTYPE
#define UNIFIEDSCRIPT_CORECLR_DELEGATE_CALLTYPE
using FUnifiedScriptHostChar = char;
#endif

namespace UE::UnifiedScript::CoreClr::Private
{
using FHostFxrInitialize = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    const FUnifiedScriptHostChar* RuntimeConfigPath,
    const void* Parameters,
    void** HostContext);
using FHostFxrGetRuntimeDelegate = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    void* HostContext,
    int32 DelegateType,
    void** Delegate);
using FHostFxrClose = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(void* HostContext);
using FLoadAssemblyAndGetFunctionPointer = int32(
    UNIFIEDSCRIPT_CORECLR_DELEGATE_CALLTYPE *)(
    const FUnifiedScriptHostChar* AssemblyPath,
    const FUnifiedScriptHostChar* TypeName,
    const FUnifiedScriptHostChar* MethodName,
    const FUnifiedScriptHostChar* DelegateTypeName,
    void* Reserved,
    void** Delegate);
using FCreateWorldEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    void* Context,
    const FUnifiedScriptNativeTransportV2* Transport);
using FDestroyWorldEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(uint64 WorldHostId);
using FGetWorldCountEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)();
using FCollectRetiredLoadContextsEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)();
using FCreateScriptEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint64 InstanceId,
    uint64 Host,
    uint64 Target,
    const uint8* AssemblyPathData,
    int32 AssemblyPathLength,
    const uint8* TypeNameData,
    int32 TypeNameLength);
using FTickScriptEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint64 InstanceId,
    float DeltaSeconds);
using FConfigureScriptDebugEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint64 InstanceId,
    const uint8* ConfigurationData,
    int32 ConfigurationLength);
using FBeginScriptDebugTickEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint64 InstanceId,
    float DeltaSeconds);
using FGetScriptDebugStateEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint64 InstanceId,
    uint8* Buffer,
    int32 BufferCapacity);
using FGetScriptReloadStateEntryPoint = FGetScriptDebugStateEntryPoint;
using FSetScriptProfilerEnabledEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint64 InstanceId,
    int32 Enabled);
using FGetScriptProfilerTraceEntryPoint = FGetScriptDebugStateEntryPoint;
using FControlScriptDebugEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint64 InstanceId,
    int32 Control,
    const uint8* PauseIdData,
    int32 PauseIdLength);
using FDisableScriptDebugEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint64 InstanceId);
using FIsScriptDebugActiveEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint64 InstanceId);
using FDispatchEventEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint64 InstanceId,
    int32 EventKind,
    uint64 Source,
    uint64 Argument);
using FReloadScriptEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint64 InstanceId,
    const uint8* AssemblyPathData,
    int32 AssemblyPathLength,
    const uint8* TypeNameData,
    int32 TypeNameLength);
using FDestroyScriptEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint64 InstanceId);
using FGetScriptCountEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId);
using FGetInvocationMetricsEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint64* OutCallFrameInvocationCount,
    uint64* OutLegacyInvocationCount);
using FGetLastErrorEntryPoint = int32(UNIFIEDSCRIPT_HOSTFXR_CALLTYPE *)(
    uint64 WorldHostId,
    uint8* Buffer,
    int32 BufferCapacity);

constexpr int32 LoadAssemblyAndGetFunctionPointerDelegateType = 5;
constexpr int32 MaximumManagedBoundaryErrorBytes = 1024 * 1024;
constexpr int32 MaximumManagedDebugStateBytes = 4 * 1024 * 1024;

FString ReadManagedUtf8(
    FGetLastErrorEntryPoint EntryPoint,
    int32 MaximumBytes,
    uint64 WorldHostId)
{
    if (EntryPoint == nullptr)
    {
        return {};
    }
    const int32 RequiredBytes = EntryPoint(WorldHostId, nullptr, 0);
    if (RequiredBytes <= 0 || RequiredBytes > MaximumBytes)
    {
        return {};
    }
    TArray<uint8> Buffer;
    Buffer.SetNumZeroed(RequiredBytes + 1);
    const int32 WrittenBytes = EntryPoint(
        WorldHostId,
        Buffer.GetData(),
        Buffer.Num());
    if (WrittenBytes <= 0 || WrittenBytes > RequiredBytes)
    {
        return {};
    }
    return FString(UTF8_TO_TCHAR(reinterpret_cast<const char*>(Buffer.GetData())));
}

FString ReadManagedDebugState(
    FGetScriptDebugStateEntryPoint EntryPoint,
    uint64 WorldHostId,
    uint64 InstanceId)
{
    if (EntryPoint == nullptr)
    {
        return {};
    }
    int32 RequiredBytes = EntryPoint(WorldHostId, InstanceId, nullptr, 0);
    for (int32 Attempt = 0; Attempt < 4; ++Attempt)
    {
        if (RequiredBytes <= 0 || RequiredBytes > MaximumManagedDebugStateBytes)
        {
            return {};
        }
        TArray<uint8> Buffer;
        Buffer.SetNumZeroed(RequiredBytes + 1);
        const int32 WrittenBytes = EntryPoint(
            WorldHostId,
            InstanceId,
            Buffer.GetData(),
            Buffer.Num());
        if (WrittenBytes <= 0 || WrittenBytes > MaximumManagedDebugStateBytes)
        {
            return {};
        }
        if (WrittenBytes > RequiredBytes)
        {
            // The worker published a larger snapshot between the size query and
            // copy. Retry against the newly reported immutable snapshot size.
            RequiredBytes = WrittenBytes;
            continue;
        }
        return FString(UTF8_TO_TCHAR(reinterpret_cast<const char*>(Buffer.GetData())));
    }
    return {};
}

FString ReadManagedBoundaryError(
    FGetLastErrorEntryPoint EntryPoint,
    uint64 WorldHostId)
{
    return ReadManagedUtf8(
        EntryPoint,
        MaximumManagedBoundaryErrorBytes,
        WorldHostId);
}

FString DescribeManagedFailure(
    const TCHAR* Operation,
    int32 Status,
    FGetLastErrorEntryPoint ErrorEntryPoint,
    uint64 WorldHostId)
{
    const FString Detail = ReadManagedBoundaryError(ErrorEntryPoint, WorldHostId);
    return Detail.IsEmpty()
        ? FString::Printf(TEXT("%s failed with status %d."), Operation, Status)
        : FString::Printf(
              TEXT("%s failed with status %d.%s%s"),
              Operation,
              Status,
              LINE_TERMINATOR,
              *Detail);
}

FString GetHostFxrFileName()
{
#if PLATFORM_WINDOWS
    return TEXT("hostfxr.dll");
#elif PLATFORM_MAC
    return TEXT("libhostfxr.dylib");
#else
    return TEXT("libhostfxr.so");
#endif
}

FString GetPlatformDotNetDirectory()
{
#if PLATFORM_WINDOWS
    return TEXT("win-x64");
#elif PLATFORM_MAC && PLATFORM_CPU_ARM_FAMILY
    return TEXT("mac-arm64");
#elif PLATFORM_MAC
    return TEXT("mac-x64");
#elif PLATFORM_CPU_ARM_FAMILY
    return TEXT("linux-arm64");
#else
    return TEXT("linux-x64");
#endif
}

void AddCandidate(TArray<FString>& Candidates, FString Candidate)
{
    if (!Candidate.IsEmpty())
    {
        FPaths::NormalizeDirectoryName(Candidate);
        Candidates.AddUnique(MoveTemp(Candidate));
    }
}

FString FindDotNetRoot()
{
    TArray<FString> Candidates;
    if (const TSharedPtr<IPlugin> Plugin =
            IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
        Plugin.IsValid())
    {
        AddCandidate(
            Candidates,
            FPaths::Combine(
                Plugin->GetBaseDir(),
                TEXT("Binaries/ThirdParty/DotNet/10.0"),
                GetPlatformDotNetDirectory()));
    }
    AddCandidate(
        Candidates,
        FPlatformMisc::GetEnvironmentVariable(TEXT("UNIFIEDSCRIPT_DOTNET_ROOT")));
    AddCandidate(
        Candidates,
        FPlatformMisc::GetEnvironmentVariable(TEXT("DOTNET_ROOT")));
    AddCandidate(
        Candidates,
        FPaths::Combine(
            FPaths::EngineDir(),
            TEXT("Binaries/ThirdParty/DotNet/10.0"),
            GetPlatformDotNetDirectory()));

    for (const FString& Candidate : Candidates)
    {
        if (IFileManager::Get().DirectoryExists(*FPaths::Combine(Candidate, TEXT("host/fxr"))))
        {
            return Candidate;
        }
    }
    return {};
}

FString FindHostFxr(const FString& DotNetRoot)
{
    const FString FxrRoot = FPaths::Combine(DotNetRoot, TEXT("host/fxr"));
    TArray<FString> Versions;
    IFileManager::Get().FindFiles(Versions, *FPaths::Combine(FxrRoot, TEXT("*")), false, true);
    Versions.Sort(TGreater<FString>());
    for (const FString& Version : Versions)
    {
        const FString Candidate = FPaths::Combine(
            FxrRoot,
            Version,
            GetHostFxrFileName());
        if (IFileManager::Get().FileExists(*Candidate))
        {
            return Candidate;
        }
    }
    return {};
}

FString GetRuntimeConfigPath()
{
    const TSharedPtr<IPlugin> Plugin =
        IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    return Plugin.IsValid()
        ? FPaths::Combine(
              Plugin->GetBaseDir(),
              TEXT("Resources/Managed/UnifiedScript.Unreal.runtimeconfig.json"))
        : FString();
}

FString GetManagedAssemblyPath()
{
    const TSharedPtr<IPlugin> Plugin =
        IPluginManager::Get().FindPlugin(TEXT("UEUnifiedScript"));
    return Plugin.IsValid()
        ? FPaths::Combine(
              Plugin->GetBaseDir(),
              TEXT("Binaries/Managed/UnifiedScript.Unreal.dll"))
        : FString();
}

}

FUnifiedScriptCoreClrHost& FUnifiedScriptCoreClrHost::Get()
{
    static FUnifiedScriptCoreClrHost Instance;
    return Instance;
}

bool FUnifiedScriptCoreClrHost::Initialize()
{
    FScopeLock Lock(&StateLock);
    if (State == EUnifiedScriptCoreClrHostState::Ready)
    {
        return true;
    }
    if (State == EUnifiedScriptCoreClrHostState::Failed)
    {
        return false;
    }
    return InitializeLocked();
}

void FUnifiedScriptCoreClrHost::Shutdown()
{
    FScopeLock Lock(&StateLock);
    if (GetWorldCountEntryPoint != nullptr)
    {
        const int32 RemainingWorlds =
            reinterpret_cast<UE::UnifiedScript::CoreClr::Private::FGetWorldCountEntryPoint>(
                GetWorldCountEntryPoint)();
        ensureMsgf(
            RemainingWorlds == 0,
            TEXT("UnifiedScript CoreCLR host stopped with %d managed Worlds."),
            RemainingWorlds);
    }
    CreateWorldEntryPoint = nullptr;
    DestroyWorldEntryPoint = nullptr;
    GetWorldCountEntryPoint = nullptr;
    CollectRetiredLoadContextsEntryPoint = nullptr;
    CreateScriptEntryPoint = nullptr;
    TickScriptEntryPoint = nullptr;
    ConfigureScriptDebugEntryPoint = nullptr;
    BeginScriptDebugTickEntryPoint = nullptr;
    GetScriptDebugStateEntryPoint = nullptr;
    GetScriptReloadStateEntryPoint = nullptr;
    SetScriptProfilerEnabledEntryPoint = nullptr;
    GetScriptProfilerTraceEntryPoint = nullptr;
    ControlScriptDebugEntryPoint = nullptr;
    DisableScriptDebugEntryPoint = nullptr;
    IsScriptDebugActiveEntryPoint = nullptr;
    DispatchEventEntryPoint = nullptr;
    ReloadScriptEntryPoint = nullptr;
    DestroyScriptEntryPoint = nullptr;
    GetScriptCountEntryPoint = nullptr;
    GetInvocationMetricsEntryPoint = nullptr;
    GetLastManagedErrorEntryPoint = nullptr;
    LoadAssemblyAndGetFunctionPointer = nullptr;
    if (HostFxrLibrary != nullptr)
    {
        FPlatformProcess::FreeDllHandle(HostFxrLibrary);
        HostFxrLibrary = nullptr;
    }
    LoadedHostFxrPath.Reset();
    LastError.Reset();
    State = EUnifiedScriptCoreClrHostState::Stopped;
}

EUnifiedScriptCoreClrHostState FUnifiedScriptCoreClrHost::GetState() const
{
    FScopeLock Lock(&StateLock);
    return State;
}

bool FUnifiedScriptCoreClrHost::IsReady() const
{
    return GetState() == EUnifiedScriptCoreClrHostState::Ready;
}

void* FUnifiedScriptCoreClrHost::GetLoadAssemblyAndGetFunctionPointer() const
{
    FScopeLock Lock(&StateLock);
    return LoadAssemblyAndGetFunctionPointer;
}

bool FUnifiedScriptCoreClrHost::CreateManagedWorld(
    uint64 WorldHostId,
    FUnifiedScriptNativeBridgeContext* Context,
    const FUnifiedScriptNativeTransportV2* Transport,
    FString& OutError)
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    FScopeLock Lock(&StateLock);
    if (State != EUnifiedScriptCoreClrHostState::Ready ||
        CreateWorldEntryPoint == nullptr)
    {
        OutError = TEXT("The UnifiedScript managed World entry point is unavailable.");
        return false;
    }
    const int32 Result = reinterpret_cast<FCreateWorldEntryPoint>(
        CreateWorldEntryPoint)(WorldHostId, Context, Transport);
    if (Result != 0)
    {
        OutError = DescribeManagedFailure(
            TEXT("Managed World creation"),
            Result,
            reinterpret_cast<FGetLastErrorEntryPoint>(GetLastManagedErrorEntryPoint),
            WorldHostId);
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::DestroyManagedWorld(
    uint64 WorldHostId,
    FString& OutError)
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    FScopeLock Lock(&StateLock);
    if (State != EUnifiedScriptCoreClrHostState::Ready ||
        DestroyWorldEntryPoint == nullptr)
    {
        OutError = TEXT("The UnifiedScript managed World entry point is unavailable.");
        return false;
    }
    const int32 Result = reinterpret_cast<FDestroyWorldEntryPoint>(
        DestroyWorldEntryPoint)(WorldHostId);
    if (Result != 0)
    {
        OutError = DescribeManagedFailure(
            TEXT("Managed World destruction"),
            Result,
            reinterpret_cast<FGetLastErrorEntryPoint>(GetLastManagedErrorEntryPoint),
            WorldHostId);
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::CreateManagedScript(
    uint64 WorldHostId,
    uint64 InstanceId,
    uint64 Host,
    uint64 Target,
    const FString& AssemblyPath,
    const FString& TypeName,
    FString& OutError)
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    FCreateScriptEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            CreateScriptEntryPoint == nullptr)
        {
            OutError = TEXT("The UnifiedScript managed script entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FCreateScriptEntryPoint>(CreateScriptEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(GetLastManagedErrorEntryPoint);
    }
    if (AssemblyPath.IsEmpty() || !IFileManager::Get().FileExists(*AssemblyPath))
    {
        OutError = FString::Printf(
            TEXT("The managed script assembly does not exist: '%s'."),
            *AssemblyPath);
        return false;
    }
    if (TypeName.IsEmpty())
    {
        OutError = TEXT("The managed script type name is empty.");
        return false;
    }

    const FTCHARToUTF8 AssemblyPathUtf8(*AssemblyPath);
    const FTCHARToUTF8 TypeNameUtf8(*TypeName);
    const int32 Result = EntryPoint(
        WorldHostId,
        InstanceId,
        Host,
        Target,
        reinterpret_cast<const uint8*>(AssemblyPathUtf8.Get()),
        AssemblyPathUtf8.Length(),
        reinterpret_cast<const uint8*>(TypeNameUtf8.Get()),
        TypeNameUtf8.Length());
    if (Result != 0)
    {
        OutError = DescribeManagedFailure(
            TEXT("Managed script creation"),
            Result,
            ErrorEntryPoint,
            WorldHostId);
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::TickManagedScript(
    uint64 WorldHostId,
    uint64 InstanceId,
    float DeltaSeconds,
    FString& OutError)
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    FTickScriptEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            TickScriptEntryPoint == nullptr)
        {
            OutError = TEXT("The UnifiedScript managed tick entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FTickScriptEntryPoint>(TickScriptEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(GetLastManagedErrorEntryPoint);
    }
    const int32 Result = EntryPoint(WorldHostId, InstanceId, DeltaSeconds);
    if (Result != 0)
    {
        OutError = DescribeManagedFailure(
            TEXT("Managed script tick"),
            Result,
            ErrorEntryPoint,
            WorldHostId);
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::ConfigureManagedScriptDebug(
    uint64 WorldHostId,
    uint64 InstanceId,
    const FString& ConfigurationJson,
    FString& OutError)
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    FConfigureScriptDebugEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            ConfigureScriptDebugEntryPoint == nullptr)
        {
            OutError = TEXT("The live World debug configuration entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FConfigureScriptDebugEntryPoint>(
            ConfigureScriptDebugEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(
            GetLastManagedErrorEntryPoint);
    }
    if (ConfigurationJson.IsEmpty())
    {
        OutError = TEXT("A live World debug configuration is required.");
        return false;
    }
    const FTCHARToUTF8 ConfigurationUtf8(*ConfigurationJson);
    const int32 Result = EntryPoint(
        WorldHostId,
        InstanceId,
        reinterpret_cast<const uint8*>(ConfigurationUtf8.Get()),
        ConfigurationUtf8.Length());
    if (Result != 0)
    {
        OutError = DescribeManagedFailure(
            TEXT("Live World debug configuration"),
            Result,
            ErrorEntryPoint,
            WorldHostId);
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::BeginManagedScriptDebugTick(
    uint64 WorldHostId,
    uint64 InstanceId,
    float DeltaSeconds,
    FString& OutError)
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    FBeginScriptDebugTickEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            BeginScriptDebugTickEntryPoint == nullptr)
        {
            OutError = TEXT("The live World debug tick entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FBeginScriptDebugTickEntryPoint>(
            BeginScriptDebugTickEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(
            GetLastManagedErrorEntryPoint);
    }
    const int32 Result = EntryPoint(WorldHostId, InstanceId, DeltaSeconds);
    if (Result != 0)
    {
        OutError = DescribeManagedFailure(
            TEXT("Live World debug tick"),
            Result,
            ErrorEntryPoint,
            WorldHostId);
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::GetManagedScriptDebugState(
    uint64 WorldHostId,
    uint64 InstanceId,
    FString& OutJson,
    FString& OutError) const
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    FGetScriptDebugStateEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            GetScriptDebugStateEntryPoint == nullptr)
        {
            OutJson.Reset();
            OutError = TEXT("The live World debug state entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FGetScriptDebugStateEntryPoint>(
            GetScriptDebugStateEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(
            GetLastManagedErrorEntryPoint);
    }
    OutJson = ReadManagedDebugState(EntryPoint, WorldHostId, InstanceId);
    if (OutJson.IsEmpty())
    {
        const FString Detail = ReadManagedBoundaryError(ErrorEntryPoint, WorldHostId);
        OutError = Detail.IsEmpty()
            ? TEXT("The managed script has no live World debug state.")
            : FString::Printf(
                  TEXT("The managed live World debug state could not be read.%s%s"),
                  LINE_TERMINATOR,
                  *Detail);
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::GetManagedScriptReloadState(
    uint64 WorldHostId,
    uint64 InstanceId,
    FString& OutJson,
    FString& OutError) const
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    FGetScriptReloadStateEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            GetScriptReloadStateEntryPoint == nullptr)
        {
            OutJson.Reset();
            OutError = TEXT("The managed reload transaction entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FGetScriptReloadStateEntryPoint>(
            GetScriptReloadStateEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(
            GetLastManagedErrorEntryPoint);
    }
    OutJson = ReadManagedDebugState(EntryPoint, WorldHostId, InstanceId);
    if (OutJson.IsEmpty())
    {
        const FString Detail = ReadManagedBoundaryError(ErrorEntryPoint, WorldHostId);
        OutError = Detail.IsEmpty()
            ? TEXT("The managed script has no reload transaction state.")
            : FString::Printf(
                  TEXT("The managed reload transaction state could not be read.%s%s"),
                  LINE_TERMINATOR,
                  *Detail);
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::SetManagedScriptProfilerEnabled(
    uint64 WorldHostId,
    uint64 InstanceId,
    bool bEnabled,
    FString& OutError)
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    FSetScriptProfilerEnabledEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            SetScriptProfilerEnabledEntryPoint == nullptr)
        {
            OutError = TEXT("The managed gameplay profiler control entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FSetScriptProfilerEnabledEntryPoint>(
            SetScriptProfilerEnabledEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(
            GetLastManagedErrorEntryPoint);
    }
    const int32 Result = EntryPoint(WorldHostId, InstanceId, bEnabled ? 1 : 0);
    if (Result != 0)
    {
        OutError = DescribeManagedFailure(
            TEXT("Managed gameplay profiler control"),
            Result,
            ErrorEntryPoint,
            WorldHostId);
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::GetManagedScriptProfilerTrace(
    uint64 WorldHostId,
    uint64 InstanceId,
    FString& OutJson,
    FString& OutError) const
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    FGetScriptProfilerTraceEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            GetScriptProfilerTraceEntryPoint == nullptr)
        {
            OutJson.Reset();
            OutError = TEXT("The managed gameplay profiler trace entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FGetScriptProfilerTraceEntryPoint>(
            GetScriptProfilerTraceEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(
            GetLastManagedErrorEntryPoint);
    }
    OutJson = ReadManagedDebugState(EntryPoint, WorldHostId, InstanceId);
    if (OutJson.IsEmpty())
    {
        const FString Detail = ReadManagedBoundaryError(ErrorEntryPoint, WorldHostId);
        OutError = Detail.IsEmpty()
            ? TEXT("The managed script has no gameplay profiler trace.")
            : FString::Printf(
                  TEXT("The managed gameplay profiler trace could not be read.%s%s"),
                  LINE_TERMINATOR,
                  *Detail);
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::ControlManagedScriptDebug(
    uint64 WorldHostId,
    uint64 InstanceId,
    int32 Control,
    const FString& PauseId,
    FString& OutError)
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    FControlScriptDebugEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            ControlScriptDebugEntryPoint == nullptr)
        {
            OutError = TEXT("The live World debug control entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FControlScriptDebugEntryPoint>(
            ControlScriptDebugEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(
            GetLastManagedErrorEntryPoint);
    }
    const FTCHARToUTF8 PauseIdUtf8(*PauseId);
    const int32 Result = EntryPoint(
        WorldHostId,
        InstanceId,
        Control,
        reinterpret_cast<const uint8*>(PauseIdUtf8.Get()),
        PauseIdUtf8.Length());
    if (Result != 0)
    {
        OutError = DescribeManagedFailure(
            TEXT("Live World debug control"),
            Result,
            ErrorEntryPoint,
            WorldHostId);
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::DisableManagedScriptDebug(
    uint64 WorldHostId,
    uint64 InstanceId,
    FString& OutError)
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    FDisableScriptDebugEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            DisableScriptDebugEntryPoint == nullptr)
        {
            OutError = TEXT("The live World debug detach entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FDisableScriptDebugEntryPoint>(
            DisableScriptDebugEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(
            GetLastManagedErrorEntryPoint);
    }
    const int32 Result = EntryPoint(WorldHostId, InstanceId);
    if (Result != 0)
    {
        OutError = DescribeManagedFailure(
            TEXT("Live World debug detach"),
            Result,
            ErrorEntryPoint,
            WorldHostId);
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::IsManagedScriptDebugActive(
    uint64 WorldHostId,
    uint64 InstanceId,
    bool& bOutActive,
    FString& OutError) const
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    bOutActive = false;
    FIsScriptDebugActiveEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            IsScriptDebugActiveEntryPoint == nullptr)
        {
            OutError = TEXT("The live World debug activity entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FIsScriptDebugActiveEntryPoint>(
            IsScriptDebugActiveEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(
            GetLastManagedErrorEntryPoint);
    }
    const int32 Result = EntryPoint(WorldHostId, InstanceId);
    if (Result < 0)
    {
        OutError = DescribeManagedFailure(
            TEXT("Live World debug activity query"),
            Result,
            ErrorEntryPoint,
            WorldHostId);
        return false;
    }
    bOutActive = Result != 0;
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::DispatchManagedEvent(
    uint64 WorldHostId,
    uint64 InstanceId,
    int32 EventKind,
    uint64 Source,
    uint64 Argument,
    FString& OutError)
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    FDispatchEventEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            DispatchEventEntryPoint == nullptr)
        {
            OutError = TEXT("The UnifiedScript managed event entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FDispatchEventEntryPoint>(DispatchEventEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(GetLastManagedErrorEntryPoint);
    }
    const int32 Result = EntryPoint(
        WorldHostId,
        InstanceId,
        EventKind,
        Source,
        Argument);
    if (Result != 0)
    {
        OutError = DescribeManagedFailure(
            TEXT("Managed script event dispatch"),
            Result,
            ErrorEntryPoint,
            WorldHostId);
        return false;
    }
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::ReloadManagedScript(
    uint64 WorldHostId,
    uint64 InstanceId,
    const FString& AssemblyPath,
    const FString& TypeName,
    int32& OutGeneration,
    FString& OutError)
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    OutGeneration = 0;
    FReloadScriptEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            ReloadScriptEntryPoint == nullptr)
        {
            OutError = TEXT("The UnifiedScript managed reload entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FReloadScriptEntryPoint>(ReloadScriptEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(GetLastManagedErrorEntryPoint);
    }
    if (AssemblyPath.IsEmpty() || !IFileManager::Get().FileExists(*AssemblyPath))
    {
        OutError = FString::Printf(
            TEXT("The managed script assembly does not exist: '%s'."),
            *AssemblyPath);
        return false;
    }
    if (TypeName.IsEmpty())
    {
        OutError = TEXT("The managed script type name is empty.");
        return false;
    }

    const FTCHARToUTF8 AssemblyPathUtf8(*AssemblyPath);
    const FTCHARToUTF8 TypeNameUtf8(*TypeName);
    const int32 Result = EntryPoint(
        WorldHostId,
        InstanceId,
        reinterpret_cast<const uint8*>(AssemblyPathUtf8.Get()),
        AssemblyPathUtf8.Length(),
        reinterpret_cast<const uint8*>(TypeNameUtf8.Get()),
        TypeNameUtf8.Length());
    if (Result <= 0)
    {
        OutError = DescribeManagedFailure(
            TEXT("Managed script reload"),
            Result,
            ErrorEntryPoint,
            WorldHostId);
        return false;
    }
    OutGeneration = Result;
    OutError.Reset();
    return true;
}

bool FUnifiedScriptCoreClrHost::DestroyManagedScript(
    uint64 WorldHostId,
    uint64 InstanceId,
    FString& OutError)
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    check(IsInGameThread());
    FDestroyScriptEntryPoint EntryPoint = nullptr;
    FGetLastErrorEntryPoint ErrorEntryPoint = nullptr;
    {
        FScopeLock Lock(&StateLock);
        if (State != EUnifiedScriptCoreClrHostState::Ready ||
            DestroyScriptEntryPoint == nullptr)
        {
            OutError = TEXT("The UnifiedScript managed destroy-script entry point is unavailable.");
            return false;
        }
        EntryPoint = reinterpret_cast<FDestroyScriptEntryPoint>(DestroyScriptEntryPoint);
        ErrorEntryPoint = reinterpret_cast<FGetLastErrorEntryPoint>(GetLastManagedErrorEntryPoint);
    }
    const int32 Result = EntryPoint(WorldHostId, InstanceId);
    if (Result != 0)
    {
        OutError = DescribeManagedFailure(
            TEXT("Managed script destruction"),
            Result,
            ErrorEntryPoint,
            WorldHostId);
        return false;
    }
    OutError.Reset();
    return true;
}

int32 FUnifiedScriptCoreClrHost::GetManagedScriptCount(uint64 WorldHostId) const
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    FScopeLock Lock(&StateLock);
    if (State != EUnifiedScriptCoreClrHostState::Ready ||
        GetScriptCountEntryPoint == nullptr)
    {
        return 0;
    }
    const int32 Result = reinterpret_cast<FGetScriptCountEntryPoint>(
        GetScriptCountEntryPoint)(WorldHostId);
    return FMath::Max(Result, 0);
}

bool FUnifiedScriptCoreClrHost::GetManagedInvocationMetrics(
    uint64 WorldHostId,
    uint64& OutCallFrameInvocationCount,
    uint64& OutLegacyInvocationCount) const
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    FScopeLock Lock(&StateLock);
    OutCallFrameInvocationCount = 0;
    OutLegacyInvocationCount = 0;
    if (State != EUnifiedScriptCoreClrHostState::Ready ||
        GetInvocationMetricsEntryPoint == nullptr)
    {
        return false;
    }
    const int32 Result = reinterpret_cast<FGetInvocationMetricsEntryPoint>(
        GetInvocationMetricsEntryPoint)(
        WorldHostId,
        &OutCallFrameInvocationCount,
        &OutLegacyInvocationCount);
    return Result == 0;
}

int32 FUnifiedScriptCoreClrHost::GetManagedWorldCount() const
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    FScopeLock Lock(&StateLock);
    return State == EUnifiedScriptCoreClrHostState::Ready &&
        GetWorldCountEntryPoint != nullptr
        ? reinterpret_cast<FGetWorldCountEntryPoint>(GetWorldCountEntryPoint)()
        : 0;
}

int32 FUnifiedScriptCoreClrHost::CollectRetiredManagedLoadContexts() const
{
    using namespace UE::UnifiedScript::CoreClr::Private;
    FScopeLock Lock(&StateLock);
    return State == EUnifiedScriptCoreClrHostState::Ready &&
        CollectRetiredLoadContextsEntryPoint != nullptr
        ? reinterpret_cast<FCollectRetiredLoadContextsEntryPoint>(
            CollectRetiredLoadContextsEntryPoint)()
        : 0;
}

FString FUnifiedScriptCoreClrHost::GetHostFxrPath() const
{
    FScopeLock Lock(&StateLock);
    return LoadedHostFxrPath;
}

FString FUnifiedScriptCoreClrHost::GetLastError() const
{
    FScopeLock Lock(&StateLock);
    return LastError;
}

bool FUnifiedScriptCoreClrHost::InitializeLocked()
{
    using namespace UE::UnifiedScript::CoreClr::Private;

    const FString RuntimeConfigPath = GetRuntimeConfigPath();
    if (RuntimeConfigPath.IsEmpty() ||
        !IFileManager::Get().FileExists(*RuntimeConfigPath))
    {
        return FailLocked(TEXT("UnifiedScript CoreCLR runtimeconfig is missing."));
    }

    const FString DotNetRoot = FindDotNetRoot();
    if (DotNetRoot.IsEmpty())
    {
        return FailLocked(TEXT("A compatible .NET root could not be found."));
    }

    LoadedHostFxrPath = FindHostFxr(DotNetRoot);
    if (LoadedHostFxrPath.IsEmpty())
    {
        return FailLocked(TEXT("hostfxr was not found under the selected .NET root."));
    }

    HostFxrLibrary = FPlatformProcess::GetDllHandle(*LoadedHostFxrPath);
    if (HostFxrLibrary == nullptr)
    {
        return FailLocked(FString::Printf(
            TEXT("Failed to load hostfxr from '%s'."),
            *LoadedHostFxrPath));
    }

    const FHostFxrInitialize InitializeForRuntimeConfig =
        reinterpret_cast<FHostFxrInitialize>(FPlatformProcess::GetDllExport(
            HostFxrLibrary,
            TEXT("hostfxr_initialize_for_runtime_config")));
    const FHostFxrGetRuntimeDelegate GetRuntimeDelegate =
        reinterpret_cast<FHostFxrGetRuntimeDelegate>(FPlatformProcess::GetDllExport(
            HostFxrLibrary,
            TEXT("hostfxr_get_runtime_delegate")));
    const FHostFxrClose Close = reinterpret_cast<FHostFxrClose>(
        FPlatformProcess::GetDllExport(HostFxrLibrary, TEXT("hostfxr_close")));
    if (InitializeForRuntimeConfig == nullptr ||
        GetRuntimeDelegate == nullptr ||
        Close == nullptr)
    {
        return FailLocked(TEXT("hostfxr does not expose the required hosting API."));
    }

    void* HostContext = nullptr;
#if PLATFORM_WINDOWS
    const int32 InitializeResult = InitializeForRuntimeConfig(
        *RuntimeConfigPath,
        nullptr,
        &HostContext);
#else
    const FTCHARToUTF8 RuntimeConfigUtf8(*RuntimeConfigPath);
    const int32 InitializeResult = InitializeForRuntimeConfig(
        RuntimeConfigUtf8.Get(),
        nullptr,
        &HostContext);
#endif
    if (InitializeResult < 0 || HostContext == nullptr)
    {
        return FailLocked(FString::Printf(
            TEXT("hostfxr runtime initialization failed with 0x%08X."),
            static_cast<uint32>(InitializeResult)));
    }

    void* LoadDelegate = nullptr;
    const int32 DelegateResult = GetRuntimeDelegate(
        HostContext,
        LoadAssemblyAndGetFunctionPointerDelegateType,
        &LoadDelegate);
    Close(HostContext);
    if (DelegateResult < 0 || LoadDelegate == nullptr)
    {
        return FailLocked(FString::Printf(
            TEXT("hostfxr delegate acquisition failed with 0x%08X."),
            static_cast<uint32>(DelegateResult)));
    }

    LoadAssemblyAndGetFunctionPointer = LoadDelegate;
    const FString ManagedAssemblyPath = GetManagedAssemblyPath();
    if (ManagedAssemblyPath.IsEmpty() ||
        !IFileManager::Get().FileExists(*ManagedAssemblyPath))
    {
        return FailLocked(TEXT(
            "UnifiedScript.Unreal.dll is not staged under the plugin Binaries/Managed directory."));
    }

    const FLoadAssemblyAndGetFunctionPointer LoadManagedEntry =
        reinterpret_cast<FLoadAssemblyAndGetFunctionPointer>(LoadDelegate);
    const FUnifiedScriptHostChar* UnmanagedCallersOnly =
        reinterpret_cast<const FUnifiedScriptHostChar*>(MAX_uint64);
#if PLATFORM_WINDOWS
    const FUnifiedScriptHostChar* AssemblyPath = *ManagedAssemblyPath;
    const FUnifiedScriptHostChar* TypeName =
        TEXT("UnifiedScript.Unreal.UnrealWorldManagedEntryPoint, UnifiedScript.Unreal");
    auto LoadEntryPoint = [&](const FUnifiedScriptHostChar* MethodName, void** OutEntryPoint)
    {
        return LoadManagedEntry(
            AssemblyPath,
            TypeName,
            MethodName,
            UnmanagedCallersOnly,
            nullptr,
            OutEntryPoint);
    };
#else
    const FTCHARToUTF8 AssemblyPathUtf8(*ManagedAssemblyPath);
    const FTCHARToUTF8 TypeNameUtf8(
        TEXT("UnifiedScript.Unreal.UnrealWorldManagedEntryPoint, UnifiedScript.Unreal"));
    auto LoadEntryPoint = [&](const FUnifiedScriptHostChar* MethodName, void** OutEntryPoint)
    {
        return LoadManagedEntry(
            AssemblyPathUtf8.Get(),
            TypeNameUtf8.Get(),
            MethodName,
            UnmanagedCallersOnly,
            nullptr,
            OutEntryPoint);
    };
#endif

#if PLATFORM_WINDOWS
    const int32 CreateResult = LoadEntryPoint(TEXT("CreateWorld"), &CreateWorldEntryPoint);
    const int32 DestroyResult = LoadEntryPoint(TEXT("DestroyWorld"), &DestroyWorldEntryPoint);
    const int32 CountResult = LoadEntryPoint(TEXT("GetWorldCount"), &GetWorldCountEntryPoint);
    const int32 CollectResult = LoadEntryPoint(
        TEXT("CollectRetiredLoadContexts"),
        &CollectRetiredLoadContextsEntryPoint);
    const int32 CreateScriptResult = LoadEntryPoint(TEXT("CreateScript"), &CreateScriptEntryPoint);
    const int32 TickScriptResult = LoadEntryPoint(TEXT("TickScript"), &TickScriptEntryPoint);
    const int32 ConfigureDebugResult = LoadEntryPoint(
        TEXT("ConfigureScriptDebug"), &ConfigureScriptDebugEntryPoint);
    const int32 BeginDebugTickResult = LoadEntryPoint(
        TEXT("BeginScriptDebugTick"), &BeginScriptDebugTickEntryPoint);
    const int32 DebugStateResult = LoadEntryPoint(
        TEXT("GetScriptDebugState"), &GetScriptDebugStateEntryPoint);
    const int32 ReloadStateResult = LoadEntryPoint(
        TEXT("GetScriptReloadState"), &GetScriptReloadStateEntryPoint);
    const int32 ProfilerControlResult = LoadEntryPoint(
        TEXT("SetScriptProfilerEnabled"), &SetScriptProfilerEnabledEntryPoint);
    const int32 ProfilerTraceResult = LoadEntryPoint(
        TEXT("GetScriptProfilerTrace"), &GetScriptProfilerTraceEntryPoint);
    const int32 DebugControlResult = LoadEntryPoint(
        TEXT("ControlScriptDebug"), &ControlScriptDebugEntryPoint);
    const int32 DisableDebugResult = LoadEntryPoint(
        TEXT("DisableScriptDebug"), &DisableScriptDebugEntryPoint);
    const int32 DebugActiveResult = LoadEntryPoint(
        TEXT("IsScriptDebugActive"), &IsScriptDebugActiveEntryPoint);
    const int32 DispatchEventResult = LoadEntryPoint(TEXT("DispatchEvent"), &DispatchEventEntryPoint);
    const int32 ReloadScriptResult = LoadEntryPoint(TEXT("ReloadScript"), &ReloadScriptEntryPoint);
    const int32 DestroyScriptResult = LoadEntryPoint(TEXT("DestroyScript"), &DestroyScriptEntryPoint);
    const int32 ScriptCountResult = LoadEntryPoint(TEXT("GetScriptCount"), &GetScriptCountEntryPoint);
    const int32 MetricsResult = LoadEntryPoint(TEXT("GetInvocationMetrics"), &GetInvocationMetricsEntryPoint);
    const int32 LastErrorResult = LoadEntryPoint(TEXT("GetLastError"), &GetLastManagedErrorEntryPoint);
#else
    const int32 CreateResult = LoadEntryPoint("CreateWorld", &CreateWorldEntryPoint);
    const int32 DestroyResult = LoadEntryPoint("DestroyWorld", &DestroyWorldEntryPoint);
    const int32 CountResult = LoadEntryPoint("GetWorldCount", &GetWorldCountEntryPoint);
    const int32 CollectResult = LoadEntryPoint(
        "CollectRetiredLoadContexts",
        &CollectRetiredLoadContextsEntryPoint);
    const int32 CreateScriptResult = LoadEntryPoint("CreateScript", &CreateScriptEntryPoint);
    const int32 TickScriptResult = LoadEntryPoint("TickScript", &TickScriptEntryPoint);
    const int32 ConfigureDebugResult = LoadEntryPoint(
        "ConfigureScriptDebug", &ConfigureScriptDebugEntryPoint);
    const int32 BeginDebugTickResult = LoadEntryPoint(
        "BeginScriptDebugTick", &BeginScriptDebugTickEntryPoint);
    const int32 DebugStateResult = LoadEntryPoint(
        "GetScriptDebugState", &GetScriptDebugStateEntryPoint);
    const int32 ReloadStateResult = LoadEntryPoint(
        "GetScriptReloadState", &GetScriptReloadStateEntryPoint);
    const int32 ProfilerControlResult = LoadEntryPoint(
        "SetScriptProfilerEnabled", &SetScriptProfilerEnabledEntryPoint);
    const int32 ProfilerTraceResult = LoadEntryPoint(
        "GetScriptProfilerTrace", &GetScriptProfilerTraceEntryPoint);
    const int32 DebugControlResult = LoadEntryPoint(
        "ControlScriptDebug", &ControlScriptDebugEntryPoint);
    const int32 DisableDebugResult = LoadEntryPoint(
        "DisableScriptDebug", &DisableScriptDebugEntryPoint);
    const int32 DebugActiveResult = LoadEntryPoint(
        "IsScriptDebugActive", &IsScriptDebugActiveEntryPoint);
    const int32 DispatchEventResult = LoadEntryPoint("DispatchEvent", &DispatchEventEntryPoint);
    const int32 ReloadScriptResult = LoadEntryPoint("ReloadScript", &ReloadScriptEntryPoint);
    const int32 DestroyScriptResult = LoadEntryPoint("DestroyScript", &DestroyScriptEntryPoint);
    const int32 ScriptCountResult = LoadEntryPoint("GetScriptCount", &GetScriptCountEntryPoint);
    const int32 MetricsResult = LoadEntryPoint("GetInvocationMetrics", &GetInvocationMetricsEntryPoint);
    const int32 LastErrorResult = LoadEntryPoint("GetLastError", &GetLastManagedErrorEntryPoint);
#endif
    if (CreateResult != 0 || DestroyResult != 0 || CountResult != 0 || CollectResult != 0 ||
        CreateScriptResult != 0 || TickScriptResult != 0 || DispatchEventResult != 0 ||
        ConfigureDebugResult != 0 || BeginDebugTickResult != 0 || DebugStateResult != 0 ||
        ReloadStateResult != 0 || ProfilerControlResult != 0 || ProfilerTraceResult != 0 ||
        DebugControlResult != 0 || DisableDebugResult != 0 || DebugActiveResult != 0 ||
        ReloadScriptResult != 0 ||
        DestroyScriptResult != 0 || ScriptCountResult != 0 || MetricsResult != 0 ||
        LastErrorResult != 0 ||
        CreateWorldEntryPoint == nullptr ||
        DestroyWorldEntryPoint == nullptr ||
        GetWorldCountEntryPoint == nullptr ||
        CollectRetiredLoadContextsEntryPoint == nullptr ||
        CreateScriptEntryPoint == nullptr ||
        TickScriptEntryPoint == nullptr ||
        ConfigureScriptDebugEntryPoint == nullptr ||
        BeginScriptDebugTickEntryPoint == nullptr ||
        GetScriptDebugStateEntryPoint == nullptr ||
        GetScriptReloadStateEntryPoint == nullptr ||
        SetScriptProfilerEnabledEntryPoint == nullptr ||
        GetScriptProfilerTraceEntryPoint == nullptr ||
        ControlScriptDebugEntryPoint == nullptr ||
        DisableScriptDebugEntryPoint == nullptr ||
        IsScriptDebugActiveEntryPoint == nullptr ||
        DispatchEventEntryPoint == nullptr ||
        ReloadScriptEntryPoint == nullptr ||
        DestroyScriptEntryPoint == nullptr ||
        GetScriptCountEntryPoint == nullptr ||
        GetInvocationMetricsEntryPoint == nullptr ||
        GetLastManagedErrorEntryPoint == nullptr)
    {
        return FailLocked(FString::Printf(
            TEXT("Managed entry-point loading failed: world=(0x%08X,0x%08X,0x%08X,0x%08X) script=(0x%08X,0x%08X,0x%08X,0x%08X,0x%08X,0x%08X) debug=(0x%08X,0x%08X,0x%08X,0x%08X,0x%08X,0x%08X,0x%08X) profiler=(0x%08X,0x%08X) diagnostics=(0x%08X,0x%08X)."),
            static_cast<uint32>(CreateResult),
            static_cast<uint32>(DestroyResult),
            static_cast<uint32>(CountResult),
            static_cast<uint32>(CollectResult),
            static_cast<uint32>(CreateScriptResult),
            static_cast<uint32>(TickScriptResult),
            static_cast<uint32>(DispatchEventResult),
            static_cast<uint32>(ReloadScriptResult),
            static_cast<uint32>(DestroyScriptResult),
            static_cast<uint32>(ScriptCountResult),
            static_cast<uint32>(ConfigureDebugResult),
            static_cast<uint32>(BeginDebugTickResult),
            static_cast<uint32>(DebugStateResult),
            static_cast<uint32>(ReloadStateResult),
            static_cast<uint32>(DebugControlResult),
            static_cast<uint32>(DisableDebugResult),
            static_cast<uint32>(DebugActiveResult),
            static_cast<uint32>(ProfilerControlResult),
            static_cast<uint32>(ProfilerTraceResult),
            static_cast<uint32>(MetricsResult),
            static_cast<uint32>(LastErrorResult)));
    }

    LastError.Reset();
    State = EUnifiedScriptCoreClrHostState::Ready;
    UE_LOG(
        LogTemp,
        Display,
        TEXT("UnifiedScript loaded CoreCLR through %s"),
        *LoadedHostFxrPath);
    return true;
}

bool FUnifiedScriptCoreClrHost::FailLocked(FString Message)
{
    if (HostFxrLibrary != nullptr)
    {
        FPlatformProcess::FreeDllHandle(HostFxrLibrary);
        HostFxrLibrary = nullptr;
    }
    LoadAssemblyAndGetFunctionPointer = nullptr;
    CreateWorldEntryPoint = nullptr;
    DestroyWorldEntryPoint = nullptr;
    GetWorldCountEntryPoint = nullptr;
    CreateScriptEntryPoint = nullptr;
    TickScriptEntryPoint = nullptr;
    ConfigureScriptDebugEntryPoint = nullptr;
    BeginScriptDebugTickEntryPoint = nullptr;
    GetScriptDebugStateEntryPoint = nullptr;
    GetScriptReloadStateEntryPoint = nullptr;
    SetScriptProfilerEnabledEntryPoint = nullptr;
    GetScriptProfilerTraceEntryPoint = nullptr;
    ControlScriptDebugEntryPoint = nullptr;
    DisableScriptDebugEntryPoint = nullptr;
    IsScriptDebugActiveEntryPoint = nullptr;
    DispatchEventEntryPoint = nullptr;
    ReloadScriptEntryPoint = nullptr;
    DestroyScriptEntryPoint = nullptr;
    GetScriptCountEntryPoint = nullptr;
    GetInvocationMetricsEntryPoint = nullptr;
    GetLastManagedErrorEntryPoint = nullptr;
    LastError = MoveTemp(Message);
    State = EUnifiedScriptCoreClrHostState::Failed;
    UE_LOG(LogTemp, Error, TEXT("%s"), *LastError);
    return false;
}

#undef UNIFIEDSCRIPT_HOSTFXR_CALLTYPE
#undef UNIFIEDSCRIPT_CORECLR_DELEGATE_CALLTYPE
