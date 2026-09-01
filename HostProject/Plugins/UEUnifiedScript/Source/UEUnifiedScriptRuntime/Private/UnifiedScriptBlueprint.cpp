#include "UnifiedScriptBlueprint.h"

#include "Engine/Engine.h"
#include "GameFramework/Actor.h"
#include "UnifiedScriptAsset.h"
#include "UnifiedScriptComponent.h"
#include "UnifiedScriptNativeTransport.h"
#include "UnifiedScriptWorldSubsystem.h"
#include "UObject/UnrealType.h"

#include UE_INLINE_GENERATED_CPP_BY_NAME(UnifiedScriptBlueprint)

namespace UE::UnifiedScript::Blueprint::Private
{
constexpr int32 ManagedFunctionAbiV2 = 2;
constexpr int32 MaximumManagedFunctionArenaBytes = 64 * 1024;
const FName WorldContextObjectName(TEXT("WorldContextObject"));
const FName KismetWorldContextName(TEXT("__WorldContext"));

bool PackValue(
    const FProperty& Property,
    const void* Container,
    FUnifiedScriptManagedFunctionValueV1& OutValue)
{
    const void* Value = Property.ContainerPtrToValuePtr<void>(Container);
    if (const FBoolProperty* Typed = CastField<FBoolProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptManagedFunctionValueKindV1::Boolean;
        OutValue.Payload = Typed->GetPropertyValue(Value) ? 1 : 0;
        return true;
    }
    if (const FByteProperty* Typed = CastField<FByteProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptManagedFunctionValueKindV1::Byte;
        OutValue.Payload = Typed->GetPropertyValue(Value);
        return true;
    }
    if (const FIntProperty* Typed = CastField<FIntProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptManagedFunctionValueKindV1::Int32;
        OutValue.Payload = static_cast<uint64>(static_cast<int64>(
            Typed->GetPropertyValue(Value)));
        return true;
    }
    if (const FInt64Property* Typed = CastField<FInt64Property>(&Property))
    {
        OutValue.Kind = EUnifiedScriptManagedFunctionValueKindV1::Int64;
        OutValue.Payload = static_cast<uint64>(Typed->GetPropertyValue(Value));
        return true;
    }
    if (const FFloatProperty* Typed = CastField<FFloatProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptManagedFunctionValueKindV1::Single;
        const float Scalar = Typed->GetPropertyValue(Value);
        static_assert(sizeof(Scalar) == sizeof(uint32));
        uint32 Bits = 0;
        FMemory::Memcpy(&Bits, &Scalar, sizeof(Bits));
        OutValue.Payload = Bits;
        return true;
    }
    if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptManagedFunctionValueKindV1::Double;
        const double Scalar = Typed->GetPropertyValue(Value);
        static_assert(sizeof(Scalar) == sizeof(uint64));
        FMemory::Memcpy(&OutValue.Payload, &Scalar, sizeof(OutValue.Payload));
        return true;
    }
    return false;
}

bool UnpackReturnValue(
    const FProperty& Property,
    const FUnifiedScriptManagedFunctionValueV1& Value,
    void* Result)
{
    if (const FBoolProperty* Typed = CastField<FBoolProperty>(&Property);
        Typed != nullptr &&
        Value.Kind == EUnifiedScriptManagedFunctionValueKindV1::Boolean &&
        Value.Payload <= 1)
    {
        Typed->SetPropertyValue(Result, Value.Payload != 0);
        return true;
    }
    if (const FByteProperty* Typed = CastField<FByteProperty>(&Property);
        Typed != nullptr &&
        Value.Kind == EUnifiedScriptManagedFunctionValueKindV1::Byte &&
        Value.Payload <= MAX_uint8)
    {
        Typed->SetPropertyValue(Result, static_cast<uint8>(Value.Payload));
        return true;
    }
    if (const FIntProperty* Typed = CastField<FIntProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptManagedFunctionValueKindV1::Int32)
    {
        Typed->SetPropertyValue(Result, static_cast<int32>(Value.Payload));
        return true;
    }
    if (const FInt64Property* Typed = CastField<FInt64Property>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptManagedFunctionValueKindV1::Int64)
    {
        Typed->SetPropertyValue(Result, static_cast<int64>(Value.Payload));
        return true;
    }
    if (const FFloatProperty* Typed = CastField<FFloatProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptManagedFunctionValueKindV1::Single)
    {
        const uint32 Bits = static_cast<uint32>(Value.Payload);
        float Scalar = 0.0f;
        FMemory::Memcpy(&Scalar, &Bits, sizeof(Scalar));
        Typed->SetPropertyValue(Result, Scalar);
        return true;
    }
    if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptManagedFunctionValueKindV1::Double)
    {
        double Scalar = 0.0;
        FMemory::Memcpy(&Scalar, &Value.Payload, sizeof(Scalar));
        Typed->SetPropertyValue(Result, Scalar);
        return true;
    }
    return false;
}

