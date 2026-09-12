#include "FinareInvocationBridge.h"
#include "FinareSoftObjectPath.h"

#include "Containers/StringConv.h"
#include "FinareGeneratedThunkRegistry.h"
#include "FinareNativeTransport.h"
#include "FinarePropertyValueCodec.h"
#include "FinareBlueprint.h"
#include "FinareBlueprintPrivate.h"
#include "FinareComponent.h"
#include "FinareAsset.h"
#include "FinareWorldSubsystem.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "Components/ActorComponent.h"
#include "Net/Core/PushModel/PushModel.h"
#include "UObject/Class.h"
#include "UObject/EnumProperty.h"
#include "UObject/StrProperty.h"
#include "UObject/SoftObjectPtr.h"
#include "UObject/StructOnScope.h"
#include "UObject/TextProperty.h"
#include "UObject/UObjectGlobals.h"
#include "UObject/UObjectIterator.h"
#include "UObject/UnrealType.h"
#include "UObject/Script.h"

namespace UE::Finare::Invocation::Private
{
	AActor* NetworkOwner(UObject* Object)
	{
		if (AActor* Actor = Cast<AActor>(Object))
		{
			return Actor;
		}
		if (UActorComponent* Component = Cast<UActorComponent>(Object))
		{
			return Component->GetOwner();
		}
		return nullptr;
	}

	void Succeed(FFinareInvocationResult& OutResult)
	{
		OutResult.Error = EFinareInvocationError::None;
		OutResult.HandleError = EFinareObjectHandleError::None;
		OutResult.Message.Reset();
	}

	void Fail(
		FFinareInvocationResult& OutResult,
		EFinareInvocationError Error,
		FString Message)
	{
		OutResult.Error = Error;
		OutResult.HandleError = EFinareObjectHandleError::None;
		OutResult.Message = MoveTemp(Message);
	}

	UObject* ResolveTarget(
		FFinareObjectRegistry& Registry,
		FFinareObjectHandle Target,
		FFinareInvocationResult& OutResult)
	{
		FFinareObjectHandleResult HandleResult;
		UObject* Object = Registry.ResolveObject(Target, HandleResult);
		if (Object != nullptr)
		{
			return Object;
		}

		OutResult.Error = EFinareInvocationError::ObjectHandleFailure;
		OutResult.HandleError = HandleResult.Error;
		OutResult.Message = MoveTemp(HandleResult.Message);
		return nullptr;
	}

