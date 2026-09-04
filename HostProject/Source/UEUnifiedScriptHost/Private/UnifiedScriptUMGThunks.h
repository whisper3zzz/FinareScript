#pragma once

#include "UnifiedScriptGeneratedThunkRegistry.h"
#include "UnifiedScriptNativeTransport.h"

namespace UE::UnifiedScript::Generated::UI
{
bool DescribeReadResult(uint64, EUnifiedScriptValueKind&, uint64&);
bool DescribeInvokeResultCount(uint64, uint32&);
bool DescribeInvokeResult(uint64, uint32, EUnifiedScriptValueKind&, uint64&);
bool ReadProperty(UObject*, FUnifiedScriptObjectRegistry&, uint64,
    FUnifiedScriptValue&, FUnifiedScriptInvocationResult&);
bool WriteProperty(UObject*, FUnifiedScriptObjectRegistry&, uint64,
    const FUnifiedScriptValue&, FUnifiedScriptInvocationResult&);
bool Invoke(UObject*, FUnifiedScriptObjectRegistry&, uint64,
    TConstArrayView<FUnifiedScriptValue>, FUnifiedScriptValue&,
    TArray<FUnifiedScriptValue>&, FUnifiedScriptInvocationResult&);
bool InvokeInt32x2(UObject*, uint64, int32, int32, int32&, FUnifiedScriptInvocationResult&);
bool InvokeFixed(UObject*, FUnifiedScriptObjectRegistry&, uint64,
    TConstArrayView<FUnifiedScriptNativeValueV3>, FUnifiedScriptNativeValueV3*,
    FUnifiedScriptInvocationResult&);

inline FUnifiedScriptGeneratedThunkTable Table()
{
    return { &DescribeReadResult, &DescribeInvokeResultCount, &DescribeInvokeResult,
        &ReadProperty, &WriteProperty, &Invoke, &InvokeInt32x2, &InvokeFixed };
}
}
