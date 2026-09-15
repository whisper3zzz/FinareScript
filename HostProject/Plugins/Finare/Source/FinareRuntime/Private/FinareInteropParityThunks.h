#pragma once
#include "FinareGeneratedThunkRegistry.h"
#include "FinareNativeTransport.h"
namespace UE::Finare::InteropParityGenerated
{
    bool DescribeReadResult(uint64, EFinareValueKind&, uint64&);
    bool DescribeInvokeResultCount(uint64, uint32&);
    bool DescribeInvokeResult(uint64, uint32, EFinareValueKind&, uint64&);
    bool ReadProperty(UObject*, FFinareObjectRegistry&, uint64, FFinareValue&, FFinareInvocationResult&);
    bool WriteProperty(UObject*, FFinareObjectRegistry&, uint64, const FFinareValue&, FFinareInvocationResult&);
    bool Invoke(UObject*, FFinareObjectRegistry&, uint64, TConstArrayView<FFinareValue>, FFinareValue&, TArray<FFinareValue>&, FFinareInvocationResult&);
    bool InvokeInt32x2(UObject*, uint64, int32, int32, int32&, FFinareInvocationResult&);
    bool InvokeFixed(UObject*, FFinareObjectRegistry&, uint64, TConstArrayView<FFinareNativeValueV3>, FFinareNativeValueV3*, FFinareInvocationResult&);
}
