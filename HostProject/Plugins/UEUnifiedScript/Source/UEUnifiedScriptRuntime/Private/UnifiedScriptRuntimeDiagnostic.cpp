#include "UnifiedScriptRuntimeDiagnostic.h"

#include "Dom/JsonObject.h"
#include "GenericPlatform/GenericPlatformCrashContext.h"
#include "HAL/CriticalSection.h"
#include "Misc/DateTime.h"
#include "Misc/ScopeLock.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

#include <atomic>

namespace UE::UnifiedScript::RuntimeDiagnostic::Private
{
constexpr const TCHAR* SchemaVersion = TEXT("unifiedscript.runtime-diagnostic/v0.1");
constexpr const TCHAR* BreadcrumbSchemaVersion =
    TEXT("unifiedscript.runtime-breadcrumbs/v0.1");
constexpr const TCHAR* CrashSchemaKey = TEXT("UnifiedScriptBreadcrumbSchema");
constexpr const TCHAR* CrashSequenceKey = TEXT("UnifiedScriptBreadcrumbSequence");
constexpr const TCHAR* CrashKindKey = TEXT("UnifiedScriptBreadcrumbKind");
constexpr const TCHAR* CrashOperationKey = TEXT("UnifiedScriptBreadcrumbOperation");
constexpr const TCHAR* CrashWorldKey = TEXT("UnifiedScriptBreadcrumbWorldHostId");
constexpr const TCHAR* CrashInstanceKey = TEXT("UnifiedScriptBreadcrumbInstanceId");
constexpr const TCHAR* CrashGenerationKey = TEXT("UnifiedScriptBreadcrumbGeneration");
constexpr const TCHAR* CrashMessageKey = TEXT("UnifiedScriptBreadcrumbMessage");
constexpr const TCHAR* CrashRingKey = TEXT("UnifiedScriptBreadcrumbs");

FCriticalSection GBreadcrumbLock;
TArray<FUnifiedScriptRuntimeBreadcrumb> GBreadcrumbs;
uint64 GDroppedBreadcrumbCount = 0;
std::atomic<uint64> GNextBreadcrumbSequence{1};

TSharedRef<FJsonObject> MakeBreadcrumbJson(
    const FUnifiedScriptRuntimeBreadcrumb& Breadcrumb)
{
    const TSharedRef<FJsonObject> Object = MakeShared<FJsonObject>();
    Object->SetStringField(TEXT("sequence"), LexToString(Breadcrumb.Sequence));
    Object->SetStringField(TEXT("timestampUtc"), Breadcrumb.TimestampUtc);
    Object->SetStringField(TEXT("kind"), Breadcrumb.Kind);
    Object->SetStringField(TEXT("operation"), Breadcrumb.Operation);
    Object->SetStringField(TEXT("message"), Breadcrumb.Message);
    Object->SetStringField(TEXT("worldHostId"), LexToString(Breadcrumb.WorldHostId));
    Object->SetStringField(TEXT("instanceId"), LexToString(Breadcrumb.InstanceId));
    Object->SetNumberField(TEXT("generation"), Breadcrumb.Generation);
    return Object;
}

FString SerializeBreadcrumbSnapshot(
    const FUnifiedScriptRuntimeBreadcrumbSnapshot& Snapshot)
{
    const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
    Root->SetStringField(TEXT("schemaVersion"), BreadcrumbSchemaVersion);
    Root->SetNumberField(
        TEXT("capacity"),
        FUnifiedScriptRuntimeBreadcrumbs::MaximumEntryCount);
    Root->SetStringField(TEXT("droppedCount"), LexToString(Snapshot.DroppedCount));
    TArray<TSharedPtr<FJsonValue>> Entries;
    Entries.Reserve(Snapshot.Entries.Num());
    for (const FUnifiedScriptRuntimeBreadcrumb& Breadcrumb : Snapshot.Entries)
    {
        Entries.Add(MakeShared<FJsonValueObject>(MakeBreadcrumbJson(Breadcrumb)));
    }
    Root->SetArrayField(TEXT("entries"), MoveTemp(Entries));

    FString Json;
    const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Json);
    return FJsonSerializer::Serialize(Root, Writer) ? Json : FString();
}

