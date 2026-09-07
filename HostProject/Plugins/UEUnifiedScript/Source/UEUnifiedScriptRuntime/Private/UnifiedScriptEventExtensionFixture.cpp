#include "UnifiedScriptEventExtensionFixture.h"

int32 AUnifiedScriptEventExtensionFixture::EvaluateInherited_Implementation(
    int32 NativeInput,
    int32& NativeAccumulator,
    int32& NativeOutput)
{
    ++ParentInvocationCount;
    NativeOutput = NativeInput + NativeAccumulator;
    NativeAccumulator = NativeOutput;
    return NativeOutput;
}

int32 AUnifiedScriptEventExtensionFixture::EvaluateInheritedAlternate_Implementation(
    int32 NativeInput,
    int32& NativeAccumulator,
    int32& NativeOutput)
{
    ++ParentInvocationCount;
    NativeOutput = NativeInput - NativeAccumulator;
    NativeAccumulator = NativeOutput;
    return NativeOutput;
}

TArray<int32> AUnifiedScriptEventExtensionFixture::ExchangeInheritedArray_Implementation(
    const TArray<int32>& NativeInput,
    TArray<int32>& NativeState,
    TArray<int32>& NativeOutput)
{
    ++ParentInvocationCount;
    NativeOutput = NativeInput;
    NativeOutput.Append(NativeState);
    NativeState = NativeOutput;
    return NativeOutput;
}
