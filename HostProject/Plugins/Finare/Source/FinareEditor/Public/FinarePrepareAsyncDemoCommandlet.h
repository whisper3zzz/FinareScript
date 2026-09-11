#pragma once

#include "Commandlets/Commandlet.h"
#include "FinarePrepareAsyncDemoCommandlet.generated.h"

/** Repository sample bootstrap. Never rewrites an existing scene. */
UCLASS()
class FINAREEDITOR_API UFinarePrepareAsyncDemoCommandlet : public UCommandlet
{
	GENERATED_BODY()

public:
	UFinarePrepareAsyncDemoCommandlet();
	virtual int32 Main(const FString& Params) override;
};