	bool ResolveMemberIdentity(
		UObject* Target,
		const FString& MemberPath,
		UClass*& OutTargetClass,
		FName& OutMemberName,
		FFinareInvocationResult& OutResult,
		bool bAllowStaticFunctionContext = false)
	{
		int32 SeparatorIndex = INDEX_NONE;
		if (!MemberPath.FindLastChar(TEXT(':'), SeparatorIndex) ||
			SeparatorIndex <= 0 ||
			SeparatorIndex == MemberPath.Len() - 1)
		{
			Fail(
				OutResult,
				EFinareInvocationError::InvalidMemberPath,
				FString::Printf(TEXT("Invalid Finare member path '%s'."), *MemberPath));
			return false;
		}

		const FString ClassPath = MemberPath.Left(SeparatorIndex);
		const FString MemberName = MemberPath.Mid(SeparatorIndex + 1);
		UClass* TargetClass = nullptr;
		constexpr TCHAR StablePrefix[] = TEXT("finare-class/");
		if (ClassPath.StartsWith(StablePrefix, ESearchCase::CaseSensitive))
		{
			const FString ClassIdText = ClassPath.Mid(UE_ARRAY_COUNT(StablePrefix) - 1);
			FGuid RequestedClassId;
			const FString RequestedDigits = ClassIdText.Mid(3);
			if (ClassIdText.Len() != 35 ||
				!ClassIdText.StartsWith(TEXT("us_"), ESearchCase::CaseSensitive) ||
				!RequestedDigits.Equals(RequestedDigits.ToLower(), ESearchCase::CaseSensitive) ||
				!FGuid::ParseExact(RequestedDigits, EGuidFormats::Digits, RequestedClassId) ||
				!RequestedClassId.IsValid())
			{
				Fail(
					OutResult,
					EFinareInvocationError::InvalidMemberPath,
					FString::Printf(
						TEXT("Stable Finare class locator '%s' has an invalid non-zero class identity."),
						*ClassPath));
				return false;
			}

			int32 MatchingClassCount = 0;
			for (UClass* Candidate = Target->GetClass(); Candidate != nullptr; Candidate = Candidate->GetSuperClass())
			{
				const UFinareBlueprintGeneratedClass* Generated =
					Cast<UFinareBlueprintGeneratedClass>(Candidate);
				if (Generated != nullptr && Generated->ManagedScriptAsset != nullptr &&
					Generated->ManagedScriptAsset->ScriptClassId == RequestedClassId)
				{
					TargetClass = Candidate;
					++MatchingClassCount;
				}
			}
			if (MatchingClassCount > 1)
			{
				Fail(
					OutResult,
					EFinareInvocationError::TargetTypeMismatch,
					FString::Printf(
						TEXT("Stable Finare class locator '%s' is ambiguous in the target class hierarchy."),
						*ClassPath));
				return false;
			}
			if (TargetClass == nullptr)
			{
				Fail(
					OutResult,
					EFinareInvocationError::TargetTypeNotFound,
					FString::Printf(
						TEXT("Stable Finare class locator '%s' is not owned by the target class hierarchy."),
						*ClassPath));
				return false;
			}
		}
		else
		{
			TargetClass = FindObject<UClass>(nullptr, *ClassPath);
		}
		if (TargetClass == nullptr)
		{
			Fail(
				OutResult,
				EFinareInvocationError::TargetTypeNotFound,
				FString::Printf(TEXT("Finare target type '%s' is not loaded."), *ClassPath));
			return false;
		}
		if (TargetClass->HasAnyClassFlags(CLASS_Interface))
		{
			const FName InterfaceMemberName(*MemberName);
			if (TargetClass->FindFunctionByName(InterfaceMemberName) == nullptr)
			{
				Fail(
					OutResult,
					EFinareInvocationError::InvalidMemberPath,
					FString::Printf(
						TEXT("Member '%s' is not declared by interface '%s'."),
						*MemberName,
						*ClassPath));
				return false;
			}
			if (!Target->GetClass()->ImplementsInterface(TargetClass))
			{
				Fail(
					OutResult,
					EFinareInvocationError::TargetTypeMismatch,
					FString::Printf(
						TEXT("Object '%s' does not implement interface '%s'."),
						*Target->GetPathName(),
						*ClassPath));
				return false;
			}

			// Interface UFunctions are declarations. Reflection fallback must resolve
			// the concrete object's UFunction before ProcessEvent so native and
			// Blueprint implementations receive the correct owner and exec frame.
			OutTargetClass = Target->GetClass();
			OutMemberName = InterfaceMemberName;
			return true;
		}
		// A static Blueprint function is invoked with a valid world-bound context
		// object, which need not be an instance of the declaring function-library
		// class. This exception is intentionally opt-in for Invoke only: this
		// resolver is also used by property access and event binding, where a
		// same-named static function must never weaken the exact owner check.
		const UFunction* RequestedFunction = TargetClass->FindFunctionByName(FName(*MemberName));
		const bool bStaticFunction = bAllowStaticFunctionContext &&
			RequestedFunction != nullptr &&
			RequestedFunction->HasAnyFunctionFlags(FUNC_Static);
		if (!bStaticFunction && !Target->IsA(TargetClass))
		{
			Fail(
				OutResult,
				EFinareInvocationError::TargetTypeMismatch,
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

	FString ReflectedFunctionPath(const UFunction* Function)
	{
		return Function->GetOuterUClass()->GetPathName() + TEXT(":") + Function->GetName();
	}

	UFunction* FindReflectedFunction(UObject* Object, uint64 Id)
	{
		UFunction* Match = nullptr;
		for (TFieldIterator<UFunction> It(Object->GetClass()); It; ++It)
		{
			FTCHARToUTF8 Path(*ReflectedFunctionPath(*It));
			if (FinareMemberId(Path.Get()) != Id) continue;
			if (Match != nullptr && Match != *It) return nullptr;
			Match = *It;
		}
		// Function-library wrappers use the caller's World handle. Their static
		// UFunctions belong to a different class, so they are not in that handle's
		// hierarchy. Resolve loaded static declarations by the same canonical ID.
		if (!Match)
		{
			for (TObjectIterator<UFunction> It; It; ++It)
			{
				if (!It->HasAnyFunctionFlags(FUNC_Static) || !It->GetOuterUClass()) continue;
				FTCHARToUTF8 Path(*ReflectedFunctionPath(*It));
				if (FinareMemberId(Path.Get()) != Id) continue;
				if (Match && Match != *It) return nullptr;
				Match = *It;
			}
		}
		return Match;
	}

	bool IsOutputParameter(const FProperty* Property)
	{
		return Property->HasAnyPropertyFlags(CPF_OutParm | CPF_ReferenceParm) &&
			!Property->HasAnyPropertyFlags(CPF_ConstParm);
	}

	bool SupportsReflectedField(const FProperty* Property, int32 Depth = 0, bool bAllowCustomStruct = true)
	{
		if (Depth > 16 || Property->ArrayDim != 1 || Property->HasAnyPropertyFlags(
			CPF_NativeAccessSpecifierPrivate | CPF_NativeAccessSpecifierProtected | CPF_Deprecated | CPF_EditorOnly)) return false;
		if (const auto* Struct = CastField<FStructProperty>(Property))
		{
			if (Struct->Struct == TBaseStructure<FVector>::Get() || Struct->Struct == TBaseStructure<FRotator>::Get() ||
				Struct->Struct == TBaseStructure<FTransform>::Get()) return true;
			if (!bAllowCustomStruct) return false;
			for (TFieldIterator<FProperty> It(Struct->Struct, EFieldIteratorFlags::ExcludeSuper); It; ++It)
				if (SupportsReflectedField(*It, Depth + 1)) return true;
			return false;
		}
		if (const auto* Array = CastField<FArrayProperty>(Property))
			return bAllowCustomStruct && SupportsReflectedField(Array->Inner, Depth + 1, false);
		if (const auto* Set = CastField<FSetProperty>(Property))
			return bAllowCustomStruct && SupportsReflectedField(Set->ElementProp, Depth + 1, false);
		if (const auto* Map = CastField<FMapProperty>(Property))
			return bAllowCustomStruct && SupportsReflectedField(Map->KeyProp, Depth + 1, false) &&
				SupportsReflectedField(Map->ValueProp, Depth + 1, false);
		return CastField<FNumericProperty>(Property) || CastField<FBoolProperty>(Property) ||
			CastField<FEnumProperty>(Property) || CastField<FStrProperty>(Property) || CastField<FNameProperty>(Property) ||
			CastField<FTextProperty>(Property) || CastField<FObjectPropertyBase>(Property) ||
			CastField<FSoftObjectProperty>(Property);
	}

	FProperty* FindReflectedProperty(UObject* Object, uint64 Id)
	{
		FProperty* Match = nullptr;
		for (TFieldIterator<FProperty> It(Object->GetClass()); It; ++It)
		{
			const FString Path = It->GetOwnerStruct()->GetPathName() + TEXT(":") + It->GetName();
			if (FinareMemberId(TCHAR_TO_UTF8(*Path)) != Id) continue;
			if (Match && Match != *It) return nullptr;
			Match = *It;
		}
		return Match;
	}

	TArray<FProperty*> ReflectedStructFields(const UScriptStruct* Struct)
	{
		TArray<FProperty*> Fields;
		for (TFieldIterator<FProperty> It(Struct, EFieldIteratorFlags::ExcludeSuper); It; ++It)
			if (!It->HasAnyPropertyFlags(CPF_Parm) && SupportsReflectedField(*It)) Fields.Add(*It);
		Fields.Sort([](const FProperty& A, const FProperty& B) { return A.GetName() < B.GetName(); });
		return Fields;
	}

	void NormalizeFrameValue(FFinareValue& Value)
	{
		if (Value.Kind == EFinareValueKind::SoftObject || Value.Kind == EFinareValueKind::SoftClass)
			Value.Kind = EFinareValueKind::String;
		for (auto& Child : Value.ContainerValues) NormalizeFrameValue(Child);
	}

	bool ReadValue(
		FFinareObjectRegistry& Registry,
		const FProperty* Property,
		const void* Container,
		FFinareValue& OutValue,
		FFinareInvocationResult& OutResult,
		bool bDirectValue = false, int32 Depth = 0)
	{
		if (Depth > 16 || Property->ArrayDim != 1)
		{
			Fail(OutResult, EFinareInvocationError::UnsupportedValueType, TEXT("Reflected value exceeds the supported shape depth or has a fixed array dimension."));
			return false;
		}
		const void* Value = bDirectValue
			                    ? Container
			                    : Property->ContainerPtrToValuePtr<void>(Container);
		const FArrayProperty* ArrayProperty = CastField<FArrayProperty>(Property);
		const FSetProperty* SetProperty = CastField<FSetProperty>(Property);
		const FMapProperty* MapProperty = CastField<FMapProperty>(Property);
		if (ArrayProperty || SetProperty || MapProperty)
		{
			const FString NativeName =
				UE::Finare::PropertyValueCodec::ContainerValueNativeName(*Property);
			const uint64 TypeId =
				UE::Finare::PropertyValueCodec::TypeIdFromPath(NativeName);
			if (TypeId == 0)
			{
				Fail(
					OutResult,
					EFinareInvocationError::UnsupportedValueType,
					FString::Printf(
						TEXT("Property '%s' has an unsupported reflected collection shape."),
						*Property->GetName()));
				return false;
			}

			TArray<FFinareValue> Values;
			const auto ReadChild = [&](const FProperty& ChildProperty, const void* ChildValue)
			{
				FFinareValue Child;
				if (!ReadValue(Registry, &ChildProperty, ChildValue, Child, OutResult, true, Depth + 1))
				{
					return false;
				}
				// V3's string cell is the published representation for soft
				// references inside an already-validated flat collection. Keep
				// scalar property reads on the strict typed soft kinds.
				if (CastField<FSoftClassProperty>(&ChildProperty) != nullptr &&
					Child.Kind == EFinareValueKind::SoftClass)
				{
					Child.Kind = EFinareValueKind::String;
				}
				else if (CastField<FSoftObjectProperty>(&ChildProperty) != nullptr &&
					Child.Kind == EFinareValueKind::SoftObject)
				{
					Child.Kind = EFinareValueKind::String;
				}
				Values.Add(MoveTemp(Child));
				return true;
			};

			if (ArrayProperty)
			{
				FScriptArrayHelper Array(ArrayProperty, const_cast<void*>(Value));
				Values.Reserve(Array.Num());
				for (int32 Index = 0; Index < Array.Num(); ++Index)
				{
					if (!ReadChild(*ArrayProperty->Inner, Array.GetRawPtr(Index)))
					{
						return false;
					}
				}
				OutValue = FFinareValue::FromArray(MoveTemp(Values), TypeId);
				return true;
			}
			if (SetProperty)
			{
				FScriptSetHelper Set(SetProperty, const_cast<void*>(Value));
				Values.Reserve(Set.Num());
				for (int32 Index = 0; Index < Set.GetMaxIndex(); ++Index)
				{
					if (Set.IsValidIndex(Index) &&
						!ReadChild(*SetProperty->ElementProp, Set.GetElementPtr(Index)))
					{
						return false;
					}
				}
				OutValue = FFinareValue::FromSet(MoveTemp(Values), TypeId);
				return true;
			}

			FScriptMapHelper Map(MapProperty, const_cast<void*>(Value));
			Values.Reserve(Map.Num() * 2);
			for (int32 Index = 0; Index < Map.GetMaxIndex(); ++Index)
			{
				if (!Map.IsValidIndex(Index))
				{
					continue;
				}
				if (!ReadChild(*MapProperty->KeyProp, Map.GetKeyPtr(Index)) ||
					!ReadChild(*MapProperty->ValueProp, Map.GetValuePtr(Index)))
				{
					return false;
				}
			}
			OutValue = FFinareValue::FromMap(MoveTemp(Values), TypeId);
			return true;
		}
		if (const FBoolProperty* BoolProperty = CastField<FBoolProperty>(Property))
		{
			OutValue = FFinareValue::FromBoolean(
				BoolProperty->GetPropertyValue(Value));
			return true;
		}
		if (const FIntProperty* IntProperty = CastField<FIntProperty>(Property))
		{
			OutValue = FFinareValue::FromInt32(
				IntProperty->GetPropertyValue(Value));
			return true;
		}
		if (const FInt8Property* TypedProperty = CastField<FInt8Property>(Property))
		{
			OutValue = FFinareValue::FromInt8(
				TypedProperty->GetPropertyValue(Value));
			return true;
		}
		if (const FByteProperty* TypedProperty = CastField<FByteProperty>(Property))
		{
			const uint8 NativeValue = TypedProperty->GetPropertyValue(Value);
			OutValue = TypedProperty->Enum != nullptr
				           ? FFinareValue::FromEnum(NativeValue, FName(*TypedProperty->Enum->GetPathName()))
				           : FFinareValue::FromUInt8(NativeValue);
			return true;
		}
		if (const FInt16Property* TypedProperty = CastField<FInt16Property>(Property))
		{
			OutValue = FFinareValue::FromInt16(
				TypedProperty->GetPropertyValue(Value));
			return true;
		}
		if (const FUInt16Property* TypedProperty = CastField<FUInt16Property>(Property))
		{
			OutValue = FFinareValue::FromUInt16(
				TypedProperty->GetPropertyValue(Value));
			return true;
		}
		if (const FUInt32Property* TypedProperty = CastField<FUInt32Property>(Property))
		{
			OutValue = FFinareValue::FromUInt32(
				TypedProperty->GetPropertyValue(Value));
			return true;
		}
		if (const FInt64Property* TypedProperty = CastField<FInt64Property>(Property))
		{
			OutValue = FFinareValue::FromInt64(
				TypedProperty->GetPropertyValue(Value));
			return true;
		}
		if (const FUInt64Property* TypedProperty = CastField<FUInt64Property>(Property))
		{
			OutValue = FFinareValue::FromUInt64(
				TypedProperty->GetPropertyValue(Value));
			return true;
		}
		if (const FFloatProperty* TypedProperty = CastField<FFloatProperty>(Property))
		{
			OutValue = FFinareValue::FromFloat32(
				TypedProperty->GetPropertyValue(Value));
			return true;
		}
		if (const FDoubleProperty* TypedProperty = CastField<FDoubleProperty>(Property))
		{
			OutValue = FFinareValue::FromFloat64(
				TypedProperty->GetPropertyValue(Value));
			return true;
		}
		if (const FStrProperty* StringProperty = CastField<FStrProperty>(Property))
		{
			OutValue = FFinareValue::FromString(
				StringProperty->GetPropertyValue(Value));
			return true;
		}
		if (const FNameProperty* TypedProperty = CastField<FNameProperty>(Property))
		{
			OutValue = FFinareValue::FromName(
				TypedProperty->GetPropertyValue(Value));
			return true;
		}
		if (const FTextProperty* TypedProperty = CastField<FTextProperty>(Property))
		{
			OutValue = FFinareValue::FromText(
				TypedProperty->GetPropertyValue(Value));
			return true;
		}
		if (const FSoftClassProperty* TypedProperty = CastField<FSoftClassProperty>(Property))
		{
			OutValue = FFinareValue::FromSoftClassPath(
				TypedProperty->GetPropertyValue(Value).ToSoftObjectPath().ToString());
			return true;
		}
		if (const FSoftObjectProperty* TypedProperty = CastField<FSoftObjectProperty>(Property))
		{
			OutValue = FFinareValue::FromSoftObjectPath(
				TypedProperty->GetPropertyValue(Value).ToSoftObjectPath().ToString());
			return true;
		}
		if (const FEnumProperty* TypedProperty = CastField<FEnumProperty>(Property))
		{
			const void* ValueAddress = Value;
			OutValue = FFinareValue::FromEnum(
				TypedProperty->GetUnderlyingProperty()->GetSignedIntPropertyValue(ValueAddress),
				FName(*TypedProperty->GetEnum()->GetPathName()));
			return true;
		}
		if (const FStructProperty* TypedProperty = CastField<FStructProperty>(Property))
		{
			if (TypedProperty->Struct == TBaseStructure<FVector>::Get())
			{
				OutValue = FFinareValue::FromVector(
					*static_cast<const FVector*>(Value));
				return true;
			}
			if (TypedProperty->Struct == TBaseStructure<FRotator>::Get())
			{
				OutValue = FFinareValue::FromRotator(
					*static_cast<const FRotator*>(Value));
				return true;
			}
			if (TypedProperty->Struct == TBaseStructure<FTransform>::Get())
			{
				OutValue = FFinareValue::FromTransform(
					*static_cast<const FTransform*>(Value));
				return true;
			}
			const auto Fields = ReflectedStructFields(TypedProperty->Struct);
			if (Fields.IsEmpty())
			{
				Fail(OutResult, EFinareInvocationError::UnsupportedValueType, TEXT("Reflected struct has no supported public value fields."));
				return false;
			}
			TArray<FFinareValue> Values;
			for (const FProperty* Field : Fields)
			{
				FFinareValue Child;
				if (!ReadValue(Registry, Field, Value, Child, OutResult, false, Depth + 1)) return false;
				NormalizeFrameValue(Child);
				Values.Add(MoveTemp(Child));
			}
			OutValue = FFinareValue::FromStruct(MoveTemp(Values),
				UE::Finare::PropertyValueCodec::TypeIdFromPath(TypedProperty->Struct->GetPathName()));
			return true;
		}
		if (const FClassProperty* TypedProperty = CastField<FClassProperty>(Property))
		{
			UClass* Object = Cast<UClass>(TypedProperty->GetObjectPropertyValue(Value));
			if (Object == nullptr)
			{
				OutValue = FFinareValue::FromObjectHandle({}, true);
				return true;
			}
			FFinareObjectHandleResult HandleResult;
			const FFinareObjectHandle Handle = Registry.RegisterObject(Object, HandleResult);
			if (!HandleResult.IsSuccess())
			{
				OutResult.Error = EFinareInvocationError::ObjectHandleFailure;
				OutResult.HandleError = HandleResult.Error;
				OutResult.Message = MoveTemp(HandleResult.Message);
				return false;
			}
			OutValue = FFinareValue::FromObjectHandle(Handle, true);
			return true;
		}
		if (const FInterfaceProperty* TypedProperty = CastField<FInterfaceProperty>(Property))
		{
			const FScriptInterface* InterfaceValue =
				static_cast<const FScriptInterface*>(Value);
			UObject* Object = InterfaceValue == nullptr ? nullptr : InterfaceValue->GetObject();
			if (Object == nullptr)
			{
				OutValue = FFinareValue::FromObjectHandle({});
				return true;
			}
			FFinareObjectHandleResult HandleResult;
			const FFinareObjectHandle Handle = Registry.RegisterObject(Object, HandleResult);
			if (!HandleResult.IsSuccess())
			{
				OutResult.Error = EFinareInvocationError::ObjectHandleFailure;
				OutResult.HandleError = HandleResult.Error;
				OutResult.Message = MoveTemp(HandleResult.Message);
				return false;
			}
			OutValue = FFinareValue::FromObjectHandle(Handle);
			return true;
		}
		if (const FObjectPropertyBase* TypedProperty = CastField<FObjectPropertyBase>(Property))
		{
			UObject* Object = TypedProperty->GetObjectPropertyValue(Value);
			if (Object == nullptr)
			{
				OutValue = FFinareValue::FromObjectHandle({});
				return true;
			}
			FFinareObjectHandleResult HandleResult;
			const FFinareObjectHandle Handle = Registry.RegisterObject(Object, HandleResult);
			if (!HandleResult.IsSuccess())
			{
				OutResult.Error = EFinareInvocationError::ObjectHandleFailure;
				OutResult.HandleError = HandleResult.Error;
				OutResult.Message = MoveTemp(HandleResult.Message);
				return false;
			}
			OutValue = FFinareValue::FromObjectHandle(Handle);
			return true;
		}

		Fail(
			OutResult,
			EFinareInvocationError::UnsupportedValueType,
			FString::Printf(
				TEXT("Property '%s' has unsupported native type '%s'."),
				*Property->GetName(),
				*Property->GetCPPType()));
		return false;
	}

	bool WriteValue(
		FFinareObjectRegistry& Registry,
		FProperty* Property,
		void* Container,
		const FFinareValue& Value,
		FFinareInvocationResult& OutResult,
		bool bDirectValue = false, int32 Depth = 0)
	{
		if (Depth > 16 || Property->ArrayDim != 1)
		{
			Fail(OutResult, EFinareInvocationError::UnsupportedValueType, TEXT("Reflected value exceeds the supported shape depth or has a fixed array dimension."));
			return false;
		}
		void* ValueAddress = bDirectValue
			                     ? Container
			                     : Property->ContainerPtrToValuePtr<void>(Container);
		const FArrayProperty* ArrayProperty = CastField<FArrayProperty>(Property);
		const FSetProperty* SetProperty = CastField<FSetProperty>(Property);
		const FMapProperty* MapProperty = CastField<FMapProperty>(Property);
		if (ArrayProperty || SetProperty || MapProperty)
		{
			const FString NativeName =
				UE::Finare::PropertyValueCodec::ContainerValueNativeName(*Property);
			const uint64 TypeId =
				UE::Finare::PropertyValueCodec::TypeIdFromPath(NativeName);
			if (TypeId == 0)
			{
				Fail(OutResult, EFinareInvocationError::UnsupportedValueType,
				     FString::Printf(TEXT("Property '%s' has an unsupported reflected collection shape."),
				                     *Property->GetName()));
				return false;
			}

			TConstArrayView<FFinareValue> Cells;
			const bool bShapeMatches = ArrayProperty
				                           ? Value.TryGetArray(TypeId, Cells)
				                           : SetProperty
				                           ? Value.TryGetSet(TypeId, Cells)
				                           : Value.TryGetMap(TypeId, Cells);
			const int32 CellsPerEntry = MapProperty ? 2 : 1;
			if (!bShapeMatches || Cells.Num() % CellsPerEntry != 0)
			{
				Fail(OutResult, EFinareInvocationError::ValueTypeMismatch,
				     FString::Printf(TEXT("Property '%s' received an incompatible collection value."),
				                     *Property->GetName()));
				return false;
			}
			auto IsNestedContainer = [](const FProperty& Child)
			{
				return CastField<FArrayProperty>(&Child) != nullptr ||
					CastField<FSetProperty>(&Child) != nullptr ||
					CastField<FMapProperty>(&Child) != nullptr;
			};
			if ((ArrayProperty && IsNestedContainer(*ArrayProperty->Inner)) ||
				(SetProperty && IsNestedContainer(*SetProperty->ElementProp)) ||
				(MapProperty &&
					(IsNestedContainer(*MapProperty->KeyProp) ||
						IsNestedContainer(*MapProperty->ValueProp))))
			{
				Fail(OutResult, EFinareInvocationError::UnsupportedValueType,
				     FString::Printf(TEXT("Property '%s' uses a nested collection, which is unsupported."),
				                     *Property->GetName()));
				return false;
			}

			const auto AllocateValue = [](const FProperty& Child) -> void*
			{
				void* Storage = FMemory::Malloc(Child.GetSize(), Child.GetMinAlignment());
				Child.InitializeValue(Storage);
				return Storage;
			};
			const auto FreeValue = [](const FProperty& Child, void* Storage)
			{
				Child.DestroyValue(Storage);
				FMemory::Free(Storage);
			};
			auto CopyCell = [&](FProperty& Child, void* Destination, const FFinareValue& Cell)
			{
				FFinareValue TypedCell = Cell;
				if (CastField<FSoftClassProperty>(&Child) != nullptr &&
					TypedCell.Kind == EFinareValueKind::String)
				{
					TypedCell.Kind = EFinareValueKind::SoftClass;
				}
				else if (CastField<FSoftObjectProperty>(&Child) != nullptr &&
					TypedCell.Kind == EFinareValueKind::String)
				{
					TypedCell.Kind = EFinareValueKind::SoftObject;
				}
				return WriteValue(Registry, &Child, Destination, TypedCell, OutResult, true, Depth + 1);
			};

			void* Temporary = AllocateValue(*Property);
			ON_SCOPE_EXIT
			{
				FreeValue(*Property, Temporary);
			};
			const auto Commit = [&]
			{
				Property->CopyCompleteValue(ValueAddress, Temporary);
			};

			if (ArrayProperty)
			{
				FScriptArrayHelper Array(ArrayProperty, Temporary);
				Array.EmptyAndAddValues(Cells.Num());
				for (int32 Index = 0; Index < Cells.Num(); ++Index)
				{
					if (!CopyCell(*ArrayProperty->Inner, Array.GetRawPtr(Index), Cells[Index]))
					{
						return false;
					}
				}
				Commit();
				return true;
			}
			if (SetProperty)
			{
				FScriptSetHelper Set(SetProperty, Temporary);
				Set.EmptyElements(Cells.Num());
				for (const FFinareValue& Cell : Cells)
				{
					void* Candidate = AllocateValue(*SetProperty->ElementProp);
					ON_SCOPE_EXIT
					{
						FreeValue(*SetProperty->ElementProp, Candidate);
					};
					if (!CopyCell(*SetProperty->ElementProp, Candidate, Cell))
					{
						return false;
					}
					for (int32 Index = 0; Index < Set.GetMaxIndex(); ++Index)
					{
						if (Set.IsValidIndex(Index) &&
							SetProperty->ElementProp->Identical(
								Set.GetElementPtr(Index), Candidate))
						{
							Fail(OutResult, EFinareInvocationError::ValueTypeMismatch,
							     FString::Printf(TEXT("Property '%s' contains a duplicate set element."),
							                     *Property->GetName()));
							return false;
						}
					}
					const int32 Added = Set.AddDefaultValue_Invalid_NeedsRehash();
					SetProperty->ElementProp->CopyCompleteValue(
						Set.GetElementPtr(Added), Candidate);
				}
				Set.Rehash();
				Commit();
				return true;
			}

			FScriptMapHelper Map(MapProperty, Temporary);
			Map.EmptyValues(Cells.Num() / 2);
			for (int32 Index = 0; Index < Cells.Num(); Index += 2)
			{
				void* CandidateKey = AllocateValue(*MapProperty->KeyProp);
				void* CandidateValue = AllocateValue(*MapProperty->ValueProp);
				ON_SCOPE_EXIT
				{
					FreeValue(*MapProperty->KeyProp, CandidateKey);
					FreeValue(*MapProperty->ValueProp, CandidateValue);
				};
				if (!CopyCell(*MapProperty->KeyProp, CandidateKey, Cells[Index]) ||
					!CopyCell(*MapProperty->ValueProp, CandidateValue, Cells[Index + 1]))
				{
					return false;
				}
				for (int32 Existing = 0; Existing < Map.GetMaxIndex(); ++Existing)
				{
					if (Map.IsValidIndex(Existing) &&
						MapProperty->KeyProp->Identical(
							Map.GetKeyPtr(Existing), CandidateKey))
					{
						Fail(OutResult, EFinareInvocationError::ValueTypeMismatch,
						     FString::Printf(TEXT("Property '%s' contains a duplicate map key."),
						                     *Property->GetName()));
						return false;
					}
				}
				const int32 Added = Map.AddDefaultValue_Invalid_NeedsRehash();
				MapProperty->KeyProp->CopyCompleteValue(Map.GetKeyPtr(Added), CandidateKey);
				MapProperty->ValueProp->CopyCompleteValue(Map.GetValuePtr(Added), CandidateValue);
			}
			Map.Rehash();
			Commit();
			return true;
		}
		if (auto* DelegateProperty = CastField<FDelegateProperty>(Property))
		{
			FScriptDelegate Delegate;
			if (!DelegateProperty->SignatureFunction || !Finare_ResolveCallback(Registry, Value,
				FinareMemberId(TCHAR_TO_UTF8(*DelegateProperty->SignatureFunction->GetPathName())), Delegate,
				OutResult))
			{
				return false;
			}
			UFunction* Bound = Delegate.GetUObject()->FindFunction(Delegate.GetFunctionName());
			if (!Bound || !Bound->IsSignatureCompatibleWith(DelegateProperty->SignatureFunction))
			{
				Fail(OutResult, EFinareInvocationError::ValueTypeMismatch,
				     TEXT("Callback signature changed since binding."));
				return false;
			}
			*static_cast<FScriptDelegate*>(ValueAddress) = Delegate;
			return true;
		}
		if (FBoolProperty* BoolProperty = CastField<FBoolProperty>(Property))
		{
			bool NativeValue = false;
			if (!Value.TryGetBoolean(NativeValue))
			{
				Fail(
					OutResult,
					EFinareInvocationError::ValueTypeMismatch,
					FString::Printf(TEXT("Property '%s' requires a boolean value."), *Property->GetName()));
				return false;
			}
			BoolProperty->SetPropertyValue(ValueAddress, NativeValue);
			return true;
		}
		if (FIntProperty* IntProperty = CastField<FIntProperty>(Property))
		{
			int32 NativeValue = 0;
			if (!Value.TryGetInt32(NativeValue))
			{
				Fail(
					OutResult,
					EFinareInvocationError::ValueTypeMismatch,
					FString::Printf(TEXT("Property '%s' requires an int32 value."), *Property->GetName()));
				return false;
			}
			IntProperty->SetPropertyValue(ValueAddress, NativeValue);
			return true;
		}
		if (FInt8Property* TypedProperty = CastField<FInt8Property>(Property))
		{
			int8 NativeValue = 0;
			if (Value.TryGetInt8(NativeValue))
			{
				TypedProperty->SetPropertyValue(ValueAddress, NativeValue);
				return true;
			}
		}
		if (FByteProperty* TypedProperty = CastField<FByteProperty>(Property))
		{
			uint8 NativeValue = 0;
			const bool bValid = TypedProperty->Enum != nullptr
				                    ? [&]
				                    {
					                    int64 EnumValue = 0;
					                    if (!Value.TryGetEnum(EnumValue, FName(*TypedProperty->Enum->GetPathName())) ||
						                    EnumValue < 0 || EnumValue > MAX_uint8)
					                    {
						                    return false;
					                    }
					                    NativeValue = static_cast<uint8>(EnumValue);
					                    return true;
				                    }()
				                    : Value.TryGetUInt8(NativeValue);
			if (bValid)
			{
				TypedProperty->SetPropertyValue(ValueAddress, NativeValue);
				return true;
			}
		}
		if (FInt16Property* TypedProperty = CastField<FInt16Property>(Property))
		{
			int16 NativeValue = 0;
			if (Value.TryGetInt16(NativeValue))
			{
				TypedProperty->SetPropertyValue(ValueAddress, NativeValue);
				return true;
			}
		}
		if (FUInt16Property* TypedProperty = CastField<FUInt16Property>(Property))
		{
			uint16 NativeValue = 0;
			if (Value.TryGetUInt16(NativeValue))
			{
				TypedProperty->SetPropertyValue(ValueAddress, NativeValue);
				return true;
			}
		}
		if (FUInt32Property* TypedProperty = CastField<FUInt32Property>(Property))
		{
			uint32 NativeValue = 0;
			if (Value.TryGetUInt32(NativeValue))
			{
				TypedProperty->SetPropertyValue(ValueAddress, NativeValue);
				return true;
			}
		}
		if (FInt64Property* TypedProperty = CastField<FInt64Property>(Property))
		{
			int64 NativeValue = 0;
			if (Value.TryGetInt64(NativeValue))
			{
				TypedProperty->SetPropertyValue(ValueAddress, NativeValue);
				return true;
			}
		}
		if (FUInt64Property* TypedProperty = CastField<FUInt64Property>(Property))
		{
			uint64 NativeValue = 0;
			if (Value.TryGetUInt64(NativeValue))
			{
				TypedProperty->SetPropertyValue(ValueAddress, NativeValue);
				return true;
			}
		}
		if (FFloatProperty* TypedProperty = CastField<FFloatProperty>(Property))
		{
			float NativeValue = 0.0f;
			if (Value.TryGetFloat32(NativeValue))
			{
				TypedProperty->SetPropertyValue(ValueAddress, NativeValue);
				return true;
			}
		}
		if (FDoubleProperty* TypedProperty = CastField<FDoubleProperty>(Property))
		{
			double NativeValue = 0.0;
			if (Value.TryGetFloat64(NativeValue))
			{
				TypedProperty->SetPropertyValue(ValueAddress, NativeValue);
				return true;
			}
		}
		if (FStrProperty* StringProperty = CastField<FStrProperty>(Property))
		{
			FString NativeValue;
			if (!Value.TryGetString(NativeValue))
			{
				Fail(
					OutResult,
					EFinareInvocationError::ValueTypeMismatch,
					FString::Printf(TEXT("Property '%s' requires a string value."), *Property->GetName()));
				return false;
			}
			StringProperty->SetPropertyValue(ValueAddress, NativeValue);
			return true;
		}
		if (FNameProperty* TypedProperty = CastField<FNameProperty>(Property))
		{
			FName NativeValue;
			if (Value.TryGetName(NativeValue))
			{
				TypedProperty->SetPropertyValue(ValueAddress, NativeValue);
				return true;
			}
		}
		if (FTextProperty* TypedProperty = CastField<FTextProperty>(Property))
		{
			FText NativeValue;
			if (Value.TryGetText(NativeValue))
			{
				TypedProperty->SetPropertyValue(ValueAddress, NativeValue);
				return true;
			}
		}
		if (FSoftClassProperty* TypedProperty = CastField<FSoftClassProperty>(Property))
		{
			FString PathText;
			if (!Value.TryGetSoftClassPath(PathText) && !Value.TryGetString(PathText))
			{
				Fail(OutResult, EFinareInvocationError::ValueTypeMismatch,
				     FString::Printf(TEXT("Property '%s' requires a soft class path."), *Property->GetName()));
				return false;
			}
			if (!FinareSoftObjectPath::IsValid(PathText))
			{
				Fail(OutResult, EFinareInvocationError::ValueTypeMismatch,
				     FString::Printf(TEXT("Property '%s' received an invalid soft class path."), *Property->GetName()));
				return false;
			}
			const FSoftObjectPath Path(PathText);
			if (!PathText.IsEmpty() && (!Path.IsValid() || Path.ToString() != PathText))
			{
				Fail(OutResult, EFinareInvocationError::ValueTypeMismatch,
				     FString::Printf(
					     TEXT("Property '%s' received an incompatible soft class path."), *Property->GetName()));
				return false;
			}
			TypedProperty->SetPropertyValue(ValueAddress, FSoftObjectPtr(Path));
			return true;
		}
		if (FSoftObjectProperty* TypedProperty = CastField<FSoftObjectProperty>(Property))
		{
			FString PathText;
			if (!Value.TryGetSoftObjectPath(PathText) && !Value.TryGetString(PathText))
			{
				Fail(OutResult, EFinareInvocationError::ValueTypeMismatch,
				     FString::Printf(TEXT("Property '%s' requires a soft object path."), *Property->GetName()));
				return false;
			}
			if (!FinareSoftObjectPath::IsValid(PathText))
			{
				Fail(OutResult, EFinareInvocationError::ValueTypeMismatch,
				     FString::Printf(
					     TEXT("Property '%s' received an invalid soft object path."), *Property->GetName()));
				return false;
			}
			const FSoftObjectPath Path(PathText);
			if (!PathText.IsEmpty() && (!Path.IsValid() || Path.ToString() != PathText))
			{
				Fail(OutResult, EFinareInvocationError::ValueTypeMismatch,
				     FString::Printf(
					     TEXT("Property '%s' received an incompatible soft object path."), *Property->GetName()));
				return false;
			}
			TypedProperty->SetPropertyValue(ValueAddress, FSoftObjectPtr(Path));
			return true;
		}
		if (FEnumProperty* TypedProperty = CastField<FEnumProperty>(Property))
		{
			int64 NativeValue = 0;
			if (Value.TryGetEnum(NativeValue, FName(*TypedProperty->GetEnum()->GetPathName())))
			{
				TypedProperty->GetUnderlyingProperty()->SetIntPropertyValue(ValueAddress, NativeValue);
				return true;
			}
		}
		if (FStructProperty* TypedProperty = CastField<FStructProperty>(Property))
		{
			if (TypedProperty->Struct == TBaseStructure<FVector>::Get())
			{
				FVector NativeValue;
				if (Value.TryGetVector(NativeValue))
				{
					*static_cast<FVector*>(ValueAddress) = NativeValue;
					return true;
				}
			}
			else if (TypedProperty->Struct == TBaseStructure<FRotator>::Get())
			{
				FRotator NativeValue;
				if (Value.TryGetRotator(NativeValue))
				{
					*static_cast<FRotator*>(ValueAddress) = NativeValue;
					return true;
				}
			}
			else if (TypedProperty->Struct == TBaseStructure<FTransform>::Get())
			{
				FTransform NativeValue;
				if (Value.TryGetTransform(NativeValue))
				{
					*static_cast<FTransform*>(ValueAddress) = NativeValue;
					return true;
				}
			}
			else
			{
				const auto Fields = ReflectedStructFields(TypedProperty->Struct);
				TConstArrayView<FFinareValue> Cells;
				const uint64 Id = UE::Finare::PropertyValueCodec::TypeIdFromPath(TypedProperty->Struct->GetPathName());
				if (Fields.IsEmpty() || !Value.TryGetStruct(Id, Cells) || Fields.Num() != Cells.Num())
				{
					Fail(OutResult, EFinareInvocationError::ValueTypeMismatch, TEXT("Reflected struct value does not match its public field layout."));
					return false;
				}
				FStructOnScope Candidate(TypedProperty->Struct);
				TypedProperty->Struct->CopyScriptStruct(Candidate.GetStructMemory(), ValueAddress);
				for (int32 Index = 0; Index < Fields.Num(); ++Index)
					if (!WriteValue(Registry, Fields[Index], Candidate.GetStructMemory(), Cells[Index], OutResult, false, Depth + 1)) return false;
				TypedProperty->Struct->CopyScriptStruct(ValueAddress, Candidate.GetStructMemory());
				return true;
			}
		}
		if (FInterfaceProperty* TypedProperty = CastField<FInterfaceProperty>(Property))
		{
			FFinareObjectHandle Handle;
			if (!Value.TryGetObjectHandle(Handle))
			{
				Fail(
					OutResult,
					EFinareInvocationError::ValueTypeMismatch,
					FString::Printf(TEXT("Property '%s' requires an interface object handle."), *Property->GetName()));
				return false;
			}

			UObject* Object = nullptr;
			if (!Handle.IsNull())
			{
				FFinareObjectHandleResult HandleResult;
				Object = Registry.ResolveObject(Handle, HandleResult);
				if (!HandleResult.IsSuccess())
				{
					OutResult.Error = EFinareInvocationError::ObjectHandleFailure;
					OutResult.HandleError = HandleResult.Error;
					OutResult.Message = MoveTemp(HandleResult.Message);
					return false;
				}
				if (Object == nullptr || TypedProperty->InterfaceClass == nullptr ||
					!Object->GetClass()->ImplementsInterface(TypedProperty->InterfaceClass))
				{
					Fail(
						OutResult,
						EFinareInvocationError::ValueTypeMismatch,
						FString::Printf(
							TEXT("Property '%s' received an object that does not implement interface '%s'."),
							*Property->GetName(),
							TypedProperty->InterfaceClass == nullptr
								? TEXT("<unknown>")
								: *TypedProperty->InterfaceClass->GetPathName()));
					return false;
				}
			}

			FScriptInterface* Destination =
				static_cast<FScriptInterface*>(ValueAddress);
			if (Handle.IsNull())
			{
				*Destination = FScriptInterface();
			}
			else
			{
				Destination->SetObject(Object);
				// A Blueprint-only implementer legitimately has no native interface
				// address. The object identity remains the valid interface value.
				Destination->SetInterface(Object->GetInterfaceAddress(TypedProperty->InterfaceClass));
			}
			return true;
		}
		if (FObjectPropertyBase* TypedProperty = CastField<FObjectPropertyBase>(Property))
		{
			const bool bClass = CastField<FClassProperty>(TypedProperty) != nullptr;
			FFinareObjectHandle Handle;
			if (Value.TryGetObjectHandle(Handle, bClass))
			{
				UObject* Object = nullptr;
				if (!Handle.IsNull())
				{
					FFinareObjectHandleResult HandleResult;
					Object = Registry.ResolveObject(Handle, HandleResult);
					if (!HandleResult.IsSuccess())
					{
						OutResult.Error = EFinareInvocationError::ObjectHandleFailure;
						OutResult.HandleError = HandleResult.Error;
						OutResult.Message = MoveTemp(HandleResult.Message);
						return false;
					}
				}
				if (const FClassProperty* ClassProperty = CastField<FClassProperty>(TypedProperty))
				{
					const UClass* Class = Cast<UClass>(Object);
					if (Object != nullptr &&
						(Class == nullptr || !Class->IsChildOf(ClassProperty->MetaClass)))
					{
						Object = nullptr;
					}
				}
				else if (Object != nullptr && !Object->IsA(TypedProperty->PropertyClass))
				{
					Object = nullptr;
				}
				if (Object != nullptr || Handle.IsNull())
				{
					TypedProperty->SetObjectPropertyValue(ValueAddress, Object);
					return true;
				}
			}
		}

		const FStructProperty* StructProperty = CastField<FStructProperty>(Property);
		const bool bSupportedStruct = StructProperty != nullptr &&
		(StructProperty->Struct == TBaseStructure<FVector>::Get() ||
			StructProperty->Struct == TBaseStructure<FRotator>::Get() ||
			StructProperty->Struct == TBaseStructure<FTransform>::Get());
		if (CastField<FNumericProperty>(Property) != nullptr ||
			CastField<FEnumProperty>(Property) != nullptr ||
			CastField<FNameProperty>(Property) != nullptr ||
			CastField<FTextProperty>(Property) != nullptr ||
			CastField<FObjectPropertyBase>(Property) != nullptr ||
			bSupportedStruct)
		{
			Fail(
				OutResult,
				EFinareInvocationError::ValueTypeMismatch,
				FString::Printf(
					TEXT("Property '%s' received an incompatible Finare value."),
					*Property->GetName()));
			return false;
		}

		Fail(
			OutResult,
			EFinareInvocationError::UnsupportedValueType,
			FString::Printf(
				TEXT("Property '%s' has unsupported native type '%s'."),
				*Property->GetName(),
				*Property->GetCPPType()));
		return false;
	}
}

FFinareValue FFinareValue::FromBoolean(bool Value)
{
	FFinareValue Result;
	Result.Kind = EFinareValueKind::Boolean;
	Result.BooleanValue = Value;
	return Result;
}

FFinareValue FFinareValue::FromInt32(int32 Value)
{
	FFinareValue Result;
	Result.Kind = EFinareValueKind::Int32;
	Result.Int32Value = Value;
	return Result;
}

FFinareValue FFinareValue::FromString(FString Value)
{
	FFinareValue Result;
	Result.Kind = EFinareValueKind::String;
	Result.StringValue = MoveTemp(Value);
	return Result;
}

FFinareValue FFinareValue::FromSoftObjectPath(FString Value)
{
	FFinareValue Result;
	Result.Kind = EFinareValueKind::SoftObject;
	Result.StringValue = MoveTemp(Value);
	return Result;
}

FFinareValue FFinareValue::FromSoftClassPath(FString Value)
{
	FFinareValue Result;
	Result.Kind = EFinareValueKind::SoftClass;
	Result.StringValue = MoveTemp(Value);
	return Result;
}

#define UE_FINARE_DEFINE_VALUE_FACTORY(Name, KindName, Type, Field) \
    FFinareValue FFinareValue::From##Name(Type Value)         \
    {                                                                      \
        FFinareValue Result;                                        \
        Result.Kind = EFinareValueKind::KindName;                   \
        Result.Field = Value;                                              \
        return Result;                                                     \
    }

UE_FINARE_DEFINE_VALUE_FACTORY(Int8, Int8, int8, Int8Value)
UE_FINARE_DEFINE_VALUE_FACTORY(UInt8, UInt8, uint8, UInt8Value)
UE_FINARE_DEFINE_VALUE_FACTORY(Int16, Int16, int16, Int16Value)
UE_FINARE_DEFINE_VALUE_FACTORY(UInt16, UInt16, uint16, UInt16Value)
UE_FINARE_DEFINE_VALUE_FACTORY(UInt32, UInt32, uint32, UInt32Value)
UE_FINARE_DEFINE_VALUE_FACTORY(Int64, Int64, int64, Int64Value)
UE_FINARE_DEFINE_VALUE_FACTORY(UInt64, UInt64, uint64, UInt64Value)
UE_FINARE_DEFINE_VALUE_FACTORY(Float32, Float32, float, Float32Value)
UE_FINARE_DEFINE_VALUE_FACTORY(Float64, Float64, double, Float64Value)
UE_FINARE_DEFINE_VALUE_FACTORY(Name, Name, FName, NameValue)
UE_FINARE_DEFINE_VALUE_FACTORY(Text, Text, FText, TextValue)
UE_FINARE_DEFINE_VALUE_FACTORY(Vector, Vector, FVector, VectorValue)
UE_FINARE_DEFINE_VALUE_FACTORY(Rotator, Rotator, FRotator, RotatorValue)
UE_FINARE_DEFINE_VALUE_FACTORY(Transform, Transform, FTransform, TransformValue)

#undef UE_FINARE_DEFINE_VALUE_FACTORY

FFinareValue FFinareValue::FromObjectHandle(
	FFinareObjectHandle Value,
	bool bClass)
{
	FFinareValue Result;
	Result.Kind = bClass ? EFinareValueKind::Class : EFinareValueKind::Object;
	Result.ObjectHandleValue = Value;
	return Result;
}

FFinareValue FFinareValue::FromEnum(int64 Value, FName InTypePath)
{
	FFinareValue Result;
	Result.Kind = EFinareValueKind::Enum;
	Result.Int64Value = Value;
	Result.TypePath = InTypePath;
	if (!InTypePath.IsNone())
	{
		const FString TypePathString = InTypePath.ToString();
		const FTCHARToUTF8 TypePathUtf8(*TypePathString);
		Result.TypeIdValue = FinareMemberId(TypePathUtf8.Get());
	}
	return Result;
}

FFinareValue FFinareValue::FromEnumTypeId(int64 Value, uint64 TypeId)
{
	FFinareValue Result;
	Result.Kind = EFinareValueKind::Enum;
	Result.Int64Value = Value;
	Result.TypeIdValue = TypeId;
	return Result;
}

namespace
{
	FFinareValue FromContainer(
		EFinareValueKind Kind,
		TArray<FFinareValue> Values,
		uint64 TypeId)
	{
		FFinareValue Result;
		Result.Kind = Kind;
		Result.TypeIdValue = TypeId;
		Result.ContainerValues = MoveTemp(Values);
		return Result;
	}
}

FFinareValue FFinareValue::FromArray(
	TArray<FFinareValue> Values,
	uint64 TypeId)
{
	return FromContainer(EFinareValueKind::Array, MoveTemp(Values), TypeId);
}

FFinareValue FFinareValue::FromStruct(
	TArray<FFinareValue> Fields,
	uint64 TypeId)
{
	return FromContainer(EFinareValueKind::Struct, MoveTemp(Fields), TypeId);
}

FFinareValue FFinareValue::FromSet(
	TArray<FFinareValue> Values,
	uint64 TypeId)
{
	return FromContainer(EFinareValueKind::Set, MoveTemp(Values), TypeId);
}

FFinareValue FFinareValue::FromMap(
	TArray<FFinareValue> FlattenedEntries,
	uint64 TypeId)
{
	return FromContainer(EFinareValueKind::Map, MoveTemp(FlattenedEntries), TypeId);
}

bool FFinareValue::TryGetBoolean(bool& OutValue) const
{
	if (Kind != EFinareValueKind::Boolean)
	{
		return false;
	}
	OutValue = BooleanValue;
	return true;
}

bool FFinareValue::TryGetInt32(int32& OutValue) const
{
	if (Kind != EFinareValueKind::Int32)
	{
		return false;
	}
	OutValue = Int32Value;
	return true;
}

bool FFinareValue::TryGetString(FString& OutValue) const
{
	if (Kind != EFinareValueKind::String)
	{
		return false;
	}
	OutValue = StringValue;
	return true;
}

bool FFinareValue::TryGetSoftObjectPath(FString& OutValue) const
{
	if (Kind != EFinareValueKind::SoftObject)
	{
		return false;
	}
	OutValue = StringValue;
	return true;
}

bool FFinareValue::TryGetSoftClassPath(FString& OutValue) const
{
	if (Kind != EFinareValueKind::SoftClass)
	{
		return false;
	}
	OutValue = StringValue;
	return true;
}

#define UE_FINARE_DEFINE_VALUE_READER(Name, KindName, Type, Field) \
    bool FFinareValue::TryGet##Name(Type& OutValue) const           \
    {                                                                     \
        if (Kind != EFinareValueKind::KindName)                    \
        {                                                                 \
            return false;                                                 \
        }                                                                 \
        OutValue = Field;                                                 \
        return true;                                                      \
    }

UE_FINARE_DEFINE_VALUE_READER(Int8, Int8, int8, Int8Value)
UE_FINARE_DEFINE_VALUE_READER(UInt8, UInt8, uint8, UInt8Value)
UE_FINARE_DEFINE_VALUE_READER(Int16, Int16, int16, Int16Value)
UE_FINARE_DEFINE_VALUE_READER(UInt16, UInt16, uint16, UInt16Value)
UE_FINARE_DEFINE_VALUE_READER(UInt32, UInt32, uint32, UInt32Value)
UE_FINARE_DEFINE_VALUE_READER(Int64, Int64, int64, Int64Value)
UE_FINARE_DEFINE_VALUE_READER(UInt64, UInt64, uint64, UInt64Value)
UE_FINARE_DEFINE_VALUE_READER(Float32, Float32, float, Float32Value)
UE_FINARE_DEFINE_VALUE_READER(Float64, Float64, double, Float64Value)
UE_FINARE_DEFINE_VALUE_READER(Name, Name, FName, NameValue)
UE_FINARE_DEFINE_VALUE_READER(Text, Text, FText, TextValue)
UE_FINARE_DEFINE_VALUE_READER(Vector, Vector, FVector, VectorValue)
UE_FINARE_DEFINE_VALUE_READER(Rotator, Rotator, FRotator, RotatorValue)
UE_FINARE_DEFINE_VALUE_READER(Transform, Transform, FTransform, TransformValue)

#undef UE_FINARE_DEFINE_VALUE_READER

bool FFinareValue::TryGetObjectHandle(
	FFinareObjectHandle& OutValue,
	bool bClass) const
{
	const EFinareValueKind ExpectedKind =
		bClass ? EFinareValueKind::Class : EFinareValueKind::Object;
	if (Kind != ExpectedKind)
	{
		return false;
	}
	OutValue = ObjectHandleValue;
	return true;
}

bool FFinareValue::TryGetEnum(int64& OutValue, FName ExpectedTypePath) const
{
	if (Kind != EFinareValueKind::Enum)
	{
		return false;
	}
	if (TypePath.IsNone())
	{
		const FTCHARToUTF8 ExpectedPath(*ExpectedTypePath.ToString());
		if (TypeIdValue == 0 || TypeIdValue != FinareMemberId(ExpectedPath.Get()))
		{
			return false;
		}
	}
	else if (TypePath != ExpectedTypePath)
	{
		return false;
	}
	OutValue = Int64Value;
	return true;
}

bool FFinareValue::TryGetEnumById(int64& OutValue, uint64 ExpectedTypeId) const
{
	if (Kind != EFinareValueKind::Enum ||
		TypeIdValue == 0 ||
		TypeIdValue != ExpectedTypeId)
	{
		return false;
	}
	OutValue = Int64Value;
	return true;
}

namespace
{
	bool TryGetContainer(
		const FFinareValue& Value,
		EFinareValueKind ExpectedKind,
		uint64 ExpectedTypeId,
		bool bRequireEvenCount,
		TConstArrayView<FFinareValue>& OutValues)
	{
		if (Value.Kind != ExpectedKind ||
			ExpectedTypeId == 0 ||
			Value.TypeIdValue != ExpectedTypeId ||
			(bRequireEvenCount && Value.ContainerValues.Num() % 2 != 0))
		{
			return false;
		}
		OutValues = Value.ContainerValues;
		return true;
	}
}

bool FFinareValue::TryGetArray(
	uint64 ExpectedTypeId,
	TConstArrayView<FFinareValue>& OutValues) const
{
	return TryGetContainer(*this, EFinareValueKind::Array, ExpectedTypeId, false, OutValues);
}

bool FFinareValue::TryGetStruct(
	uint64 ExpectedTypeId,
	TConstArrayView<FFinareValue>& OutFields) const
{
	return TryGetContainer(*this, EFinareValueKind::Struct, ExpectedTypeId, false, OutFields);
}

bool FFinareValue::TryGetSet(
	uint64 ExpectedTypeId,
	TConstArrayView<FFinareValue>& OutValues) const
{
	return TryGetContainer(*this, EFinareValueKind::Set, ExpectedTypeId, false, OutValues);
}

bool FFinareValue::TryGetMap(
	uint64 ExpectedTypeId,
	TConstArrayView<FFinareValue>& OutFlattenedEntries) const
{
	return TryGetContainer(
		*this,
		EFinareValueKind::Map,
		ExpectedTypeId,
		true,
		OutFlattenedEntries);
}

FFinareInvocationBridge::FFinareInvocationBridge(
	FFinareObjectRegistry& InRegistry)
	: Registry(InRegistry)
{
}

bool FFinareInvocationBridge::ReadProperty(
	FFinareObjectHandle Target,
	const FString& PropertyPath,
	FFinareValue& OutValue,
	FFinareInvocationResult& OutResult)
{
	using namespace UE::Finare::Invocation::Private;

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
			EFinareInvocationError::PropertyNotFound,
			FString::Printf(TEXT("Property '%s' was not found."), *PropertyPath));
		return false;
	}
	if (!Property->HasAnyPropertyFlags(CPF_BlueprintVisible | CPF_NativeAccessSpecifierPublic))
	{
		Fail(
			OutResult,
			EFinareInvocationError::PropertyNotReadable,
			FString::Printf(TEXT("Property '%s' is neither public nor Blueprint-readable."), *PropertyPath));
		return false;
	}
	if (!ReadValue(Registry, Property, Object, OutValue, OutResult))
	{
		return false;
	}

	Succeed(OutResult);
	return true;
}

bool FFinareInvocationBridge::WriteProperty(
	FFinareObjectHandle Target,
	const FString& PropertyPath,
	const FFinareValue& Value,
	FFinareInvocationResult& OutResult)
{
	using namespace UE::Finare::Invocation::Private;

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
			EFinareInvocationError::PropertyNotFound,
			FString::Printf(TEXT("Property '%s' was not found."), *PropertyPath));
		return false;
	}
	if (!Property->HasAnyPropertyFlags(CPF_BlueprintVisible | CPF_NativeAccessSpecifierPublic) ||
		Property->HasAnyPropertyFlags(CPF_BlueprintReadOnly | CPF_EditConst))
	{
		Fail(
			OutResult,
			EFinareInvocationError::PropertyNotWritable,
			FString::Printf(TEXT("Property '%s' is not writable through reflection."), *PropertyPath));
		return false;
	}
	if (Property->HasAnyPropertyFlags(CPF_Net) && Cast<UFinareBlueprintGeneratedClass>(Property->GetOwnerClass()) !=
		nullptr)
	{
		AActor* Owner = NetworkOwner(Object);
		if (Owner == nullptr || !Owner->HasAuthority())
		{
			Fail(OutResult, EFinareInvocationError::PropertyNotWritable,
			     TEXT("Replicated Script Class state can only be written by authority; send a Server RPC instead."));
			return false;
		}
	}
	if (!WriteValue(Registry, Property, Object, Value, OutResult))
	{
		return false;
	}
	// Generated state writes do not execute a K2 SetVariable node. Notify the
	// standard replication dirty tracker as that node would, including Iris.
	MARK_PROPERTY_DIRTY(Object, Property);

	Succeed(OutResult);
	return true;
}

