#include "UnifiedScriptInvocationBridge.h"

#include "UnifiedScriptGeneratedThunks.h"
#include "UObject/Class.h"
#include "UObject/StructOnScope.h"
#include "UObject/UObjectGlobals.h"
#include "UObject/UnrealType.h"

namespace UE::UnifiedScript::Invocation::Private
{
void Succeed(FUnifiedScriptInvocationResult& OutResult)
{
    OutResult.Error = EUnifiedScriptInvocationError::None;
    OutResult.HandleError = EUnifiedScriptObjectHandleError::None;
    OutResult.Message.Reset();
}

void Fail(
    FUnifiedScriptInvocationResult& OutResult,
    EUnifiedScriptInvocationError Error,
    FString Message)
{
    OutResult.Error = Error;
    OutResult.HandleError = EUnifiedScriptObjectHandleError::None;
    OutResult.Message = MoveTemp(Message);
}

UObject* ResolveTarget(
    FUnifiedScriptObjectRegistry& Registry,
    FUnifiedScriptObjectHandle Target,
    FUnifiedScriptInvocationResult& OutResult)
{
    FUnifiedScriptObjectHandleResult HandleResult;
    UObject* Object = Registry.ResolveObject(Target, HandleResult);
    if (Object != nullptr)
    {
        return Object;
    }

    OutResult.Error = EUnifiedScriptInvocationError::ObjectHandleFailure;
    OutResult.HandleError = HandleResult.Error;
    OutResult.Message = MoveTemp(HandleResult.Message);
    return nullptr;
}

bool ResolveMemberIdentity(
    UObject* Target,
    const FString& MemberPath,
    UClass*& OutTargetClass,
    FName& OutMemberName,
    FUnifiedScriptInvocationResult& OutResult)
{
    int32 SeparatorIndex = INDEX_NONE;
    if (!MemberPath.FindLastChar(TEXT(':'), SeparatorIndex) ||
        SeparatorIndex <= 0 ||
        SeparatorIndex == MemberPath.Len() - 1)
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::InvalidMemberPath,
            FString::Printf(TEXT("Invalid UnifiedScript member path '%s'."), *MemberPath));
        return false;
    }

    const FString ClassPath = MemberPath.Left(SeparatorIndex);
    const FString MemberName = MemberPath.Mid(SeparatorIndex + 1);
    UClass* TargetClass = FindObject<UClass>(nullptr, *ClassPath);
    if (TargetClass == nullptr)
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::TargetTypeNotFound,
            FString::Printf(TEXT("UnifiedScript target type '%s' is not loaded."), *ClassPath));
        return false;
    }
    if (!Target->IsA(TargetClass))
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::TargetTypeMismatch,
            FString::Printf(
                TEXT("Object '%s' is not an instance of '%s'."),
                *Target->GetPathName(),
                *ClassPath));
        return false;
    }

    OutTargetClass = TargetClass;
    OutMemberName = FName(*MemberName);
    return true;
}

bool ReadValue(
    const FProperty* Property,
    const void* Container,
    FUnifiedScriptValue& OutValue,
    FUnifiedScriptInvocationResult& OutResult)
{
    if (const FBoolProperty* BoolProperty = CastField<FBoolProperty>(Property))
    {
        OutValue = FUnifiedScriptValue::FromBoolean(
            BoolProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FIntProperty* IntProperty = CastField<FIntProperty>(Property))
    {
        OutValue = FUnifiedScriptValue::FromInt32(
            IntProperty->GetPropertyValue_InContainer(Container));
        return true;
    }
    if (const FStrProperty* StringProperty = CastField<FStrProperty>(Property))
    {
        OutValue = FUnifiedScriptValue::FromString(
            StringProperty->GetPropertyValue_InContainer(Container));
        return true;
    }

    Fail(
        OutResult,
        EUnifiedScriptInvocationError::UnsupportedValueType,
        FString::Printf(
            TEXT("Property '%s' has unsupported native type '%s'."),
            *Property->GetName(),
            *Property->GetCPPType()));
    return false;
}

bool WriteValue(
    FProperty* Property,
    void* Container,
    const FUnifiedScriptValue& Value,
    FUnifiedScriptInvocationResult& OutResult)
{
    if (FBoolProperty* BoolProperty = CastField<FBoolProperty>(Property))
    {
        bool NativeValue = false;
        if (!Value.TryGetBoolean(NativeValue))
        {
            Fail(
                OutResult,
                EUnifiedScriptInvocationError::ValueTypeMismatch,
                FString::Printf(TEXT("Property '%s' requires a boolean value."), *Property->GetName()));
            return false;
        }
        BoolProperty->SetPropertyValue_InContainer(Container, NativeValue);
        return true;
    }
    if (FIntProperty* IntProperty = CastField<FIntProperty>(Property))
    {
        int32 NativeValue = 0;
        if (!Value.TryGetInt32(NativeValue))
        {
            Fail(
                OutResult,
                EUnifiedScriptInvocationError::ValueTypeMismatch,
                FString::Printf(TEXT("Property '%s' requires an int32 value."), *Property->GetName()));
            return false;
        }
        IntProperty->SetPropertyValue_InContainer(Container, NativeValue);
        return true;
    }
    if (FStrProperty* StringProperty = CastField<FStrProperty>(Property))
    {
        FString NativeValue;
        if (!Value.TryGetString(NativeValue))
        {
            Fail(
                OutResult,
                EUnifiedScriptInvocationError::ValueTypeMismatch,
                FString::Printf(TEXT("Property '%s' requires a string value."), *Property->GetName()));
            return false;
        }
        StringProperty->SetPropertyValue_InContainer(Container, NativeValue);
        return true;
    }

    Fail(
        OutResult,
        EUnifiedScriptInvocationError::UnsupportedValueType,
        FString::Printf(
            TEXT("Property '%s' has unsupported native type '%s'."),
            *Property->GetName(),
            *Property->GetCPPType()));
    return false;
}
}