UUnifiedScriptComponent* ResolveComponent(UObject* Context)
{
    if (UUnifiedScriptComponent* Component = Cast<UUnifiedScriptComponent>(Context))
    {
        return Component;
    }
    if (AActor* Actor = Cast<AActor>(Context))
    {
        return Actor->FindComponentByClass<UUnifiedScriptComponent>();
    }
    return nullptr;
}

UUnifiedScriptWorldSubsystem* ResolveWorldHost(UObject* WorldContextObject)
{
    UWorld* World = GEngine != nullptr && WorldContextObject != nullptr
        ? GEngine->GetWorldFromContextObject(
            WorldContextObject,
            EGetWorldErrorMode::ReturnNull)
        : nullptr;
    return World != nullptr
        ? World->GetSubsystem<UUnifiedScriptWorldSubsystem>()
        : nullptr;
}

uint64 DoubleBits(double Value)
{
    uint64 Bits = 0;
    static_assert(sizeof(Bits) == sizeof(Value));
    FMemory::Memcpy(&Bits, &Value, sizeof(Bits));
    return Bits;
}

double BitsDouble(uint64 Bits)
{
    double Value = 0.0;
    static_assert(sizeof(Bits) == sizeof(Value));
    FMemory::Memcpy(&Value, &Bits, sizeof(Value));
    return Value;
}