bool FFinareInvocationBridge::Invoke(
	FFinareObjectHandle Target,
	const FString& FunctionPath,
	TConstArrayView<FFinareValue> Arguments,
	FFinareValue& OutReturnValue,
	FFinareInvocationResult& OutResult)
{
	TArray<FFinareValue> Outputs;
	return Invoke(Target, FunctionPath, Arguments, OutReturnValue, Outputs, OutResult);
}

bool FFinareInvocationBridge::Invoke(
	FFinareObjectHandle Target, const FString& FunctionPath,
	TConstArrayView<FFinareValue> Arguments, FFinareValue& OutReturnValue,
	TArray<FFinareValue>& OutOutputValues, FFinareInvocationResult& OutResult)
{
	using namespace UE::Finare::Invocation::Private;

	UObject* Object = ResolveTarget(Registry, Target, OutResult);
	if (Object == nullptr)
	{
		return false;
	}

	UClass* TargetClass = nullptr;
	FName FunctionName;
	if (!ResolveMemberIdentity(
		Object,
		FunctionPath,
		TargetClass,
		FunctionName,
		OutResult,
		true))
	{
		return false;
	}

	UFunction* Function = TargetClass->FindFunctionByName(FunctionName);
	const FFinareManagedFunctionBinding* RpcBinding = nullptr;
	for (UClass* Class = TargetClass; Class != nullptr; Class = Class->GetSuperClass())
	{
		auto* Generated = Cast<UFinareBlueprintGeneratedClass>(Class);
		if (Generated == nullptr)
		{
			continue;
		}
		for (const auto& Pair : Generated->ManagedFunctionBindings)
		{
			if (Pair.Value.RpcTarget != EFinareRpcTarget::None &&
				((Function != nullptr && Function->GetFName() == Pair.Key) ||
					(Function == nullptr && Pair.Value.ManagedName == FunctionName.ToString())))
			{
				RpcBinding = &Pair.Value;
				Function = Generated->FindFunctionByName(Pair.Key);
				break;
			}
		}
		if (RpcBinding != nullptr)
		{
			break;
		}
	}
	if (Function == nullptr)
	{
		Fail(
			OutResult,
			EFinareInvocationError::FunctionNotFound,
			FString::Printf(TEXT("Function '%s' was not found."), *FunctionPath));
		return false;
	}
	const bool bStaticFunction = Function->HasAnyFunctionFlags(FUNC_Static);
	if (!Function->HasAnyFunctionFlags(FUNC_BlueprintCallable | FUNC_BlueprintPure | FUNC_Public))
	{
		Fail(
			OutResult,
			EFinareInvocationError::FunctionNotCallable,
			FString::Printf(TEXT("Function '%s' is neither public nor Blueprint-callable."), *FunctionPath));
		return false;
	}

	TArray<FProperty*, TInlineAllocator<8>> InputProperties;
	if (RpcBinding != nullptr)
	{
		AActor* Owner = NetworkOwner(Object);
		const UActorComponent* Component = Cast<UActorComponent>(Object);
		const bool bClient = Owner != nullptr && Owner->GetNetMode() == NM_Client;
		// bReplicates itself is not replicated. An Actor enabled at runtime on
		// the server can retain a non-replicating archetype on its clients;
		// UE's received local role identifies that replica, not the CDO flag.
		const bool bNetworkedActor = Owner != nullptr && (bClient
			                                                  ? Owner->GetLocalRole() == ROLE_AutonomousProxy || Owner->
			                                                  GetLocalRole() == ROLE_SimulatedProxy
			                                                  : Owner->GetIsReplicated());
		const bool bAllowed = bNetworkedActor &&
			(Component == nullptr || Component->GetIsReplicated()) &&
			(!bClient || (RpcBinding->RpcTarget == EFinareRpcTarget::Server && Owner->HasLocalNetOwner())) &&
			(bClient || RpcBinding->RpcTarget != EFinareRpcTarget::Client ||
				Owner->GetNetMode() == NM_Standalone || Owner->GetNetConnection() != nullptr || Owner->
				HasLocalNetOwner());
		if (!bAllowed || !Function->HasAnyFunctionFlags(FUNC_Net))
		{
			Fail(OutResult, EFinareInvocationError::FunctionNotCallable,
			     FString::Printf(
				     TEXT(
					     "RPC '%s' has no valid replicated owner/connection or authority for this direction (networkedActor=%d, componentReplicated=%d, localOwner=%d, netFunction=%d)."),
				     *FunctionPath, bNetworkedActor, Component == nullptr || Component->GetIsReplicated(),
				     Owner != nullptr && Owner->HasLocalNetOwner(), Function->HasAnyFunctionFlags(FUNC_Net)));
			return false;
		}
	}
	TArray<FProperty*, TInlineAllocator<8>> OutputProperties;
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
		if (IsOutputParameter(Parameter)) OutputProperties.Add(Parameter);
		if (!IsOutputParameter(Parameter) || Parameter->HasAnyPropertyFlags(CPF_ReferenceParm))
			InputProperties.Add(Parameter);
	}

	if (InputProperties.Num() != Arguments.Num())
	{
		Fail(
			OutResult,
			EFinareInvocationError::ArgumentCountMismatch,
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
		if (!WriteValue(Registry, InputProperties[Index], ParameterMemory, Arguments[Index], OutResult))
		{
			return false;
		}
	}

	UObject* ProcessTarget = Object;
	if (bStaticFunction)
	{
		UClass* DeclaringClass = Function->GetOuterUClass();
		ProcessTarget = DeclaringClass != nullptr ? DeclaringClass->GetDefaultObject() : nullptr;
		if (ProcessTarget == nullptr)
		{
			Fail(
				OutResult,
				EFinareInvocationError::FunctionNotCallable,
				FString::Printf(TEXT("Static function '%s' has no declaring class default object."), *FunctionPath));
			return false;
		}
	}
	// Validate the result shapes before any user function can mutate gameplay.
	FFinareValue CandidateReturn;
	TArray<FFinareValue> CandidateOutputs;
	if (ReturnProperty && !ReadValue(Registry, ReturnProperty, ParameterMemory, CandidateReturn, OutResult)) return false;
	for (FProperty* Output : OutputProperties)
	{
		FFinareValue Descriptor;
		if (!ReadValue(Registry, Output, ParameterMemory, Descriptor, OutResult)) return false;
	}
	ProcessTarget->ProcessEvent(Function, ParameterMemory);
	if (ReturnProperty && !ReadValue(Registry, ReturnProperty, ParameterMemory, CandidateReturn, OutResult)) return false;
	for (FProperty* Output : OutputProperties)
	{
		FFinareValue Value;
		if (!ReadValue(Registry, Output, ParameterMemory, Value, OutResult)) return false;
		CandidateOutputs.Add(MoveTemp(Value));
	}
	OutReturnValue = MoveTemp(CandidateReturn);
	OutOutputValues = MoveTemp(CandidateOutputs);

	Succeed(OutResult);
	return true;
}

