#include "FinareAuthoringTransBuffer.h"

#include "UObject/Class.h"

void FAuthoringBlueprintTransaction::BeginExplicitAuthoringCapture()
{
	check(!bExplicitAuthoringCaptureOpen);
	ExplicitAuthoringObjects.Reset();
	bExplicitAuthoringCaptureOpen = true;
}

void FAuthoringBlueprintTransaction::EndExplicitAuthoringCapture()
{
	check(bExplicitAuthoringCaptureOpen);
	bExplicitAuthoringCaptureOpen = false;
}

bool FAuthoringBlueprintTransaction::IsAuthoringObjectAllowed(UObject* Object) const
{
	return Object != nullptr && ExplicitAuthoringObjects.Contains(FObjectKey(Object));
}

void FAuthoringBlueprintTransaction::SaveObject(UObject* Object)
{
	if (Object == nullptr || Object->IsA<UStruct>())
	{
		return;
	}
	if (bExplicitAuthoringCaptureOpen)
	{
		ExplicitAuthoringObjects.Add(FObjectKey(Object));
	}
	else if (!IsAuthoringObjectAllowed(Object))
	{
		return;
	}
	FTransaction::SaveObject(Object);
}

void FAuthoringBlueprintTransaction::SaveArray(
	UObject* Object,
	FScriptArray* Array,
	int32 Index,
	int32 Count,
	int32 Oper,
	int32 ElementSize,
	uint32 ElementAlignment,
	STRUCT_DC DefaultConstructor,
	STRUCT_AR Serializer,
	STRUCT_DTOR Destructor)
{
	if (!IsAuthoringObjectAllowed(Object))
	{
		return;
	}
	FTransaction::SaveArray(
		Object,
		Array,
		Index,
		Count,
		Oper,
		ElementSize,
		ElementAlignment,
		DefaultConstructor,
		Serializer,
		Destructor);
}

void FAuthoringBlueprintTransaction::StoreUndo(
	UObject* Object,
	TUniquePtr<FChange> UndoChange)
{
	if (!IsAuthoringObjectAllowed(Object))
	{
		return;
	}
	FTransaction::StoreUndo(Object, MoveTemp(UndoChange));
}

void FAuthoringBlueprintTransaction::SnapshotObject(
	UObject* Object,
	TArrayView<const FProperty*> Properties)
{
	if (!IsAuthoringObjectAllowed(Object))
	{
		return;
	}
	FTransaction::SnapshotObject(Object, Properties);
}

int32 UFinareAuthoringTransBuffer::Begin(
	const TCHAR* SessionContext,
	const FText& Description)
{
	return BeginInternal<FAuthoringBlueprintTransaction>(SessionContext, Description);
}