bool PackValueV2(
    const FProperty& Property,
    const void* Container,
    UUnifiedScriptWorldSubsystem& Host,
    TArray<uint8, TInlineAllocator<512>>& Arena,
    FUnifiedScriptNativeValueV3& OutValue,
    FString& OutError)
{
    const void* Value = Property.ContainerPtrToValuePtr<void>(Container);
    if (const FBoolProperty* Typed = CastField<FBoolProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Boolean;
        OutValue.Payload0 = Typed->GetPropertyValue(Value) ? 1 : 0;
        return true;
    }
    if (const FByteProperty* Typed = CastField<FByteProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::UInt8;
        OutValue.Payload0 = Typed->GetPropertyValue(Value);
        return true;
    }
    if (const FIntProperty* Typed = CastField<FIntProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Int32;
        OutValue.Payload0 = static_cast<uint64>(static_cast<int64>(
            Typed->GetPropertyValue(Value)));
        return true;
    }
    if (const FInt64Property* Typed = CastField<FInt64Property>(&Property))
    {
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Int64;
        OutValue.Payload0 = static_cast<uint64>(Typed->GetPropertyValue(Value));
        return true;
    }
    if (const FFloatProperty* Typed = CastField<FFloatProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Float32;
        const float Scalar = Typed->GetPropertyValue(Value);
        uint32 Bits = 0;
        FMemory::Memcpy(&Bits, &Scalar, sizeof(Bits));
        OutValue.Payload0 = Bits;
        return true;
    }
    if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(&Property))
    {
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Float64;
        OutValue.Payload0 = DoubleBits(Typed->GetPropertyValue(Value));
        return true;
    }
    if (const FStrProperty* Typed = CastField<FStrProperty>(&Property))
    {
        const FString& String = Typed->GetPropertyValue(Value);
        const FTCHARToUTF8 Utf8(*String);
        if (Utf8.Length() > MaximumManagedFunctionArenaBytes - Arena.Num())
        {
            OutError = FString::Printf(
                TEXT("Blueprint string parameter '%s' exceeds the %d-byte managed call arena."),
                *Property.GetName(),
                MaximumManagedFunctionArenaBytes);
            return false;
        }
        const int32 Offset = Arena.Num();
        if (Utf8.Length() > 0)
        {
            const int32 AddedOffset = Arena.AddUninitialized(Utf8.Length());
            FMemory::Memcpy(Arena.GetData() + AddedOffset, Utf8.Get(), Utf8.Length());
        }
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::String;
        OutValue.Flags = EUnifiedScriptNativeValueFlagsV3::ArenaBacked;
        OutValue.Payload0 = static_cast<uint64>(Offset);
        OutValue.Payload1 = static_cast<uint64>(Utf8.Length());
        return true;
    }
    if (const FObjectPropertyBase* Typed = CastField<FObjectPropertyBase>(&Property))
    {
        UObject* Object = Typed->GetObjectPropertyValue(Value);
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Object;
        if (Object == nullptr)
        {
            OutValue.Flags = EUnifiedScriptNativeValueFlagsV3::Null;
            return true;
        }
        FUnifiedScriptObjectHandleResult HandleResult;
        const FUnifiedScriptObjectHandle Handle = Host.RegisterObject(Object, HandleResult);
        if (!HandleResult.IsSuccess())
        {
            OutError = HandleResult.Message;
            return false;
        }
        OutValue.Payload0 = Handle.Value;
        return true;
    }
    if (const FStructProperty* Typed = CastField<FStructProperty>(&Property);
        Typed != nullptr && Typed->Struct == TBaseStructure<FVector>::Get())
    {
        const FVector& Vector = *static_cast<const FVector*>(Value);
        OutValue.Kind = EUnifiedScriptNativeValueKindV3::Vector3;
        OutValue.Payload0 = DoubleBits(Vector.X);
        OutValue.Payload1 = DoubleBits(Vector.Y);
        OutValue.Payload2 = DoubleBits(Vector.Z);
        return true;
    }
    OutError = FString::Printf(
        TEXT("Blueprint parameter '%s' uses unsupported ABI v2 property '%s'."),
        *Property.GetName(),
        *Property.GetClass()->GetName());
    return false;
}