void PublishCrashContext(
    const FUnifiedScriptRuntimeBreadcrumb& Breadcrumb,
    const FUnifiedScriptRuntimeBreadcrumbSnapshot& Snapshot)
{
    if (!IsInGameThread())
    {
        return;
    }
    FGenericCrashContext::SetGameData(CrashSchemaKey, BreadcrumbSchemaVersion);
    FGenericCrashContext::SetGameData(
        CrashSequenceKey,
        LexToString(Breadcrumb.Sequence));
    FGenericCrashContext::SetGameData(CrashKindKey, Breadcrumb.Kind);
    FGenericCrashContext::SetGameData(CrashOperationKey, Breadcrumb.Operation);
    FGenericCrashContext::SetGameData(
        CrashWorldKey,
        LexToString(Breadcrumb.WorldHostId));
    FGenericCrashContext::SetGameData(
        CrashInstanceKey,
        LexToString(Breadcrumb.InstanceId));
    FGenericCrashContext::SetGameData(
        CrashGenerationKey,
        LexToString(Breadcrumb.Generation));
    FGenericCrashContext::SetGameData(CrashMessageKey, Breadcrumb.Message);
    FGenericCrashContext::SetGameData(
        CrashRingKey,
        SerializeBreadcrumbSnapshot(Snapshot));
}

bool ReadRequiredString(
    const TSharedPtr<FJsonObject>& Object,
    const TCHAR* Field,
    FString& OutValue,
    FString& OutError)
{
    if (!Object.IsValid() || !Object->TryGetStringField(Field, OutValue))
    {
        OutError = FString::Printf(
            TEXT("Runtime diagnostic field '%s' must be a string."),
            Field);
        return false;
    }
    return true;
}

bool ParseUint64(const FString& Value, const TCHAR* Field, uint64& OutValue, FString& OutError)
{
    if (Value.IsEmpty())
    {
        OutValue = 0;
        return true;
    }
    FString Digits = Value;
    int32 Base = 10;
    if (Digits.StartsWith(TEXT("0x"), ESearchCase::IgnoreCase))
    {
        Digits.RightChopInline(2);
        Base = 16;
    }
    if (Digits.IsEmpty())
    {
        OutError = FString::Printf(TEXT("Runtime diagnostic field '%s' is invalid."), Field);
        return false;
    }
    uint64 Result = 0;
    for (const TCHAR Character : Digits)
    {
        const int32 Digit = Character >= TEXT('0') && Character <= TEXT('9')
            ? Character - TEXT('0')
            : Character >= TEXT('a') && Character <= TEXT('f')
                ? Character - TEXT('a') + 10
                : Character >= TEXT('A') && Character <= TEXT('F')
                    ? Character - TEXT('A') + 10
                    : -1;
        if (Digit < 0 || Digit >= Base ||
            Result > (MAX_uint64 - static_cast<uint64>(Digit)) / static_cast<uint64>(Base))
        {
            OutError = FString::Printf(TEXT("Runtime diagnostic field '%s' is invalid."), Field);
            return false;
        }
        Result = Result * static_cast<uint64>(Base) + static_cast<uint64>(Digit);
    }
    OutValue = Result;
    return true;
}

bool ParseSource(
    const TSharedPtr<FJsonObject>& Object,
    FUnifiedScriptRuntimeSourceLocation& OutSource,
    FString& OutError)
{
    double Line = 0.0;
    double Column = 0.0;
    if (!ReadRequiredString(Object, TEXT("filePath"), OutSource.FilePath, OutError) ||
        !Object->TryGetNumberField(TEXT("line"), Line) || Line < 1.0 ||
        !Object->TryGetNumberField(TEXT("column"), Column) || Column < 1.0 ||
        !ReadRequiredString(Object, TEXT("methodName"), OutSource.MethodName, OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("The managed runtime diagnostic source is invalid.");
        }
        return false;
    }
    OutSource.Line = static_cast<int32>(Line);
    OutSource.Column = static_cast<int32>(Column);
    return true;
}
}

FString FUnifiedScriptRuntimeBreadcrumbSnapshot::ToJson() const
{
    return UE::UnifiedScript::RuntimeDiagnostic::Private::
        SerializeBreadcrumbSnapshot(*this);
}

