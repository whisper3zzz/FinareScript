#include "UnifiedScriptAuthoringTransBuffer.h"

#include "UObject/Class.h"

void FAuthoringBlueprintTransaction::SaveObject(UObject* Object)
{
    if (Object == nullptr || Object->IsA<UStruct>())
    {
        return;
    }
    FTransaction::SaveObject(Object);
}

int32 UUnifiedScriptAuthoringTransBuffer::Begin(
    const TCHAR* SessionContext,
    const FText& Description)
{
    return BeginInternal<FAuthoringBlueprintTransaction>(SessionContext, Description);
}
