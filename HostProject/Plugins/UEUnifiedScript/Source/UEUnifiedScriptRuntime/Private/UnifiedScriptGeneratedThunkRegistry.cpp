#include "UnifiedScriptGeneratedThunkRegistry.h"

#include "Misc/ScopeRWLock.h"
#include "UnifiedScriptGeneratedThunks.h"

namespace UE::UnifiedScript::Generated::Registry::Private
{
FRWLock& TableLock()
{
    static FRWLock Lock;
    return Lock;
}

TMap<FName, FUnifiedScriptGeneratedThunkTable>& ExternalTables()
{
    static TMap<FName, FUnifiedScriptGeneratedThunkTable> Tables;
    return Tables;
}

FUnifiedScriptGeneratedThunkTable BuiltInTable()
{
    return {
        &UE::UnifiedScript::Generated::DescribeReadResult,
        &UE::UnifiedScript::Generated::DescribeInvokeResultCount,
        &UE::UnifiedScript::Generated::DescribeInvokeResult,
        &UE::UnifiedScript::Generated::ReadProperty,
        &UE::UnifiedScript::Generated::WriteProperty,
        &UE::UnifiedScript::Generated::Invoke,
    };
}

bool ResolvePropertyTable(
    uint64 MemberId,
    FUnifiedScriptGeneratedThunkTable& OutTable,
    EUnifiedScriptValueKind& OutKind,
    uint64& OutTypeId)
{
    bool bFound = false;
    bool bAmbiguous = false;
    const auto Consider = [&](const FUnifiedScriptGeneratedThunkTable& Table)
    {
        EUnifiedScriptValueKind Kind;
        uint64 TypeId = 0;
        if (!Table.DescribeReadResult(MemberId, Kind, TypeId))
        {
            return;
        }
        if (bFound)
        {
            bAmbiguous = true;
            return;
        }
        bFound = true;
        OutTable = Table;
        OutKind = Kind;
        OutTypeId = TypeId;
    };

    Consider(BuiltInTable());
    FReadScopeLock Lock(TableLock());
    for (const TPair<FName, FUnifiedScriptGeneratedThunkTable>& Entry : ExternalTables())
    {
        Consider(Entry.Value);
    }
    return bFound && !bAmbiguous;
}

bool ResolveInvocationTable(
    uint64 MemberId,
    FUnifiedScriptGeneratedThunkTable& OutTable,
    uint32& OutResultCount)
{
    bool bFound = false;
    bool bAmbiguous = false;
    const auto Consider = [&](const FUnifiedScriptGeneratedThunkTable& Table)
    {
        uint32 ResultCount = 0;
        if (!Table.DescribeInvokeResultCount(MemberId, ResultCount))
        {
            return;
        }
        if (bFound)
        {
            bAmbiguous = true;
            return;
        }
        bFound = true;
        OutTable = Table;
        OutResultCount = ResultCount;
    };

    Consider(BuiltInTable());
    FReadScopeLock Lock(TableLock());
    for (const TPair<FName, FUnifiedScriptGeneratedThunkTable>& Entry : ExternalTables())
    {
        Consider(Entry.Value);
    }
    return bFound && !bAmbiguous;
}

bool FailMissing(uint64 MemberId, FUnifiedScriptInvocationResult& OutResult)
{
    OutResult.Error = EUnifiedScriptInvocationError::InvalidMemberPath;
    OutResult.HandleError = EUnifiedScriptObjectHandleError::None;
    OutResult.Message = FString::Printf(
        TEXT("No unique generated thunk table owns member ID 0x%016llx."),
        static_cast<unsigned long long>(MemberId));
    return false;
}
}

bool FUnifiedScriptGeneratedThunkRegistry::RegisterTable(
    FName OwnerModule,
    const FUnifiedScriptGeneratedThunkTable& Table)
{
    using namespace UE::UnifiedScript::Generated::Registry::Private;
    if (OwnerModule.IsNone() || !Table.IsValid())
    {
        return false;
    }
    FWriteScopeLock Lock(TableLock());
    if (ExternalTables().Contains(OwnerModule))
    {
        return false;
    }
    ExternalTables().Add(OwnerModule, Table);
    return true;
}

