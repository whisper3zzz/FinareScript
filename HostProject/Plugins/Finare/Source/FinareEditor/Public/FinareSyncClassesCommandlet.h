#pragma once

#include "Commandlets/Commandlet.h"
#include "FinareSyncClassesCommandlet.generated.h"

/**
 * Reconciles generated Script Class Blueprints with managed Declaration IR.
 * Deployment assets must already exist, so this command runs immediately after
 * FinareSyncAssets in both installed workflows and CI.
 */
UCLASS()
class FINAREEDITOR_API UFinareSyncClassesCommandlet : public UCommandlet
{
	GENERATED_BODY()

public:
	UFinareSyncClassesCommandlet();

	virtual int32 Main(const FString& Params) override;
};
