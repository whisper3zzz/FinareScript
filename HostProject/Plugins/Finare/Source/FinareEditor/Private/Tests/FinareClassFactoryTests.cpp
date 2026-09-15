#if WITH_DEV_AUTOMATION_TESTS

#include "FinareClassFactory.h"

#include "Engine/Blueprint.h"
#include "Engine/BlueprintGeneratedClass.h"
#include "Engine/Engine.h"
#include "Engine/World.h"
#include "EnhancedInputComponent.h"
#include "HAL/FileManager.h"
#include "HAL/PlatformProcess.h"
#include "InputAction.h"
#include "Kismet2/BlueprintEditorUtils.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "Misc/Paths.h"
#include "Misc/ScopeExit.h"
#include "PackageTools.h"
#include "FinareActor.h"
#include "FinareBlueprint.h"
#include "FinareClassBases.h"
#include "FinareCoreClrHost.h"
#include "FinareComponent.h"
#include "FinareInteropFixture.h"
#include "FinareProjectSetup.h"
#include "FinareWorldSubsystem.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"

namespace UE::Finare::ClassFactory::Tests
{
	FString ResolveConfiguredPath(const FString& Value)
	{
		FString Result = Value;
		Result.ReplaceInline(
			TEXT("{ProjectDir}"),
			*FPaths::ConvertRelativePathToFull(FPaths::ProjectDir()),
			ESearchCase::IgnoreCase);
		Result = FPaths::ConvertRelativePathToFull(Result);
		FPaths::NormalizeFilename(Result);
		return Result;
	}

	struct FScopedCreatedClassCleanup
	{
		FString DeploymentPath;
		TArray<uint8> OriginalDeployment;
		FString FirstPlayRequestPath;
		TArray<uint8> OriginalFirstPlayRequest;
		bool bHadFirstPlayRequest = false;
		TArray<FString> SourcePaths;
		TArray<FString> PackagePaths;

		~FScopedCreatedClassCleanup()
		{
			if (!DeploymentPath.IsEmpty() && !OriginalDeployment.IsEmpty())
			{
				FFileHelper::SaveArrayToFile(OriginalDeployment, *DeploymentPath);
			}
			if (!FirstPlayRequestPath.IsEmpty())
			{
				if (bHadFirstPlayRequest)
				{
					FFileHelper::SaveArrayToFile(
						OriginalFirstPlayRequest,
						*FirstPlayRequestPath);
				}
				else
				{
					IFileManager::Get().Delete(*FirstPlayRequestPath, false, true);
				}
			}
			for (const FString& SourcePath : SourcePaths)
			{
				IFileManager::Get().Delete(*SourcePath, false, true);
			}
			for (const FString& PackagePath : PackagePaths)
			{
				IFileManager::Get().Delete(*PackagePath, false, true);
			}
		}
	};

