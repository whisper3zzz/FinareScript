#include "FinareGeneratedThunkRegistry.h"

#include "Misc/ScopeRWLock.h"
#include "FinareGeneratedThunks.h"

namespace UE::Finare::Generated::Registry::Private
{
FRWLock& TableLock()
{
    static FRWLock Lock;
    return Lock;
}

TMap<FName, FFinareGeneratedThunkTable>& ExternalTables()
{
    static TMap<FName, FFinareGeneratedThunkTable> Tables;
    return Tables;
}

FFinareGeneratedThunkTable BuiltInTable()
{
    return {
        &UE::Finare::Generated::DescribeReadResult,
        &UE::Finare::Generated::DescribeInvokeResultCount,
        &UE::Finare::Generated::DescribeInvokeResult,
        &UE::Finare::Generated::ReadProperty,
        &UE::Finare::Generated::WriteProperty,
        &UE::Finare::Generated::Invoke,
        &UE::Finare::Generated::InvokeInt32x2,
        &UE::Finare::Generated::InvokeFixed,
    };
}

bool ResolvePropertyTable(
    uint64 MemberId,
    FFinareGeneratedThunkTable& OutTable,
    EFinareValueKind& OutKind,
    uint64& OutTypeId)
{
    const FFinareGeneratedThunkTable BuiltIn = BuiltInTable();
    if (BuiltIn.DescribeReadResult(MemberId, OutKind, OutTypeId))
    {
        OutTable = BuiltIn;
        return true;
    }

    bool bFound = false;
    bool bAmbiguous = false;
    const auto Consider = [&](const FFinareGeneratedThunkTable& Table)
    {
        EFinareValueKind Kind;
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

    FReadScopeLock Lock(TableLock());
    for (const TPair<FName, FFinareGeneratedThunkTable>& Entry : ExternalTables())
    {
        Consider(Entry.Value);
    }
    return bFound && !bAmbiguous;
}

bool ResolveInvocationTable(
    uint64 MemberId,
    FFinareGeneratedThunkTable& OutTable,
    uint32& OutResultCount)
{
    const FFinareGeneratedThunkTable BuiltIn = BuiltInTable();
    if (BuiltIn.DescribeInvokeResultCount(MemberId, OutResultCount))
    {
        OutTable = BuiltIn;
        return true;
    }

    bool bFound = false;
    bool bAmbiguous = false;
    const auto Consider = [&](const FFinareGeneratedThunkTable& Table)
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

    FReadScopeLock Lock(TableLock());
    for (const TPair<FName, FFinareGeneratedThunkTable>& Entry : ExternalTables())
    {
        Consider(Entry.Value);
    }
    return bFound && !bAmbiguous;
}

bool FailMissing(uint64 MemberId, FFinareInvocationResult& OutResult)
{
    OutResult.Error = EFinareInvocationError::InvalidMemberPath;
    OutResult.HandleError = EFinareObjectHandleError::None;
    OutResult.Message = FString::Printf(
        TEXT("No unique generated thunk table owns member ID 0x%016llx."),
        static_cast<unsigned long long>(MemberId));
    return false;
}
}

bool FFinareGeneratedThunkRegistry::RegisterTable(
    FName OwnerModule,
    const FFinareGeneratedThunkTable& Table)
{
    using namespace UE::Finare::Generated::Registry::Private;
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

void FFinareGeneratedThunkRegistry::UnregisterTable(FName OwnerModule)
{
    using namespace UE::Finare::Generated::Registry::Private;
    FWriteScopeLock Lock(TableLock());
    ExternalTables().Remove(OwnerModule);
}

bool FFinareGeneratedThunkRegistry::DescribeReadResult(
    uint64 MemberId,
    EFinareValueKind& OutKind,
    uint64& OutTypeId)
{
    using namespace UE::Finare::Generated::Registry::Private;
    FFinareGeneratedThunkTable Table;
    return ResolvePropertyTable(MemberId, Table, OutKind, OutTypeId);
}

bool FFinareGeneratedThunkRegistry::DescribeInvokeResultCount(
    uint64 MemberId,
    uint32& OutResultCount)
{
    using namespace UE::Finare::Generated::Registry::Private;
    FFinareGeneratedThunkTable Table;
    return ResolveInvocationTable(MemberId, Table, OutResultCount);
}

bool FFinareGeneratedThunkRegistry::DescribeInvokeResult(
    uint64 MemberId,
    uint32 ResultIndex,
    EFinareValueKind& OutKind,
    uint64& OutTypeId)
{
    using namespace UE::Finare::Generated::Registry::Private;
    FFinareGeneratedThunkTable Table;
    uint32 ResultCount = 0;
    return ResolveInvocationTable(MemberId, Table, ResultCount) &&
        ResultIndex < ResultCount &&
        Table.DescribeInvokeResult(MemberId, ResultIndex, OutKind, OutTypeId);
}

bool FFinareGeneratedThunkRegistry::ReadProperty(
    UObject* Target,
    FFinareObjectRegistry& Registry,
    uint64 MemberId,
    FFinareValue& OutValue,
    FFinareInvocationResult& OutResult)
{
    using namespace UE::Finare::Generated::Registry::Private;
    FFinareGeneratedThunkTable Table;
    EFinareValueKind Kind;
    uint64 TypeId = 0;
    return ResolvePropertyTable(MemberId, Table, Kind, TypeId)
        ? Table.ReadProperty(Target, Registry, MemberId, OutValue, OutResult)
        : FailMissing(MemberId, OutResult);
}

bool FFinareGeneratedThunkRegistry::WriteProperty(
    UObject* Target,
    FFinareObjectRegistry& Registry,
    uint64 MemberId,
    const FFinareValue& Value,
    FFinareInvocationResult& OutResult)
{
    using namespace UE::Finare::Generated::Registry::Private;
    FFinareGeneratedThunkTable Table;
    EFinareValueKind Kind;
    uint64 TypeId = 0;
    return ResolvePropertyTable(MemberId, Table, Kind, TypeId)
        ? Table.WriteProperty(Target, Registry, MemberId, Value, OutResult)
        : FailMissing(MemberId, OutResult);
}

bool FFinareGeneratedThunkRegistry::Invoke(
    UObject* Target,
    FFinareObjectRegistry& Registry,
    uint64 MemberId,
    TConstArrayView<FFinareValue> Arguments,
    FFinareValue& OutReturnValue,
    TArray<FFinareValue>& OutOutputValues,
    FFinareInvocationResult& OutResult)
{
    using namespace UE::Finare::Generated::Registry::Private;
    FFinareGeneratedThunkTable Table;
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

bool FFinareGeneratedThunkRegistry::InvokeInt32x2(
    UObject* Target,
    uint64 MemberId,
    int32 Argument0,
    int32 Argument1,
    int32& OutReturnValue,
    FFinareInvocationResult& OutResult)
{
    using namespace UE::Finare::Generated::Registry::Private;
    FFinareGeneratedThunkTable Table;
    uint32 ResultCount = 0;
    return ResolveInvocationTable(MemberId, Table, ResultCount) &&
            Table.InvokeInt32x2 != nullptr
        ? Table.InvokeInt32x2(
            Target,
            MemberId,
            Argument0,
            Argument1,
            OutReturnValue,
            OutResult)
        : FailMissing(MemberId, OutResult);
}

bool FFinareGeneratedThunkRegistry::InvokeFixed(
    UObject* Target,
    FFinareObjectRegistry& Registry,
    uint64 MemberId,
    TConstArrayView<FFinareNativeValueV3> Arguments,
    FFinareNativeValueV3* OutReturnValue,
    FFinareInvocationResult& OutResult)
{
    using namespace UE::Finare::Generated::Registry::Private;
    FFinareGeneratedThunkTable Table;
    uint32 ResultCount = 0;
    return ResolveInvocationTable(MemberId, Table, ResultCount) &&
            Table.InvokeFixed != nullptr
        ? Table.InvokeFixed(
            Target,
            Registry,
            MemberId,
            Arguments,
            OutReturnValue,
            OutResult)
        : FailMissing(MemberId, OutResult);
}
