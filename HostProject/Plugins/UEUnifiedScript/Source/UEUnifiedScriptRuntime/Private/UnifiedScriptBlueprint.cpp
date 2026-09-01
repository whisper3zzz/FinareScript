#include "UnifiedScriptBlueprint.h"

#include "GameFramework/Actor.h"
#include "UnifiedScriptComponent.h"
#include "UObject/UnrealType.h"

#include UE_INLINE_GENERATED_CPP_BY_NAME(UnifiedScriptBlueprint)

namespace UE::UnifiedScript::Blueprint::Private
{
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