	template <typename TFactory>
	bool CreateAndVerify(
		FAutomationTestBase& Test,
		const FString& TypeName,
		EFinareClassKind ExpectedKind,
		FScopedCreatedClassCleanup* Cleanup)
	{
		const bool bSupportsFirstPlay =
			ExpectedKind == EFinareClassKind::Actor ||
			ExpectedKind == EFinareClassKind::ActorComponent;
		const FString FirstPlayRequestPath = FFinareProjectSetup::GetFirstPlayRequestPath(
			FPaths::ProjectDir());
		TArray<uint8> FirstPlayBefore;
		const bool bHadFirstPlayBefore = FFileHelper::LoadFileToArray(
			FirstPlayBefore,
			*FirstPlayRequestPath);
		const FString PackageName = FString::Printf(
			TEXT("/Game/UnifiedScriptAutomation/%s"),
			*TypeName);
		UPackage* Package = CreatePackage(*PackageName);
		TFactory* Factory = NewObject<TFactory>();
		UBlueprint* Blueprint = Cast<UBlueprint>(Factory->FactoryCreateNew(
			UBlueprint::StaticClass(),
			Package,
			*TypeName,
			RF_Public | RF_Standalone | RF_Transactional,
			nullptr,
			GWarn,
			TEXT("FinareClassFactoryAutomation")));
		Test.TestNotNull(TEXT("Script Class Blueprint is created"), Blueprint);
		if (Blueprint == nullptr || Blueprint->GeneratedClass == nullptr)
		{
			return false;
		}

		const UFinareBlueprint* UnifiedBlueprint =
			Cast<UFinareBlueprint>(Blueprint);
		Test.TestNotNull(
			TEXT("Generated proxy uses the product-owned Blueprint asset class"),
			UnifiedBlueprint);
		UFinareAsset* ScriptAsset = UnifiedBlueprint != nullptr
			                            ? UnifiedBlueprint->ScriptAsset.Get()
			                            : nullptr;
		if (ExpectedKind == EFinareClassKind::Actor)
		{
			Test.TestTrue(
				TEXT("Generated Actor is selectable as an ordinary Actor class"),
				Blueprint->GeneratedClass->IsChildOf(AActor::StaticClass()));
			Test.TestNotNull(
				TEXT("Generated Actor exposes an editable Blueprint property"),
				FindFProperty<FProperty>(Blueprint->GeneratedClass, TEXT("ManagedActorState")));
			Test.TestNotNull(
				TEXT("Generated Actor exposes a callable Blueprint function"),
				Blueprint->GeneratedClass->FindFunctionByName(TEXT("SetManagedActorState")));
			const AFinareActor* Defaults = Cast<AFinareActor>(
				Blueprint->GeneratedClass->GetDefaultObject());
			Test.TestNotNull(TEXT("Actor proxy has the expected native parent"), Defaults);
			Test.TestTrue(
				TEXT("Actor CDO and Blueprint proxy retain the same sidecar"),
				Defaults != nullptr && Defaults->ScriptComponent != nullptr &&
				Defaults->ScriptComponent->ScriptAsset == ScriptAsset);
		}
		else if (ExpectedKind == EFinareClassKind::ActorComponent)
		{
			Test.TestTrue(
				TEXT("Generated Component is attachable as an ordinary ActorComponent class"),
				Blueprint->GeneratedClass->IsChildOf(UActorComponent::StaticClass()));
			Test.TestNotNull(
				TEXT("Generated Component exposes an editable Blueprint property"),
				FindFProperty<FProperty>(Blueprint->GeneratedClass, TEXT("bStartAutomatically")));
			Test.TestNotNull(
				TEXT("Generated Component exposes a callable Blueprint function"),
				Blueprint->GeneratedClass->FindFunctionByName(TEXT("SetScriptStateInt32")));
			const UFinareComponent* Defaults = Cast<UFinareComponent>(
				Blueprint->GeneratedClass->GetDefaultObject());
			Test.TestNotNull(TEXT("Component proxy has the expected native parent"), Defaults);
			Test.TestTrue(
				TEXT("Component CDO and Blueprint proxy retain the same sidecar"),
				Defaults != nullptr && Defaults->ScriptAsset == ScriptAsset);
		}
		else if (ExpectedKind == EFinareClassKind::Object)
		{
			Test.TestTrue(
				TEXT("Generated Object is instantiable as an ordinary UObject class"),
				Blueprint->GeneratedClass->IsChildOf(UFinareObject::StaticClass()));
			Test.TestNotNull(
				TEXT("Generated Object has the expected native CDO"),
				Cast<UFinareObject>(Blueprint->GeneratedClass->GetDefaultObject()));
		}
		else if (ExpectedKind == EFinareClassKind::BlueprintFunctionLibrary)
		{
			Test.TestTrue(
				TEXT("Generated library is an ordinary Blueprint Function Library class"),
				Blueprint->GeneratedClass->IsChildOf(
					UFinareBlueprintFunctionLibrary::StaticClass()));
			Test.TestEqual(
				TEXT("Generated library uses the Function Library Blueprint kind"),
				Blueprint->BlueprintType,
				BPTYPE_FunctionLibrary);
		}
		Test.TestNotNull(TEXT("Generated proxy retains its Script Asset sidecar"), ScriptAsset);
		if (ScriptAsset == nullptr)
		{
			return false;
		}

		UClass* ExpectedTargetClass =
			ExpectedKind == EFinareClassKind::Actor
				? AFinareActor::StaticClass()
				: ExpectedKind == EFinareClassKind::ActorComponent
				? AActor::StaticClass()
				: ExpectedKind == EFinareClassKind::Object
				? UFinareObject::StaticClass()
				: UFinareBlueprintFunctionLibrary::StaticClass();
		Test.TestEqual(
			TEXT("Script Class pins the exact reviewed native target contract"),
			ScriptAsset->RequiredTargetClass.Get(),
			ExpectedTargetClass);
		Test.TestEqual(TEXT("Script Class kind is stable"), ScriptAsset->ScriptClassKind, ExpectedKind);
		Test.TestTrue(TEXT("Script Class ID is persistent"), ScriptAsset->ScriptClassId.IsValid());
		Test.TestEqual(
			TEXT("Generated class path is exact"),
			ScriptAsset->GeneratedClassPath.ToString(),
			Blueprint->GeneratedClass->GetPathName());
		Test.TestTrue(
			TEXT("Managed type identity includes the created class"),
			ScriptAsset->ScriptTypeName.EndsWith(TEXT(".") + TypeName));
		Test.TestTrue(
			TEXT("Declaration identity uses a stable project/class path"),
			ScriptAsset->DeclarationAssetPath.EndsWith(TEXT("/") + TypeName));

		if (bSupportsFirstPlay)
		{
			FFinareFirstPlayRequest FirstPlayRequest;
			FString FirstPlayError;
			Test.TestTrue(
				TEXT("Lifecycle Script Class writes its first-Play handoff"),
				FFinareProjectSetup::ReadFirstPlayRequest(
					FirstPlayRequestPath,
					FirstPlayRequest,
					FirstPlayError));
			Test.TestEqual(
				TEXT("First-Play handoff pins the same semantic class ID"),
				FirstPlayRequest.RequestId,
				FString::Printf(
					TEXT("us_%s"),
					*ScriptAsset->ScriptClassId.ToString(EGuidFormats::Digits).ToLower()));
			Test.TestEqual(
				TEXT("First-Play handoff pins the Blueprint proxy"),
				FirstPlayRequest.BlueprintAssetPath,
				Blueprint->GetPathName());
			Test.TestEqual(
				TEXT("First-Play handoff pins the Script Asset sidecar"),
				FirstPlayRequest.ScriptAssetPath,
				ScriptAsset->GetPathName());
			Test.TestTrue(
				TEXT("First-Play handoff waits for Build/Onboard"),
				FirstPlayRequest.IsBuildRequired());
		}
		else
		{
			TArray<uint8> FirstPlayAfter;
			const bool bHasFirstPlayAfter = FFileHelper::LoadFileToArray(
				FirstPlayAfter,
				*FirstPlayRequestPath);
			Test.TestEqual(
				TEXT("Non-lifecycle proxy preserves first-Play request existence"),
				bHasFirstPlayAfter,
				bHadFirstPlayBefore);
			Test.TestTrue(
				TEXT("Non-lifecycle proxy preserves first-Play request bytes"),
				!bHadFirstPlayBefore || FirstPlayAfter == FirstPlayBefore);
		}

		const FString SourcePath = ResolveConfiguredPath(ScriptAsset->SourcePath);
		if (Cleanup != nullptr)
		{
			Cleanup->SourcePaths.Add(SourcePath);
		}
		FString Source;
		Test.TestTrue(
			TEXT("Managed Script Class source is written"), FFileHelper::LoadFileToString(Source, *SourcePath));
		Test.TestTrue(
			TEXT("Managed source pins the same semantic class ID"),
			Source.Contains(FString::Printf(
				TEXT("ClassId = \"us_%s\""),
				*ScriptAsset->ScriptClassId.ToString(EGuidFormats::Digits).ToLower())));
		const TCHAR* ManagedKind =
			ExpectedKind == EFinareClassKind::Actor
				? TEXT("Actor")
				: ExpectedKind == EFinareClassKind::ActorComponent
				? TEXT("ActorComponent")
				: ExpectedKind == EFinareClassKind::Object
				? TEXT("Object")
				: TEXT("BlueprintFunctionLibrary");
		Test.TestTrue(
			TEXT("Managed source pins the reviewed proxy class kind"),
			Source.Contains(FString::Printf(
				TEXT("[UnrealScriptClass(UnrealScriptClassKind.%s,"),
				ManagedKind)));
		Test.TestTrue(
			TEXT("Managed source pins the exact generated Blueprint class path"),
			Source.Contains(FString::Printf(
				TEXT("GeneratedClassPath = \"%s\""),
				*Blueprint->GeneratedClass->GetPathName())));
		Test.TestTrue(
			TEXT("Managed source emits a Visual-compatible block body"),
			Source.Contains(TEXT("AddOne(int value)\n    {\n        return value + 1;\n    }")));
		if (ExpectedKind == EFinareClassKind::BlueprintFunctionLibrary)
		{
			Test.TestTrue(
				TEXT("Managed library emits a static class and function"),
				Source.Contains(TEXT("public static class")) &&
				Source.Contains(TEXT("public static int AddOne")));
			Test.TestFalse(
				TEXT("Managed library does not emit instance state"),
				Source.Contains(TEXT("[Expose(")));
		}
		else
		{
			Test.TestTrue(
				TEXT("Managed instance source exposes editable state"),
				Source.Contains(TEXT("[Expose(")));
			Test.TestTrue(
				TEXT("Managed instance source owns its default in a normal C# constructor"),
				Source.Contains(FString::Printf(
					TEXT("public %s()\n    {\n        StartValue = 1;"),
					*TypeName)));
			Test.TestTrue(
				TEXT("Managed instance scaffold demonstrates the common Blueprint state matrix"),
				Source.Contains(TEXT("public partial UnrealName StateTag")) &&
				Source.Contains(TEXT("public partial UnrealText DisplayText")) &&
				Source.Contains(TEXT("public partial UnrealObject? LinkedObject")) &&
				Source.Contains(TEXT("public partial UnrealClassHandle LinkedClass")) &&
				Source.Contains(TEXT("public partial UnrealVector SpawnOffset")) &&
				Source.Contains(TEXT("StateTag = new UnrealName(\"Demo\");")) &&
				Source.Contains(TEXT("DisplayText = new UnrealText(\"Ready\");")) &&
				Source.Contains(TEXT("SpawnOffset = new UnrealVector(100.0, 0.0, 0.0);")));
			if (ExpectedKind == EFinareClassKind::Object)
			{
				Test.TestTrue(
					TEXT("Managed Object uses generic object lifecycle hooks"),
					Source.Contains(TEXT(": UnrealObjectScript")) &&
					Source.Contains(TEXT("OnInitialized")) &&
					Source.Contains(TEXT("OnUpdate")));
				Test.TestFalse(
					TEXT("Managed Object does not advertise gameplay Blueprint events"),
					Source.Contains(TEXT("[UnrealBlueprintEvent]")));
			}
			else
			{
				const TCHAR* ExpectedBase = ExpectedKind == EFinareClassKind::Actor
					                            ? TEXT("UnrealActorScript")
					                            : TEXT("UnrealActorComponentScript");
				Test.TestTrue(
					TEXT("Managed lifecycle source uses its exact reviewed base"),
					Source.Contains(FString::Printf(TEXT(": %s"), ExpectedBase)));
				Test.TestTrue(TEXT("Managed source includes BeginPlay"), Source.Contains(TEXT("OnBeginPlay")));
				Test.TestTrue(TEXT("Managed source includes Tick"), Source.Contains(TEXT("OnTick")));
				Test.TestTrue(TEXT("Managed source includes EndPlay"), Source.Contains(TEXT("OnEndPlay")));
				Test.TestTrue(
					TEXT("Managed source includes an explicit-Super Blueprint event"),
					Source.Contains(TEXT("[UnrealBlueprintEvent]")) &&
					Source.Contains(TEXT("OnManagedActivated(int amount)")));
			}
		}

		const FString BlueprintFilename = FPackageName::LongPackageNameToFilename(
			PackageName,
			FPackageName::GetAssetPackageExtension());
		const FString ScriptFilename = FPackageName::LongPackageNameToFilename(
			ScriptAsset->GetOutermost()->GetName(),
			FPackageName::GetAssetPackageExtension());
		if (Cleanup != nullptr)
		{
			Cleanup->PackagePaths.Add(BlueprintFilename);
			Cleanup->PackagePaths.Add(ScriptFilename);
		}
		FSavePackageArgs SaveArguments;
		SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
		SaveArguments.SaveFlags = SAVE_None;
		Test.TestTrue(TEXT("Blueprint proxy package can be saved"), UPackage::SavePackage(
			              Package,
			              Blueprint,
			              *BlueprintFilename,
			              SaveArguments));
		Test.TestTrue(TEXT("Script Asset sidecar package can be saved"), UPackage::SavePackage(
			              ScriptAsset->GetOutermost(),
			              ScriptAsset,
			              *ScriptFilename,
			              SaveArguments));
		Test.TestTrue(TEXT("Saved Blueprint proxy exists"), IFileManager::Get().FileExists(*BlueprintFilename));
		Test.TestTrue(TEXT("Saved Script Asset sidecar exists"), IFileManager::Get().FileExists(*ScriptFilename));
		return true;
	}

