#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "WorldTerrainActor.generated.h"

class UProceduralMeshComponent;
class UStaticMeshComponent;
struct FAI003WorldData;

/**
 * Builds the shared world as 8x8 procedural mesh chunks with complex collision,
 * plus an ocean plane at sea level. Meshes are generated from the same arrays
 * the Unity client uses, so both engines show the same landscape.
 */
UCLASS()
class UE5GAMECLIENT_API AWorldTerrainActor : public AActor
{
	GENERATED_BODY()

public:
	AWorldTerrainActor();

	void Build(const FAI003WorldData& Data);

	static constexpr int32 ChunksPerAxis = 8;

private:
	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USceneComponent> Root;

	UPROPERTY(Transient)
	TArray<TObjectPtr<UProceduralMeshComponent>> Chunks;

	UPROPERTY(Transient)
	TObjectPtr<UStaticMeshComponent> Ocean;
};