bool FFinareInvocationBridge::DescribeReflectedInvocation(
	FFinareObjectHandle Target, uint64 Id,
	TArray<FFinareValue>& OutDescriptors, FFinareInvocationResult& OutResult)
{
	using namespace UE::Finare::Invocation::Private;
	UObject* Object = ResolveTarget(Registry, Target, OutResult);
	if (!Object) return false;
	UFunction* Function = FindReflectedFunction(Object, Id);
	if (!Function || !Function->HasAnyFunctionFlags(FUNC_BlueprintCallable | FUNC_BlueprintPure | FUNC_Public))
	{
		Fail(OutResult, EFinareInvocationError::InvalidMemberPath, TEXT("No callable reflected function matches this member ID."));
		return false;
	}
	FStructOnScope Storage(Function);
	TArray<FFinareValue> Descriptors;
	const auto Describe = [&](FProperty* Property)
	{
		FFinareValue Value;
		if (!ReadValue(Registry, Property, Storage.GetStructMemory(), Value, OutResult)) return false;
		NormalizeFrameValue(Value);
		Descriptors.Add(MoveTemp(Value));
		return true;
	};
	if (FProperty* Return = Function->GetReturnProperty())
		if (!Describe(Return)) return false;
	for (TFieldIterator<FProperty> It(Function); It; ++It)
		if (It->HasAnyPropertyFlags(CPF_Parm) && !It->HasAnyPropertyFlags(CPF_ReturnParm) && IsOutputParameter(*It))
			if (!Describe(*It)) return false;
	OutDescriptors = MoveTemp(Descriptors);
	Succeed(OutResult);
	return true;
}

