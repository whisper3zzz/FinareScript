#if WITH_DEV_AUTOMATION_TESTS

#include "FinareBlueprintClassMaterializer.h"
#include "FinareActor.h"
#include "FinareAsset.h"
#include "FinareBlueprint.h"
#include "FinareComponent.h"
#include "FinareCoreClrHost.h"
#include "FinareInvocationBridge.h"
#include "FinareWorldSubsystem.h"
#include "FinareEditorModule.h"
#include "Editor.h"
#include "FileHelpers.h"
#include "Engine/Engine.h"
#include "Engine/NetConnection.h"
#include "Engine/NetDriver.h"
#include "EngineUtils.h"
#include "GameFramework/GameModeBase.h"
#include "GameFramework/PlayerController.h"
#include "HAL/FileManager.h"
#include "Interfaces/IPluginManager.h"
#include "Kismet2/KismetEditorUtilities.h"
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/PackageName.h"
#include "PackageTools.h"
#include "PlayInEditorDataTypes.h"
#include "Settings/LevelEditorPlaySettings.h"
#include "Tests/AutomationEditorCommon.h"
#include "UObject/Package.h"
#include "UObject/SavePackage.h"
#include "UObject/StructOnScope.h"
#include "UObject/UnrealType.h"

namespace UE::Finare::Networking::Tests
{
	constexpr const TCHAR* MapName = TEXT("/Game/UnifiedScriptTests/NetworkingMap");

	struct FScenario
	{
		FAutomationTestBase& Test;
		TStrongObjectPtr<UFinareBlueprint> Blueprint;
		int32 InitialWorldCount = FFinareCoreClrHost::Get().GetManagedWorldCount();
		bool bOwnsPackage = false;
		bool bOwnsMap = false;
		bool bStartedPIE = false;
		bool bPreviousPrecompiledPlay = false;
		const bool bComponent;
		const FString ClassName;
		const FString PackageName;

		explicit FScenario(FAutomationTestBase& InTest, bool bInComponent = false)
			: Test(InTest), bComponent(bInComponent),
			  ClassName(bInComponent ? TEXT("BP_NetworkingComponentDemo") : TEXT("BP_NetworkingDemo")),
			  PackageName(TEXT("/Game/UnifiedScriptTests/") + ClassName)
		{
		}

		bool ValidateInitialNotifyLifetime();
		bool ValidateExactOwnerRouting();
		bool ValidateExactComponentOwnerRouting();

		~FScenario()
		{
			if (bStartedPIE && GEditor)
			{
				if (GEditor->PlayWorld)
				{
					GEditor->EndPlayMap();
				}
				IFinareEditorModule::Get().SetPrecompiledAutomationPlay(bPreviousPrecompiledPlay);
			}
			Test.TestEqual(TEXT("All networking managed Worlds detach"),
			               FFinareCoreClrHost::Get().GetManagedWorldCount(), InitialWorldCount);
			if (bOwnsMap)
			{
				FAutomationEditorCommonUtils::CreateNewMap();
				IFileManager::Get().Delete(*FPackageName::LongPackageNameToFilename(
					                           MapName, FPackageName::GetMapPackageExtension()), false, true);
			}
			Blueprint.Reset();
			if (bOwnsPackage)
			{
				if (UPackage* Package = FindPackage(nullptr, *PackageName))
				{
					Package->SetDirtyFlag(false);
					TArray<UPackage*> Packages{Package};
					UPackageTools::UnloadPackages(Packages);
				}
				IFileManager::Get().Delete(*FPackageName::LongPackageNameToFilename(
					                           PackageName, FPackageName::GetAssetPackageExtension()), false, true);
			}
		}

		UFinareComponent* Component(UObject* Object) const
		{
			return bComponent
				       ? Cast<UFinareComponent>(Object)
				       : CastChecked<AFinareActor>(Object)->GetFinareComponent();
		}

		AActor* Owner(UObject* Object) const
		{
			return bComponent ? Component(Object)->GetOwner() : CastChecked<AActor>(Object);
		}

		FString TextValue(UObject* Object, const TCHAR* Name) const
		{
			const FStrProperty* Property = FindFProperty<FStrProperty>(Object->GetClass(), Name);
			return Property ? Property->GetPropertyValue_InContainer(Object) : TEXT("<missing>");
		}

		UFunction* Function(const TCHAR* ManagedName) const
		{
			auto* Class = Cast<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass);
			for (const auto& Pair : Class->ManagedFunctionBindings)
			{
				if (Pair.Value.ManagedName == ManagedName)
				{
					return Class->FindFunctionByName(Pair.Key);
				}
			}
			return nullptr;
		}

		int32 Value(UObject* Object, const TCHAR* Name) const
		{
			const FIntProperty* Property = FindFProperty<FIntProperty>(Object->GetClass(), Name);
			return Property ? Property->GetPropertyValue_InContainer(Object) : MIN_int32;
		}

		void Call(UObject* Object, const TCHAR* Name, int32 Argument)
		{
			UFunction* Method = Function(Name);
			FStructOnScope Parameters(Method);
			for (TFieldIterator<FIntProperty> It(Method); It; ++It)
			{
				if (It->HasAnyPropertyFlags(CPF_Parm))
				{
					It->SetPropertyValue_InContainer(Parameters.GetStructMemory(), Argument);
				}
			}
			Object->ProcessEvent(Method, Parameters.GetStructMemory());
		}