void FUnifiedScriptGeneratedThunkRegistry::UnregisterTable(FName OwnerModule)
{
    using namespace UE::UnifiedScript::Generated::Registry::Private;
    FWriteScopeLock Lock(TableLock());
    ExternalTables().Remove(OwnerModule);
}

bool FUnifiedScriptGeneratedThunkRegistry::DescribeReadResult(
    uint64 MemberId,
    EUnifiedScriptValueKind& OutKind,
    uint64& OutTypeId)
{
    using namespace UE::UnifiedScript::Generated::Registry::Private;
    FUnifiedScriptGeneratedThunkTable Table;
    return ResolvePropertyTable(MemberId, Table, OutKind, OutTypeId);
}

bool FUnifiedScriptGeneratedThunkRegistry::DescribeInvokeResultCount(
    uint64 MemberId,
    uint32& OutResultCount)
{
    using namespace UE::UnifiedScript::Generated::Registry::Private;
    FUnifiedScriptGeneratedThunkTable Table;
    return ResolveInvocationTable(MemberId, Table, OutResultCount);
}

bool FUnifiedScriptGeneratedThunkRegistry::DescribeInvokeResult(
    uint64 MemberId,
    uint32 ResultIndex,
    EUnifiedScriptValueKind& OutKind,
    uint64& OutTypeId)
{
    using namespace UE::UnifiedScript::Generated::Registry::Private;
    FUnifiedScriptGeneratedThunkTable Table;
    uint32 ResultCount = 0;
    return ResolveInvocationTable(MemberId, Table, ResultCount) &&
        ResultIndex < ResultCount &&
        Table.DescribeInvokeResult(MemberId, ResultIndex, OutKind, OutTypeId);
}

bool FUnifiedScriptGeneratedThunkRegistry::ReadProperty(
    UObject* Target,
    FUnifiedScriptObjectRegistry& Registry,
    uint64 MemberId,
    FUnifiedScriptValue& OutValue,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Generated::Registry::Private;
    FUnifiedScriptGeneratedThunkTable Table;
    EUnifiedScriptValueKind Kind;
    uint64 TypeId = 0;
    return ResolvePropertyTable(MemberId, Table, Kind, TypeId)
        ? Table.ReadProperty(Target, Registry, MemberId, OutValue, OutResult)
        : FailMissing(MemberId, OutResult);
}

bool FUnifiedScriptGeneratedThunkRegistry::WriteProperty(
    UObject* Target,
    FUnifiedScriptObjectRegistry& Registry,
    uint64 MemberId,
    const FUnifiedScriptValue& Value,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Generated::Registry::Private;
    FUnifiedScriptGeneratedThunkTable Table;
    EUnifiedScriptValueKind Kind;
    uint64 TypeId = 0;
    return ResolvePropertyTable(MemberId, Table, Kind, TypeId)
        ? Table.WriteProperty(Target, Registry, MemberId, Value, OutResult)
        : FailMissing(MemberId, OutResult);
}

bool FUnifiedScriptGeneratedThunkRegistry::Invoke(
    UObject* Target,
    FUnifiedScriptObjectRegistry& Registry,
    uint64 MemberId,
    TConstArrayView<FUnifiedScriptValue> Arguments,
    FUnifiedScriptValue& OutReturnValue,
    TArray<FUnifiedScriptValue>& OutOutputValues,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Generated::Registry::Private;
    FUnifiedScriptGeneratedThunkTable Table;
    uint32 ResultCount = 0;
    return ResolveInvocationTable(MemberId, Table, ResultCount)
        ? Table.Invoke(
            Target,
            Registry,
            MemberId,
            Arguments,
            OutReturnValue,
            OutOutputValues,
            OutResult)
        : FailMissing(MemberId, OutResult);
}
