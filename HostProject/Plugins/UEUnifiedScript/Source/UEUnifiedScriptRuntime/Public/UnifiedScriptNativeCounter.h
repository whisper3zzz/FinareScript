#pragma once

#include "CoreMinimal.h"

/** Ordinary C++ sample types: no UObject, UCLASS, UFUNCTION or UPROPERTY. */
namespace UE::UnifiedScript::NativeExample
{
struct FCounterChild
{
    int32 Value = 0;
    int32 Add(int32 Delta) { Value += Delta; return Value; }
};

class FCounter
{
public:
    inline static int32 LiveCount = 0;
    inline static int32 DestroyCount = 0;
    inline static int32 ArgumentCalls = 0;
    FCounter() : FCounter(1) {}
    explicit FCounter(int32 Initial) : ChildState{Initial} { ++LiveCount; }
    explicit FCounter(const FCounterChild& Initial) : FCounter(Initial.Value) { ++ArgumentCalls; }
    ~FCounter() { --LiveCount; ++DestroyCount; }
    FCounter(const FCounter&) = delete;
    FCounter& operator=(const FCounter&) = delete;
    int32 Bias = 2;
    FCounterChild* GetChild() { return &ChildState; }
    FCounter* Clone() const { return new FCounter(ChildState.Value); }
    // Deliberate contract violation to prove rejection cannot delete the old owner.
    FCounter* InvalidOwnedAliasForTest() { return this; }
    int32 Scale(int32 Value) const { return ChildState.Value * Value; }
    double Scale(double Value) const { return ChildState.Value * Value; }
    static int32 Multiply(int32 Left, int32 Right) { return Left * Right; }
    bool Flip(bool Value) const { return !Value; }
    FVector Shift(const FVector& Value) const { return Value + FVector(Bias); }
    void Reset() { ChildState.Value = 0; }
    int32 ReadPointer(FCounterChild* Other) const { ++ArgumentCalls; return Other->Value; }
    int32 ReadConstPointer(const FCounterChild* Other) const { ++ArgumentCalls; return Other->Value; }
    int32 ReadOptional(FCounterChild* Other) const { ++ArgumentCalls; return Other ? Other->Value : -1; }
    int32 ReadOptionalConst(const FCounterChild* Other) const { ++ArgumentCalls; return Other ? Other->Value : -1; }
    int32 AddFrom(const FCounterChild& Other) { ++ArgumentCalls; ChildState.Value += Other.Value; return ChildState.Value; }
    static void AddTo(FCounterChild& Other, int32 Delta) { ++ArgumentCalls; Other.Value += Delta; }
    static int32 Sum(const FCounterChild& Left, const FCounterChild& Right) { ++ArgumentCalls; return Left.Value + Right.Value; }
private:
    FCounterChild ChildState;
};
}
