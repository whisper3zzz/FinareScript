/**
 * @Copyright: 2026 Tencent Inc. All rights reserved. reserved.
 * @Author: whispersong
 * @Date: 2026-08-25 10:42:29
 * @LastEditors: whispersong
 * @LastEditTime: 2026-08-25 10:47:07
 * @FilePath: \Plugins\UEUnifiedScript\Source\UEUnifiedScriptRuntime\Private\UnifiedScriptInteropFixture.cpp
 * @Description:
 */
#include "UnifiedScriptInteropFixture.h"

FVector UUnifiedScriptInteropFixture::AddVector(FVector Left, FVector Right) const
{
    return Left + Right;
}

FTransform UUnifiedScriptInteropFixture::EchoTransform(FTransform Value) const
{
    return Value;
}

UObject* UUnifiedScriptInteropFixture::EchoObject(UObject* Value) const
{
    return Value;
}

UClass* UUnifiedScriptInteropFixture::EchoClass(UClass* Value) const
{
    return Value;
}

int32 UUnifiedScriptInteropFixture::StringLength(FString Value) const
{
    return Value.Len();
}

int32 UUnifiedScriptInteropFixture::SumArray(const TArray<int32>& Values) const
{
    int32 Result = 0;
    for (const int32 Value : Values)
    {
        Result += Value;
    }
    return Result;
}

int32 UUnifiedScriptInteropFixture::CountSet(const TSet<int32>& Values) const
{
    return Values.Num();
}

int32 UUnifiedScriptInteropFixture::SumMap(const TMap<int32, int32>& Values) const
{
    int32 Result = 0;
    for (const TPair<int32, int32>& Entry : Values)
    {
        Result += Entry.Key + Entry.Value;
    }
    return Result;
}

TArray<int32> UUnifiedScriptInteropFixture::EchoArray(const TArray<int32>& Values) const
{
    return Values;
}

TSet<int32> UUnifiedScriptInteropFixture::EchoSet(const TSet<int32>& Values) const
{
    return Values;
}

TMap<int32, int32> UUnifiedScriptInteropFixture::EchoMap(
    const TMap<int32, int32>& Values) const
{
    return Values;
}

TArray<int32> UUnifiedScriptInteropFixture::MakeArray(int32 Count) const
{
    TArray<int32> Values;
    Count = FMath::Clamp(Count, 0, 4096);
    Values.Reserve(Count);
    for (int32 Index = 0; Index < Count; ++Index)
    {
        Values.Add(Index);
    }
    return Values;
}

bool UUnifiedScriptInteropFixture::MutateValues(
    int32 Input,
    int32& OutDoubled,
    int32& Accumulator) const
{
    OutDoubled = Input * 2;
    Accumulator += Input;
    return true;
}

int64 UUnifiedScriptInteropFixture::AddInt64(int64 Left, int64 Right) const
{
    return Left + Right;
}
