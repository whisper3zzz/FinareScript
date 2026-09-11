#pragma once

#include "FinareBlueprint.h"
#include "FinareNativeTransport.h"

class UFinareWorldSubsystem;

namespace UE::Finare::Blueprint::Private
{
	bool PackValueV2(
		const FProperty& Property,
		const void* Container,
		UFinareWorldSubsystem& Host,
		TArray<uint8, TInlineAllocator<512>>& Arena,
		const FFinareManagedValueTypeBinding* TypeBinding,
		TConstArrayView<FName> StructPrefix,
		FFinareNativeValueV3& OutValue,
		FString& OutError,
		bool bDirectValue = false);

	bool UnpackReturnValueV2(
		const FProperty& Property,
		const FFinareNativeValueV3& Value,
		TConstArrayView<uint8> Arena,
		uint32 ByteCount,
		UFinareWorldSubsystem& Host,
		const FFinareManagedValueTypeBinding* TypeBinding,
		TConstArrayView<FName> StructPrefix,
		void* Result,
		FString& OutError);

	/** Builds the activation shape with the same reflected codec used by the class producer. */
	bool BuildActivationShapeJson(
		const FProperty& Property,
		const FFinareManagedValueTypeBinding& Binding,
		FString& OutJson,
		FString& OutError);
}
