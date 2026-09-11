#include "Engine/World.h"
#include "Engine/StaticMeshActor.h"
#include "GameFramework/Actor.h"
#include "Interfaces/IPluginManager.h"
#include "Kismet/GameplayStatics.h"
#include "Misc/AutomationTest.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "FinareCoreClrHost.h"
#include "FinareInteropFixture.h"
#include "FinareInvocationBridge.h"
#include "FinareObjectRegistry.h"
#include "FinareWorldSubsystem.h"

namespace UE::Finare::ReferenceContainerTests
{
	constexpr uint64 ActorArrayType = FinareMemberId("TArray<UObject*>");
	constexpr uint64 ActorClassSetType = FinareMemberId("TSet<UClass*>");
	constexpr uint64 IntMapType = FinareMemberId("TMap<int32,int32>");
	constexpr uint64 SoftObjectMapType = FinareMemberId("TMap<FName,FSoftObjectPath>");
	constexpr uint64 SoftClassArrayType = FinareMemberId("TArray<FSoftClassPath>");

	constexpr uint64 ActorArrayProperty = FinareMemberId(
		"/Script/FinareRuntime.FinareInteropFixture:ActorArrayValue");
	constexpr uint64 ActorClassSetProperty = FinareMemberId(
		"/Script/FinareRuntime.FinareInteropFixture:ActorClassSetValue");
	constexpr uint64 IntMapProperty = FinareMemberId(
		"/Script/FinareRuntime.FinareInteropFixture:IntMapValue");
	constexpr uint64 SoftObjectMapProperty = FinareMemberId(
		"/Script/FinareRuntime.FinareInteropFixture:SoftObjectMapValue");
	constexpr uint64 SoftClassArrayProperty = FinareMemberId(
		"/Script/FinareRuntime.FinareInteropFixture:SoftClassArrayValue");
	constexpr uint64 EchoActorArray = FinareMemberId(
		"/Script/FinareRuntime.FinareInteropFixture:EchoActorArray");
	constexpr uint64 EchoActorClassSet = FinareMemberId(
		"/Script/FinareRuntime.FinareInteropFixture:EchoActorClassSet");
	constexpr uint64 EchoSoftObjectMap = FinareMemberId(
		"/Script/FinareRuntime.FinareInteropFixture:EchoSoftObjectMap");
	constexpr uint64 MutateActorArray = FinareMemberId(
		"/Script/FinareRuntime.FinareInteropFixture:MutateActorArray");
	constexpr uint64 MutateSoftObjectMap = FinareMemberId(
		"/Script/FinareRuntime.FinareInteropFixture:MutateSoftObjectMap");
	constexpr uint64 GetAllActorsOfClass = FinareMemberId(
		"/Script/Engine.GameplayStatics:GetAllActorsOfClass");

	FFinareValue ObjectCell(FFinareObjectHandle Handle)
	{
		return FFinareValue::FromObjectHandle(Handle);
	}

	FFinareValue ClassCell(FFinareObjectHandle Handle)
	{
		return FFinareValue::FromObjectHandle(Handle, true);
	}

	FFinareValue ObjectArray(
		FFinareObjectHandle First,
		FFinareObjectHandle Second)
	{
		TArray<FFinareValue> Cells;
		Cells.Add(ObjectCell(First));
		Cells.Add(ObjectCell(Second));
		return FFinareValue::FromArray(MoveTemp(Cells), ActorArrayType);
	}

	FFinareValue SoftObjectMap()
	{
		TArray<FFinareValue> Cells;
		Cells.Add(FFinareValue::FromName(FName(TEXT("Item"))));
		Cells.Add(FFinareValue::FromString(TEXT("/Game/Items/BP_Item.BP_Item")));
		Cells.Add(FFinareValue::FromName(FName(TEXT("Null"))));
		Cells.Add(FFinareValue::FromString(TEXT("")));
		return FFinareValue::FromMap(MoveTemp(Cells), SoftObjectMapType);
	}