	constexpr const TCHAR* PersistentActorType = TEXT("M13C0PersistentActor");
	constexpr const TCHAR* PersistentComponentType = TEXT("M13C0PersistentComponent");
	constexpr const TCHAR* PersistentPackageRoot =
		TEXT("/Game/UnifiedScriptAutomation");

	FString PersistentPackageName(const TCHAR* TypeName)
	{
		return FString::Printf(TEXT("%s/%s"), PersistentPackageRoot, TypeName);
	}

	FString PersistentObjectPath(const TCHAR* TypeName)
	{
		return FString::Printf(
			TEXT("%s/%s.%s"),
			PersistentPackageRoot,
			TypeName,
			TypeName);
	}

	FString PersistentSidecarPath(const TCHAR* TypeName)
	{
		return FString::Printf(
			TEXT("%s/SA_%s.SA_%s"),
			PersistentPackageRoot,
			TypeName,
			TypeName);
	}

	FString ProcessReceiptPath(const TCHAR* Stage)
	{
		return FPaths::ConvertRelativePathToFull(FPaths::Combine(
			FPaths::ProjectSavedDir(),
			TEXT("Finare/ScriptClassGoldenPath"),
			FString(Stage) + TEXT(".pid")));
	}

	bool WriteProcessReceipt(FAutomationTestBase& Test, const TCHAR* Stage)
	{
		const FString Path = ProcessReceiptPath(Stage);
		IFileManager::Get().MakeDirectory(*FPaths::GetPath(Path), true);
		return Test.TestTrue(
			*FString::Printf(TEXT("Writes %s process receipt"), Stage),
			FFileHelper::SaveStringToFile(
				FString::FromInt(static_cast<int32>(FPlatformProcess::GetCurrentProcessId())),
				*Path,
				FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM));
	}

	bool AugmentPersistentActorGameplayContracts(FAutomationTestBase& Test)
	{
		UFinareAsset* ActorAsset = LoadObject<UFinareAsset>(
			nullptr,
			*PersistentSidecarPath(PersistentActorType));
		if (!Test.TestNotNull(
			TEXT("Loads the persistent Actor sidecar for gameplay-contract authoring"),
			ActorAsset))
		{
			return false;
		}

		const FString SourcePath = ResolveConfiguredPath(ActorAsset->SourcePath);
		FString Source;
		if (!Test.TestTrue(
			TEXT("Loads the persistent Actor source for gameplay-contract authoring"),
			FFileHelper::LoadFileToString(Source, *SourcePath)))
		{
			return false;
		}

		if (!Source.Contains(TEXT("using Finare.Unreal.Generated;")))
		{
			const FString UsingAnchor = TEXT("using Finare.Unreal;\n");
			if (!Test.TestTrue(
				TEXT("Persistent Actor source exposes the Unreal namespace anchor"),
				Source.Contains(UsingAnchor)))
			{
				return false;
			}
			Source.ReplaceInline(
				*UsingAnchor,
				TEXT("using Finare.Unreal;\n")
				TEXT("using Finare.Unreal.Generated;\n"),
				ESearchCase::CaseSensitive);
		}

		if (!Source.Contains(TEXT("[UnrealInputAction(")))
		{
			const FString LifecycleAnchor =
				TEXT("    protected override void OnBeginPlay()\n");
			const FString GameplayContracts =
				TEXT("    private FinareComponent _component = null!;\n\n")
				TEXT("    [UnrealInputAction(\n")
				TEXT("        \"/Script/FinareRuntime.FinareInputActionFixture\",\n")
				TEXT("        TriggerEvent = UnrealInputTriggerEvent.Triggered,\n")
				TEXT("        ValueType = UnrealInputValueType.Axis1D,\n")
				TEXT("        ConsumeInput = true)]\n")
				TEXT("    public void OnManagedMove(float value)\n")
				TEXT("    {\n")
				TEXT("        _component.ManagedState = value == 0f ? \"Input:0\" : \"Input:nonzero\";\n")
				TEXT("    }\n\n")
				TEXT("    [Visual]\n")
				TEXT("    [UnrealEvent(\n")
				TEXT("        typeof(FinareComponent),\n")
				TEXT("        \"/Script/FinareRuntime.FinareComponent:OnManagedSignal\",\n")
				TEXT("        Source = UnrealEventSource.Host)]\n")
				TEXT("    private void OnManagedSignal(int value, UnrealObjectHandle context)\n")
				TEXT("    {\n")
				TEXT("        _component.ManagedState += \"|Event:\" + value;\n")
				TEXT("    }\n\n")
				TEXT("    [UnrealInterfaceFunction(\n")
				TEXT("        \"/Script/FinareRuntime.FinareInteropContract\",\n")
				TEXT("        \"TransformManagedValue\")]\n")
				TEXT("    public int TransformManagedValue(int value)\n")
				TEXT("    {\n")
				TEXT("        return value * 2;\n")
				TEXT("    }\n\n");
			if (!Test.TestTrue(
				TEXT("Persistent Actor source exposes the BeginPlay insertion anchor"),
				Source.Contains(LifecycleAnchor)))
			{
				return false;
			}
			Source.ReplaceInline(
				*LifecycleAnchor,
				*(GameplayContracts + LifecycleAnchor),
				ESearchCase::CaseSensitive);
		}

		if (!Source.Contains(TEXT("_component = new FinareComponent(")))
		{
			const FString StateInitialization =
				TEXT("        StartValue = AddOne(StartValue);\n");
			if (!Test.TestTrue(
				TEXT("Persistent Actor source exposes the BeginPlay body anchor"),
				Source.Contains(StateInitialization)))
			{
				return false;
			}
			Source.ReplaceInline(
				*StateInitialization,
				TEXT("        _component = new FinareComponent(HostHandle, Bridge);\n")
				TEXT("        StartValue = AddOne(StartValue);\n"),
				ESearchCase::CaseSensitive);
		}

		return Test.TestTrue(
			TEXT("Writes the persistent Actor gameplay contracts"),
			FFileHelper::SaveStringToFile(
				Source,
				*SourcePath,
				FFileHelper::EEncodingOptions::ForceUTF8WithoutBOM));
	}

	UFinareComponent* GetActorScriptComponent(UBlueprint* Blueprint)
	{
		const AFinareActor* Defaults = Blueprint != nullptr &&
		                               Blueprint->GeneratedClass != nullptr
			                               ? Cast<AFinareActor>(Blueprint->GeneratedClass->GetDefaultObject())
			                               : nullptr;
		return Defaults != nullptr ? Defaults->ScriptComponent : nullptr;
	}

	FString DescribeActorComponent(const UClass* OwnerClass, const UFinareComponent* Component)
	{
		return FString::Printf(
			TEXT("owner=%s classFlags=0x%08x component=%s componentName=%s archetype=%s scriptAsset=%s"),
			OwnerClass != nullptr ? *OwnerClass->GetPathName() : TEXT("<null>"),
			OwnerClass != nullptr ? static_cast<uint32>(OwnerClass->ClassFlags) : 0u,
			Component != nullptr ? *Component->GetPathName() : TEXT("<null>"),
			Component != nullptr ? *Component->GetName() : TEXT("<null>"),
			Component != nullptr && Component->GetArchetype() != nullptr
				? *Component->GetArchetype()->GetPathName()
				: TEXT("<null>"),
			Component != nullptr && Component->ScriptAsset != nullptr
				? *Component->ScriptAsset->GetPathName()
				: TEXT("<null>"));
	}