		bool Build()
		{
			if (!Test.TestFalse(TEXT("Fixture must not overwrite an existing asset"),
			                    FindObject<UFinareBlueprint>(nullptr, *(PackageName + TEXT(".") + ClassName)) != nullptr
			                    ||
			                    FindObject<UFinareAsset>(nullptr, *(PackageName + TEXT(".NetworkingScript"))) != nullptr
			                    ||
			                    FPackageName::DoesPackageExist(PackageName)))
			{
				return false;
			}
			FString Declaration;
			const TSharedPtr<IPlugin> Plugin = IPluginManager::Get().FindPlugin(TEXT("Finare"));
			const FString DemoDirectory = Plugin.IsValid()
				                              ? FPaths::Combine(Plugin->GetBaseDir(), TEXT("Binaries/Managed/Demo"))
				                              : FString();
			const FString DeclarationPath = FPaths::Combine(
				DemoDirectory,
				TEXT("Finare.Unreal.Demo.asset.json"));
			if (!Test.TestTrue(TEXT("Load declarations emitted from the actual C# sample"),
			                   FFileHelper::LoadFileToString(Declaration, *DeclarationPath)))
			{
				return false;
			}
			UPackage* Package = CreatePackage(*PackageName);
			bOwnsPackage = true;
			Blueprint.Reset(Cast<UFinareBlueprint>(FKismetEditorUtilities::CreateBlueprint(
				bComponent ? UFinareComponent::StaticClass() : AFinareActor::StaticClass(), Package, FName(*ClassName),
				BPTYPE_Normal,
				UFinareBlueprint::StaticClass(), UFinareBlueprintGeneratedClass::StaticClass())));
			if (!Test.TestNotNull(TEXT("Create generated networking Actor"), Blueprint.Get()))
			{
				return false;
			}
			UFinareAsset* Asset = NewObject<UFinareAsset>(Package, TEXT("NetworkingScript"), RF_Public | RF_Standalone);
			FGuid::ParseExact(
				bComponent ? TEXT("762043152e714d68b05a31426591ee02") : TEXT("762043152e714d68b05a31426591ee01"),
				EGuidFormats::Digits, Asset->ScriptClassId);
			Asset->ScriptClassKind = bComponent ? EFinareClassKind::ActorComponent : EFinareClassKind::Actor;
			Asset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
			Asset->ScriptTypeName = bComponent
				                        ? TEXT("Finare.Unreal.Demo.NetworkingComponentDemoScript")
				                        : TEXT("Finare.Unreal.Demo.NetworkingDemoScript");
			Asset->DeclarationAssetPath = bComponent
				                              ? TEXT("Unreal/NetworkingComponentDemo")
				                              : TEXT("Unreal/NetworkingDemo");
			Asset->DeclarationPath = DeclarationPath;
			Asset->CompiledChunkPath = TEXT("{PluginDir}/Binaries/Managed/Demo/Finare.Unreal.Demo.uschunk");
			Asset->ScriptAssemblyPath = TEXT("{PluginDir}/Binaries/Managed/Demo/Finare.Unreal.Demo.dll");
			Blueprint->ScriptAsset = Asset;
			FFinareBlueprintClassSyncResult Result;
			FString Error;
			if (!Test.TestTrue(*FString::Printf(TEXT("Materialize networking declarations: %s"), *Error),
			                   FFinareBlueprintClassMaterializer::SynchronizeJson(
				                   *Asset, *Blueprint, Declaration, false, Result, Error)))
			{
				Test.AddError(Error);
				return false;
			}
			auto* Class = Cast<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass);
			UObject* CDO = Class->GetDefaultObject();
			Test.TestTrue(
				TEXT("Network declarations enable replication"),
				bComponent ? Component(CDO)->GetIsReplicated() : Owner(CDO)->GetIsReplicated());
			if (!bComponent)
			{
				Owner(CDO)->bAlwaysRelevant = true;
			}
			Component(CDO)->ScriptAsset = Asset;
			Component(CDO)->bTickAutomatically = false;
			for (const TCHAR* Name : {TEXT("ServerAdd"), TEXT("ClientAccepted"), TEXT("AllObserved")})
			{
				UFunction* Method = Function(Name);
				if (!Test.TestNotNull(Name, Method))
				{
					return false;
				}
				Test.TestTrue(
					TEXT("RPC is native, reliable and networked"),
					Method->HasAllFunctionFlags(FUNC_Native | FUNC_Net | FUNC_NetReliable));
			}
			Test.TestTrue(TEXT("Server direction"), Function(TEXT("ServerAdd"))->HasAnyFunctionFlags(FUNC_NetServer));
			Test.TestTrue(
				TEXT("Client direction"), Function(TEXT("ClientAccepted"))->HasAnyFunctionFlags(FUNC_NetClient));
			Test.TestTrue(
				TEXT("Multicast direction"), Function(TEXT("AllObserved"))->HasAnyFunctionFlags(FUNC_NetMulticast));
			auto* Score = FindFProperty<FIntProperty>(Class, TEXT("Score"));
			auto* Secret = FindFProperty<FIntProperty>(Class, TEXT("OwnerSecret"));
			if (!Test.TestNotNull(TEXT("Replicated Score"), Score) || !Test.TestNotNull(
				TEXT("Owner-only state"), Secret))
			{
				return false;
			}
			Test.TestTrue(
				TEXT("Score replicates with notification"), Score->HasAllPropertyFlags(CPF_Net | CPF_RepNotify));
			Test.TestEqual(
				TEXT("RepNotify uses stable generated function identity"), Score->RepNotifyFunc,
				Function(TEXT("ScoreChanged"))->GetFName());
			Test.TestEqual(
				TEXT("Owner-only condition reaches compiled property"), Secret->GetBlueprintReplicationCondition(),
				COND_OwnerOnly);
			if (!Test.TestTrue(TEXT("Unchanged declarations validate"),
			                   FFinareBlueprintClassMaterializer::SynchronizeJson(
				                   *Asset, *Blueprint, Declaration, true, Result, Error)))
			{
				return false;
			}
			const int32 PropertyCount = Blueprint->NewVariables.Num();
			UClass* PublishedClass = Blueprint->GeneratedClass;
			for (const FString& Invalid : {
				     Declaration.Replace(
					     TEXT("\"repNotifyProperty\": \"Score\""), TEXT("\"repNotifyProperty\": \"Missing\"")),
				     Declaration.Replace(TEXT("\"target\": \"server\""), TEXT("\"target\": \"invalid\"")),
				     Declaration.Replace(
					     TEXT("\"replicationCondition\": \"ownerOnly\""), TEXT("\"replicationCondition\": \"invalid\""))
			     })
			{
				Test.TestNotEqual(TEXT("Mutation changes the network contract"), Invalid, Declaration);
				Test.TestFalse(TEXT("Invalid network contract cannot publish"),
				               FFinareBlueprintClassMaterializer::SynchronizeJson(
					               *Asset, *Blueprint, Invalid, false, Result, Error));
				Test.TestFalse(TEXT("Invalid contract explains the failure"), Error.IsEmpty());
				Test.TestEqual(TEXT("Failure preserves generated class"), Blueprint->GeneratedClass.Get(),
				               PublishedClass);
				Test.TestEqual(TEXT("Failure preserves property set"), Blueprint->NewVariables.Num(), PropertyCount);
			}
			FSavePackageArgs Save;
			Save.TopLevelFlags = RF_Public | RF_Standalone;
			Save.SaveFlags = SAVE_NoError;
			Test.TestTrue(TEXT("Save runtime networking defaults"), UPackage::SavePackage(Package, Blueprint.Get(),
				              *FPackageName::LongPackageNameToFilename(PackageName,
				                                                       FPackageName::GetAssetPackageExtension()),
				              Save));
			Blueprint.Reset();
			Package->SetDirtyFlag(false);
			TArray<UPackage*> Packages{Package};
			if (!Test.TestTrue(
				TEXT("Unload generated networking class before runtime"), UPackageTools::UnloadPackages(Packages)))
			{
				return false;
			}
			Blueprint.Reset(LoadObject<UFinareBlueprint>(nullptr, *(PackageName + TEXT(".") + ClassName)));
			if (!Test.TestNotNull(TEXT("Reload persisted networking class"), Blueprint.Get()))
			{
				return false;
			}
			CDO = Blueprint->GeneratedClass->GetDefaultObject();
			Test.TestTrue(
				TEXT("Replication default survives reload"),
				bComponent ? Component(CDO)->GetIsReplicated() : Owner(CDO)->GetIsReplicated());
			Test.TestEqual(TEXT("RepNotify binding survives reload"),
			               FindFProperty<FIntProperty>(Blueprint->GeneratedClass, TEXT("Score"))->RepNotifyFunc,
			               Function(TEXT("ScoreChanged"))->GetFName());
			return !Test.HasAnyErrors();
		}
	};

	// Runs actual UE client connections and actor channels in CMD/NullRHI. No role
	// spoofing or manually copied replicated values stand in for network delivery.
	class FConnectedScenario final : public IAutomationLatentCommand
	{
		TSharedPtr<FScenario> Scenario;
		double Started = FPlatformTime::Seconds();
		int32 Phase = 0;
		TWeakObjectPtr<AActor> ServerActor;
		TWeakObjectPtr<UObject> ServerScript;
		TArray<TWeakObjectPtr<UObject>> Clients;

	public:
		explicit FConnectedScenario(TSharedPtr<FScenario> InScenario) : Scenario(MoveTemp(InScenario))
		{
		}

		virtual bool Update() override
		{
			auto& S = *Scenario;
			if (FPlatformTime::Seconds() - Started > 45.0)
			{
				S.Test.AddError(FString::Printf(TEXT("Connected networking scenario timed out in phase %d"), Phase));
				Scenario.Reset();
				return true;
			}
			UWorld* Server = nullptr;
			TArray<UWorld*> ClientWorlds;
			for (const FWorldContext& Context : GEngine->GetWorldContexts())
			{
				UWorld* World = Context.World();
				if (Context.WorldType != EWorldType::PIE || !World || !World->GetNetDriver())
				{
					continue;
				}
				if (World->GetNetDriver()->IsServer())
				{
					Server = World;
				}
				else
				{
					ClientWorlds.Add(World);
				}
			}
			if (!Server || ClientWorlds.Num() != 2 || Server->GetNetDriver()->ClientConnections.Num() != 2)
			{
				return false;
			}
			for (UNetConnection* Connection : Server->GetNetDriver()->ClientConnections)
			{
				if (!Connection->PlayerController || !Connection->ViewTarget)
				{
					return false;
				}
			}

			if (Phase == 0)
			{
				FActorSpawnParameters Params;
				Params.Owner = Server->GetNetDriver()->ClientConnections[0]->PlayerController;
				Params.SpawnCollisionHandlingOverride = ESpawnActorCollisionHandlingMethod::AlwaysSpawn;
				ServerActor = Server->SpawnActor<AActor>(
					S.bComponent ? AActor::StaticClass() : S.Blueprint->GeneratedClass.Get(), Params);
				if (!S.Test.TestNotNull(TEXT("Spawn server Actor"), ServerActor.Get()))
				{
					Scenario.Reset();
					return true;
				}
				if (S.bComponent)
				{
					ServerActor->SetReplicates(true);
					ServerActor->bAlwaysRelevant = true;
					auto* Component = NewObject<UFinareComponent>(ServerActor.Get(), S.Blueprint->GeneratedClass,
					                                              TEXT("NetworkBehavior"));
					ServerActor->AddInstanceComponent(Component);
					Component->RegisterComponent();
					ServerScript = Component;
				}
				else
				{
					ServerScript = ServerActor.Get();
				}
				if (!S.Test.TestTrue(*S.Component(ServerScript.Get())->LastScriptError,
				                     S.Component(ServerScript.Get())->bScriptRunning))
				{
					Scenario.Reset();
					return true;
				}
				S.Call(ServerScript.Get(), TEXT("InitializeScore"), 10);
				ServerActor->ForceNetUpdate();
				Phase = 1;
				S.Test.AddInfo(TEXT("Server spawned generated Actor; waiting for replication on two clients."));
			}
			Clients.Reset();
			for (UWorld* World : ClientWorlds)
			{
				if (S.bComponent)
				{
					for (TActorIterator<AActor> It(World); It; ++It)
					{
						TInlineComponentArray<UFinareComponent*> Components(*It);
						for (auto* Component : Components)
						{
							if (Component->GetClass() == S.Blueprint->GeneratedClass)
							{
								Clients.Add(Component);
							}
						}
					}
				}
				else
				{
					for (TActorIterator<AFinareActor> It(World, S.Blueprint->GeneratedClass.Get()); It; ++It)
					{
						Clients.Add(*It);
					}
				}
			}
			if (Clients.Num() != 2)
			{
				return false;
			}
			UObject* Owner = nullptr;
			UObject* Observer = nullptr;
			for (auto Client : Clients)
			{
				if (!S.Component(Client.Get())->bScriptRunning)
				{
					return false;
				}
				if (S.Owner(Client.Get())->HasLocalNetOwner())
				{
					Owner = Client.Get();
				}
				else
				{
					Observer = Client.Get();
				}
			}
			if (!Owner || !Observer)
			{
				return false;
			}
			if (Phase == 1)
			{
				if (S.Value(Owner, TEXT("Score")) != 10 || S.Value(Observer, TEXT("Score")) != 10 ||
					S.Value(Owner, TEXT("OwnerSecret")) != 100)
				{
					return false;
				}
				S.Test.TestEqual(
					TEXT("Owner-only value is withheld from observer"), S.Value(Observer, TEXT("OwnerSecret")), 0);
				for (auto Client : Clients)
				{
					S.Test.TestEqual(TEXT("Initial RepNotify runs once"), S.Value(Client.Get(), TEXT("Notifications")),
					                 1);
					S.Test.TestEqual(
						TEXT("Initial RepNotify receives previous value"), S.Value(Client.Get(), TEXT("PreviousScore")),
						0);
					if (S.bComponent)
					{
						S.Test.TestEqual(TEXT("Initial string value"), S.TextValue(Client.Get(), TEXT("Label")),
						                 FString(TEXT("Ready")));
						S.Test.TestEqual(
							TEXT("Initial string RepNotify owns the previous value"),
							S.TextValue(Client.Get(), TEXT("PreviousLabel")), FString());
						S.Test.TestEqual(
							TEXT("Initial string notification runs once"),
							S.Value(Client.Get(), TEXT("LabelNotifications")), 1);
						S.Test.TestEqual(
							TEXT("InitialOnly sends the initial component value"),
							S.Value(Client.Get(), TEXT("InitialScore")), 10);
						S.Test.TestEqual(
							TEXT("InitialOrOwner initially reaches both clients"),
							S.Value(Client.Get(), TEXT("InitialOrOwnerScore")), 10);
					}
				}
				if (S.bComponent)
				{
					S.Test.TestEqual(
						TEXT("SkipOwner hides initial component state from owner"),
						S.Value(Owner, TEXT("ObserverScore")), 0);
					S.Test.TestEqual(
						TEXT("SkipOwner delivers initial component state to observer"),
						S.Value(Observer, TEXT("ObserverScore")), 10);
				}
				// A normal local method cannot write replicated state on a client.
				S.Test.AddExpectedError(
					TEXT("cannot be written by a non-authoritative Actor"),
					EAutomationExpectedErrorFlags::Contains,
					1);
				S.Call(Owner, TEXT("InitializeScore"), 999);
				S.Test.TestEqual(
					TEXT("Client cannot overwrite authoritative state"), S.Value(Owner, TEXT("Score")), 10);
				{
					FFinareObjectRegistry Registry(Observer->GetWorld());
					FFinareInvocationBridge Bridge(Registry);
					FFinareObjectHandleResult HandleResult;
					const auto Handle = Registry.RegisterObject(Observer, HandleResult);
					FFinareInvocationResult Result;
					FFinareValue Return;
					TArray<FFinareValue> Args{FFinareValue::FromInt32(7)};
					const FString ClassPath = S.Blueprint->GeneratedClass->GetPathName();
					S.Test.TestFalse(TEXT("Non-owning client cannot send Server RPC"),
					                 Bridge.Invoke(Handle, ClassPath + TEXT(":ServerAdd"), Args, Return, Result));
					S.Test.TestEqual(
						TEXT("Ownership rejection is explicit"), Result.Error,
						EFinareInvocationError::FunctionNotCallable);
					S.Test.TestFalse(TEXT("Client cannot originate Multicast RPC"),
					                 Bridge.Invoke(Handle, ClassPath + TEXT(":AllObserved"), Args, Return, Result));
					S.Test.TestFalse(TEXT("Direct bridge writes also enforce replication authority"),
					                 Bridge.WriteProperty(Handle, ClassPath + TEXT(":Score"), Args[0], Result));
					S.Test.TestEqual(
						TEXT("Replicated write rejection is explicit"), Result.Error,
						EFinareInvocationError::PropertyNotWritable);
				}
				if (S.bComponent)
				{
					FFinareObjectRegistry Registry(Owner->GetWorld());
					FFinareInvocationBridge Bridge(Registry);
					FFinareObjectHandleResult HandleResult;
					const auto Handle = Registry.RegisterObject(Owner, HandleResult);
					FFinareInvocationResult Result;
					FFinareValue Return;
					S.Component(Owner)->SetIsReplicated(false);
					S.Test.TestFalse(
						TEXT("A non-replicating component cannot send even with an owned replicated Actor"),
						Bridge.Invoke(Handle, S.Blueprint->GeneratedClass->GetPathName() + TEXT(":ServerPing"), {},
						              Return, Result));
					S.Test.TestEqual(
						TEXT("Non-replicating component rejection is explicit"), Result.Error,
						EFinareInvocationError::FunctionNotCallable);
					S.Component(Owner)->SetIsReplicated(true);
				}
				S.Call(Owner, TEXT("RequestAdd"), 7);
				Phase = 2;
				S.Test.AddInfo(TEXT("Owning client sent Server RPC through C# SendRpc."));
			}
			if (Phase == 2 || Phase == 3)
			{
				const int32 Expected = Phase == 2 ? 17 : 20;
				if (S.Value(ServerScript.Get(), TEXT("Score")) != Expected || S.Value(Owner, TEXT("Score")) != Expected
					||
					S.Value(Observer, TEXT("Score")) != Expected || S.Value(Owner, TEXT("AcknowledgedScore")) !=
					Expected ||
					S.Value(Owner, TEXT("BroadcastScore")) != Expected || S.Value(Observer, TEXT("BroadcastScore")) !=
					Expected ||
					S.Value(ServerScript.Get(), TEXT("BroadcastScore")) != Expected ||
					S.Value(Owner, TEXT("OwnerSecret")) != Expected * 10)
				{
					return false;
				}
				S.Test.TestEqual(TEXT("Client RPC reaches only owner"), S.Value(Observer, TEXT("AcknowledgedScore")),
				                 0);
				S.Test.TestEqual(
					TEXT("Replication retains owner-only audience"), S.Value(Observer, TEXT("OwnerSecret")), 0);
				for (auto Client : Clients)
				{
					S.Test.TestEqual(
						TEXT("RepNotify counts real changes"), S.Value(Client.Get(), TEXT("Notifications")), Phase);
					S.Test.TestEqual(
						TEXT("RepNotify receives preceding replicated value"),
						S.Value(Client.Get(), TEXT("PreviousScore")), Phase == 2 ? 10 : 17);
				}
				if (S.bComponent)
				{
					const FString Label = Phase == 2 ? TEXT("First") : TEXT("Second");
					for (auto Client : Clients)
					{
						S.Test.TestEqual(
							TEXT("InitialOnly withholds later component changes"),
							S.Value(Client.Get(), TEXT("InitialScore")), 10);
						S.Test.TestEqual(
							TEXT("Zero-input RPC updates replicated component state"),
							S.Value(Client.Get(), TEXT("Pings")), Phase - 1);
						S.Test.TestEqual(
							TEXT("Replicated string reaches both clients"), S.TextValue(Client.Get(), TEXT("Label")),
							Label);
						S.Test.TestEqual(
							TEXT("String RepNotify preserves previous data"),
							S.TextValue(Client.Get(), TEXT("PreviousLabel")),
							FString(Phase == 2 ? TEXT("Ready") : TEXT("First")));
						S.Test.TestEqual(
							TEXT("String notifications include reload"),
							S.Value(Client.Get(), TEXT("LabelNotifications")), Phase);
						S.Test.TestEqual(TEXT("Two-input multicast carries float payload"),
						                 FindFProperty<FFloatProperty>(Client->GetClass(), TEXT("BroadcastGain"))->
						                 GetPropertyValue_InContainer(Client.Get()), 1.5f);
					}
					S.Test.TestEqual(
						TEXT("InitialOrOwner keeps updating owner"), S.Value(Owner, TEXT("InitialOrOwnerScore")),
						Expected);
					S.Test.TestEqual(
						TEXT("InitialOrOwner preserves observer's initial value"),
						S.Value(Observer, TEXT("InitialOrOwnerScore")), 10);
					S.Test.TestEqual(
						TEXT("SkipOwner keeps the owning mirror unchanged"), S.Value(Owner, TEXT("ObserverScore")), 0);
					S.Test.TestEqual(
						TEXT("SkipOwner keeps updating observer"), S.Value(Observer, TEXT("ObserverScore")), Expected);
					S.Test.TestEqual(
						TEXT("Authority still changes its InitialOnly value"),
						S.Value(ServerScript.Get(), TEXT("InitialScore")), Expected);
					S.Test.TestEqual(
						TEXT("Three-input client RPC carries string"), S.TextValue(Owner, TEXT("AcknowledgedLabel")),
						Label);
					S.Test.TestEqual(TEXT("Object RPC resolves to the owning client's Actor, not a server handle"),
					                 FindFProperty<FObjectPropertyBase>(Owner->GetClass(), TEXT("AcknowledgedActor"))->
					                 GetObjectPropertyValue_InContainer(Owner), static_cast<UObject*>(S.Owner(Owner)));
					S.Test.TestEqual(TEXT("Four-input server RPC carries float"),
					                 FindFProperty<FFloatProperty>(ServerScript->GetClass(), TEXT("Gain"))->
					                 GetPropertyValue_InContainer(ServerScript.Get()), 1.5f);
					S.Test.TestTrue(TEXT("Four-input server RPC carries bool"),
					                FindFProperty<FBoolProperty>(ServerScript->GetClass(), TEXT("Enabled"))->
					                GetPropertyValue_InContainer(ServerScript.Get()));
					S.Test.TestEqual(TEXT("Four-input server RPC carries Vector"),
					                 *FindFProperty<FStructProperty>(ServerScript->GetClass(), TEXT("Aim"))->
					                 ContainerPtrToValuePtr<FVector>(ServerScript.Get()), FVector(1, 2, 3));
				}
				if (Phase == 2)
				{
					S.Test.TestTrue(
						TEXT("Reload owning client's managed generation"), S.Component(Owner)->ReloadScript());
					S.Call(Owner, TEXT("RequestAdd"), 3);
					Phase = 3;
					return false;
				}
				if (S.bComponent)
				{
					S.Call(Owner, TEXT("RequestCommonValues"), 0);
					Phase = 4;
					return false;
				}
				if (!S.Test.HasAnyErrors())
				{
					S.Test.AddInfo(TEXT(
						"PASS: two connected clients, replicated/owner-only state, RepNotify old values, Server/Client/Multicast RPC and reload."));
				}
				Scenario.Reset();
				return true;
			}
			if (Phase == 4)
			{
				for (UObject* Endpoint : {ServerScript.Get(), Owner})
				{
					const FString Error = S.TextValue(Endpoint, TEXT("CommonPayloadError"));
					if (!Error.IsEmpty())
					{
						S.Test.AddError(Error);
						Scenario.Reset();
						return true;
					}
				}
				constexpr int64 ExpectedWide = 9007199254740993ll;
				auto* Received = FindFProperty<FInt64Property>(ServerScript->GetClass(), TEXT("ReceivedWide"));
				auto* Acknowledged = FindFProperty<FInt64Property>(Owner->GetClass(), TEXT("AcknowledgedWide"));
				if (Received->GetPropertyValue_InContainer(ServerScript.Get()) != ExpectedWide ||
					Acknowledged->GetPropertyValue_InContainer(Owner) != ExpectedWide)
				{
					return false;
				}
				S.Test.TestEqual(TEXT("Common-value Client RPC remains owner-only"),
				                 Acknowledged->GetPropertyValue_InContainer(Observer), static_cast<int64>(0));
				S.Test.TestTrue(
					TEXT("Reload after wide integer state and common-value RPC"), S.Component(Owner)->ReloadScript());
				S.Test.TestEqual(TEXT("Wide integer state retains precision after reload"),
				                 Acknowledged->GetPropertyValue_InContainer(Owner), ExpectedWide);
				if (!S.Test.HasAnyErrors())
				{
					S.Test.AddInfo(TEXT(
						"PASS: ActorComponent conditions, mixed RPC payloads, typed enum/Rotator/Transform, integer beyond binary64 precision and reload."));
				}
				Scenario.Reset();
				return true;
			}
			return false;
		}
	};
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareConnectedNetworkingTest,
                                 "Finare.Unreal.Networking.ConnectedScriptClass",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareConnectedComponentNetworkingTest,
                                 "Finare.Unreal.Networking.ConnectedComponent",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareExactOwnerRoutingTest,
                                 "Finare.Unreal.Networking.ExactOwnerRouting",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FFinareExactComponentOwnerRoutingTest,
                                 "Finare.Unreal.Networking.ExactComponentOwnerRouting",
                                 EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