bool FFinareInvocationBridge::ReadPropertyById(
	FFinareObjectHandle Target,
	uint64 MemberIdValue,
	FFinareValue& OutValue,
	FFinareInvocationResult& OutResult)
{
	using namespace UE::Finare::Invocation::Private;
	UObject* Object = ResolveTarget(Registry, Target, OutResult);
	if (Object == nullptr ||
		!FFinareGeneratedThunkRegistry::ReadProperty(
			Object,
			Registry,
			MemberIdValue,
			OutValue,
			OutResult))
	{
		if (Object && OutResult.Error == EFinareInvocationError::InvalidMemberPath)
			if (FProperty* Property = FindReflectedProperty(Object, MemberIdValue))
				return ReadProperty(Target, Property->GetOwnerStruct()->GetPathName() + TEXT(":") + Property->GetName(), OutValue, OutResult);
		return false;
	}
	FastInvocationCount.fetch_add(1, std::memory_order_relaxed);
	return true;
}

bool FFinareInvocationBridge::WritePropertyById(
	FFinareObjectHandle Target,
	uint64 MemberIdValue,
	const FFinareValue& Value,
	FFinareInvocationResult& OutResult)
{
	using namespace UE::Finare::Invocation::Private;
	UObject* Object = ResolveTarget(Registry, Target, OutResult);
	if (Object == nullptr ||
		!FFinareGeneratedThunkRegistry::WriteProperty(
			Object,
			Registry,
			MemberIdValue,
			Value,
			OutResult))
	{
		if (Object && OutResult.Error == EFinareInvocationError::InvalidMemberPath)
			if (FProperty* Property = FindReflectedProperty(Object, MemberIdValue))
				return WriteProperty(Target, Property->GetOwnerStruct()->GetPathName() + TEXT(":") + Property->GetName(), Value, OutResult);
		return false;
	}
	FastInvocationCount.fetch_add(1, std::memory_order_relaxed);
	return true;
}