	bool VerifyActorComponentDefaults(
		FAutomationTestBase& Test,
		const TCHAR* Label,
		UClass* OwnerClass,
		UFinareComponent* Component,
		UFinareAsset* ExpectedScriptAsset,
		bool ExpectedStartAutomatically,
		bool ExpectedTickAutomatically,
		UFinareComponent* ExpectedArchetype)
	{
		const FString Prefix = FString::Printf(TEXT("%s: "), Label);
		if (!Test.TestNotNull(
			Prefix + TEXT("has an Actor ScriptComponent"),
			Component))
		{
			Test.AddError(DescribeActorComponent(OwnerClass, Component));
			return false;
		}

		bool bValid = true;
		bValid &= Test.TestTrue(
			Prefix + TEXT("generated class is compiled from a Blueprint"),
			OwnerClass != nullptr &&
			EnumHasAnyFlags(OwnerClass->ClassFlags, CLASS_CompiledFromBlueprint));
		bValid &= Test.TestEqual(
			Prefix + TEXT("uses the stable native component subobject name"),
			Component->GetFName(),
			FName(TEXT("UnifiedScript")));
		bValid &= Test.TestEqual(
			Prefix + TEXT("inherits the exact factory ScriptAsset"),
			Component->ScriptAsset.Get(),
			ExpectedScriptAsset);
		bValid &= Test.TestEqual(
			Prefix + TEXT("inherits bStartAutomatically"),
			Component->bStartAutomatically,
			ExpectedStartAutomatically);
		bValid &= Test.TestEqual(
			Prefix + TEXT("inherits bTickAutomatically"),
			Component->bTickAutomatically,
			ExpectedTickAutomatically);
		if (ExpectedArchetype != nullptr)
		{
			bValid &= Test.TestEqual(
				Prefix + TEXT("uses the expected parent component archetype"),
				static_cast<UObject*>(Component->GetArchetype()),
				static_cast<UObject*>(ExpectedArchetype));
		}
		if (!bValid)
		{
			Test.AddError(DescribeActorComponent(OwnerClass, Component));
		}
		return bValid;
	}
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareInheritedActorComponentDefaultsTest,
	"Finare.Unreal.ScriptClass.InheritedActorComponentDefaults",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareInheritedActorComponentDefaultsTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::ClassFactory::Tests;

