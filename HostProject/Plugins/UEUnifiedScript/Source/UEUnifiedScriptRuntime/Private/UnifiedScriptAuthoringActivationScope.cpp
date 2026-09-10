#include "UnifiedScriptAuthoringActivationScope.h"

#if WITH_EDITOR

#include "Components/ActorComponent.h"
#include "GameFramework/Actor.h"

namespace UE::UnifiedScript::AuthoringActivation::Private
{
TMap<FObjectKey, int32>& ScopeEntries()
{
    static TMap<FObjectKey, int32> Entries;
    return Entries;
}

bool IsClassSuppressed(const UClass* Class)
{
    if (Class == nullptr)
    {
        return false;
    }
    const TMap<FObjectKey, int32>& Entries = ScopeEntries();
    for (const UClass* Candidate = Class; Candidate != nullptr; Candidate = Candidate->GetSuperClass())
    {
        const UObject* GeneratedBy = Candidate->ClassGeneratedBy.Get();
        if (GeneratedBy == nullptr)
        {
            continue;
        }
        const int32* Count = Entries.Find(FObjectKey(GeneratedBy));
        if (Count != nullptr && *Count > 0)
        {
            return true;
        }
    }
    return false;
}
} // namespace UE::UnifiedScript::AuthoringActivation::Private

FUnifiedScriptAuthoringActivationScope::FUnifiedScriptAuthoringActivationScope(UObject* InOwner)
{
    check(IsInGameThread());
    check(IsValid(InOwner));

    OwnerKey = FObjectKey(InOwner);
    Owner.Reset(InOwner);
    int32& Count =
        UE::UnifiedScript::AuthoringActivation::Private::ScopeEntries().FindOrAdd(OwnerKey);
    ++Count;
    bRegistered = true;
}

FUnifiedScriptAuthoringActivationScope::~FUnifiedScriptAuthoringActivationScope()
{
    if (!bRegistered)
    {
        return;
    }
    check(IsInGameThread());

    TMap<FObjectKey, int32>& Entries =
        UE::UnifiedScript::AuthoringActivation::Private::ScopeEntries();
    int32* Count = Entries.Find(OwnerKey);
    if (Count != nullptr)
    {
        --*Count;
        if (*Count <= 0)
        {
            Entries.Remove(OwnerKey);
        }
    }
    Owner.Reset();
}

bool FUnifiedScriptAuthoringActivationScope::IsSuppressedForComponent(const UActorComponent& Component)
{
    check(IsInGameThread());
    if (UE::UnifiedScript::AuthoringActivation::Private::IsClassSuppressed(Component.GetClass()))
    {
        return true;
    }
    const AActor* OwnerActor = Component.GetOwner();
    return OwnerActor != nullptr &&
        UE::UnifiedScript::AuthoringActivation::Private::IsClassSuppressed(OwnerActor->GetClass());
}

#endif // WITH_EDITOR
