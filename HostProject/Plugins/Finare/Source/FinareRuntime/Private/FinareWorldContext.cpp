#include "FinareWorldContext.h"

#include "Engine/World.h"

namespace UE::Finare::WorldContext
{
	UWorld* Resolve(const UObject* Object)
	{
		if (!IsValid(Object))
		{
			return nullptr;
		}

		if (UWorld* DirectWorld = const_cast<UObject*>(Object)->GetWorld())
		{
			return DirectWorld;
		}

		for (const UObject* Outer = Object->GetOuter(); Outer != nullptr;
			 Outer = Outer->GetOuter())
		{
			if (UWorld* World = Cast<UWorld>(const_cast<UObject*>(Outer)))
			{
				return World;
			}
			if (UWorld* OuterWorld = const_cast<UObject*>(Outer)->GetWorld())
			{
				return OuterWorld;
			}
		}
		return nullptr;
	}
}