bool UnpackReturnValueV2(
    const FProperty& Property,
    const FUnifiedScriptNativeValueV3& Value,
    TConstArrayView<uint8> Arena,
    uint32 ByteCount,
    UUnifiedScriptWorldSubsystem& Host,
    void* Result,
    FString& OutError)
{
    if (const FBoolProperty* Typed = CastField<FBoolProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Boolean &&
        Value.Payload0 <= 1)
    {
        Typed->SetPropertyValue(Result, Value.Payload0 != 0);
        return true;
    }
    if (const FByteProperty* Typed = CastField<FByteProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::UInt8 &&
        Value.Payload0 <= MAX_uint8)
    {
        Typed->SetPropertyValue(Result, static_cast<uint8>(Value.Payload0));
        return true;
    }
    if (const FIntProperty* Typed = CastField<FIntProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Int32)
    {
        Typed->SetPropertyValue(Result, static_cast<int32>(Value.Payload0));
        return true;
    }
    if (const FInt64Property* Typed = CastField<FInt64Property>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Int64)
    {
        Typed->SetPropertyValue(Result, static_cast<int64>(Value.Payload0));
        return true;
    }
    if (const FFloatProperty* Typed = CastField<FFloatProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Float32)
    {
        const uint32 Bits = static_cast<uint32>(Value.Payload0);
        float Scalar = 0.0f;
        FMemory::Memcpy(&Scalar, &Bits, sizeof(Scalar));
        Typed->SetPropertyValue(Result, Scalar);
        return true;
    }
    if (const FDoubleProperty* Typed = CastField<FDoubleProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Float64)
    {
        Typed->SetPropertyValue(Result, BitsDouble(Value.Payload0));
        return true;
    }
    if (const FStrProperty* Typed = CastField<FStrProperty>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::String &&
        Value.Flags == EUnifiedScriptNativeValueFlagsV3::ArenaBacked &&
        Value.Payload0 <= ByteCount && Value.Payload1 <= ByteCount - Value.Payload0)
    {
        const int32 Offset = static_cast<int32>(Value.Payload0);
        const int32 Length = static_cast<int32>(Value.Payload1);
        const FUTF8ToTCHAR Converted(
            reinterpret_cast<const ANSICHAR*>(Arena.GetData() + Offset),
            Length);
        Typed->SetPropertyValue(Result, FString(Converted.Length(), Converted.Get()));
        return true;
    }
    if (const FObjectPropertyBase* Typed = CastField<FObjectPropertyBase>(&Property);
        Typed != nullptr && Value.Kind == EUnifiedScriptNativeValueKindV3::Object)
    {
        if (Value.Payload0 == 0)
        {
            Typed->SetObjectPropertyValue(Result, nullptr);
            return true;
        }
        FUnifiedScriptObjectHandleResult HandleResult;
        UObject* Object = Host.ResolveObject({Value.Payload0}, HandleResult);
        if (!HandleResult.IsSuccess() ||
            (Object != nullptr && !Object->IsA(Typed->PropertyClass)))
        {
            OutError = HandleResult.IsSuccess()
                ? FString::Printf(
                    TEXT("Returned object '%s' is not a '%s'."),
                    *GetPathNameSafe(Object),
                    *GetPathNameSafe(Typed->PropertyClass))
                : HandleResult.Message;
            return false;
        }
        Typed->SetObjectPropertyValue(Result, Object);
        return true;
    }
    if (const FStructProperty* Typed = CastField<FStructProperty>(&Property);
        Typed != nullptr && Typed->Struct == TBaseStructure<FVector>::Get() &&
        Value.Kind == EUnifiedScriptNativeValueKindV3::Vector3)
    {
        *static_cast<FVector*>(Result) = FVector(
            BitsDouble(Value.Payload0),
            BitsDouble(Value.Payload1),
            BitsDouble(Value.Payload2));
        return true;
    }
    OutError = FString::Printf(
        TEXT("Managed ABI v2 return kind %u does not match Blueprint property '%s'."),
        static_cast<uint32>(Value.Kind),
        *Property.GetName());
    return false;
}
}

UClass* UUnifiedScriptBlueprint::GetBlueprintClass() const
{
    return UUnifiedScriptBlueprintGeneratedClass::StaticClass();
}

void UUnifiedScriptBlueprintGeneratedClass::PostLoad()
{
    Super::PostLoad();
    BindManagedFunctions();
}

void UUnifiedScriptBlueprintGeneratedClass::BindManagedFunctions()
{
    check(IsInGameThread());
    for (const TPair<FName, FUnifiedScriptManagedFunctionBinding>& Pair :
         ManagedFunctionBindings)
    {
        UFunction* Function = FindFunctionByName(
            Pair.Key,
            EIncludeSuperFlag::ExcludeSuper);
        if (Function == nullptr)
        {
            continue;
        }
        // Keep the Blueprint bytecode marker so ProcessEvent enters the
        // function, but replace its execution pointer after compile/load. A
        // dynamic per-declaration FUNC_Native would make UClass::Bind search a
        // nonexistent handwritten native registration on every recompile.
        Function->FunctionFlags &= ~FUNC_Native;
        Function->FunctionFlags |= FUNC_Public | FUNC_BlueprintCallable;
        Function->SetNativeFunc(
            &UUnifiedScriptBlueprintGeneratedClass::execDispatchManagedFunction);
    }
}

bool UUnifiedScriptBlueprintGeneratedClass::IsManagedFunctionBound(
    FName FunctionName) const
{
    const UFunction* Function = FindFunctionByName(
        FunctionName,
        EIncludeSuperFlag::ExcludeSuper);
    return Function != nullptr &&
        Function->GetNativeFunc() ==
            &UUnifiedScriptBlueprintGeneratedClass::execDispatchManagedFunction;
}