	FFinareValue SoftClassArray()
	{
		TArray<FFinareValue> Cells;
		Cells.Add(FFinareValue::FromString(TEXT("/Script/Engine.Actor")));
		Cells.Add(FFinareValue::FromString(TEXT("")));
		return FFinareValue::FromArray(MoveTemp(Cells), SoftClassArrayType);
	}

	bool HasObjectCells(
		const FFinareValue& Value,
		uint64 TypeId,
		TConstArrayView<FFinareObjectHandle> Expected)
	{
		TConstArrayView<FFinareValue> Cells;
		if (!Value.TryGetArray(TypeId, Cells) || Cells.Num() != Expected.Num())
		{
			return false;
		}
		for (int32 Index = 0; Index < Cells.Num(); ++Index)
		{
			FFinareObjectHandle Handle;
			if (!Cells[Index].TryGetObjectHandle(Handle) || Handle != Expected[Index])
			{
				return false;
			}
		}
		return true;
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareReferenceContainerRoundTripTest,
	"Finare.Unreal.ReferenceContainers.GeneratedRoundTrip",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareReferenceContainerRoundTripTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::ReferenceContainerTests;
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	TestNotNull(TEXT("Reference container World exists"), World);
	if (World == nullptr)
	{
		return false;
	}
	World->AddToRoot();
	ON_SCOPE_EXIT
	{
		World->RemoveFromRoot();
		World->DestroyWorld(false);
	};

	FFinareObjectRegistry Registry(World);
	FFinareInvocationBridge Bridge(Registry);
	FFinareObjectHandleResult HandleResult;
	UFinareInteropFixture* Fixture = NewObject<UFinareInteropFixture>(World);
	AStaticMeshActor* FirstMatchingActor = World->SpawnActor<AStaticMeshActor>();
	AStaticMeshActor* SecondMatchingActor = World->SpawnActor<AStaticMeshActor>();
	AActor* PlainActor = World->SpawnActor<AActor>();
	AActor* StaleActor = World->SpawnActor<AActor>();
	TestNotNull(TEXT("Reference fixture exists"), Fixture);
	TestNotNull(TEXT("First matching StaticMeshActor exists"), FirstMatchingActor);
	TestNotNull(TEXT("Second matching StaticMeshActor exists"), SecondMatchingActor);
	TestNotNull(TEXT("Nonmatching Actor exists"), PlainActor);
	TestNotNull(TEXT("Stale candidate exists"), StaleActor);
	if (Fixture == nullptr || FirstMatchingActor == nullptr || SecondMatchingActor == nullptr ||
		PlainActor == nullptr || StaleActor == nullptr)
	{
		return false;
	}

	const FFinareObjectHandle FixtureHandle = Registry.RegisterObject(Fixture, HandleResult);
	TestTrue(TEXT("Fixture handle is registered"), HandleResult.IsSuccess());
	const FFinareObjectHandle FirstHandle = Registry.RegisterObject(FirstMatchingActor, HandleResult);
	TestTrue(TEXT("First matching Actor handle is registered"), HandleResult.IsSuccess());
	const FFinareObjectHandle SecondHandle = Registry.RegisterObject(SecondMatchingActor, HandleResult);
	TestTrue(TEXT("Second matching Actor handle is registered"), HandleResult.IsSuccess());
	const FFinareObjectHandle PlainHandle = Registry.RegisterObject(PlainActor, HandleResult);
	TestTrue(TEXT("Nonmatching Actor handle is registered"), HandleResult.IsSuccess());
	const FFinareObjectHandle StaleHandle = Registry.RegisterObject(StaleActor, HandleResult);
	TestTrue(TEXT("Stale candidate handle is registered"), HandleResult.IsSuccess());
	const FFinareObjectHandle ActorClassHandle = Registry.RegisterObject(AStaticMeshActor::StaticClass(), HandleResult);
	TestTrue(TEXT("StaticMeshActor class handle is registered"), HandleResult.IsSuccess());
	const FFinareObjectHandle UObjectClassHandle = Registry.RegisterObject(UObject::StaticClass(), HandleResult);
	TestTrue(TEXT("Wrong class handle is registered"), HandleResult.IsSuccess());

	const FFinareValue InitialActors = ObjectArray(FirstHandle, SecondHandle);
	const FFinareObjectHandle ExpectedActors[] = {FirstHandle, SecondHandle};
	FFinareInvocationResult InvocationResult;
	TestTrue(TEXT("Actor array property write succeeds"),
	         Bridge.WritePropertyById(FixtureHandle, ActorArrayProperty, InitialActors, InvocationResult));
	TestTrue(TEXT("Actor array property read preserves identity"), [&]
	{
		FFinareValue ReadValue;
		return Bridge.ReadPropertyById(FixtureHandle, ActorArrayProperty, ReadValue, InvocationResult) &&
			HasObjectCells(ReadValue, ActorArrayType, MakeArrayView(ExpectedActors));
	}());

	FFinareValue Echoed;
	TArray<FFinareValue> NoOutputs;
	TestTrue(TEXT("Actor array return uses the canonical wire ID"),
	         Bridge.InvokeById(FixtureHandle, EchoActorArray, MakeArrayView(&InitialActors, 1), Echoed, NoOutputs,
	                           InvocationResult));
	TestTrue(TEXT("Actor array return keeps both object identities"),
	         HasObjectCells(Echoed, ActorArrayType, MakeArrayView(ExpectedActors)));

	FFinareValue WrongOuter = ObjectArray(FirstHandle, SecondHandle);
	WrongOuter.TypeIdValue = FinareMemberId("TArray<AActor*>");
	Fixture->ActorArrayValue = {FirstMatchingActor};
	TestFalse(TEXT("Wrong outer container ID is rejected before mutation"),
	          Bridge.WritePropertyById(FixtureHandle, ActorArrayProperty, WrongOuter, InvocationResult));
	TestTrue(TEXT("Wrong outer ID preserves the original Actor"),
	         Fixture->ActorArrayValue.Num() == 1 && Fixture->ActorArrayValue[0] == FirstMatchingActor);
	FFinareObjectHandleResult ReleaseResult;
	TestTrue(TEXT("Stale candidate is released"), Registry.ReleaseObject(StaleHandle, ReleaseResult));
	FFinareValue StaleArray = ObjectArray(StaleHandle, SecondHandle);
	TestFalse(TEXT("Stale element is rejected before mutation"),
	          Bridge.WritePropertyById(FixtureHandle, ActorArrayProperty, StaleArray, InvocationResult));
	TestTrue(TEXT("Stale element preserves the original Actor"),
	         Fixture->ActorArrayValue.Num() == 1 && Fixture->ActorArrayValue[0] == FirstMatchingActor);

	TArray<FFinareValue> ClassCells;
	ClassCells.Add(ClassCell(ActorClassHandle));
	FFinareValue ActorClasses = FFinareValue::FromSet(MoveTemp(ClassCells), ActorClassSetType);
	TestTrue(TEXT("Typed Actor class set property write succeeds"),
	         Bridge.WritePropertyById(FixtureHandle, ActorClassSetProperty, ActorClasses, InvocationResult));
	TArray<FFinareValue> WrongClassCells;
	WrongClassCells.Add(ClassCell(UObjectClassHandle));
	FFinareValue WrongClasses = FFinareValue::FromSet(MoveTemp(WrongClassCells), ActorClassSetType);
	const int32 ClassCountBeforeReject = Fixture->ActorClassSetValue.Num();
	TestFalse(TEXT("Incompatible typed class is rejected before mutation"),
	          Bridge.WritePropertyById(FixtureHandle, ActorClassSetProperty, WrongClasses, InvocationResult));
	TestTrue(TEXT("Incompatible class preserves the original class"),
	         Fixture->ActorClassSetValue.Num() == ClassCountBeforeReject &&
	         Fixture->ActorClassSetValue.Contains(AStaticMeshActor::StaticClass()));

	TArray<FFinareValue> MapCells;
	MapCells.Add(FFinareValue::FromInt32(1));
	MapCells.Add(FFinareValue::FromInt32(2));
	MapCells.Add(FFinareValue::FromInt32(3));
	MapCells.Add(FFinareValue::FromInt32(4));
	FFinareValue IntMap = FFinareValue::FromMap(MoveTemp(MapCells), IntMapType);
	TestTrue(TEXT("Map property write uses its primitive wire identity"),
	         Bridge.WritePropertyById(FixtureHandle, IntMapProperty, IntMap, InvocationResult));
	FFinareValue ReadMap;
	TestTrue(TEXT("Map property read succeeds"),
	         Bridge.ReadPropertyById(FixtureHandle, IntMapProperty, ReadMap, InvocationResult));
	TConstArrayView<FFinareValue> ReadMapCells;
	TestTrue(TEXT("Map property round-trip retains entries"),
	         ReadMap.TryGetMap(IntMapType, ReadMapCells) && ReadMapCells.Num() == 4);

	FFinareValue SoftObjects = SoftObjectMap();
	FFinareValue SoftClasses = SoftClassArray();
	TestTrue(TEXT("Soft object map accepts unloaded and null paths"),
	         Bridge.WritePropertyById(FixtureHandle, SoftObjectMapProperty, SoftObjects, InvocationResult));
	TestTrue(TEXT("Soft class array accepts unloaded and null paths"),
	         Bridge.WritePropertyById(FixtureHandle, SoftClassArrayProperty, SoftClasses, InvocationResult));
	FFinareValue EchoedSoft;
	TestTrue(TEXT("Soft object map return preserves paths"),
	         Bridge.InvokeById(FixtureHandle, EchoSoftObjectMap, MakeArrayView(&SoftObjects, 1), EchoedSoft, NoOutputs,
	                           InvocationResult));
	TConstArrayView<FFinareValue> SoftCells;
	TestTrue(TEXT("Soft object map return retains the null path"),
	         EchoedSoft.TryGetMap(SoftObjectMapType, SoftCells) && SoftCells.Num() == 4 &&
	         SoftCells[3].StringValue.IsEmpty());

	TArray<FFinareValue> EmptyActorCells;
	FFinareValue Accumulator = FFinareValue::FromArray(MoveTemp(EmptyActorCells), ActorArrayType);
	TArray<FFinareValue> ActorArguments;
	ActorArguments.Add(InitialActors);
	ActorArguments.Add(Accumulator);
	FFinareValue MutationResult;
	TArray<FFinareValue> MutationOutputs;
	TestTrue(TEXT("Actor array ref/out exchange succeeds"),
	         Bridge.InvokeById(FixtureHandle, MutateActorArray, ActorArguments, MutationResult, MutationOutputs,
	                           InvocationResult));
	bool bMutationSucceeded = false;
	TestTrue(
		TEXT("Actor array mutation returns success"),
		MutationResult.TryGetBoolean(bMutationSucceeded) && bMutationSucceeded);
	TestEqual(TEXT("Actor array mutation returns output and accumulator"), MutationOutputs.Num(), 2);
	if (MutationOutputs.Num() == 2)
	{
		TestTrue(TEXT("Actor array output retains identities"),
		         HasObjectCells(MutationOutputs[0], ActorArrayType, MakeArrayView(ExpectedActors)));
		TestTrue(TEXT("Actor array accumulator retains identities"),
		         HasObjectCells(MutationOutputs[1], ActorArrayType, MakeArrayView(ExpectedActors)));
	}

	ActorArguments.Reset();
	TArray<FFinareValue> EmptySoftCells;
	FFinareValue EmptySoftObjects = FFinareValue::FromMap(MoveTemp(EmptySoftCells), SoftObjectMapType);
	ActorArguments.Add(EmptySoftObjects);
	ActorArguments.Add(SoftObjects);
	MutationOutputs.Reset();
	TestTrue(TEXT("Soft object map ref/out exchange succeeds"),
	         Bridge.InvokeById(FixtureHandle, MutateSoftObjectMap, ActorArguments, MutationResult, MutationOutputs,
	                           InvocationResult));
	TestEqual(TEXT("Soft object map mutation returns output and accumulator"), MutationOutputs.Num(), 2);

	TArray<FFinareValue> GameplayArguments;
	GameplayArguments.Add(ObjectCell(FirstHandle));
	GameplayArguments.Add(ClassCell(ActorClassHandle));
	MutationOutputs.Reset();
	TestTrue(TEXT("GameplayStatics generated call succeeds"),
	         Bridge.InvokeById(FixtureHandle, GetAllActorsOfClass, GameplayArguments, MutationResult, MutationOutputs,
	                           InvocationResult));
	TestEqual(TEXT("GameplayStatics returns one output array"), MutationOutputs.Num(), 1);
	if (MutationOutputs.Num() == 1)
	{
		TConstArrayView<FFinareValue> FoundActors;
		TestTrue(TEXT("GameplayStatics output uses UObject wire identity"),
		         MutationOutputs[0].TryGetArray(ActorArrayType, FoundActors));
		bool bFoundFirst = false;
		bool bFoundSecond = false;
		bool bFoundPlain = false;
		for (const FFinareValue& Cell : FoundActors)
		{
			FFinareObjectHandle Handle;
			if (Cell.TryGetObjectHandle(Handle))
			{
				bFoundFirst |= Handle == FirstHandle;
				bFoundSecond |= Handle == SecondHandle;
				bFoundPlain |= Handle == PlainHandle;
			}
		}
		TestTrue(TEXT("GameplayStatics finds exactly the two matching StaticMeshActors"),
		         FoundActors.Num() == 2 && bFoundFirst && bFoundSecond && !bFoundPlain);
	}
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareReferenceContainerManagedTest,
	"Finare.Unreal.ReferenceContainers.ManagedRoundTrip",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareReferenceContainerManagedTest::RunTest(const FString& Parameters)
{
	const int32 InitialWorldCount = FFinareCoreClrHost::Get().GetManagedWorldCount();
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	TestNotNull(TEXT("Managed reference container World exists"), World);
	if (World == nullptr)
	{
		return false;
	}
	World->AddToRoot();

	UFinareWorldSubsystem* Host = World->GetSubsystem<UFinareWorldSubsystem>();
	const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
	TestNotNull(TEXT("Managed reference container host exists"), Host);
	TestTrue(TEXT("Finare plugin is installed"), Plugin.IsValid());
	if (Host == nullptr || !Plugin.IsValid())
	{
		World->RemoveFromRoot();
		World->DestroyWorld(false);
		return false;
	}

	UFinareInteropFixture* Fixture = NewObject<UFinareInteropFixture>(World);
	AStaticMeshActor* FirstActor = World->SpawnActor<AStaticMeshActor>();
	AStaticMeshActor* SecondActor = World->SpawnActor<AStaticMeshActor>();
	AActor* NonMatchingActor = World->SpawnActor<AActor>();
	TestNotNull(TEXT("Managed fixture exists"), Fixture);
	TestNotNull(TEXT("First managed matching actor exists"), FirstActor);
	TestNotNull(TEXT("Second managed matching actor exists"), SecondActor);
	TestNotNull(TEXT("Managed nonmatching actor exists"), NonMatchingActor);
	if (Fixture == nullptr || FirstActor == nullptr || SecondActor == nullptr || NonMatchingActor == nullptr)
	{
		World->RemoveFromRoot();
		World->DestroyWorld(false);
		return false;
	}

	World->InitializeActorsForPlay(FURL());
	TestTrue(TEXT("Managed reference World initializes actors"), World->AreActorsInitialized());

	FFinareObjectHandleResult HandleResult;
	const FFinareObjectHandle FixtureHandle = Host->RegisterObject(Fixture, HandleResult);
	const FFinareObjectHandle TargetHandle = Host->RegisterObject(FirstActor, HandleResult);
	TestTrue(TEXT("Managed fixture handle is registered"), HandleResult.IsSuccess());
	if (FixtureHandle.IsNull() || TargetHandle.IsNull())
	{
		World->RemoveFromRoot();
		World->DestroyWorld(false);
		return false;
	}

	const FString Assembly = FPaths::Combine(
		Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo/Finare.Unreal.Demo.dll"));
	FString Error;
	const bool bStarted = Host->CreateManagedScript(
		8801,
		FixtureHandle,
		TargetHandle,
		Assembly,
		TEXT("Finare.Unreal.Demo.ReferenceCollectionGameplayDemoScript"),
		Error);
	TestTrue(*FString::Printf(TEXT("Managed reference sample starts: %s"), *Error), bStarted);
	if (bStarted)
	{
		TestEqual(TEXT("Managed sample observes both initial matching actors"), Fixture->Int32Value, 2);
		TestEqual(TEXT("Managed sample stores both actor identities"), Fixture->ActorArrayValue.Num(), 2);
		TestTrue(TEXT("Managed sample stores the first matching actor"), Fixture->ActorArrayValue.Contains(FirstActor));
		TestTrue(
			TEXT("Managed sample stores the second matching actor"), Fixture->ActorArrayValue.Contains(SecondActor));
		TestEqual(TEXT("Managed sample stores typed Actor class"), Fixture->ActorClassSetValue.Num(), 1);
		TestTrue(
			TEXT("Managed sample stores StaticMeshActor class"),
			Fixture->ActorClassSetValue.Contains(AStaticMeshActor::StaticClass()));
		TestEqual(TEXT("Managed sample stores typed soft object map"), Fixture->SoftObjectMapValue.Num(), 2);
		const TSoftObjectPtr<UObject>* NullSoftObject = Fixture->SoftObjectMapValue.Find(FName(TEXT("Null")));
		TestTrue(
			TEXT("Managed sample preserves null soft object path"),
			NullSoftObject != nullptr && NullSoftObject->IsNull());
		TestEqual(TEXT("Managed sample stores typed soft class paths"), Fixture->SoftClassArrayValue.Num(), 2);
		TestTrue(TEXT("Managed sample preserves null soft class path"),
		         Fixture->SoftClassArrayValue.Num() == 2 && Fixture->SoftClassArrayValue[1].IsNull());

		AStaticMeshActor* ThirdActor = World->SpawnActor<AStaticMeshActor>();
		TestNotNull(TEXT("New matching actor for managed reload exists"), ThirdActor);
		Fixture->Int32Value = -31337;
		int32 Generation = 0;
		const bool bReloaded = Host->ReloadManagedScript(
			8801,
			Assembly,
			TEXT("Finare.Unreal.Demo.ReferenceCollectionGameplayDemoScript"),
			Generation,
			Error);
		TestTrue(*FString::Printf(TEXT("Managed reference sample reloads: %s"), *Error), bReloaded);
		TestTrue(TEXT("Managed reference reload advances generation"), Generation > 0);
		TestEqual(TEXT("Reload observes the newly spawned matching actor"), Fixture->Int32Value, 3);
		TestEqual(TEXT("Reload stores all matching actor identities"), Fixture->ActorArrayValue.Num(), 3);
		TestTrue(TEXT("Reload retains the first matching actor"), Fixture->ActorArrayValue.Contains(FirstActor));
		TestTrue(TEXT("Reload retains the second matching actor"), Fixture->ActorArrayValue.Contains(SecondActor));
		TestTrue(
			TEXT("Reload stores the new matching actor"),
			ThirdActor != nullptr && Fixture->ActorArrayValue.Contains(ThirdActor));
		TestTrue(TEXT("Reload retains nonmatching actor exclusion"),
		         !Fixture->ActorArrayValue.Contains(NonMatchingActor));
		TestTrue(TEXT("Managed reference sample stops"), Host->DestroyManagedScript(8801, Error));
	}

	FFinareObjectHandleResult ReleaseResult;
	Host->ReleaseObject(TargetHandle, ReleaseResult);
	Host->ReleaseObject(FixtureHandle, ReleaseResult);
	World->RemoveFromRoot();
	World->DestroyWorld(false);
	TestEqual(
		TEXT("Managed reference World cleanup releases the CoreCLR context"),
		FFinareCoreClrHost::Get().GetManagedWorldCount(),
		InitialWorldCount);
	return true;
}
