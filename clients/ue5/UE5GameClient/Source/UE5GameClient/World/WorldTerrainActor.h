#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "World/WorldData.h"
#include "WorldTerrainActor.generated.h"

class UProceduralMeshComponent;
class UStaticMeshComponent;
class UMaterialInterface;

/**
 * Shared world terrain with runtime streaming.
 *
 * The world is split into 8x8 chunks (256 m each at the default world). Chunks
 * are built lazily around the local player and rebuilt at a coarser LOD when
 * far away; chunks outside the view distance are hidden. All geometry still
 * comes from the same world arrays the Unity client uses.
 */
UCLASS()
class UE5GAMECLIENT_API AWorldTerrainActor : public AActor
{
	GENERATED_BODY()

public:
	AWorldTerrainActor();

	void Build(const FAI003WorldData& Data);

	virtual void Tick(float DeltaSeconds) override;

	static constexpr int32 ChunksPerAxis = 8;

	// Streaming tuning (centimetres).
	static constexpr float NearDistanceCm = 45000.f;   // LOD0, stride 1
	static constexpr float MidDistanceCm = 90000.f;    // LOD1, stride 2
	static constexpr float ViewDistanceCm = 140000.f;  // LOD2, stride 4
	static constexpr float UpdateInterval = 0.25f;

private:
	void UpdateStreaming(bool bInitial);
	void BuildChunk(int32 ChunkX, int32 ChunkY, int32 Lod, UProceduralMeshComponent* Mesh);
	int32 DesiredLod(float DistanceCm) const;

	UPROPERTY(VisibleAnywhere)
	TObjectPtr<USceneComponent> Root;

	UPROPERTY(Transient)
	TArray<TObjectPtr<UProceduralMeshComponent>> Chunks;

	UPROPERTY(Transient)
	TArray<int32> ChunkLods; // -1 = not built

	UPROPERTY(Transient)
	TObjectPtr<UStaticMeshComponent> Ocean;

	UPROPERTY(Transient)
	TObjectPtr<UMaterialInterface> TerrainMaterial;

	FAI003WorldData World;
	float NextUpdate = 0.f;
	float StatsTimer = 0.f;
	int32 ChunksBuilt = 0;
	int32 ChunksVisible = 0;
	int32 ChunksRebuilt = 0;
	float LastUpdateMs = 0.f;
};
