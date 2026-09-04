#pragma once

#include "Blueprint/UserWidget.h"
#include "UnifiedScriptAsyncDemoWidget.generated.h"

/** Sample view only. Buttons, async loading and Actor ownership are managed.
 * A Widget Blueprint with the same named controls can replace this layout. */
UCLASS()
class UEUNIFIEDSCRIPTHOST_API UUnifiedScriptAsyncDemoWidget : public UUserWidget
{
    GENERATED_BODY()

public:
    virtual bool Initialize() override;
};