void FUnifiedScriptRuntimeBreadcrumbs::RecordDiagnostic(
    const FUnifiedScriptRuntimeDiagnostic& Diagnostic)
{
    FUnifiedScriptRuntimeBreadcrumb Breadcrumb;
    Breadcrumb.Kind = Diagnostic.Kind;
    Breadcrumb.Operation = Diagnostic.Operation;
    Breadcrumb.Message = Diagnostic.Message;
    Breadcrumb.WorldHostId = Diagnostic.WorldHostId;
    Breadcrumb.InstanceId = Diagnostic.InstanceId;
    Breadcrumb.Generation = Diagnostic.Generation;
    Record(MoveTemp(Breadcrumb));
}

void FUnifiedScriptRuntimeBreadcrumbs::RecordLifecycle(
    const FString& Operation,
    uint64 WorldHostId,
    const FString& Message)
{
    FUnifiedScriptRuntimeBreadcrumb Breadcrumb;
    Breadcrumb.Kind = TEXT("lifecycle");
    Breadcrumb.Operation = Operation;
    Breadcrumb.Message = Message;
    Breadcrumb.WorldHostId = WorldHostId;
    Record(MoveTemp(Breadcrumb));
}

void FUnifiedScriptRuntimeBreadcrumbs::RecordFailure(
    const FString& Operation,
    const FString& Message,
    uint64 WorldHostId,
    uint64 InstanceId,
    int32 Generation)
{
    FUnifiedScriptRuntimeBreadcrumb Breadcrumb;
    Breadcrumb.Kind = TEXT("nativeFailure");
    Breadcrumb.Operation = Operation;
    Breadcrumb.Message = Message;
    Breadcrumb.WorldHostId = WorldHostId;
    Breadcrumb.InstanceId = InstanceId;
    Breadcrumb.Generation = Generation;
    Record(MoveTemp(Breadcrumb));
}

FUnifiedScriptRuntimeBreadcrumbSnapshot FUnifiedScriptRuntimeBreadcrumbs::Capture()
{
    using namespace UE::UnifiedScript::RuntimeDiagnostic::Private;
    FScopeLock Lock(&GBreadcrumbLock);
    FUnifiedScriptRuntimeBreadcrumbSnapshot Snapshot;
    Snapshot.DroppedCount = GDroppedBreadcrumbCount;
    Snapshot.Entries = GBreadcrumbs;
    return Snapshot;
}

FString FUnifiedScriptRuntimeBreadcrumbs::ExportJson()
{
    return Capture().ToJson();
}

void FUnifiedScriptRuntimeBreadcrumbs::Record(
    FUnifiedScriptRuntimeBreadcrumb Breadcrumb)
{
    using namespace UE::UnifiedScript::RuntimeDiagnostic::Private;
    do
    {
        Breadcrumb.Sequence = GNextBreadcrumbSequence.fetch_add(
            1,
            std::memory_order_relaxed);
    }
    while (Breadcrumb.Sequence == 0);
    Breadcrumb.TimestampUtc = FDateTime::UtcNow().ToIso8601();
    Breadcrumb.Kind.LeftInline(MaximumKindLength, EAllowShrinking::No);
    Breadcrumb.Operation.LeftInline(MaximumOperationLength, EAllowShrinking::No);
    Breadcrumb.Message.LeftInline(MaximumMessageLength, EAllowShrinking::No);

    FUnifiedScriptRuntimeBreadcrumbSnapshot CrashSnapshot;
    {
        FScopeLock Lock(&GBreadcrumbLock);
        if (GBreadcrumbs.Num() == MaximumEntryCount)
        {
            GBreadcrumbs.RemoveAt(0, 1, EAllowShrinking::No);
            ++GDroppedBreadcrumbCount;
        }
        GBreadcrumbs.Add(Breadcrumb);
        CrashSnapshot.DroppedCount = GDroppedBreadcrumbCount;
        const int32 CrashEntryCount = FMath::Min(
            MaximumCrashContextEntryCount,
            GBreadcrumbs.Num());
        CrashSnapshot.Entries.Append(
            GBreadcrumbs.GetData() + GBreadcrumbs.Num() - CrashEntryCount,
            CrashEntryCount);
    }
    PublishCrashContext(Breadcrumb, CrashSnapshot);
}