bool FFinareInvocationBridge::InvokeById(
	FFinareObjectHandle Target,
	uint64 MemberIdValue,
	TConstArrayView<FFinareValue> Arguments,
	FFinareValue& OutReturnValue,
	FFinareInvocationResult& OutResult)
{
	TArray<FFinareValue> IgnoredOutputValues;
	return InvokeById(
		Target,
		MemberIdValue,
		Arguments,
		OutReturnValue,
		IgnoredOutputValues,
		OutResult);
}

bool FFinareInvocationBridge::InvokeById(
	FFinareObjectHandle Target,
	uint64 MemberIdValue,
	TConstArrayView<FFinareValue> Arguments,
	FFinareValue& OutReturnValue,
	TArray<FFinareValue>& OutOutputValues,
	FFinareInvocationResult& OutResult)
{
	using namespace UE::Finare::Invocation::Private;
	UObject* Object = ResolveTarget(Registry, Target, OutResult);
	if (Object == nullptr) return false;
	uint32 GeneratedResultCount = 0;
	if (!FFinareGeneratedThunkRegistry::DescribeInvokeResultCount(MemberIdValue, GeneratedResultCount))
	{
		UFunction* Function = FindReflectedFunction(Object, MemberIdValue);
		if (Function)
			return Invoke(Target, ReflectedFunctionPath(Function), Arguments, OutReturnValue, OutOutputValues, OutResult);
	}
	if (Object == nullptr ||
		!FFinareGeneratedThunkRegistry::Invoke(
			Object,
			Registry,
			MemberIdValue,
			Arguments,
			OutReturnValue,
			OutOutputValues,
			OutResult))
	{
		return false;
	}
	FastInvocationCount.fetch_add(1, std::memory_order_relaxed);
	return true;
}