FUnifiedScriptValue FUnifiedScriptValue::FromBoolean(bool Value)
{
    FUnifiedScriptValue Result;
    Result.Kind = EUnifiedScriptValueKind::Boolean;
    Result.BooleanValue = Value;
    return Result;
}

FUnifiedScriptValue FUnifiedScriptValue::FromInt32(int32 Value)
{
    FUnifiedScriptValue Result;
    Result.Kind = EUnifiedScriptValueKind::Int32;
    Result.Int32Value = Value;
    return Result;
}

FUnifiedScriptValue FUnifiedScriptValue::FromString(FString Value)
{
    FUnifiedScriptValue Result;
    Result.Kind = EUnifiedScriptValueKind::String;
    Result.StringValue = MoveTemp(Value);
    return Result;
}

bool FUnifiedScriptValue::TryGetBoolean(bool& OutValue) const
{
    if (Kind != EUnifiedScriptValueKind::Boolean)
    {
        return false;
    }
    OutValue = BooleanValue;
    return true;
}

bool FUnifiedScriptValue::TryGetInt32(int32& OutValue) const
{
    if (Kind != EUnifiedScriptValueKind::Int32)
    {
        return false;
    }
    OutValue = Int32Value;
    return true;
}

bool FUnifiedScriptValue::TryGetString(FString& OutValue) const
{
    if (Kind != EUnifiedScriptValueKind::String)
    {
        return false;
    }
    OutValue = StringValue;
    return true;
}

FUnifiedScriptInvocationBridge::FUnifiedScriptInvocationBridge(
    FUnifiedScriptObjectRegistry& InRegistry)
    : Registry(InRegistry)
{
}

bool FUnifiedScriptInvocationBridge::ReadProperty(
    FUnifiedScriptObjectHandle Target,
    const FString& PropertyPath,
    FUnifiedScriptValue& OutValue,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;

    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr)
    {
        return false;
    }

    UClass* TargetClass = nullptr;
    FName PropertyName;
    if (!ResolveMemberIdentity(Object, PropertyPath, TargetClass, PropertyName, OutResult))
    {
        return false;
    }

    FProperty* Property = FindFProperty<FProperty>(TargetClass, PropertyName);
    if (Property == nullptr)
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::PropertyNotFound,
            FString::Printf(TEXT("Property '%s' was not found."), *PropertyPath));
        return false;
    }
    if (!Property->HasAnyPropertyFlags(CPF_BlueprintVisible))
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::PropertyNotReadable,
            FString::Printf(TEXT("Property '%s' is not Blueprint-readable."), *PropertyPath));
        return false;
    }
    if (!ReadValue(Property, Object, OutValue, OutResult))
    {
        return false;
    }

    Succeed(OutResult);
    return true;
}

bool FUnifiedScriptInvocationBridge::WriteProperty(
    FUnifiedScriptObjectHandle Target,
    const FString& PropertyPath,
    const FUnifiedScriptValue& Value,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;

    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr)
    {
        return false;
    }

    UClass* TargetClass = nullptr;
    FName PropertyName;
    if (!ResolveMemberIdentity(Object, PropertyPath, TargetClass, PropertyName, OutResult))
    {
        return false;
    }

    FProperty* Property = FindFProperty<FProperty>(TargetClass, PropertyName);
    if (Property == nullptr)
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::PropertyNotFound,
            FString::Printf(TEXT("Property '%s' was not found."), *PropertyPath));
        return false;
    }
    if (!Property->HasAnyPropertyFlags(CPF_BlueprintVisible) ||
        Property->HasAnyPropertyFlags(CPF_BlueprintReadOnly))
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::PropertyNotWritable,
            FString::Printf(TEXT("Property '%s' is not Blueprint-writable."), *PropertyPath));
        return false;
    }
    if (!WriteValue(Property, Object, Value, OutResult))
    {
        return false;
    }

    Succeed(OutResult);
    return true;
}