#if WITH_DEV_AUTOMATION_TESTS
void FUnifiedScriptRuntimeBreadcrumbs::ResetForTests()
{
    using namespace UE::UnifiedScript::RuntimeDiagnostic::Private;
    {
        FScopeLock Lock(&GBreadcrumbLock);
        GBreadcrumbs.Reset();
        GDroppedBreadcrumbCount = 0;
        GNextBreadcrumbSequence.store(1, std::memory_order_relaxed);
    }
    if (IsInGameThread())
    {
        FGenericCrashContext::SetGameData(CrashSchemaKey, FString());
        FGenericCrashContext::SetGameData(CrashSequenceKey, FString());
        FGenericCrashContext::SetGameData(CrashKindKey, FString());
        FGenericCrashContext::SetGameData(CrashOperationKey, FString());
        FGenericCrashContext::SetGameData(CrashWorldKey, FString());
        FGenericCrashContext::SetGameData(CrashInstanceKey, FString());
        FGenericCrashContext::SetGameData(CrashGenerationKey, FString());
        FGenericCrashContext::SetGameData(CrashMessageKey, FString());
        FGenericCrashContext::SetGameData(CrashRingKey, FString());
    }
}
#endif

FString FUnifiedScriptRuntimeSourceLocation::ToDisplayString() const
{
    return IsValid()
        ? FString::Printf(TEXT("%s:%d:%d"), *FilePath, Line, FMath::Max(Column, 1))
        : FString();
}

bool FUnifiedScriptRuntimeDiagnostic::IsValid() const
{
    return SchemaVersion == UE::UnifiedScript::RuntimeDiagnostic::Private::SchemaVersion &&
        Sequence > 0 && !Kind.IsEmpty() && !Operation.IsEmpty() && !Message.IsEmpty();
}

FString FUnifiedScriptRuntimeDiagnostic::ToDisplayString() const
{
    FString Result = FString::Printf(
        TEXT("%s during %s: %s [World=%llu Instance=%llu Generation=%d]"),
        *Kind,
        *Operation,
        *Message,
        WorldHostId,
        InstanceId,
        Generation);
    if (Source.IsValid())
    {
        Result += FString::Printf(TEXT(" (%s)"), *Source.ToDisplayString());
    }
    if (Bridge.IsSet())
    {
        Result += FString::Printf(
            TEXT(" [Bridge=%s Handle=%s Member=%s]"),
            *Bridge.Error,
            *Bridge.HandleError,
            Bridge.MemberPath.IsEmpty() ? TEXT("<unknown>") : *Bridge.MemberPath);
    }
    return Result;
}

