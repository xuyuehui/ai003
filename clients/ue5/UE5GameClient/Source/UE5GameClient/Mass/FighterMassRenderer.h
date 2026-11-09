// Copyright AI003. All rights reserved.

#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "MassArchetypeTypes.h"
#include "MassEntityHandle.h"
#include "MassEntityTypes.h"

#include "FighterMassRenderer.generated.h"

class ARemotePlayer;

/**
 * View layer for Mass room fighters: keeps one remote-player actor per entity and
 * pushes the entity's interpolated pose/state to it every tick.
 */
UCLASS()
class UE5GAMECLIENT_API AAI003FighterMassRenderer : public AActor
{
	GENERATED_BODY()

public:
	AAI003FighterMassRenderer();

	void Upsert(const FString& Id, int32 Slot, const FVector& Location, const FVector& Velocity,
		float Yaw, const FString& Anim, int32 Health, bool bAlive);
	void Remove(const FString& Id);
	void Clear();
	ARemotePlayer* FindView(const FString& Id) const;

protected:
	virtual void Tick(float DeltaSeconds) override;

private:
	UPROPERTY(Transient)
	TObjectPtr<USceneComponent> Root;

	TMap<FString, FMassEntityHandle> Entities;
	FMassArchetypeHandle FighterArchetype;
	TMap<FString, TWeakObjectPtr<ARemotePlayer>> Views;
};