static bool RunNetworkingScenario(FAutomationTestBase& Test, bool bComponent)
{
	using namespace UE::Finare::Networking::Tests;
	if (!Test.TestNotNull(TEXT("CMD editor engine available"), GEditor) ||
		!Test.TestNull(TEXT("Networking test must not interrupt an existing PIE session"), GEditor->PlayWorld))
	{
		return false;
	}
	auto Scenario = MakeShared<FScenario>(Test, bComponent);
	if (!Scenario->Build())
	{
		return false;
	}
	if (!bComponent && !Scenario->ValidateInitialNotifyLifetime())
	{
		return false;
	}
	if (!Test.TestFalse(TEXT("Fixture must not overwrite an existing map"), FPackageName::DoesPackageExist(MapName)))
	{
		return false;
	}
	UWorld* Map = FAutomationEditorCommonUtils::CreateNewMap();
	Scenario->bOwnsMap = true;
	if (!Test.TestTrue(
		TEXT("Save a real map for replicated level identity"), UEditorLoadingAndSavingUtils::SaveMap(Map, MapName)))
	{
		return false;
	}
	auto* Settings = NewObject<ULevelEditorPlaySettings>();
	Settings->SetPlayNetMode(EPlayNetMode::PIE_Client);
	Settings->SetPlayNumberOfClients(2);
	Settings->bLaunchSeparateServer = true;
	Settings->GameGetsMouseControl = false;
	Settings->SetRunUnderOneProcess(true);
	FRequestPlaySessionParams Session;
	Session.EditorPlaySettings = Settings;
	Session.GameModeOverride = AGameModeBase::StaticClass();
	Session.bAllowOnlineSubsystem = false;
	Session.GlobalMapOverride = MapName;
	// This is a networking runtime test using explicitly emitted inputs, not
	// a test of the separate daily-build coordinator or its source mutations.
	Scenario->bPreviousPrecompiledPlay = IFinareEditorModule::Get().SetPrecompiledAutomationPlay(true);
	GEditor->RequestPlaySession(Session);
	Scenario->bStartedPIE = true;
	ADD_LATENT_AUTOMATION_COMMAND(FConnectedScenario(Scenario));
	return true;
}