bool FFinareInvocationBridge::InvokeParentById(
	FFinareObjectHandle Host,
	uint64 MemberIdValue,
	const FFinareNativeCallFrameV3& InputFrame,
	uint32 EntryByteCount,
	FFinareParentCallResult& OutParentResult,
	FFinareInvocationResult& OutResult)
{
	using namespace UE::Finare::Blueprint::Private;
	using namespace UE::Finare::Invocation::Private;

	OutParentResult.ResultCells.Reset();
	OutParentResult.Arena.Reset();

	UObject* HostObject = ResolveTarget(Registry, Host, OutResult);
	if (HostObject == nullptr)
	{
		return false;
	}

	UFinareComponent* Component = Cast<UFinareComponent>(HostObject);
	if (Component == nullptr)
	{
		Fail(
			OutResult,
			EFinareInvocationError::TargetTypeMismatch,
			TEXT("Blueprint parent invocation host must be a running Finare component."));
		return false;
	}
	UObject* Receiver = Component->GetParentInvocationReceiver();
	const UFinareBlueprintGeneratedClass* ManagedClass =
		Component->GetActiveManagedGeneratedClass();
	if (Receiver == nullptr || ManagedClass == nullptr ||
		!Component->OwnsActiveManagedOverride(ManagedClass))
	{
		Fail(
			OutResult,
			EFinareInvocationError::TargetTypeMismatch,
			TEXT("Blueprint parent invocation has no exact active managed owner for the host component."));
		return false;
	}

	const FFinareManagedFunctionBinding* Binding =
		ManagedClass->FindManagedOverrideBinding(MemberIdValue);
	if (Binding == nullptr)
	{
		Fail(
			OutResult,
			EFinareInvocationError::InvalidMemberPath,
			TEXT("Blueprint parent member ID does not match an active persisted override route."));
		return false;
	}

	UClass* DeclaringClass = FindObject<UClass>(
		nullptr,
		*Binding->BlueprintOverrideDeclaringTypePath);
	if (DeclaringClass == nullptr || !Receiver->IsA(DeclaringClass))
	{
		Fail(
			OutResult,
			EFinareInvocationError::TargetTypeMismatch,
			FString::Printf(
				TEXT("Blueprint parent receiver '%s' is not an instance of declaring class '%s'."),
				*GetPathNameSafe(Receiver),
				*Binding->BlueprintOverrideDeclaringTypePath));
		return false;
	}

	const FName ParentName(*Binding->BlueprintOverrideFunctionName);
	UFunction* DeclaredParentFunction = DeclaringClass->FindFunctionByName(
		ParentName,
		EIncludeSuperFlag::ExcludeSuper);
	if (DeclaredParentFunction == nullptr ||
		!DeclaredParentFunction->HasAnyFunctionFlags(FUNC_BlueprintEvent) ||
		DeclaredParentFunction->HasAnyFunctionFlags(FUNC_Static | FUNC_Final | FUNC_Net))
	{
		Fail(
			OutResult,
			EFinareInvocationError::FunctionNotCallable,
			FString::Printf(
				TEXT("Blueprint parent '%s:%s' is not an instance Blueprint event."),
				*Binding->BlueprintOverrideDeclaringTypePath,
				*Binding->BlueprintOverrideFunctionName));
		return false;
	}

	// The declaration identifies the route, while the first local function
	// above it is the effective implementation. This preserves an ordinary
	// Blueprint parent body between the managed class and the native owner.
	UFunction* ParentFunction = nullptr;
	bool bReachedDeclaringClass = false;
	for (UClass* Candidate = ManagedClass->GetSuperClass();
	     Candidate != nullptr;
	     Candidate = Candidate->GetSuperClass())
	{
		if (UFunction* CandidateFunction = Candidate->FindFunctionByName(
				ParentName,
				EIncludeSuperFlag::ExcludeSuper);
			CandidateFunction != nullptr)
		{
			ParentFunction = CandidateFunction;
			break;
		}
		if (Candidate == DeclaringClass)
		{
			bReachedDeclaringClass = true;
			break;
		}
	}
	if (ParentFunction == nullptr && bReachedDeclaringClass)
	{
		ParentFunction = DeclaredParentFunction;
	}
	if (ParentFunction == nullptr || !Receiver->IsA(ParentFunction->GetOuterUClass()) ||
		!ParentFunction->HasAnyFunctionFlags(FUNC_BlueprintEvent) ||
		ParentFunction->HasAnyFunctionFlags(FUNC_Static | FUNC_Final | FUNC_Net))
	{
		Fail(
			OutResult,
			EFinareInvocationError::FunctionNotCallable,
			FString::Printf(
				TEXT("Blueprint parent route '%s:%s' has no callable effective parent implementation."),
				*Binding->BlueprintOverrideDeclaringTypePath,
				*Binding->BlueprintOverrideFunctionName));
		return false;
	}

	// A generated managed owner between the receiver and the declaring class
	// would re-enter another managed generation through Call Parent. Refuse
	// that unsupported C3 route before touching the parent parameter frame.
	for (UClass* Candidate = ManagedClass->GetSuperClass();
	     Candidate != nullptr;
	     Candidate = Candidate->GetSuperClass())
	{
		const auto* Generated = Cast<UFinareBlueprintGeneratedClass>(Candidate);
		if (Generated != nullptr &&
			Generated->IsManagedFunctionBound(ParentName))
		{
			Fail(
				OutResult,
				EFinareInvocationError::FunctionNotCallable,
				FString::Printf(
					TEXT(
						"Blueprint parent route '%s:%s' crosses another managed owner; C3 parent dispatch is unsupported."),
					*Binding->BlueprintOverrideDeclaringTypePath,
					*Binding->BlueprintOverrideFunctionName));
			return false;
		}
	}

	if (Binding->ParameterNames.Num() != Binding->ParameterTypes.Num() ||
		Binding->ParameterNames.Num() != Binding->ParameterDirections.Num() ||
		InputFrame.ByteCount != EntryByteCount ||
		EntryByteCount > 64u * 1024u ||
		EntryByteCount > InputFrame.ByteCapacity ||
		(EntryByteCount > 0 && InputFrame.ByteBuffer == nullptr))
	{
		Fail(
			OutResult,
			EFinareInvocationError::UnsupportedParameter,
			TEXT("Blueprint parent route has an incomplete parameter contract or input arena."));
		return false;
	}

	TArray<FProperty*, TInlineAllocator<8>> Parameters;
	Parameters.Reserve(Binding->ParameterNames.Num());
	for (const FName ParameterName : Binding->ParameterNames)
	{
		FProperty* Parameter = FindFProperty<FProperty>(ParentFunction, ParameterName);
		if (Parameter == nullptr ||
			!Parameter->HasAnyPropertyFlags(CPF_Parm) ||
			Parameter->HasAnyPropertyFlags(CPF_ReturnParm))
		{
			Fail(
				OutResult,
				EFinareInvocationError::UnsupportedParameter,
				FString::Printf(
					TEXT("Blueprint parent '%s' cannot resolve native parameter '%s'."),
					*ParentFunction->GetPathName(),
					*ParameterName.ToString()));
			return false;
		}
		bool bNativeConst = Parameter->HasAnyPropertyFlags(CPF_ConstParm);
#if WITH_METADATA
		bNativeConst = bNativeConst || Parameter->HasMetaData(TEXT("NativeConst"));
#endif
		const EFinareManagedParameterDirection Direction =
			Binding->ParameterDirections[Parameters.Num()];
		const bool bValidDirection =
			Direction == EFinareManagedParameterDirection::Input
				? (!Parameter->HasAnyPropertyFlags(CPF_OutParm) || bNativeConst)
				: Direction == EFinareManagedParameterDirection::InputOutput
				? Parameter->HasAnyPropertyFlags(CPF_ReferenceParm) && !bNativeConst
				: Parameter->HasAnyPropertyFlags(CPF_OutParm) && !bNativeConst;
		if (!bValidDirection)
		{
			Fail(
				OutResult,
				EFinareInvocationError::UnsupportedParameter,
				FString::Printf(
					TEXT("Blueprint parent parameter '%s' direction does not match managed route."),
					*ParameterName.ToString()));
			return false;
		}
		Parameters.Add(Parameter);
	}

	uint32 ExpectedArgumentCount = 0;
	for (const EFinareManagedParameterDirection Direction :
	     Binding->ParameterDirections)
	{
		if (Direction != EFinareManagedParameterDirection::Output)
		{
			++ExpectedArgumentCount;
		}
	}
	uint32 ExpectedResultCount =
		ParentFunction->GetReturnProperty() != nullptr ? 1u : 0u;
	for (const EFinareManagedParameterDirection Direction :
	     Binding->ParameterDirections)
	{
		if (Direction != EFinareManagedParameterDirection::Input)
		{
			++ExpectedResultCount;
		}
	}
	if (InputFrame.ArgumentCount != ExpectedArgumentCount ||
		InputFrame.ResultCount != ExpectedResultCount ||
		InputFrame.ResultCapacity < ExpectedResultCount ||
		(ExpectedResultCount > 0 && InputFrame.Results == nullptr))
	{
		Fail(
			OutResult,
			EFinareInvocationError::ArgumentCountMismatch,
			FString::Printf(
				TEXT("Blueprint parent route expected %u input and %u result cells, received %u and %u."),
				ExpectedArgumentCount,
				ExpectedResultCount,
				InputFrame.ArgumentCount,
				InputFrame.ResultCount));
		return false;
	}

	UWorld* World = Receiver->GetWorld();
	UFinareWorldSubsystem* HostSubsystem = World != nullptr
		                                       ? World->GetSubsystem<UFinareWorldSubsystem>()
		                                       : nullptr;
	if (HostSubsystem == nullptr)
	{
		Fail(
			OutResult,
			EFinareInvocationError::TransportUnavailable,
			TEXT("Blueprint parent receiver has no managed World subsystem."));
		return false;
	}

	FStructOnScope ParameterStorage(ParentFunction);
	void* ParameterMemory = ParameterStorage.GetStructMemory();
	TConstArrayView<uint8> InputArena(
		InputFrame.ByteBuffer,
		static_cast<int32>(InputFrame.ByteCapacity));
	uint32 InputIndex = 0;
	for (int32 Index = 0; Index < Parameters.Num(); ++Index)
	{
		const EFinareManagedParameterDirection Direction =
			Binding->ParameterDirections[Index];
		if (Direction == EFinareManagedParameterDirection::Output)
		{
			continue;
		}
		FString CodecError;
		if (!UnpackReturnValueV2(
			*Parameters[Index],
			InputFrame.Arguments[InputIndex++],
			InputArena,
			EntryByteCount,
			*HostSubsystem,
			&Binding->ParameterTypes[Index],
			{},
			Parameters[Index]->ContainerPtrToValuePtr<void>(ParameterMemory),
			CodecError))
		{
			Fail(
				OutResult,
				EFinareInvocationError::ValueTypeMismatch,
				FString::Printf(
					TEXT("Blueprint parent input '%s' could not be decoded: %s"),
					*Parameters[Index]->GetName(),
					*CodecError));
			return false;
		}
	}

	// Validate every result descriptor before invoking user Blueprint code.
	// The managed frame carries the complete V2 type identity, while the
	// payload offsets and scalar values are call-specific.  Packing the
	// initialized native values gives us the same recursive container/struct
	// schema check used for the eventual result without allowing a malformed
	// result cell to reach ProcessEvent.
	TArray<uint8, TInlineAllocator<512>> DescriptorArena;
	uint32 DescriptorResultIndex = 0;
	auto ValidateResultDescriptor = [&](
		const FProperty& Property,
		const FFinareManagedValueTypeBinding* TypeBinding,
		const FFinareNativeValueV3& Actual,
		const TCHAR* Label) -> bool
	{
		FFinareNativeValueV3 Expected;
		FString DescriptorError;
		if (!PackValueV2(
			Property,
			ParameterMemory,
			*HostSubsystem,
			DescriptorArena,
			TypeBinding,
			{},
			Expected,
			DescriptorError))
		{
			Fail(
				OutResult,
				EFinareInvocationError::ValueTypeMismatch,
				FString::Printf(
					TEXT("Blueprint parent %s result schema could not be described: %s"),
					Label,
					*DescriptorError));
			return false;
		}
		const bool bObjectDefault =
			(Expected.Kind == EFinareNativeValueKindV3::Object ||
				Expected.Kind == EFinareNativeValueKindV3::Class) &&
			Expected.Flags == EFinareNativeValueFlagsV3::Null &&
			Actual.Flags == EFinareNativeValueFlagsV3::None;
		const bool bShapeMatches =
			Expected.Kind == Actual.Kind &&
			Expected.TypeId == Actual.TypeId &&
			(Expected.Flags == Actual.Flags || bObjectDefault) &&
			(Expected.Kind != EFinareNativeValueKindV3::Struct ||
				(Expected.Payload2 == Actual.Payload2 &&
					Expected.Payload3 == Actual.Payload3)) &&
			((Expected.Kind != EFinareNativeValueKindV3::Array &&
					Expected.Kind != EFinareNativeValueKindV3::Set &&
					Expected.Kind != EFinareNativeValueKindV3::Map) ||
				(Actual.Payload2 == 0 &&
					Expected.Payload3 == Actual.Payload3)) &&
			(Expected.Kind != EFinareNativeValueKindV3::Transform ||
				Expected.Payload1 == Actual.Payload1);
		if (!bShapeMatches)
		{
			Fail(
				OutResult,
				EFinareInvocationError::ValueTypeMismatch,
				FString::Printf(
					TEXT("Blueprint parent %s result descriptor does not match the native property schema."),
					Label));
			return false;
		}
		return true;
	};
	if (const FProperty* ReturnProperty = ParentFunction->GetReturnProperty();
		ReturnProperty != nullptr)
	{
		if (!ValidateResultDescriptor(
			*ReturnProperty,
			&Binding->ReturnType,
			InputFrame.Results[DescriptorResultIndex++],
			TEXT("return")))
		{
			return false;
		}
	}
	for (int32 Index = 0; Index < Parameters.Num(); ++Index)
	{
		if (Binding->ParameterDirections[Index] ==
			EFinareManagedParameterDirection::Input)
		{
			continue;
		}
		if (!ValidateResultDescriptor(
			*Parameters[Index],
			&Binding->ParameterTypes[Index],
			InputFrame.Results[DescriptorResultIndex++],
			*Parameters[Index]->GetName()))
		{
			return false;
		}
	}

	Receiver->ProcessEvent(ParentFunction, ParameterMemory);

	TArray<uint8, TInlineAllocator<512>> Arena;
	Arena.SetNum(EntryByteCount);
	if (EntryByteCount > 0)
	{
		FMemory::Memcpy(Arena.GetData(), InputFrame.ByteBuffer, EntryByteCount);
	}
	TArray<FFinareNativeValueV3, TInlineAllocator<8>> ResultCells;
	ResultCells.SetNumZeroed(ExpectedResultCount);
	uint32 ResultIndex = 0;
	FString CodecError;
	if (const FProperty* ReturnProperty = ParentFunction->GetReturnProperty();
		ReturnProperty != nullptr &&
		!PackValueV2(
			*ReturnProperty,
			ParameterMemory,
			*HostSubsystem,
			Arena,
			&Binding->ReturnType,
			{},
			ResultCells[ResultIndex++],
			CodecError))
	{
		Fail(
			OutResult,
			EFinareInvocationError::ValueTypeMismatch,
			FString::Printf(
				TEXT("Blueprint parent return could not be encoded: %s"),
				*CodecError));
		return false;
	}
	for (int32 Index = 0; Index < Parameters.Num(); ++Index)
	{
		if (Binding->ParameterDirections[Index] ==
			EFinareManagedParameterDirection::Input)
		{
			continue;
		}
		CodecError.Reset();
		if (!PackValueV2(
			*Parameters[Index],
			ParameterMemory,
			*HostSubsystem,
			Arena,
			&Binding->ParameterTypes[Index],
			{},
			ResultCells[ResultIndex++],
			CodecError))
		{
			Fail(
				OutResult,
				EFinareInvocationError::ValueTypeMismatch,
				FString::Printf(
					TEXT("Blueprint parent output '%s' could not be encoded: %s"),
					*Parameters[Index]->GetName(),
					*CodecError));
			return false;
		}
	}
	OutParentResult.ResultCells.SetNumUninitialized(
		ResultCells.Num() * sizeof(FFinareNativeValueV3));
	if (!ResultCells.IsEmpty())
	{
		FMemory::Memcpy(
			OutParentResult.ResultCells.GetData(),
			ResultCells.GetData(),
			OutParentResult.ResultCells.Num());
	}
	OutParentResult.Arena.Append(Arena.GetData(), Arena.Num());
	Succeed(OutResult);
	FastInvocationCount.fetch_add(1, std::memory_order_relaxed);
	return true;
}