bool FUnifiedScriptRuntimeDiagnostic::TryParseBoundaryError(
    const FString& BoundaryError,
    FUnifiedScriptRuntimeDiagnostic& OutDiagnostic,
    FString& OutError)
{
    using namespace UE::UnifiedScript::RuntimeDiagnostic::Private;
    const FString Prefix = FString::Printf(
        TEXT("{\"schemaVersion\":\"%s\""),
        UE::UnifiedScript::RuntimeDiagnostic::Private::SchemaVersion);
    const int32 JsonStart = BoundaryError.Find(Prefix, ESearchCase::CaseSensitive);
    if (JsonStart == INDEX_NONE)
    {
        OutError = TEXT("The managed boundary error has no runtime-diagnostic/v0.1 payload.");
        return false;
    }

    TSharedPtr<FJsonObject> Root;
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(
        BoundaryError.Mid(JsonStart));
    if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
    {
        OutError = TEXT("The managed runtime diagnostic is not valid JSON.");
        return false;
    }

    FUnifiedScriptRuntimeDiagnostic Parsed;
    if (!ReadRequiredString(Root, TEXT("schemaVersion"), Parsed.SchemaVersion, OutError) ||
        Parsed.SchemaVersion != UE::UnifiedScript::RuntimeDiagnostic::Private::SchemaVersion ||
        !ReadRequiredString(Root, TEXT("timestampUtc"), Parsed.TimestampUtc, OutError) ||
        !ReadRequiredString(Root, TEXT("kind"), Parsed.Kind, OutError) ||
        !ReadRequiredString(Root, TEXT("operation"), Parsed.Operation, OutError) ||
        !ReadRequiredString(Root, TEXT("message"), Parsed.Message, OutError) ||
        !ReadRequiredString(Root, TEXT("exceptionType"), Parsed.ExceptionType, OutError) ||
        !ReadRequiredString(Root, TEXT("stackTrace"), Parsed.StackTrace, OutError) ||
        !ReadRequiredString(Root, TEXT("assemblyPath"), Parsed.AssemblyPath, OutError) ||
        !ReadRequiredString(Root, TEXT("typeName"), Parsed.TypeName, OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("The managed runtime diagnostic schema version is unsupported.");
        }
        return false;
    }

    double Sequence = 0.0;
    double Generation = 0.0;
    FString WorldHostId;
    FString InstanceId;
    FString HostHandle;
    FString TargetHandle;
    if (!Root->TryGetNumberField(TEXT("sequence"), Sequence) || Sequence < 1.0 ||
        !Root->TryGetNumberField(TEXT("generation"), Generation) || Generation < 0.0 ||
        !ReadRequiredString(Root, TEXT("worldHostId"), WorldHostId, OutError) ||
        !ReadRequiredString(Root, TEXT("instanceId"), InstanceId, OutError) ||
        !ReadRequiredString(Root, TEXT("hostHandle"), HostHandle, OutError) ||
        !ReadRequiredString(Root, TEXT("targetHandle"), TargetHandle, OutError) ||
        !ParseUint64(WorldHostId, TEXT("worldHostId"), Parsed.WorldHostId, OutError) ||
        !ParseUint64(InstanceId, TEXT("instanceId"), Parsed.InstanceId, OutError) ||
        !ParseUint64(HostHandle, TEXT("hostHandle"), Parsed.HostHandle, OutError) ||
        !ParseUint64(TargetHandle, TEXT("targetHandle"), Parsed.TargetHandle, OutError))
    {
        if (OutError.IsEmpty())
        {
            OutError = TEXT("The managed runtime diagnostic has invalid numeric identities.");
        }
        return false;
    }
    Parsed.Sequence = static_cast<int64>(Sequence);
    Parsed.Generation = static_cast<int32>(Generation);

    const TSharedPtr<FJsonObject>* Source = nullptr;
    if (Root->TryGetObjectField(TEXT("source"), Source) && Source != nullptr)
    {
        if (!ParseSource(*Source, Parsed.Source, OutError))
        {
            return false;
        }
    }

    const TArray<TSharedPtr<FJsonValue>>* SourceFrames = nullptr;
    if (Root->TryGetArrayField(TEXT("sourceFrames"), SourceFrames) && SourceFrames != nullptr)
    {
        if (SourceFrames->Num() > 32)
        {
            OutError = TEXT("The managed runtime diagnostic has more than 32 source frames.");
            return false;
        }
        for (const TSharedPtr<FJsonValue>& Value : *SourceFrames)
        {
            const TSharedPtr<FJsonObject> FrameObject = Value.IsValid()
                ? Value->AsObject()
                : nullptr;
            FUnifiedScriptRuntimeSourceLocation Frame;
            if (!FrameObject.IsValid() || !ParseSource(FrameObject, Frame, OutError))
            {
                return false;
            }
            Parsed.SourceFrames.Add(MoveTemp(Frame));
        }
    }
    if (Parsed.Source.IsValid() && Parsed.SourceFrames.IsEmpty())
    {
        Parsed.SourceFrames.Add(Parsed.Source);
    }

    const TSharedPtr<FJsonObject>* Bridge = nullptr;
    if (Root->TryGetObjectField(TEXT("bridge"), Bridge) && Bridge != nullptr)
    {
        FString BridgeTarget;
        FString MemberId;
        if (!ReadRequiredString(*Bridge, TEXT("error"), Parsed.Bridge.Error, OutError) ||
            !ReadRequiredString(*Bridge, TEXT("handleError"), Parsed.Bridge.HandleError, OutError) ||
            !ReadRequiredString(*Bridge, TEXT("targetHandle"), BridgeTarget, OutError) ||
            !ReadRequiredString(*Bridge, TEXT("memberId"), MemberId, OutError) ||
            !ReadRequiredString(*Bridge, TEXT("memberPath"), Parsed.Bridge.MemberPath, OutError) ||
            !ParseUint64(BridgeTarget, TEXT("bridge.targetHandle"), Parsed.Bridge.TargetHandle, OutError) ||
            !ParseUint64(MemberId, TEXT("bridge.memberId"), Parsed.Bridge.MemberId, OutError))
        {
            return false;
        }
    }

    Parsed.RawJson = BoundaryError.Mid(JsonStart);
    if (!Parsed.IsValid())
    {
        OutError = TEXT("The managed runtime diagnostic is incomplete.");
        return false;
    }
    OutDiagnostic = MoveTemp(Parsed);
    OutError.Reset();
    return true;
}
