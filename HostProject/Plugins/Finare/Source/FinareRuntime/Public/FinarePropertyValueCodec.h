#pragma once

#include "CoreMinimal.h"
#include "UObject/EnumProperty.h"
#include "UObject/SoftObjectPtr.h"
#include "UObject/UnrealType.h"

/**
 * Shared native identity helpers for reflected values.
 *
 * The generated Blueprint call-frame codec and the reflection property bridge
 * must derive the same wire type ID for a container.  Keep this helper free of
 * runtime state so both paths use exactly the same spelling and hash.
 */
namespace UE::Finare::PropertyValueCodec
{
	inline uint64 TypeIdFromPath(const FString& Path)
	{
		constexpr uint64 OffsetBasis = 14695981039346656037ull;
		constexpr uint64 Prime = 1099511628211ull;
		if (Path.IsEmpty())
		{
			return 0;
		}

		uint64 Hash = OffsetBasis;
		for (const TCHAR Character : Path)
		{
			if (Character > 0x7f)
			{
				return 0;
			}
			Hash ^= static_cast<uint8>(Character);
			Hash *= Prime;
		}
		return Hash;
	}

	inline FString ContainerValueNativeName(const FProperty& Property)
	{
		if (CastField<FInt8Property>(&Property)) return TEXT("int8");
		if (CastField<FInt16Property>(&Property)) return TEXT("int16");
		if (CastField<FUInt16Property>(&Property)) return TEXT("uint16");
		if (CastField<FUInt32Property>(&Property)) return TEXT("uint32");
		if (CastField<FUInt64Property>(&Property)) return TEXT("uint64");
		if (const FEnumProperty* Typed = CastField<FEnumProperty>(&Property))
		{
			return Typed->GetEnum()->GetName();
		}
		if (const FByteProperty* Typed = CastField<FByteProperty>(&Property);
			Typed != nullptr && Typed->Enum != nullptr)
		{
			return Typed->Enum->GetName();
		}
		if (CastField<FBoolProperty>(&Property))
		{
			return TEXT("bool");
		}
		if (CastField<FByteProperty>(&Property))
		{
			return TEXT("uint8");
		}
		if (CastField<FIntProperty>(&Property))
		{
			return TEXT("int32");
		}
		if (CastField<FInt64Property>(&Property))
		{
			return TEXT("int64");
		}
		if (CastField<FFloatProperty>(&Property))
		{
			return TEXT("float");
		}
		if (CastField<FDoubleProperty>(&Property))
		{
			return TEXT("double");
		}
		if (CastField<FStrProperty>(&Property))
		{
			return TEXT("FString");
		}
		if (CastField<FNameProperty>(&Property))
		{
			return TEXT("FName");
		}
		if (CastField<FTextProperty>(&Property))
		{
			return TEXT("FText");
		}
		// FSoftClassProperty derives from FSoftObjectProperty; keep this order.
		if (CastField<FSoftClassProperty>(&Property))
		{
			return TEXT("FSoftClassPath");
		}
		if (CastField<FSoftObjectProperty>(&Property))
		{
			return TEXT("FSoftObjectPath");
		}
		if (CastField<FClassProperty>(&Property))
		{
			return TEXT("UClass*");
		}
		if (CastField<FObjectPropertyBase>(&Property))
		{
			return TEXT("UObject*");
		}
		if (const FStructProperty* Typed = CastField<FStructProperty>(&Property))
		{
			if (Typed->Struct == TBaseStructure<FVector>::Get())
			{
				return TEXT("FVector");
			}
			if (Typed->Struct == TBaseStructure<FRotator>::Get())
			{
				return TEXT("FRotator");
			}
			if (Typed->Struct == TBaseStructure<FTransform>::Get())
			{
				return TEXT("FTransform");
			}
			return Typed->Struct->GetStructCPPName();
		}
		if (const FArrayProperty* Typed = CastField<FArrayProperty>(&Property))
		{
			return TEXT("TArray<") + ContainerValueNativeName(*Typed->Inner) + TEXT(">");
		}
		if (const FSetProperty* Typed = CastField<FSetProperty>(&Property))
		{
			return TEXT("TSet<") + ContainerValueNativeName(*Typed->ElementProp) + TEXT(">");
		}
		if (const FMapProperty* Typed = CastField<FMapProperty>(&Property))
		{
			return TEXT("TMap<") + ContainerValueNativeName(*Typed->KeyProp) + TEXT(",") +
				ContainerValueNativeName(*Typed->ValueProp) + TEXT(">");
		}
		return FString();
	}
}