bool FFinareInvocationBridge::InvokeInt32x2ById(
	FFinareObjectHandle Target,
	uint64 MemberIdValue,
	int32 Argument0,
	int32 Argument1,
	int32& OutReturnValue,
	FFinareInvocationResult& OutResult)
{
	using namespace UE::Finare::Invocation::Private;
	UObject* Object = ResolveTarget(Registry, Target, OutResult);
	if (Object == nullptr ||
		!FFinareGeneratedThunkRegistry::InvokeInt32x2(
			Object,
			MemberIdValue,
			Argument0,
			Argument1,
			OutReturnValue,
			OutResult))
	{
		return false;
	}
	FastInvocationCount.fetch_add(1, std::memory_order_relaxed);
	return true;
}

bool FFinareInvocationBridge::InvokeFixedById(
	FFinareObjectHandle Target,
	uint64 MemberIdValue,
	TConstArrayView<FFinareNativeValueV3> Arguments,
	FFinareNativeValueV3* OutReturnValue,
	FFinareInvocationResult& OutResult)
{
	using namespace UE::Finare::Invocation::Private;
	UObject* Object = ResolveTarget(Registry, Target, OutResult);
	if (Object == nullptr ||
		!FFinareGeneratedThunkRegistry::InvokeFixed(
			Object,
			Registry,
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

uint64 FFinareInvocationBridge::GetFastInvocationCount() const
{
	return FastInvocationCount.load(std::memory_order_relaxed);
}

bool FFinareInvocationBridge::ResolveMulticastDelegate(
	FFinareObjectHandle Target,
	const FString& EventPath,
	UObject*& OutSource,
	const FMulticastDelegateProperty*& OutProperty,
	FFinareInvocationResult& OutResult)
{
	using namespace UE::Finare::Invocation::Private;
	check(IsInGameThread());
	OutSource = nullptr;
	OutProperty = nullptr;

	UObject* Source = ResolveTarget(Registry, Target, OutResult);
	UClass* TargetClass = nullptr;
	FName MemberName;
	if (Source == nullptr ||
		!ResolveMemberIdentity(Source, EventPath, TargetClass, MemberName, OutResult))
	{
		return false;
	}

	const FMulticastDelegateProperty* Property =
		FindFProperty<FMulticastDelegateProperty>(TargetClass, MemberName);
	if (Property == nullptr || Property->SignatureFunction == nullptr)
	{
		Fail(
			OutResult,
			EFinareInvocationError::PropertyNotFound,
			FString::Printf(
				TEXT("Finare event '%s' is not a multicast delegate property."),
				*EventPath));
		return false;
	}
	if (!Property->HasAnyPropertyFlags(CPF_BlueprintAssignable))
	{
		Fail(
			OutResult,
			EFinareInvocationError::PropertyNotWritable,
			FString::Printf(
				TEXT("Finare event '%s' is not BlueprintAssignable."),
				*EventPath));
		return false;
	}

	OutSource = Source;
	OutProperty = Property;
	Succeed(OutResult);
	return true;
}

bool FFinareInvocationBridge::ReadDelegateArguments(
	const UFunction* Signature,
	const void* ParameterMemory,
	TArray<FFinareValue>& OutArguments,
	FFinareInvocationResult& OutResult)
{
	using namespace UE::Finare::Invocation::Private;
	check(IsInGameThread());
	OutArguments.Reset();
	if (Signature == nullptr || (Signature->ParmsSize > 0 && ParameterMemory == nullptr))
	{
		Fail(
			OutResult,
			EFinareInvocationError::InvalidMemberPath,
			TEXT("A delegate dispatch requires a signature and memory for non-empty parameters."));
		return false;
	}

	for (TFieldIterator<FProperty> It(Signature, EFieldIteratorFlags::ExcludeSuper); It; ++It)
	{
		const FProperty* Parameter = *It;
		if (!Parameter->HasAnyPropertyFlags(CPF_Parm) ||
			Parameter->HasAnyPropertyFlags(CPF_ReturnParm))
		{
			continue;
		}

		FFinareValue Value;
		if (!ReadValue(Registry, Parameter, ParameterMemory, Value, OutResult))
		{
			ReleaseEventArguments(OutArguments);
			OutArguments.Reset();
			return false;
		}
		OutArguments.Add(MoveTemp(Value));
	}

	Succeed(OutResult);
	return true;
}

bool FFinareInvocationBridge::WriteCallbackOutputs(const UFunction* Signature, void* Parameters,
                                                   TConstArrayView<FFinareValue> Outputs,
                                                   FFinareInvocationResult& OutResult)
{
	using namespace UE::Finare::Invocation::Private;
	check(IsInGameThread());
	if (!Signature || (Signature->ParmsSize && !Parameters))
	{
		Fail(OutResult, EFinareInvocationError::InvalidMemberPath, TEXT("Callback output memory is unavailable."));
		return false;
	}
	TArray<FProperty*> Properties;
	if (FProperty* Return = Signature->GetReturnProperty())
	{
		Properties.Add(Return);
	}
	for (TFieldIterator<FProperty> It(Signature, EFieldIteratorFlags::ExcludeSuper); It; ++It)
	{
		if (It->HasAllPropertyFlags(CPF_Parm | CPF_OutParm) && !It->HasAnyPropertyFlags(CPF_ReturnParm | CPF_ConstParm))
		{
			Properties.Add(*It);
		}
	}
	if (Properties.Num() != Outputs.Num())
	{
		Fail(OutResult, EFinareInvocationError::ArgumentCountMismatch,
		     TEXT("Callback result/ref/out count does not match its signature."));
		return false;
	}
	FStructOnScope Candidate(Signature);
	for (int32 Index = 0; Index < Properties.Num(); ++Index)
	{
		if (!WriteValue(Registry, Properties[Index], Candidate.GetStructMemory(), Outputs[Index], OutResult))
		{
			return false;
		}
	}
	for (FProperty* Property : Properties)
	{
		Property->CopyCompleteValue_InContainer(Parameters, Candidate.GetStructMemory());
	}
	Succeed(OutResult);
	return true;
}

void FFinareInvocationBridge::ReleaseEventArguments(
	TConstArrayView<FFinareValue> Arguments)
{
	check(IsInGameThread());
	TFunction<void(const FFinareValue&)> ReleaseValue =
		[this, &ReleaseValue](const FFinareValue& Value)
	{
		if ((Value.Kind == EFinareValueKind::Object ||
				Value.Kind == EFinareValueKind::Class) &&
			!Value.ObjectHandleValue.IsNull())
		{
			FFinareObjectHandleResult Result;
			Registry.ReleaseObject(Value.ObjectHandleValue, Result);
			ensureMsgf(Result.IsSuccess(), TEXT("%s"), *Result.Message);
		}
		for (const FFinareValue& Child : Value.ContainerValues)
		{
			ReleaseValue(Child);
		}
	};

	for (const FFinareValue& Value : Arguments)
	{
		ReleaseValue(Value);
	}
}