bool FFinareConnectedNetworkingTest::RunTest(const FString& Parameters)
{
	return RunNetworkingScenario(*this, false);
}

bool FFinareConnectedComponentNetworkingTest::RunTest(const FString& Parameters)
{
	return RunNetworkingScenario(*this, true);
}

bool FFinareExactOwnerRoutingTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::Networking::Tests;
	FScenario Scenario(*this, false);
	return Scenario.Build() && Scenario.ValidateExactOwnerRouting() && !HasAnyErrors();
}

bool FFinareExactComponentOwnerRoutingTest::RunTest(const FString& Parameters)
{
	using namespace UE::Finare::Networking::Tests;
	FScenario Scenario(*this, true);
	return Scenario.Build() && Scenario.ValidateExactComponentOwnerRouting() && !HasAnyErrors();
}

bool UE::Finare::Networking::Tests::FScenario::ValidateExactOwnerRouting()
{
	if (!Test.TestFalse(TEXT("Actor exact-owner test uses the Actor fixture"), bComponent))
	{
		return false;
	}

	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!Test.TestNotNull(TEXT("Create exact-owner World"), World))
	{
		return false;
	}
	World->AddToRoot();
	World->InitializeActorsForPlay(FURL());

	AFinareActor* Actor = nullptr;
	AFinareActor* PreStartActor = nullptr;
	UFinareComponent* Matching = nullptr;
	UFinareComponent* Ambiguous = nullptr;
	UFinareBlueprintGeneratedClass* GeneratedClass =
		Cast<UFinareBlueprintGeneratedClass>(Blueprint->GeneratedClass);
	UFinareComponent* CDOComponent = GeneratedClass != nullptr
		                                 ? Component(GeneratedClass->GetDefaultObject())
		                                 : nullptr;
	const bool bOriginalStartAutomatically = CDOComponent != nullptr
		                                         ? CDOComponent->bStartAutomatically
		                                         : true;
	ON_SCOPE_EXIT
	{
		if (Ambiguous != nullptr)
		{
			Ambiguous->EndPlay(EEndPlayReason::Destroyed);
			Ambiguous->DestroyComponent();
			Ambiguous = nullptr;
		}
		if (Matching != nullptr)
		{
			Matching->EndPlay(EEndPlayReason::Destroyed);
			Matching->DestroyComponent();
			Matching = nullptr;
		}
		if (PreStartActor != nullptr && !PreStartActor->IsActorBeingDestroyed())
		{
			PreStartActor->StopManagedBehavior();
			PreStartActor->Destroy();
			PreStartActor = nullptr;
		}
		if (Actor != nullptr && !Actor->IsActorBeingDestroyed())
		{
			Actor->Destroy();
			Actor = nullptr;
		}
		if (CDOComponent != nullptr)
		{
			CDOComponent->bStartAutomatically = bOriginalStartAutomatically;
		}
		World->RemoveFromRoot();
		World->DestroyWorld(false);
	};

	if (!Test.TestNotNull(TEXT("Exact-owner fixture generated class exists"), GeneratedClass) ||
		!Test.TestNotNull(TEXT("Exact-owner fixture Script Asset exists"), Blueprint->ScriptAsset.Get()) ||
		!Test.TestNotNull(TEXT("Exact-owner CDO component exists"), CDOComponent))
	{
		return false;
	}
	UFinareAsset* ScriptAsset = Blueprint->ScriptAsset;
	CDOComponent->bStartAutomatically = false;
	Actor = World->SpawnActor<AFinareActor>(Blueprint->GeneratedClass);
	UFinareComponent* Canonical = Actor != nullptr ? Component(Actor) : nullptr;
	if (!Test.TestNotNull(TEXT("Exact-owner Actor spawns"), Actor) ||
		!Test.TestNotNull(TEXT("Exact-owner canonical component exists"), Canonical))
	{
		return false;
	}
	Canonical->ScriptAsset = ScriptAsset;
	Canonical->bStartAutomatically = false;

	auto CreateExtraComponent = [&](const TCHAR* Name, FName OwnershipKey)
	{
		UFinareComponent* Extra = NewObject<UFinareComponent>(
			Actor,
			UFinareComponent::StaticClass(),
			FName(Name));
		if (Extra != nullptr)
		{
			Actor->AddInstanceComponent(Extra);
			Extra->ScriptAsset = ScriptAsset;
			Extra->bStartAutomatically = false;
			Extra->bTickAutomatically = false;
			Extra->LifecycleOwnershipKey = OwnershipKey;
			Extra->RegisterComponent();
			Extra->BeginPlay();
		}
		return Extra;
	};

	Matching = CreateExtraComponent(
		TEXT("ExactOwnerMatchingComponent"),
		FName(TEXT("ExactOwnerMatching")));
	if (!Test.TestNotNull(TEXT("Exact-owner later component exists"), Matching) ||
		!Test.TestTrue(TEXT("Exact-owner later component starts"), Matching->StartScript()))
	{
		return false;
	}
	Test.TestTrue(
		TEXT("Earlier cold component has the configured generated identity"),
		Canonical->OwnsConfiguredManagedOverride(GeneratedClass));

	UFunction* Initialize = Function(TEXT("InitializeScore"));
	if (!Test.TestNotNull(TEXT("Exact-owner InitializeScore exists"), Initialize))
	{
		return false;
	}
	FFinareManagedFunctionBinding* InitializeBinding =
		GeneratedClass->ManagedFunctionBindings.Find(Initialize->GetFName());
	if (!Test.TestNotNull(TEXT("Exact-owner InitializeScore binding exists"), InitializeBinding))
	{
		return false;
	}
	const int32 OriginalAbi = InitializeBinding->AbiVersion;
	const FIntProperty* ValueProperty = FindFProperty<FIntProperty>(Initialize, TEXT("value"));
	if (!Test.TestNotNull(TEXT("Exact-owner InitializeScore value pin exists"), ValueProperty))
	{
		return false;
	}
	auto InvokeInitialize = [&](int32 Value)
	{
		FStructOnScope Parameters(Initialize);
		ValueProperty->SetPropertyValue_InContainer(Parameters.GetStructMemory(), Value);
		Actor->ProcessEvent(Initialize, Parameters.GetStructMemory());
	};

	InvokeInitialize(17);
	Test.TestEqual(
		TEXT("Ordinary generated ABI v2 dispatch (native call frame v3) reaches the later active owner"),
		Value(Actor, TEXT("Score")),
		17);

	UFinareAsset* SwappedAsset = NewObject<UFinareAsset>(
		Blueprint->GetOutermost(),
		TEXT("ConfiguredSwapNetworkingAsset"));
	SwappedAsset->ScriptClassId = FGuid::NewGuid();
	SwappedAsset->ScriptClassKind = EFinareClassKind::Actor;
	SwappedAsset->GeneratedClassPath = FSoftClassPath(Blueprint->GeneratedClass);
	Matching->ScriptAsset = SwappedAsset;
	Test.TestFalse(
		TEXT("Configured swap no longer claims the active generated identity"),
		Matching->OwnsConfiguredManagedOverride(GeneratedClass));
	InvokeInitialize(23);
	Test.TestEqual(
		TEXT("Configured asset swap keeps dispatch on the active generation"),
		Value(Actor, TEXT("Score")),
		23);

	InitializeBinding->AbiVersion = 1;
	InvokeInitialize(29);
	InitializeBinding->AbiVersion = OriginalAbi;
	Test.TestEqual(
		TEXT("Ordinary generated ABI v1 call reaches the active owner"),
		Value(Actor, TEXT("Score")),
		29);

	UFunction* Notify = Function(TEXT("ScoreChanged"));
	const FIntProperty* PreviousProperty =
		FindFProperty<FIntProperty>(Notify, TEXT("previous"));
	if (!Test.TestNotNull(TEXT("Exact-owner RepNotify function exists"), Notify) ||
		!Test.TestNotNull(TEXT("Exact-owner RepNotify previous pin exists"), PreviousProperty))
	{
		return false;
	}
	Ambiguous = CreateExtraComponent(
		TEXT("ExactOwnerAmbiguousComponent"),
		FName(TEXT("ExactOwnerAmbiguous")));
	if (!Test.TestNotNull(TEXT("Second active owner exists"), Ambiguous) ||
		!Test.TestTrue(TEXT("Second active owner starts"), Ambiguous->StartScript()))
	{
		return false;
	}
	const int32 BeforeAmbiguous = Value(Actor, TEXT("Score"));
	Test.AddExpectedError(
		TEXT("has no exact managed owner"),
		EAutomationExpectedErrorFlags::Contains,
		1);
	InvokeInitialize(31);
	Test.TestEqual(
		TEXT("Multiple active owners reject the ordinary generated call"),
		Value(Actor, TEXT("Score")),
		BeforeAmbiguous);
	const int32 BeforeActiveAmbiguousNotify = Value(Actor, TEXT("Notifications"));
	FStructOnScope ActiveNotifyParameters(Notify);
	PreviousProperty->SetPropertyValue_InContainer(
		ActiveNotifyParameters.GetStructMemory(),
		29);
	Test.AddExpectedError(
		TEXT("has no exact managed owner"),
		EAutomationExpectedErrorFlags::Contains,
		1);
	Actor->ProcessEvent(Notify, ActiveNotifyParameters.GetStructMemory());
	Test.TestEqual(
		TEXT("Active ambiguity does not fall through to configured RepNotify"),
		Value(Actor, TEXT("Notifications")),
		BeforeActiveAmbiguousNotify);

	Ambiguous->EndPlay(EEndPlayReason::Destroyed);
	Ambiguous->DestroyComponent();
	Ambiguous = nullptr;
	Matching->StopScript();
	Matching->ScriptAsset = ScriptAsset;
	const int32 BeforePreStartAmbiguous = Value(Actor, TEXT("Notifications"));
	FStructOnScope NotifyParameters(Notify);
	PreviousProperty->SetPropertyValue_InContainer(
		NotifyParameters.GetStructMemory(),
		29);
	Test.AddExpectedError(
		TEXT("has no exact managed owner"),
		EAutomationExpectedErrorFlags::Contains,
		1);
	Actor->ProcessEvent(Notify, NotifyParameters.GetStructMemory());
	Test.TestEqual(
		TEXT("Multiple configured pre-start owners reject RepNotify"),
		Value(Actor, TEXT("Notifications")),
		BeforePreStartAmbiguous);

	CDOComponent->bStartAutomatically = true;
	PreStartActor = World->SpawnActor<AFinareActor>(Blueprint->GeneratedClass);
	UFinareComponent* PreStartComponent =
		PreStartActor != nullptr ? Component(PreStartActor) : nullptr;
	if (!Test.TestNotNull(TEXT("Unique pre-start Actor spawns"), PreStartActor) ||
		!Test.TestNotNull(TEXT("Unique pre-start component exists"), PreStartComponent))
	{
		return false;
	}
	PreStartComponent->ScriptAsset = nullptr;
	PreStartComponent->ScriptAssetPath = ScriptAsset->GetPathName();
	PreStartComponent->bStartAutomatically = true;
	Test.TestFalse(TEXT("Unique pre-start component is not running yet"), PreStartComponent->bScriptRunning);
	const int32 BeforeQueuedNotify = Value(PreStartActor, TEXT("Notifications"));
	FStructOnScope WrongTargetParameters(Notify);
	PreviousProperty->SetPropertyValue_InContainer(
		WrongTargetParameters.GetStructMemory(),
		37);
	Test.TestFalse(
		TEXT("Queue rejects a notify target different from the configured Actor receiver"),
		PreStartComponent->QueueInitialRepNotify(
			Actor,
			Notify,
			WrongTargetParameters.GetStructMemory()));
	FStructOnScope CorrectTargetParameters(Notify);
	PreviousProperty->SetPropertyValue_InContainer(
		CorrectTargetParameters.GetStructMemory(),
		41);
	PreStartActor->ProcessEvent(Notify, CorrectTargetParameters.GetStructMemory());
	Test.TestEqual(
		TEXT("Unique ScriptAssetPath RepNotify remains queued before start"),
		Value(PreStartActor, TEXT("Notifications")),
		BeforeQueuedNotify);
	Test.TestTrue(
		TEXT("Unique ScriptAssetPath owner starts and replays exactly once"),
		PreStartActor->StartManagedBehavior());
	Test.TestEqual(
		TEXT("Unique pre-start RepNotify delivers once after real start"),
		Value(PreStartActor, TEXT("Notifications")),
		1);
	Test.TestEqual(
		TEXT("Unique pre-start RepNotify preserves its previous value"),
		Value(PreStartActor, TEXT("PreviousScore")),
		41);
	return !Test.HasAnyErrors();
}