DEFINE_FUNCTION(UUnifiedScriptBlueprintGeneratedClass::execDispatchManagedFunction)
{
    using namespace UE::UnifiedScript::Blueprint::Private;
    UFunction* Function = Stack.Node;
    const UUnifiedScriptBlueprintGeneratedClass* GeneratedClass = Function != nullptr
        ? Cast<UUnifiedScriptBlueprintGeneratedClass>(Function->GetOuter())
        : nullptr;
    const FUnifiedScriptManagedFunctionBinding* Binding = GeneratedClass != nullptr
        ? GeneratedClass->ManagedFunctionBindings.Find(Function->GetFName())
        : nullptr;
    const FProperty* ReturnProperty = Function != nullptr
        ? Function->GetReturnProperty()
        : nullptr;
    if (ReturnProperty != nullptr && RESULT_PARAM != nullptr)
    {
        ReturnProperty->ClearValue(RESULT_PARAM);
    }
    if (Function == nullptr || Binding == nullptr || Binding->ManagedName.IsEmpty())
    {
        UE_LOG(LogTemp, Error, TEXT("UnifiedScript managed function binding is missing."));
        return;
    }

    if (Binding->AbiVersion >= ManagedFunctionAbiV2)
    {
        UUnifiedScriptComponent* Component = nullptr;
        UUnifiedScriptWorldSubsystem* Host = nullptr;
        if (Binding->bStatic)
        {
            const FObjectPropertyBase* WorldContextProperty =
                FindFProperty<FObjectPropertyBase>(Function, WorldContextObjectName);
            UObject* WorldContextObject = WorldContextProperty != nullptr
                ? WorldContextProperty->GetObjectPropertyValue_InContainer(Stack.Locals)
                : nullptr;
            Host = ResolveWorldHost(WorldContextObject);
        }
        else
        {
            Component = ResolveComponent(Context);
            Host = Component != nullptr
                ? ResolveWorldHost(Component)
                : nullptr;
        }
        if (Host == nullptr || (!Binding->bStatic && Component == nullptr))
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("UnifiedScript function '%s' has no managed World host."),
                *Function->GetPathName());
            return;
        }

        TArray<FUnifiedScriptNativeValueV3, TInlineAllocator<4>> ArgumentsV2;
        TArray<uint8, TInlineAllocator<512>> Arena;
        Arena.Reserve(MaximumManagedFunctionArenaBytes);
        FString MarshalError;
        for (TFieldIterator<FProperty> It(Function, EFieldIteratorFlags::ExcludeSuper); It; ++It)
        {
            const FProperty* Parameter = *It;
            if (!Parameter->HasAnyPropertyFlags(CPF_Parm) ||
                Parameter->HasAnyPropertyFlags(CPF_ReturnParm) ||
                (Binding->bStatic &&
                 (Parameter->GetFName() == WorldContextObjectName ||
                  Parameter->GetFName() == KismetWorldContextName)))
            {
                continue;
            }
            FUnifiedScriptNativeValueV3& Value = ArgumentsV2.AddDefaulted_GetRef();
            if (ArgumentsV2.Num() > 4 ||
                !PackValueV2(*Parameter, Stack.Locals, *Host, Arena, Value, MarshalError))
            {
                UE_LOG(
                    LogTemp,
                    Error,
                    TEXT("UnifiedScript cannot marshal Blueprint parameter '%s' for '%s': %s"),
                    *Parameter->GetName(),
                    *Function->GetPathName(),
                    MarshalError.IsEmpty() ? TEXT("unsupported call shape") : *MarshalError);
                return;
            }
        }

        const uint32 InputByteCount = static_cast<uint32>(Arena.Num());
        Arena.SetNumUninitialized(MaximumManagedFunctionArenaBytes, EAllowShrinking::No);
        FUnifiedScriptNativeValueV3 ResultV2;
        FUnifiedScriptNativeCallFrameV3 Frame;
        Frame.Arguments = ArgumentsV2.GetData();
        Frame.ArgumentCount = ArgumentsV2.Num();
        Frame.Results = ReturnProperty != nullptr ? &ResultV2 : nullptr;
        Frame.ResultCapacity = ReturnProperty != nullptr ? 1 : 0;
        Frame.ByteBuffer = Arena.GetData();
        Frame.ByteCapacity = Arena.Num();
        Frame.ByteCount = InputByteCount;

        FString Error;
        bool bInvoked = false;
        if (Binding->bStatic)
        {
            UUnifiedScriptAsset* ScriptAsset = GeneratedClass->ManagedScriptAsset;
            FString AssemblyPath;
            FString TypeName;
            if (ScriptAsset == nullptr ||
                !ScriptAsset->ResolveRuntimeConfiguration(
                    true,
                    AssemblyPath,
                    TypeName,
                    Error))
            {
                UE_LOG(
                    LogTemp,
                    Error,
                    TEXT("UnifiedScript static function '%s' cannot resolve its Script Asset: %s"),
                    *Function->GetPathName(),
                    Error.IsEmpty() ? TEXT("missing generated-class sidecar") : *Error);
                return;
            }
            bInvoked = Host->InvokeManagedScriptFunctionV2(
                0,
                AssemblyPath,
                TypeName,
                Binding->ManagedName,
                Frame,
                Error);
        }
        else
        {
            bInvoked = Component->InvokeManagedScriptFunctionV2(
                Binding->ManagedName,
                Frame,
                Error);
        }
        if (!bInvoked || Frame.Status != EUnifiedScriptNativeCallStatusV3::Success)
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("UnifiedScript function '%s' ABI v2 dispatch failed (status=%u): %s"),
                *Function->GetPathName(),
                static_cast<uint32>(Frame.Status),
                *Error);
            return;
        }
        const uint32 ExpectedResultCount = ReturnProperty != nullptr ? 1 : 0;
        if (Frame.ResultCount != ExpectedResultCount)
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("UnifiedScript function '%s' returned %u result cells; expected %u."),
                *Function->GetPathName(),
                Frame.ResultCount,
                ExpectedResultCount);
            return;
        }
        if (ReturnProperty != nullptr &&
            (RESULT_PARAM == nullptr ||
             !UnpackReturnValueV2(
                 *ReturnProperty,
                 ResultV2,
                 Arena,
                 Frame.ByteCount,
                 *Host,
                 RESULT_PARAM,
                 Error)))
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("UnifiedScript cannot marshal ABI v2 return value for '%s': %s"),
                *Function->GetPathName(),
                *Error);
        }
        return;
    }

    TArray<FUnifiedScriptManagedFunctionValueV1, TInlineAllocator<4>> Arguments;
    for (TFieldIterator<FProperty> It(Function, EFieldIteratorFlags::ExcludeSuper); It; ++It)
    {
        const FProperty* Parameter = *It;
        if (!Parameter->HasAnyPropertyFlags(CPF_Parm) ||
            Parameter->HasAnyPropertyFlags(CPF_ReturnParm))
        {
            continue;
        }
        FUnifiedScriptManagedFunctionValueV1& Value = Arguments.AddDefaulted_GetRef();
        if (Arguments.Num() > 4 || !PackValue(*Parameter, Stack.Locals, Value))
        {
            UE_LOG(
                LogTemp,
                Error,
                TEXT("UnifiedScript cannot marshal Blueprint parameter '%s' for '%s'."),
                *Parameter->GetName(),
                *Function->GetPathName());
            return;
        }
    }

    UUnifiedScriptComponent* Component = ResolveComponent(Context);
    if (Component == nullptr)
    {
        UE_LOG(
            LogTemp,
            Error,
            TEXT("UnifiedScript function '%s' has no managed component host."),
            *Function->GetPathName());
        return;
    }
    FUnifiedScriptManagedFunctionValueV1 Result;
    FString Error;
    if (!Component->InvokeManagedScriptFunction(
            Binding->ManagedName,
            Arguments,
            Result,
            Error))
    {
        UE_LOG(LogTemp, Error, TEXT("UnifiedScript function '%s' failed: %s"),
            *Function->GetPathName(), *Error);
        return;
    }
    if (ReturnProperty == nullptr)
    {
        if (Result.Kind != EUnifiedScriptManagedFunctionValueKindV1::Void)
        {
            UE_LOG(LogTemp, Error, TEXT("UnifiedScript void function '%s' returned kind %u."),
                *Function->GetPathName(), static_cast<uint32>(Result.Kind));
        }
        return;
    }
    if (RESULT_PARAM == nullptr || !UnpackReturnValue(*ReturnProperty, Result, RESULT_PARAM))
    {
        UE_LOG(LogTemp, Error, TEXT("UnifiedScript cannot marshal return value for '%s'."),
            *Function->GetPathName());
    }
}
