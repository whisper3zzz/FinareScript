#if WITH_DEV_AUTOMATION_TESTS

#include "FinareWorldLibrary.h"
#include "FinareInteropFixture.h"

#include "Components/ActorComponent.h"
#include "Engine/World.h"
#include "GameFramework/Actor.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "Misc/AutomationTest.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareObjectFactoryTest,
	"Finare.Unreal.Objects.Factory",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareObjectFactoryTest::RunTest(const FString& Parameters)
{
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	UWorld* OtherWorld = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("Factory World exists"), World) ||
		!TestNotNull(TEXT("Foreign World exists"), OtherWorld))
	{
		return false;
	}
	World->AddToRoot();
	OtherWorld->AddToRoot();

	UObject* WorldOuter = NewObject<UFinareInteropFixture>(World);
	UObject* NativeObject = UFinareWorldLibrary::CreateObject(
		World,
		UFinareInteropFixture::StaticClass(),
		WorldOuter);
	TestNotNull(TEXT("Ordinary native UObject can be created"), NativeObject);
	TestTrue(
		TEXT("Created native UObject keeps the requested Outer"),
		NativeObject != nullptr && NativeObject->GetOuter() == WorldOuter);
	World->TimeSeconds = 17.5;
	TestTrue(
		TEXT("An ordinary UObject can be a World context through its Outer"),
		FMath::IsNearlyEqual(
			UFinareWorldLibrary::GetWorldTimeSeconds(WorldOuter),
			17.5f));

	UObject* ForeignOuter = NewObject<UFinareInteropFixture>(OtherWorld);
	TestNull(
		TEXT("Cross-World Outer is rejected"),
		UFinareWorldLibrary::CreateObject(
			World,
			UFinareInteropFixture::StaticClass(),
			ForeignOuter));
	TestNull(
		TEXT("Template Outer is rejected"),
		UFinareWorldLibrary::CreateObject(
			World,
			UFinareInteropFixture::StaticClass(),
			UObject::StaticClass()->GetDefaultObject()));
	TestNull(
		TEXT("Abstract Object base is rejected"),
		UFinareWorldLibrary::CreateObject(World, UObject::StaticClass(), nullptr));
	TestNull(
		TEXT("Actor class is not an ordinary Object class"),
		UFinareWorldLibrary::CreateObject(World, AActor::StaticClass(), nullptr));
	TestNull(
		TEXT("ActorComponent class is not an ordinary Object class"),
		UFinareWorldLibrary::CreateObject(
			World,
			UActorComponent::StaticClass(),
			nullptr));
	TestNull(
		TEXT("BlueprintFunctionLibrary class is not an ordinary Object class"),
		UFinareWorldLibrary::CreateObject(
			World,
			UBlueprintFunctionLibrary::StaticClass(),
			nullptr));
	TestNull(
		TEXT("UClass instances are not ordinary Objects"),
		UFinareWorldLibrary::CreateObject(World, UClass::StaticClass(), nullptr));

	OtherWorld->RemoveFromRoot();
	OtherWorld->DestroyWorld(false);
	World->RemoveFromRoot();
	World->DestroyWorld(false);
	return true;
}

#endif
