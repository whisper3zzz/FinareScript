#pragma once

#include "CoreMinimal.h"

class UObject;
class UWorld;

namespace UE::Finare::WorldContext
{
	/** Resolves a UObject's World without requiring an Actor owner. */
	FINARERUNTIME_API UWorld* Resolve(const UObject* Object);
}