bool UE::Finare::Networking::Tests::FScenario::ValidateExactComponentOwnerRouting()
{
	if (!Test.TestTrue(TEXT("Component exact-owner test uses the component fixture"), bComponent))
	{
		return false;
	}
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!Test.TestNotNull(TEXT("Create exact-component World"), World))
	{
		return false;
	}
	World->AddToRoot();
	World->InitializeActorsForPlay(FURL());
	AActor* Actor = World->SpawnActor<AActor>();
	UFinareComponent* ScriptComponent = Actor != nullptr
		                                    ? NewObject<UFinareComponent>(
			                                    Actor, Blueprint->GeneratedClass, TEXT("ExactComponentOwner"))
		                                    : nullptr;
	ON_SCOPE_EXIT
	{
		if (ScriptComponent != nullptr)
		{
			ScriptComponent->EndPlay(EEndPlayReason::Destroyed);
			ScriptComponent->DestroyComponent();
			ScriptComponent = nullptr;
		}
		if (Actor != nullptr && !Actor->IsActorBeingDestroyed())
		{
			Actor->Destroy();
			Actor = nullptr;
		}
		World->RemoveFromRoot();
		World->DestroyWorld(false);
	};
	if (!Test.TestNotNull(TEXT("Exact-component Actor exists"), Actor) ||
		!Test.TestNotNull(TEXT("Exact-component generated receiver exists"), ScriptComponent))
	{
		return false;
	}
	Actor->AddInstanceComponent(ScriptComponent);
	ScriptComponent->ScriptAsset = Blueprint->ScriptAsset;
	ScriptComponent->bStartAutomatically = false;
	ScriptComponent->bTickAutomatically = false;
	ScriptComponent->LifecycleOwnershipKey = FName(TEXT("ExactComponentOwner"));
	ScriptComponent->RegisterComponent();
	ScriptComponent->BeginPlay();
	if (!Test.TestTrue(TEXT("Exact-component receiver starts"), ScriptComponent->StartScript()))
	{
		return false;
	}
	UFunction* Initialize = Function(TEXT("InitializeScore"));
	if (!Test.TestNotNull(TEXT("Exact-component InitializeScore exists"), Initialize))
	{
		return false;
	}
	const FIntProperty* ValueProperty = FindFProperty<FIntProperty>(Initialize, TEXT("value"));
	if (!Test.TestNotNull(TEXT("Exact-component value pin exists"), ValueProperty))
	{
		return false;
	}
	FStructOnScope Parameters(Initialize);
	ValueProperty->SetPropertyValue_InContainer(Parameters.GetStructMemory(), 37);
	ScriptComponent->ProcessEvent(Initialize, Parameters.GetStructMemory());
	Test.TestEqual(
		TEXT("ActorComponent receiver dispatches through itself"),
		Value(ScriptComponent, TEXT("Score")),
		37);
	Test.TestEqual(
		TEXT("ActorComponent parent receiver is itself"),
		ScriptComponent->GetParentInvocationReceiver(),
		static_cast<UObject*>(ScriptComponent));
	return !Test.HasAnyErrors();
}

