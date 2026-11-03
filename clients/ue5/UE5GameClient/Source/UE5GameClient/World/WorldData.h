#pragma once

#include "CoreMinimal.h"

/**
 * The shared world arrays, loaded from <Project>/WorldData (written by
 * server/cmd/worldgen). Coordinates are metres in world space with Y up, the
 * same layout the Unity client consumes.
 */
struct FAI003WorldData
{
	int32 Version = 1;
	FString Name;
	int64 Seed = 0;
	float SizeMeters = 2048.f;
	int32 Resolution = 513;
	float CellSize = 4.f;
	float SeaLevel = 0.f;
	/** x, height, z in metres (Y is up in the data files). */
	FVector SpawnMeters = FVector::ZeroVector;

	TArray<float> Heights;
	TArray<FColor> Colors;
	TArray<uint8> Biomes;

	bool Load(const FString& Directory, FString& OutError);

	/** Bilinear height in metres, matching the Go and Unity samplers. */
	float HeightAt(float X, float Y) const;

	/** Up-facing normal from central differences (UE axes: X, Y horizontal, Z up). */
	FVector NormalAt(int32 Ix, int32 Iy) const;

	/** Spawn point converted to Unreal coordinates, in metres. */
	FVector SpawnUE() const { return FVector(SpawnMeters.X, SpawnMeters.Z, SpawnMeters.Y); }
};
