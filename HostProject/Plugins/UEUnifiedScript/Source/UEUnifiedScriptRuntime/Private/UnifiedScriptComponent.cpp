#include "UnifiedScriptComponent.h"

UUnifiedScriptComponent::UUnifiedScriptComponent()
{
    PrimaryComponentTick.bCanEverTick = false;
}

void UUnifiedScriptComponent::RequestReload()
{
    bReloadRequested = true;
}

int32 UUnifiedScriptComponent::Add(int32 Left, int32 Right) const
{
    return Left + Right;
}

bool UUnifiedScriptComponent::ConsumeReloadRequest()
{
    const bool bWasRequested = bReloadRequested;
    bReloadRequested = false;
    return bWasRequested;
}