bool UE::Finare::Networking::Tests::FScenario::ValidateInitialNotifyLifetime()
{
	UWorld* World = UWorld::CreateWorld(EWorldType::Game, false);
	if (!Test.TestNotNull(TEXT("Create initial-notify World"), World))
	{
		return false;
	}
	World->AddToRoot();
	World->InitializeActorsForPlay(FURL());
	auto* Actor = World->SpawnActor<AFinareActor>(Blueprint->GeneratedClass);
	auto* Canceled = World->SpawnActor<AFinareActor>(Blueprint->GeneratedClass);
	bool Ready = Test.TestNotNull(TEXT("Create pending Actor"), Actor) && Test.TestNotNull(
		TEXT("Create canceled Actor"), Canceled);
	if (Ready)
	{
		UFunction* Notify = Function(TEXT("ScoreChanged"));
		{
			FStructOnScope OldValue(Notify);
			auto* Property = FindFProperty<FIntProperty>(Notify, TEXT("previous"));
			Property->SetPropertyValue_InContainer(OldValue.GetStructMemory(), 42);
			Test.TestTrue(
				TEXT("Queue owns initial previous value"),
				Actor->GetFinareComponent()->QueueInitialRepNotify(Actor, Notify, OldValue.GetStructMemory()));
			Test.TestTrue(
				TEXT("Queue a later-canceled initial value"),
				Canceled->GetFinareComponent()->QueueInitialRepNotify(Canceled, Notify, OldValue.GetStructMemory()));
			Property->SetPropertyValue_InContainer(OldValue.GetStructMemory(), 73);
			Test.TestTrue(
				TEXT("Pre-start changes coalesce"),
				Actor->GetFinareComponent()->QueueInitialRepNotify(Actor, Notify, OldValue.GetStructMemory()));
		}
		// The source parameter block is gone. The queue must survive GC without
		// a raw parameter address or unreported function/object reference.
		CollectGarbage(RF_NoFlags);
		Canceled->StopManagedBehavior();
		Test.TestTrue(TEXT("Initial pending notification survives activation"), Actor->StartManagedBehavior());
		Test.TestEqual(TEXT("Initial queue preserves first previous value"), Value(Actor, TEXT("PreviousScore")), 42);
		Test.TestEqual(TEXT("Initial queue delivers once"), Value(Actor, TEXT("Notifications")), 1);
		Test.TestTrue(
			TEXT("Canceled Actor can start without replaying old notifications"), Canceled->StartManagedBehavior());
		Test.TestEqual(TEXT("Stop discards pending initial notifications"), Value(Canceled, TEXT("Notifications")), 0);
		Actor->StopManagedBehavior();
		FStructOnScope OldValue(Notify);
		Test.TestFalse(TEXT("Stopping does not re-arm the initial notification queue"),
		               Actor->GetFinareComponent()->QueueInitialRepNotify(Actor, Notify, OldValue.GetStructMemory()));
		Canceled->StopManagedBehavior();
	}
	World->RemoveFromRoot();
	World->DestroyWorld(false);
	if (Ready && !Test.HasAnyErrors())
	{
		Test.AddInfo(TEXT("PASS: initial notification snapshots survive GC, coalesce, and clear on stop."));
	}
	return Ready && !Test.HasAnyErrors();
}

#endif