	FScopedCreatedClassCleanup Cleanup;
	Cleanup.DeploymentPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectDir(),
		TEXT("../samples/Unreal/Finare.Unreal.Demo.deployment.json")));
	if (!FFileHelper::LoadFileToArray(Cleanup.OriginalDeployment, *Cleanup.DeploymentPath))
	{
		AddError(TEXT("Cannot snapshot the deployment manifest before the inherited defaults test."));
		return false;
	}
	Cleanup.FirstPlayRequestPath = FFinareProjectSetup::GetFirstPlayRequestPath(
		FPaths::ProjectDir());
	Cleanup.bHadFirstPlayRequest = FFileHelper::LoadFileToArray(
		Cleanup.OriginalFirstPlayRequest,
		*Cleanup.FirstPlayRequestPath);

	const FString Suffix = FGuid::NewGuid().ToString(EGuidFormats::Digits).Left(12);
	const FString BaseName = TEXT("M13C3InheritedActor_") + Suffix;
	const FString ChildName = BaseName + TEXT("_Child");
	const FString GrandchildName = BaseName + TEXT("_Grandchild");
	const FString BasePackageName = PersistentPackageName(*BaseName);
	const FString ChildPackageName = PersistentPackageName(*ChildName);
	const FString GrandchildPackageName = PersistentPackageName(*GrandchildName);
	const FString SidecarPackageName = FString::Printf(
		TEXT("%s/SA_%s"),
		PersistentPackageRoot,
		*BaseName);

	auto AddPackagePath = [&Cleanup](const FString& PackageName)
	{
		FString Filename = FPackageName::LongPackageNameToFilename(
			PackageName,
			FPackageName::GetAssetPackageExtension());
		Filename = FPaths::ConvertRelativePathToFull(Filename);
		FPaths::NormalizeFilename(Filename);
		Cleanup.PackagePaths.AddUnique(Filename);
	};
	// Register every owned package before any factory/Blueprint operation so a
	// partial creation still cleans only this test's GUID-named files.
	AddPackagePath(BasePackageName);
	AddPackagePath(ChildPackageName);
	AddPackagePath(GrandchildPackageName);
	AddPackagePath(SidecarPackageName);
	const TArray<FString> OwnedPackageNames = {
		BasePackageName,
		ChildPackageName,
		GrandchildPackageName,
		SidecarPackageName
	};
	ON_SCOPE_EXIT
	{
		TArray<UPackage*> PackagesToUnloadAtScopeExit;
		for (const FString& PackageName : OwnedPackageNames)
		{
			if (UPackage* Package = FindPackage(nullptr, *PackageName))
			{
				Package->SetDirtyFlag(false);
				PackagesToUnloadAtScopeExit.Add(Package);
			}
		}
		if (PackagesToUnloadAtScopeExit.Num() > 0)
		{
			UPackageTools::UnloadPackages(PackagesToUnloadAtScopeExit);
		}
	};

	UPackage* BasePackage = CreatePackage(*BasePackageName);
	UFinareActorClassFactory* Factory = NewObject<UFinareActorClassFactory>();
	UBlueprint* BaseBlueprint = BasePackage != nullptr && Factory != nullptr
		                            ? Cast<UBlueprint>(Factory->FactoryCreateNew(
			                            UBlueprint::StaticClass(),
			                            BasePackage,
			                            FName(*BaseName),
			                            RF_Public | RF_Standalone | RF_Transactional,
			                            nullptr,
			                            GWarn,
			                            TEXT("FinareInheritedActorComponentDefaults")))
		                            : nullptr;
	if (!TestNotNull(TEXT("Factory creates the inherited-defaults Actor Blueprint"), BaseBlueprint) ||
		!TestNotNull(
			TEXT("Factory-created Actor Blueprint has a generated class"),
			BaseBlueprint != nullptr ? BaseBlueprint->GeneratedClass.Get() : nullptr))
	{
		return false;
	}

	UFinareBlueprint* UnifiedBaseBlueprint = Cast<UFinareBlueprint>(BaseBlueprint);
	UFinareAsset* ScriptAsset = UnifiedBaseBlueprint != nullptr
		                            ? UnifiedBaseBlueprint->ScriptAsset.Get()
		                            : nullptr;
	if (!TestNotNull(TEXT("Factory-created Actor retains its ScriptAsset"), ScriptAsset))
	{
		return false;
	}
	Cleanup.SourcePaths.AddUnique(ResolveConfiguredPath(ScriptAsset->SourcePath));
	AddPackagePath(ScriptAsset->GetOutermost()->GetName());

	AFinareActor* BaseCDO = Cast<AFinareActor>(
		BaseBlueprint->GeneratedClass->GetDefaultObject());
	UFinareComponent* BaseComponent = BaseCDO != nullptr
		                                  ? BaseCDO->ScriptComponent
		                                  : nullptr;
	if (!TestNotNull(TEXT("Factory-created Actor has a native ScriptComponent CDO"), BaseComponent))
	{
		AddError(DescribeActorComponent(BaseBlueprint->GeneratedClass, BaseComponent));
		return false;
	}
	const bool InitialStartAutomatically = BaseComponent->bStartAutomatically;
	const bool InitialTickAutomatically = BaseComponent->bTickAutomatically;
	VerifyActorComponentDefaults(
		*this,
		TEXT("Base CDO"),
		BaseBlueprint->GeneratedClass,
		BaseComponent,
		ScriptAsset,
		InitialStartAutomatically,
		InitialTickAutomatically,
		nullptr);

	auto EditBoolDefault = [this](
		UFinareComponent& Component,
		UBlueprint& Blueprint,
		const TCHAR* PropertyName,
		bool& Value) -> bool
	{
		FBoolProperty* Property = FindFProperty<FBoolProperty>(Component.GetClass(), PropertyName);
		if (!this->TestNotNull(
			*FString::Printf(TEXT("Finds editable %s property"), PropertyName),
			Property))
		{
			return false;
		}
		const bool NewValue = !Property->GetPropertyValue_InContainer(&Component);
		Component.Modify();
		Component.PreEditChange(Property);
		Property->SetPropertyValue_InContainer(&Component, NewValue);
		FPropertyChangedEvent PropertyChanged(Property);
		Component.PostEditChangeProperty(PropertyChanged);
		FBlueprintEditorUtils::MarkBlueprintAsModified(&Blueprint);
		Blueprint.MarkPackageDirty();
		Value = NewValue;
		return this->TestTrue(
			*FString::Printf(TEXT("Property notification writes %s to the base CDO"), PropertyName),
			Property->GetPropertyValue_InContainer(&Component) == NewValue);
	};
	bool ExpectedStartAutomatically = InitialStartAutomatically;
	bool ExpectedTickAutomatically = InitialTickAutomatically;
	bool bEditedStart = EditBoolDefault(
		*BaseComponent,
		*BaseBlueprint,
		TEXT("bStartAutomatically"),
		ExpectedStartAutomatically);
	BaseComponent = GetActorScriptComponent(BaseBlueprint);
	bEditedStart &= BaseComponent != nullptr;
	bool bEditedTick = BaseComponent != nullptr && EditBoolDefault(
		*BaseComponent,
		*BaseBlueprint,
		TEXT("bTickAutomatically"),
		ExpectedTickAutomatically);
	BaseComponent = GetActorScriptComponent(BaseBlueprint);
	TestTrue(
		TEXT("Edits the base Actor component defaults through property notifications"), bEditedStart && bEditedTick);
	if (!bEditedStart || !bEditedTick ||
		!TestNotNull(TEXT("Re-reads the base CDO after property notifications"), BaseComponent))
	{
		return false;
	}
	TestEqual(
		TEXT("Base CDO bStartAutomatically changes before Compile"),
		BaseComponent->bStartAutomatically,
		ExpectedStartAutomatically);
	TestEqual(
		TEXT("Base CDO bTickAutomatically changes before Compile"),
		BaseComponent->bTickAutomatically,
		ExpectedTickAutomatically);
	FKismetEditorUtilities::CompileBlueprint(BaseBlueprint);
	TestEqual(
		TEXT("Compiles the factory-created base Blueprint after default edits"),
		BaseBlueprint->Status,
		BS_UpToDate);
	BaseComponent = GetActorScriptComponent(BaseBlueprint);
	if (!TestNotNull(TEXT("Re-reads the factory base CDO after compiling edited defaults"), BaseComponent))
	{
		AddError(DescribeActorComponent(BaseBlueprint->GeneratedClass, BaseComponent));
		return false;
	}
	TestNotEqual(
		TEXT("bStartAutomatically is a non-default inherited value"), ExpectedStartAutomatically,
		InitialStartAutomatically);
	TestNotEqual(
		TEXT("bTickAutomatically is a non-default inherited value"), ExpectedTickAutomatically,
		InitialTickAutomatically);
	VerifyActorComponentDefaults(
		*this,
		TEXT("Edited base CDO"),
		BaseBlueprint->GeneratedClass,
		BaseComponent,
		ScriptAsset,
		ExpectedStartAutomatically,
		ExpectedTickAutomatically,
		nullptr);

	auto CreateOrdinaryChild = [this](
		UClass* ParentClass,
		const FString& PackageName,
		const FString& AssetName,
		UPackage*& OutPackage) -> UBlueprint*
	{
		OutPackage = CreatePackage(*PackageName);
		UBlueprint* Blueprint = OutPackage != nullptr
			                        ? FKismetEditorUtilities::CreateBlueprint(
				                        ParentClass,
				                        OutPackage,
				                        FName(*AssetName),
				                        BPTYPE_Normal,
				                        UBlueprint::StaticClass(),
				                        UBlueprintGeneratedClass::StaticClass(),
				                        FName(TEXT("FinareInheritedActorComponentDefaults")))
			                        : nullptr;
		if (!TestNotNull(*FString::Printf(TEXT("Creates ordinary Blueprint %s"), *AssetName), Blueprint))
		{
			return nullptr;
		}
		FKismetEditorUtilities::CompileBlueprint(Blueprint);
		this->TestEqual(
			*FString::Printf(TEXT("Compiles ordinary Blueprint %s"), *AssetName),
			Blueprint->Status,
			BS_UpToDate);
		return Blueprint;
	};

	UPackage* ChildPackage = nullptr;
	UBlueprint* ChildBlueprint = CreateOrdinaryChild(
		BaseBlueprint->GeneratedClass,
		ChildPackageName,
		ChildName,
		ChildPackage);
	UPackage* GrandchildPackage = nullptr;
	UBlueprint* GrandchildBlueprint = ChildBlueprint != nullptr
		                                  ? CreateOrdinaryChild(
			                                  ChildBlueprint->GeneratedClass,
			                                  GrandchildPackageName,
			                                  GrandchildName,
			                                  GrandchildPackage)
		                                  : nullptr;
	if (!TestNotNull(TEXT("Creates the ordinary Blueprint child"), ChildBlueprint) ||
		!TestNotNull(TEXT("Creates the ordinary Blueprint grandchild"), GrandchildBlueprint))
	{
		return false;
	}

	AFinareActor* ChildCDO = Cast<AFinareActor>(
		ChildBlueprint->GeneratedClass->GetDefaultObject());
	AFinareActor* GrandchildCDO = Cast<AFinareActor>(
		GrandchildBlueprint->GeneratedClass->GetDefaultObject());
	UFinareComponent* ChildComponent = ChildCDO != nullptr
		                                   ? ChildCDO->ScriptComponent
		                                   : nullptr;
	UFinareComponent* GrandchildComponent = GrandchildCDO != nullptr
		                                        ? GrandchildCDO->ScriptComponent
		                                        : nullptr;
	bool bValid = VerifyActorComponentDefaults(
		*this,
		TEXT("Child CDO"),
		ChildBlueprint->GeneratedClass,
		ChildComponent,
		ScriptAsset,
		ExpectedStartAutomatically,
		ExpectedTickAutomatically,
		BaseComponent);
	bValid &= VerifyActorComponentDefaults(
		*this,
		TEXT("Grandchild CDO"),
		GrandchildBlueprint->GeneratedClass,
		GrandchildComponent,
		ScriptAsset,
		ExpectedStartAutomatically,
		ExpectedTickAutomatically,
		ChildComponent);
	bValid &= this->TestTrue(
		TEXT("Base, child, and grandchild CDO components are distinct objects"),
		BaseComponent != ChildComponent &&
		BaseComponent != GrandchildComponent &&
		ChildComponent != GrandchildComponent);

	const FString BaseObjectPath = BaseBlueprint->GetPathName();
	const FString ChildObjectPath = ChildBlueprint->GetPathName();
	const FString GrandchildObjectPath = GrandchildBlueprint->GetPathName();
	const FString ScriptAssetObjectPath = ScriptAsset->GetPathName();

	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("Creates a transient world for an inherited Actor instance"), World))
	{
		return false;
	}
	World->AddToRoot();
	FWorldContext* WorldContext = nullptr;
	if (GEngine != nullptr)
	{
		WorldContext = &GEngine->CreateNewWorldContext(EWorldType::Game);
		WorldContext->SetCurrentWorld(World);
	}
	AFinareActor* Instance = Cast<AFinareActor>(
		World->SpawnActor(ChildBlueprint->GeneratedClass));
	UFinareComponent* InstanceComponent = Instance != nullptr
		                                      ? Instance->ScriptComponent
		                                      : nullptr;
	bValid &= this->TestNotNull(TEXT("Spawns an ordinary child Actor instance"), Instance);
	bValid &= VerifyActorComponentDefaults(
		*this,
		TEXT("Child Actor instance"),
		ChildBlueprint->GeneratedClass,
		InstanceComponent,
		ScriptAsset,
		ExpectedStartAutomatically,
		ExpectedTickAutomatically,
		ChildComponent);
	bValid &= this->TestTrue(
		TEXT("Actor instance component is distinct from CDO components"),
		InstanceComponent != nullptr &&
		InstanceComponent != BaseComponent &&
		InstanceComponent != ChildComponent &&
		InstanceComponent != GrandchildComponent);
	if (Instance != nullptr)
	{
		Instance->Destroy();
	}
	if (WorldContext != nullptr && GEngine != nullptr)
	{
		GEngine->DestroyWorldContext(World);
		WorldContext = nullptr;
	}
	World->RemoveFromRoot();
	World->DestroyWorld(false);

	auto SaveAssetPackage = [this](
		UPackage* Package,
		UObject* Asset,
		const FString& PackageName,
		const TCHAR* Label) -> bool
	{
		if (Package == nullptr || Asset == nullptr)
		{
			return false;
		}
		const FString Filename = FPackageName::LongPackageNameToFilename(
			PackageName,
			FPackageName::GetAssetPackageExtension());
		FSavePackageArgs SaveArguments;
		SaveArguments.TopLevelFlags = RF_Public | RF_Standalone;
		SaveArguments.SaveFlags = SAVE_None;
		const bool bSaved = UPackage::SavePackage(
			Package,
			Asset,
			*Filename,
			SaveArguments);
		this->TestTrue(*FString::Printf(TEXT("Saves %s"), Label), bSaved);
		return bSaved;
	};
	bValid &= SaveAssetPackage(BasePackage, BaseBlueprint, BasePackageName, TEXT("base Blueprint package"));
	bValid &= SaveAssetPackage(ChildPackage, ChildBlueprint, ChildPackageName, TEXT("child Blueprint package"));
	bValid &= SaveAssetPackage(
		GrandchildPackage,
		GrandchildBlueprint,
		GrandchildPackageName,
		TEXT("grandchild Blueprint package"));
	bValid &= SaveAssetPackage(
		ScriptAsset->GetOutermost(),
		ScriptAsset,
		ScriptAsset->GetOutermost()->GetName(),
		TEXT("factory ScriptAsset package"));

	TArray<UPackage*> PackagesToUnload;
	PackagesToUnload.AddUnique(GrandchildPackage);
	PackagesToUnload.AddUnique(ChildPackage);
	PackagesToUnload.AddUnique(BasePackage);
	PackagesToUnload.AddUnique(ScriptAsset->GetOutermost());
	for (UPackage* Package : PackagesToUnload)
	{
		if (Package != nullptr)
		{
			Package->SetDirtyFlag(false);
		}
	}
	const TWeakObjectPtr<UBlueprint> WeakBaseBlueprint = BaseBlueprint;
	const TWeakObjectPtr<UBlueprint> WeakChildBlueprint = ChildBlueprint;
	const TWeakObjectPtr<UBlueprint> WeakGrandchildBlueprint = GrandchildBlueprint;
	const TWeakObjectPtr<UFinareAsset> WeakScriptAsset = ScriptAsset;
	bValid &= this->TestTrue(
		TEXT("Unloads saved factory and inherited Blueprint packages"),
		UPackageTools::UnloadPackages(PackagesToUnload));
	bValid &= this->TestTrue(
		TEXT("Unloading invalidates the four saved factory-owned assets"),
		!WeakBaseBlueprint.IsValid() &&
		!WeakChildBlueprint.IsValid() &&
		!WeakGrandchildBlueprint.IsValid() &&
		!WeakScriptAsset.IsValid());

	UBlueprint* ReloadedBaseBlueprint = LoadObject<UBlueprint>(nullptr, *BaseObjectPath);
	UBlueprint* ReloadedChildBlueprint = LoadObject<UBlueprint>(nullptr, *ChildObjectPath);
	UBlueprint* ReloadedGrandchildBlueprint = LoadObject<UBlueprint>(nullptr, *GrandchildObjectPath);
	UFinareAsset* ReloadedScriptAsset = LoadObject<UFinareAsset>(
		nullptr,
		*ScriptAssetObjectPath);
	bValid &= this->TestNotNull(TEXT("Loads the saved factory Blueprint"), ReloadedBaseBlueprint);
	bValid &= this->TestNotNull(TEXT("Loads the saved ordinary child Blueprint"), ReloadedChildBlueprint);
	bValid &= this->TestNotNull(
		TEXT("Loads the saved ordinary grandchild Blueprint"),
		ReloadedGrandchildBlueprint);
	bValid &= this->TestNotNull(TEXT("Loads the saved factory ScriptAsset"), ReloadedScriptAsset);
	if (ReloadedBaseBlueprint != nullptr && ReloadedChildBlueprint != nullptr &&
		ReloadedGrandchildBlueprint != nullptr && ReloadedScriptAsset != nullptr)
	{
		UFinareComponent* ReloadedBaseComponent = GetActorScriptComponent(ReloadedBaseBlueprint);
		UFinareComponent* ReloadedChildComponent = GetActorScriptComponent(ReloadedChildBlueprint);
		UFinareComponent* ReloadedGrandchildComponent =
			GetActorScriptComponent(ReloadedGrandchildBlueprint);
		bValid &= VerifyActorComponentDefaults(
			*this,
			TEXT("Reloaded base CDO"),
			ReloadedBaseBlueprint->GeneratedClass,
			ReloadedBaseComponent,
			ReloadedScriptAsset,
			ExpectedStartAutomatically,
			ExpectedTickAutomatically,
			nullptr);
		bValid &= VerifyActorComponentDefaults(
			*this,
			TEXT("Reloaded child CDO"),
			ReloadedChildBlueprint->GeneratedClass,
			ReloadedChildComponent,
			ReloadedScriptAsset,
			ExpectedStartAutomatically,
			ExpectedTickAutomatically,
			ReloadedBaseComponent);
		bValid &= VerifyActorComponentDefaults(
			*this,
			TEXT("Reloaded grandchild CDO"),
			ReloadedGrandchildBlueprint->GeneratedClass,
			ReloadedGrandchildComponent,
			ReloadedScriptAsset,
			ExpectedStartAutomatically,
			ExpectedTickAutomatically,
			ReloadedChildComponent);
		bValid &= this->TestTrue(
			TEXT("Reloaded CDO components remain distinct"),
			ReloadedBaseComponent != ReloadedChildComponent &&
			ReloadedBaseComponent != ReloadedGrandchildComponent &&
			ReloadedChildComponent != ReloadedGrandchildComponent);
	}
	return bValid && !HasAnyErrors();
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinareClassFactoryContractTest,
	"Finare.Unreal.ScriptClass.FactoryContract",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinareClassFactoryContractTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::ClassFactory::Tests;
	const UFinareActorClassFactory* ActorFactory =
		GetDefault<UFinareActorClassFactory>();
	const UFinareActorComponentClassFactory* ComponentFactory =
		GetDefault<UFinareActorComponentClassFactory>();
	const UFinareObjectClassFactory* ObjectFactory =
		GetDefault<UFinareObjectClassFactory>();
	const UFinareBlueprintFunctionLibraryClassFactory* LibraryFactory =
		GetDefault<UFinareBlueprintFunctionLibraryClassFactory>();
	TestFalse(
		TEXT("Actor Script Class is hidden from create-then-rename menus"),
		ActorFactory->ShouldShowInNewMenu());
	TestFalse(
		TEXT("Component Script Class is hidden from create-then-rename menus"),
		ComponentFactory->ShouldShowInNewMenu());
	TestFalse(
		TEXT("Object Script Class is hidden from create-then-rename menus"),
		ObjectFactory->ShouldShowInNewMenu());
	TestFalse(
		TEXT("Function Library Script Class is hidden from create-then-rename menus"),
		LibraryFactory->ShouldShowInNewMenu());
	FScopedCreatedClassCleanup Cleanup;
	Cleanup.DeploymentPath = FPaths::ConvertRelativePathToFull(FPaths::Combine(
		FPaths::ProjectDir(),
		TEXT("../samples/Unreal/Finare.Unreal.Demo.deployment.json")));
	if (!FFileHelper::LoadFileToArray(Cleanup.OriginalDeployment, *Cleanup.DeploymentPath))
	{
		AddError(TEXT("Cannot snapshot the deployment manifest before the Script Class factory test."));
		return false;
	}
	Cleanup.FirstPlayRequestPath = FFinareProjectSetup::GetFirstPlayRequestPath(
		FPaths::ProjectDir());
	Cleanup.bHadFirstPlayRequest = FFileHelper::LoadFileToArray(
		Cleanup.OriginalFirstPlayRequest,
		*Cleanup.FirstPlayRequestPath);

	const FString Suffix = FGuid::NewGuid()
	                       .ToString(EGuidFormats::Digits)
	                       .Left(12);
	const bool bActor = CreateAndVerify<UFinareActorClassFactory>(
		*this,
		TEXT("M13Actor_") + Suffix,
		EFinareClassKind::Actor,
		&Cleanup);
	const bool bComponent = CreateAndVerify<UFinareActorComponentClassFactory>(
		*this,
		TEXT("M13Component_") + Suffix,
		EFinareClassKind::ActorComponent,
		&Cleanup);
	const bool bObject = CreateAndVerify<UFinareObjectClassFactory>(
		*this,
		TEXT("M13Object_") + Suffix,
		EFinareClassKind::Object,
		&Cleanup);
	const bool bLibrary =
		CreateAndVerify<UFinareBlueprintFunctionLibraryClassFactory>(
			*this,
			TEXT("M13Library_") + Suffix,
			EFinareClassKind::BlueprintFunctionLibrary,
			&Cleanup);
	return bActor && bComponent && bObject && bLibrary;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinarePersistentClassCreateTest,
	"Finare.Integration.ScriptClass.CreatePersistent",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinarePersistentClassCreateTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::ClassFactory::Tests;
	const bool bActor = CreateAndVerify<UFinareActorClassFactory>(
		*this,
		PersistentActorType,
		EFinareClassKind::Actor,
		nullptr);
	const bool bComponent = CreateAndVerify<UFinareActorComponentClassFactory>(
		*this,
		PersistentComponentType,
		EFinareClassKind::ActorComponent,
		nullptr);
	const bool bGameplayContracts = bActor &&
		AugmentPersistentActorGameplayContracts(*this);
	return bActor && bComponent && bGameplayContracts &&
		WriteProcessReceipt(*this, TEXT("create"));
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FFinarePersistentClassReloadLifecycleTest,
	"Finare.Integration.ScriptClass.ReloadLifecycle",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FFinarePersistentClassReloadLifecycleTest::RunTest(
	const FString& Parameters)
{
	using namespace UE::Finare::ClassFactory::Tests;
	FString CreateProcessText;
	if (!TestTrue(
		TEXT("Reads the first-process creation receipt"),
		FFileHelper::LoadFileToString(
			CreateProcessText,
			*ProcessReceiptPath(TEXT("create")))))
	{
		return false;
	}
	CreateProcessText.TrimStartAndEndInline();
	TestNotEqual(
		TEXT("Reload verification runs in a different Editor process"),
		FCString::Atoi(*CreateProcessText),
		static_cast<int32>(FPlatformProcess::GetCurrentProcessId()));

	UBlueprint* ActorBlueprint = LoadObject<UBlueprint>(
		nullptr,
		*PersistentObjectPath(PersistentActorType));
	UBlueprint* ComponentBlueprint = LoadObject<UBlueprint>(
		nullptr,
		*PersistentObjectPath(PersistentComponentType));
	if (!TestNotNull(TEXT("Reloads the saved Actor Blueprint"), ActorBlueprint) ||
		!TestNotNull(TEXT("Reloads the saved Component Blueprint"), ComponentBlueprint) ||
		!TestNotNull(TEXT("Reloaded Actor Blueprint has a generated class"),
		             ActorBlueprint != nullptr ? ActorBlueprint->GeneratedClass.Get() : nullptr) ||
		!TestNotNull(TEXT("Reloaded Component Blueprint has a generated class"),
		             ComponentBlueprint != nullptr ? ComponentBlueprint->GeneratedClass.Get() : nullptr))
	{
		return false;
	}

	UFinareAsset* ActorAsset = LoadObject<UFinareAsset>(
		nullptr,
		*PersistentSidecarPath(PersistentActorType));
	UFinareAsset* ComponentAsset = LoadObject<UFinareAsset>(
		nullptr,
		*PersistentSidecarPath(PersistentComponentType));
	if (!TestNotNull(TEXT("Reloads the saved Actor Script Asset"), ActorAsset) ||
		!TestNotNull(TEXT("Reloads the saved Component Script Asset"), ComponentAsset))
	{
		return false;
	}

	TestTrue(
		TEXT("Reloaded Actor CDO retains the exact Script Asset"),
		GetActorScriptComponent(ActorBlueprint) != nullptr &&
		GetActorScriptComponent(ActorBlueprint)->ScriptAsset == ActorAsset);
	const UFinareComponent* ComponentDefaults =
		Cast<UFinareComponent>(ComponentBlueprint->GeneratedClass->GetDefaultObject());
	TestTrue(
		TEXT("Reloaded Component CDO retains the exact Script Asset"),
		ComponentDefaults != nullptr && ComponentDefaults->ScriptAsset == ComponentAsset);

	const UFinareBlueprintGeneratedClass* ActorGeneratedClass =
		Cast<UFinareBlueprintGeneratedClass>(ActorBlueprint->GeneratedClass);
	if (!TestNotNull(
		TEXT("Reloaded Actor retains the product GeneratedClass"),
		ActorGeneratedClass))
	{
		return false;
	}
	TestEqual(
		TEXT("Reloaded Actor retains one Enhanced Input contract"),
		ActorGeneratedClass->ManagedInputActionBindings.Num(),
		1);
	TestEqual(
		TEXT("Reloaded Actor retains one delegate contract"),
		ActorGeneratedClass->ManagedDelegateBindings.Num(),
		1);
	TestEqual(
		TEXT("Reloaded Actor retains one interface contract"),
		ActorGeneratedClass->ManagedInterfaceBindings.Num(),
		1);
	TestTrue(
		TEXT("Reloaded Actor implements the native interface"),
		ActorBlueprint->GeneratedClass->ImplementsInterface(
			UFinareInteropContract::StaticClass()));
	const FFinareManagedFunctionBinding* ReloadedInterfaceBinding =
		ActorGeneratedClass->ManagedFunctionBindings.Find(
			TEXT("TransformManagedValue"));
	TestTrue(
		TEXT("Reloaded Actor retains the interface event-entry contract"),
		ReloadedInterfaceBinding != nullptr &&
		ReloadedInterfaceBinding->bInterfaceFunction);
	TestTrue(
		TEXT("Reloaded Actor restores the managed interface dispatcher"),
		ActorGeneratedClass->IsManagedFunctionBound(
			TEXT("TransformManagedValue")));

	const int32 InitialManagedWorldCount =
		FFinareCoreClrHost::Get().GetManagedWorldCount();
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!TestNotNull(TEXT("Creates the second-process gameplay World"), World))
	{
		return false;
	}
	World->AddToRoot();
	World->InitializeActorsForPlay(FURL());
	TestTrue(
		TEXT("Second-process gameplay World enables Actor ProcessEvent dispatch"),
		World->AreActorsInitialized());
	UFinareWorldSubsystem* Host =
		World->GetSubsystem<UFinareWorldSubsystem>();
	if (!TestNotNull(TEXT("Gameplay World owns a managed host"), Host))
	{
		World->RemoveFromRoot();
		World->DestroyWorld(false);
		return false;
	}

	AFinareActor* ScriptActor = Cast<AFinareActor>(
		World->SpawnActor(ActorBlueprint->GeneratedClass));
	AActor* ComponentOwner = World->SpawnActor<AActor>();
	UFinareComponent* AttachedComponent = ComponentOwner != nullptr
		                                      ? NewObject<UFinareComponent>(
			                                      ComponentOwner,
			                                      ComponentBlueprint->GeneratedClass)
		                                      : nullptr;
	if (!TestNotNull(TEXT("Spawns the reloaded Script Actor class"), ScriptActor) ||
		!TestNotNull(TEXT("Spawns a Component owner"), ComponentOwner) ||
		!TestNotNull(TEXT("Attaches the reloaded Script Component class"), AttachedComponent))
	{
		World->RemoveFromRoot();
		World->DestroyWorld(false);
		return false;
	}
	TestTrue(
		TEXT("First persistent Actor instance retains the managed interface dispatcher"),
		ActorGeneratedClass->IsManagedFunctionBound(
			TEXT("TransformManagedValue")));
	UFunction* OwnedInterfaceFunction =
		ActorGeneratedClass->FindFunctionByName(
			TEXT("TransformManagedValue"),
			EIncludeSuperFlag::ExcludeSuper);
	TestNotNull(
		TEXT("Persistent generated class owns the interface function"),
		OwnedInterfaceFunction);
	ComponentOwner->AddInstanceComponent(AttachedComponent);
	AttachedComponent->RegisterComponentWithWorld(World);

	UFinareComponent* ActorComponent = ScriptActor->ScriptComponent;
	if (!TestNotNull(TEXT("Reloaded Script Actor owns its host component"), ActorComponent))
	{
		World->RemoveFromRoot();
		World->DestroyWorld(false);
		return false;
	}
	FString IdentityError;
	TestTrue(
		*FString::Printf(TEXT("Actor generated-class identity reloads exactly: %s"), *IdentityError),
		ActorAsset->ValidateGeneratedClassIdentity(
			ActorComponent,
			ScriptActor,
			IdentityError));
	IdentityError.Reset();
	TestTrue(
		*FString::Printf(TEXT("Component generated-class identity reloads exactly: %s"), *IdentityError),
		ComponentAsset->ValidateGeneratedClassIdentity(
			AttachedComponent,
			ComponentOwner,
			IdentityError));
	ActorComponent->BeginPlay();
	AttachedComponent->BeginPlay();
	TestTrue(
		*FString::Printf(TEXT("Actor Script Class enters managed BeginPlay: %s"),
		                 *ActorComponent->LastScriptError),
		ActorComponent->bScriptRunning);
	TestTrue(
		*FString::Printf(TEXT("Component Script Class enters managed BeginPlay: %s"),
		                 *AttachedComponent->LastScriptError),
		AttachedComponent->bScriptRunning);
	TestEqual(TEXT("Actor class materializes its editable StartValue"),
	          ActorComponent->GetScriptStateInt32(TEXT("StartValue"), -1), 1);
	TestEqual(TEXT("Component class materializes its editable StartValue"),
	          AttachedComponent->GetScriptStateInt32(TEXT("StartValue"), -1), 1);
	TestEqual(TEXT("Two generated Script Classes are live in one World"),
	          Host->GetManagedScriptCount(), 2);
	TestEqual(
		TEXT("Persistent Actor owns exactly one Enhanced Input handle"),
		ActorComponent->GetManagedInputBindingCount(),
		1);
	TestEqual(
		TEXT("Persistent Actor owns exactly one delegate subscription"),
		Host->GetEventSubscriptionCount(),
		1);
	TestTrue(
		TEXT("Persistent interface dispatcher remains installed after managed BeginPlay"),
		ActorGeneratedClass->IsManagedFunctionBound(
			TEXT("TransformManagedValue")));
	if (OwnedInterfaceFunction != nullptr)
	{
		const FIntProperty* InterfaceValueProperty =
			FindFProperty<FIntProperty>(OwnedInterfaceFunction, TEXT("Value"));
		const FIntProperty* InterfaceReturnProperty =
			CastField<FIntProperty>(OwnedInterfaceFunction->GetReturnProperty());
		if (TestNotNull(
				TEXT("Persistent interface function retains its value parameter"),
				InterfaceValueProperty) &&
			TestNotNull(
				TEXT("Persistent interface function retains its return parameter"),
				InterfaceReturnProperty))
		{
			FStructOnScope InterfaceParameters(OwnedInterfaceFunction);
			InterfaceValueProperty->SetPropertyValue_InContainer(
				InterfaceParameters.GetStructMemory(),
				20);
			ScriptActor->ProcessEvent(
				OwnedInterfaceFunction,
				InterfaceParameters.GetStructMemory());
			TestEqual(
				TEXT("Persistent owned interface UFunction directly reaches managed code"),
				InterfaceReturnProperty->GetPropertyValue_InContainer(
					InterfaceParameters.GetStructMemory()),
				40);
		}
	}
	TestEqual(
		TEXT("Persistent Actor interface reaches its managed implementation"),
		IFinareInteropContract::Execute_TransformManagedValue(
			ScriptActor,
			21),
		42);

	ActorComponent->ManagedState = TEXT("Delegate");
	ActorComponent->BroadcastManagedSignal(73, ActorComponent);
	TestEqual(
		TEXT("Persistent Actor delegate dispatches exactly once"),
		ActorComponent->ManagedState,
		FString(TEXT("Delegate|Event:73")));

	UEnhancedInputComponent* EnhancedInput = Cast<UEnhancedInputComponent>(
		ScriptActor->InputComponent);
	if (TestNotNull(
		TEXT("Persistent Actor creates its Enhanced Input component"),
		EnhancedInput))
	{
		TestEqual(
			TEXT("Persistent Actor owns one generated action binding"),
			EnhancedInput->GetActionEventBindings().Num(),
			1);
		if (EnhancedInput->GetActionEventBindings().Num() == 1)
		{
			const FEnhancedInputActionEventBinding& InputBinding =
				*EnhancedInput->GetActionEventBindings()[0];
			FInputActionInstance InputInstance(InputBinding.GetAction());
			InputBinding.Execute(InputInstance);
			TestEqual(
				TEXT("Persistent Actor Enhanced Input reaches managed code"),
				ActorComponent->ManagedState,
				FString(TEXT("Input:0")));
		}
	}

	TestTrue(
		*FString::Printf(
			TEXT("Persistent gameplay-contract reload succeeds: %s"),
			*ActorComponent->LastScriptError),
		ActorComponent->ReloadScript());
	TestEqual(
		TEXT("Reload retains one Enhanced Input handle"),
		ActorComponent->GetManagedInputBindingCount(),
		1);
	TestEqual(
		TEXT("Reload replaces rather than duplicates delegate subscriptions"),
		Host->GetEventSubscriptionCount(),
		1);
	if (EnhancedInput != nullptr)
	{
		TestEqual(
			TEXT("Reload does not duplicate native action bindings"),
			EnhancedInput->GetActionEventBindings().Num(),
			1);
	}
	ActorComponent->ManagedState = TEXT("Reloaded");
	ActorComponent->BroadcastManagedSignal(74, ActorComponent);
	TestEqual(
		TEXT("Reloaded delegate dispatches exactly once"),
		ActorComponent->ManagedState,
		FString(TEXT("Reloaded|Event:74")));
	TestEqual(
		TEXT("Reloaded interface uses the active managed generation"),
		IFinareInteropContract::Execute_TransformManagedValue(
			ScriptActor,
			22),
		44);

	Host->EndManagedFrame();
	Host->BeginManagedFrame();
	ActorComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
	AttachedComponent->TickComponent(0.016f, LEVELTICK_All, nullptr);
	TestTrue(
		*FString::Printf(TEXT("Actor Script Class completes managed Tick: %s"),
		                 *ActorComponent->LastScriptError),
		ActorComponent->bScriptRunning && ActorComponent->LastScriptError.IsEmpty());
	TestTrue(
		*FString::Printf(TEXT("Component Script Class completes managed Tick: %s"),
		                 *AttachedComponent->LastScriptError),
		AttachedComponent->bScriptRunning && AttachedComponent->LastScriptError.IsEmpty());

	ActorComponent->EndPlay(EEndPlayReason::Destroyed);
	AttachedComponent->EndPlay(EEndPlayReason::Destroyed);
	TestFalse(TEXT("Actor Script Class stops on EndPlay"), ActorComponent->bScriptRunning);
	TestFalse(TEXT("Component Script Class stops on EndPlay"), AttachedComponent->bScriptRunning);
	TestEqual(TEXT("EndPlay releases both managed instances"),
	          Host->GetManagedScriptCount(), 0);
	TestEqual(TEXT("EndPlay releases both lifecycle claims"),
	          Host->GetGameplayLifecycleClaimCount(), 0);
	TestEqual(TEXT("EndPlay releases every Enhanced Input handle"),
	          ActorComponent->GetManagedInputBindingCount(), 0);
	TestEqual(TEXT("EndPlay releases every delegate subscription"),
	          Host->GetEventSubscriptionCount(), 0);
	TestNull(
		TEXT("EndPlay destroys the Actor input component owned by Finare"),
		ScriptActor->InputComponent.Get());

	AttachedComponent->UnregisterComponent();
	ScriptActor->Destroy();
	ComponentOwner->Destroy();
	World->RemoveFromRoot();
	World->DestroyWorld(false);
	TestEqual(
		TEXT("World teardown releases its collectible managed context"),
		FFinareCoreClrHost::Get().GetManagedWorldCount(),
		InitialManagedWorldCount);
	return WriteProcessReceipt(*this, TEXT("reload"));
}

#endif