bool FUnifiedScriptInvocationBridge::Invoke(
    FUnifiedScriptObjectHandle Target,
    const FString& FunctionPath,
    TConstArrayView<FUnifiedScriptValue> Arguments,
    FUnifiedScriptValue& OutReturnValue,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;

    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr)
    {
        return false;
    }

    UClass* TargetClass = nullptr;
    FName FunctionName;
    if (!ResolveMemberIdentity(Object, FunctionPath, TargetClass, FunctionName, OutResult))
    {
        return false;
    }

    UFunction* Function = TargetClass->FindFunctionByName(FunctionName);
    if (Function == nullptr)
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::FunctionNotFound,
            FString::Printf(TEXT("Function '%s' was not found."), *FunctionPath));
        return false;
    }
    if (Function->HasAnyFunctionFlags(FUNC_Static) ||
        !Function->HasAnyFunctionFlags(FUNC_BlueprintCallable | FUNC_BlueprintPure))
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::FunctionNotCallable,
            FString::Printf(TEXT("Function '%s' is not an instance Blueprint call."), *FunctionPath));
        return false;
    }

    TArray<FProperty*, TInlineAllocator<8>> InputProperties;
    FProperty* ReturnProperty = nullptr;
    for (TFieldIterator<FProperty> Iterator(Function); Iterator; ++Iterator)
    {
        FProperty* Parameter = *Iterator;
        if (!Parameter->HasAnyPropertyFlags(CPF_Parm))
        {
            continue;
        }
        if (Parameter->HasAnyPropertyFlags(CPF_ReturnParm))
        {
            ReturnProperty = Parameter;
            continue;
        }
        if (Parameter->HasAnyPropertyFlags(CPF_OutParm | CPF_ReferenceParm))
        {
            Fail(
                OutResult,
                EUnifiedScriptInvocationError::UnsupportedParameter,
                FString::Printf(
                    TEXT("Function '%s' contains unsupported output or reference parameter '%s'."),
                    *FunctionPath,
                    *Parameter->GetName()));
            return false;
        }
        InputProperties.Add(Parameter);
    }

    if (InputProperties.Num() != Arguments.Num())
    {
        Fail(
            OutResult,
            EUnifiedScriptInvocationError::ArgumentCountMismatch,
            FString::Printf(
                TEXT("Function '%s' requires %d arguments but received %d."),
                *FunctionPath,
                InputProperties.Num(),
                Arguments.Num()));
        return false;
    }

    FStructOnScope ParameterStorage(Function);
    void* ParameterMemory = ParameterStorage.GetStructMemory();
    for (int32 Index = 0; Index < InputProperties.Num(); ++Index)
    {
        if (!WriteValue(InputProperties[Index], ParameterMemory, Arguments[Index], OutResult))
        {
            return false;
        }
    }

    Object->ProcessEvent(Function, ParameterMemory);
    if (ReturnProperty != nullptr)
    {
        if (!ReadValue(ReturnProperty, ParameterMemory, OutReturnValue, OutResult))
        {
            return false;
        }
    }
    else
    {
        OutReturnValue = {};
    }

    Succeed(OutResult);
    return true;
}

bool FUnifiedScriptInvocationBridge::ReadPropertyById(
    FUnifiedScriptObjectHandle Target,
    uint64 MemberIdValue,
    FUnifiedScriptValue& OutValue,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;
    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr ||
        !UE::UnifiedScript::Generated::ReadProperty(
            Object,
            MemberIdValue,
            OutValue,
            OutResult))
    {
        return false;
    }
    FastInvocationCount.fetch_add(1, std::memory_order_relaxed);
    return true;
}

bool FUnifiedScriptInvocationBridge::WritePropertyById(
    FUnifiedScriptObjectHandle Target,
    uint64 MemberIdValue,
    const FUnifiedScriptValue& Value,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;
    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr ||
        !UE::UnifiedScript::Generated::WriteProperty(
            Object,
            MemberIdValue,
            Value,
            OutResult))
    {
        return false;
    }
    FastInvocationCount.fetch_add(1, std::memory_order_relaxed);
    return true;
}

bool FUnifiedScriptInvocationBridge::InvokeById(
    FUnifiedScriptObjectHandle Target,
    uint64 MemberIdValue,
    TConstArrayView<FUnifiedScriptValue> Arguments,
    FUnifiedScriptValue& OutReturnValue,
    FUnifiedScriptInvocationResult& OutResult)
{
    using namespace UE::UnifiedScript::Invocation::Private;
    UObject* Object = ResolveTarget(Registry, Target, OutResult);
    if (Object == nullptr ||
        !UE::UnifiedScript::Generated::Invoke(
            Object,
            MemberIdValue,
            Arguments,
            OutReturnValue,
            OutResult))
    {
        return false;
    }
    FastInvocationCount.fetch_add(1, std::memory_order_relaxed);
    return true;
}

uint64 FUnifiedScriptInvocationBridge::GetFastInvocationCount() const
{
    return FastInvocationCount.load(std::memory_order_relaxed);
}
