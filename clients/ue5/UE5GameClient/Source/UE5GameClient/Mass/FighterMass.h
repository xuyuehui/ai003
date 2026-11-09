// Copyright AI003. All rights reserved.

#pragma once

#include "CoreMinimal.h"
#include "MassEntityTypes.h"
#include "MassEntityQuery.h"
#include "MassProcessor.h"

#include "FighterMass.generated.h"

/** Authoritative + interpolated state of one remote room fighter, stored in Mass. */
USTRUCT()
struct FAI003FighterFragment : public FMassFragment
{
	GENERATED_BODY()

	UPROPERTY()
	int32 Slot = 0;

	UPROPERTY()
	FVector TargetLocation = FVector::ZeroVector;

	UPROPERTY()
	FVector RenderLocation = FVector::ZeroVector;

	UPROPERTY()
	FVector Velocity = FVector::ZeroVector;

	UPROPERTY()
	float Yaw = 0.f;

	UPROPERTY()
	float RenderYaw = 0.f;

	UPROPERTY()
	int32 Health = 100;

	UPROPERTY()
	bool bAlive = true;

	UPROPERTY()
	FName Anim = TEXT("idle");

	UPROPERTY()
	double LastUpdate = 0.0;
};

/** Tag for room fighters represented by Mass entities. */
USTRUCT()
struct FAI003RemoteFighterTag : public FMassTag
{
	GENERATED_BODY()
};

/**
 * Moves RenderLocation towards the latest server target (100 ms interpolation)
 * and extrapolates up to 500 ms using the last velocity when snapshots stop.
 */
UCLASS()
class UE5GAMECLIENT_API UAI003FighterInterpProcessor : public UMassProcessor
{
	GENERATED_BODY()

public:
	UAI003FighterInterpProcessor();

protected:
	virtual void ConfigureQueries(const TSharedRef<FMassEntityManager>& EntityManager) override;
	virtual void Execute(FMassEntityManager& EntityManager, FMassExecutionContext& Context) override;

private:
	FMassEntityQuery EntityQuery;
};
