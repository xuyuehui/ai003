#include "Mass/FighterMassRenderer.h"

#include "Mass/FighterMass.h"
#include "MassEntityManager.h"
#include "MassEntitySubsystem.h"
#include "Net/RemotePlayer.h"

AAI003FighterMassRenderer::AAI003FighterMassRenderer()
{
	PrimaryActorTick.bCanEverTick = true;
	Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	SetRootComponent(Root);
}

void AAI003FighterMassRenderer::Upsert(const FString& Id, int32 Slot, const FVector& Location,
	const FVector& Velocity, float Yaw, const FString& Anim, int32 Health, bool bAlive)
{
	UMassEntitySubsystem* Mass = GetWorld() ? GetWorld()->GetSubsystem<UMassEntitySubsystem>() : nullptr;
	if (Mass == nullptr)
	{
		return;
	}
	FMassEntityManager& Manager = Mass->GetMutableEntityManager();
	FMassEntityHandle& Handle = Entities.FindOrAdd(Id);
	if (!Manager.IsEntityValid(Handle))
	{
		if (!FighterArchetype.IsValid())
		{
			const UScriptStruct* Types[] = { FAI003FighterFragment::StaticStruct(), FAI003RemoteFighterTag::StaticStruct() };
			FighterArchetype = Manager.CreateArchetype(TConstArrayView<const UScriptStruct*>(Types, 2));
		}
		Handle = Manager.CreateEntity(FighterArchetype);
		UE_LOG(LogTemp, Display, TEXT("[AI003][MASS] entity created for %s"), *Id);
		FAI003FighterFragment& Fresh = Manager.GetFragmentDataChecked<FAI003FighterFragment>(Handle);
		Fresh.RenderLocation = Location;
		Fresh.RenderYaw = Yaw;
		Fresh.LastUpdate = FPlatformTime::Seconds();
	}
	FAI003FighterFragment& Frag = Manager.GetFragmentDataChecked<FAI003FighterFragment>(Handle);
	Frag.Slot = Slot;
	Frag.TargetLocation = Location;
	Frag.Velocity = Velocity;
	Frag.Yaw = Yaw;
	Frag.Health = Health;
	Frag.bAlive = bAlive;
	Frag.Anim = FName(*Anim);
	Frag.LastUpdate = FPlatformTime::Seconds();
}

void AAI003FighterMassRenderer::Remove(const FString& Id)
{
	if (TWeakObjectPtr<ARemotePlayer>* View = Views.Find(Id))
	{
		if (View->IsValid())
		{
			View->Get()->Destroy();
		}
		Views.Remove(Id);
	}
	if (FMassEntityHandle* Handle = Entities.Find(Id))
	{
		if (UMassEntitySubsystem* Mass = GetWorld() ? GetWorld()->GetSubsystem<UMassEntitySubsystem>() : nullptr)
		{
			FMassEntityManager& Manager = Mass->GetMutableEntityManager();
			if (Manager.IsEntityValid(*Handle))
			{
				Manager.DestroyEntity(*Handle);
			}
		}
		Entities.Remove(Id);
	}
}

void AAI003FighterMassRenderer::Clear()
{
	for (const TPair<FString, TWeakObjectPtr<ARemotePlayer>>& Pair : Views)
	{
		if (Pair.Value.IsValid())
		{
			Pair.Value->Destroy();
		}
	}
	Views.Reset();
	if (UMassEntitySubsystem* Mass = GetWorld() ? GetWorld()->GetSubsystem<UMassEntitySubsystem>() : nullptr)
	{
		FMassEntityManager& Manager = Mass->GetMutableEntityManager();
		for (const TPair<FString, FMassEntityHandle>& Pair : Entities)
		{
			if (Manager.IsEntityValid(Pair.Value))
			{
				Manager.DestroyEntity(Pair.Value);
			}
		}
	}
	Entities.Reset();
}

ARemotePlayer* AAI003FighterMassRenderer::FindView(const FString& Id) const
{
	const TWeakObjectPtr<ARemotePlayer>* View = Views.Find(Id);
	return View ? View->Get() : nullptr;
}

void AAI003FighterMassRenderer::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);

	UMassEntitySubsystem* Mass = GetWorld() ? GetWorld()->GetSubsystem<UMassEntitySubsystem>() : nullptr;
	if (Mass == nullptr)
	{
		return;
	}
	FMassEntityManager& Manager = Mass->GetMutableEntityManager();
	for (const TPair<FString, FMassEntityHandle>& Pair : Entities)
	{
		if (!Manager.IsEntityValid(Pair.Value))
		{
			continue;
		}
		const FAI003FighterFragment* Frag = Manager.GetFragmentDataPtr<FAI003FighterFragment>(Pair.Value);
		if (Frag == nullptr)
		{
			continue;
		}

		TWeakObjectPtr<ARemotePlayer>& ViewPtr = Views.FindOrAdd(Pair.Key);
		ARemotePlayer* View = ViewPtr.Get();
		if (View == nullptr)
		{
			View = GetWorld()->SpawnActor<ARemotePlayer>(ARemotePlayer::StaticClass(),
				Frag->RenderLocation, FRotator(0.f, Frag->RenderYaw, 0.f));
			ViewPtr = View;
			if (View != nullptr)
			{
				View->SetMassDriven(true);
			}
		}
		if (View != nullptr)
		{
			View->SetMassPose(Frag->RenderLocation, Frag->RenderYaw);
			View->SetMassVisual(Frag->Anim.ToString(), Frag->bAlive);
		}
	}
}
