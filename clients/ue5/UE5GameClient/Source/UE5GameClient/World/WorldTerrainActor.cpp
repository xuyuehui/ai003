#include "World/WorldTerrainActor.h"

#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "Kismet/GameplayStatics.h"
#include "Materials/MaterialInterface.h"
#include "ProceduralMeshComponent.h"
#include "World/WorldData.h"

AWorldTerrainActor::AWorldTerrainActor()
{
	PrimaryActorTick.bCanEverTick = true;
	PrimaryActorTick.TickInterval = 0.1f;

	Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	RootComponent = Root;
}

void AWorldTerrainActor::Build(const FAI003WorldData& Data)
{
	World = Data;
	TerrainMaterial = LoadObject<UMaterialInterface>(
		nullptr, TEXT("/Game/AI003/M_TerrainVertexColor.M_TerrainVertexColor"));
	UMaterialInterface* OceanMaterial = LoadObject<UMaterialInterface>(
		nullptr, TEXT("/Game/AI003/M_Ocean.M_Ocean"));

	if (TerrainMaterial == nullptr)
	{
		UE_LOG(LogTemp, Error, TEXT("[AI003] missing material /Game/AI003/M_TerrainVertexColor; run scripts/setup-ue-assets.cmd"));
	}
	if (OceanMaterial == nullptr)
	{
		UE_LOG(LogTemp, Error, TEXT("[AI003] missing material /Game/AI003/M_Ocean; run scripts/setup-ue-assets.cmd"));
	}

	const int32 CellsPerChunk = (World.Resolution - 1) / ChunksPerAxis;
	if (CellsPerChunk * ChunksPerAxis != World.Resolution - 1)
	{
		UE_LOG(LogTemp, Error, TEXT("[AI003] resolution %d does not split into %d chunks"), World.Resolution, ChunksPerAxis);
		return;
	}

	const int32 Count = ChunksPerAxis * ChunksPerAxis;
	Chunks.SetNum(Count);
	ChunkLods.Init(-1, Count);
	ChunksBuilt = 0;
	ChunksVisible = 0;
	ChunksRebuilt = 0;

	// Ocean: the engine plane is 100x100 cm, so scale = size in metres * 4.
	Ocean = NewObject<UStaticMeshComponent>(this);
	Ocean->SetMobility(EComponentMobility::Movable);
	Ocean->RegisterComponent();
	Ocean->AttachToComponent(Root, FAttachmentTransformRules::KeepRelativeTransform);
	Ocean->SetCollisionEnabled(ECollisionEnabled::NoCollision);
	Ocean->SetCastShadow(false);

	if (UStaticMesh* PlaneMesh = LoadObject<UStaticMesh>(nullptr, TEXT("/Engine/BasicShapes/Plane.Plane")))
	{
		Ocean->SetStaticMesh(PlaneMesh);
	}
	if (OceanMaterial != nullptr)
	{
		Ocean->SetMaterial(0, OceanMaterial);
	}

	Ocean->SetRelativeLocation(FVector(0.f, 0.f, World.SeaLevel * 100.f));
	const float OceanScale = World.SizeMeters * 4.f;
	Ocean->SetRelativeScale3D(FVector(OceanScale, OceanScale, 1.f));

	// Build the near chunks immediately so the player has ground under them.
	UpdateStreaming(true);
	NextUpdate = UpdateInterval;
	UE_LOG(LogTemp, Display, TEXT("[AI003] terrain streaming ready: %d chunks, %dx%d vertices, %.0f m view"),
		Count, World.Resolution, World.Resolution, ViewDistanceCm / 100.f);
}

int32 AWorldTerrainActor::DesiredLod(float DistanceCm) const
{
	if (DistanceCm <= NearDistanceCm) return 0;
	if (DistanceCm <= MidDistanceCm) return 1;
	if (DistanceCm <= ViewDistanceCm) return 2;
	return -1;
}

void AWorldTerrainActor::BuildChunk(int32 ChunkX, int32 ChunkY, int32 Lod, UProceduralMeshComponent* Mesh)
{
	const int32 CellsPerChunk = (World.Resolution - 1) / ChunksPerAxis;
	const int32 Stride = Lod == 0 ? 1 : Lod == 1 ? 2 : 4;
	const int32 VertsPerAxis = CellsPerChunk / Stride + 1;
	const float Half = World.SizeMeters * 0.5f;
	const float Cell = World.CellSize;
	constexpr float MetresToCentimetres = 100.f;

	TArray<FVector> Vertices;
	TArray<FVector> Normals;
	TArray<FVector2D> UVs;
	TArray<FLinearColor> Colors;
	TArray<int32> Triangles;
	TArray<FProcMeshTangent> Tangents;

	const int32 VertexCount = VertsPerAxis * VertsPerAxis;
	Vertices.Reserve(VertexCount);
	Normals.Reserve(VertexCount);
	UVs.Reserve(VertexCount);
	Colors.Reserve(VertexCount);
	Triangles.Reserve((VertsPerAxis - 1) * (VertsPerAxis - 1) * 6);

	const int32 BaseX = ChunkX * CellsPerChunk;
	const int32 BaseY = ChunkY * CellsPerChunk;

	for (int32 LocalY = 0; LocalY < VertsPerAxis; ++LocalY)
	{
		// UE Y = -Unity z: rows run backwards inside the chunk so the landscape is
		// not mirrored while triangle winding stays intact.
		const int32 Gy = BaseY + (CellsPerChunk - LocalY * Stride);
		for (int32 LocalX = 0; LocalX < VertsPerAxis; ++LocalX)
		{
			const int32 Gx = BaseX + LocalX * Stride;
			const int32 Index = Gy * World.Resolution + Gx;

			Vertices.Add(FVector(
				LocalX * Stride * Cell * MetresToCentimetres,
				LocalY * Stride * Cell * MetresToCentimetres,
				World.Heights[Index] * MetresToCentimetres));
			Normals.Add(World.NormalAt(Gx, Gy));
			UVs.Add(FVector2D(
				static_cast<float>(Gx) / (World.Resolution - 1),
				static_cast<float>(Gy) / (World.Resolution - 1)));
			const FColor VertexColor = World.Colors[Index];
			Colors.Add(FLinearColor(
				VertexColor.R / 255.f,
				VertexColor.G / 255.f,
				VertexColor.B / 255.f,
				1.f));
		}
	}

	for (int32 LocalY = 0; LocalY < VertsPerAxis - 1; ++LocalY)
	{
		for (int32 LocalX = 0; LocalX < VertsPerAxis - 1; ++LocalX)
		{
			const int32 I0 = LocalY * VertsPerAxis + LocalX;
			const int32 I1 = I0 + 1;
			const int32 I2 = I0 + VertsPerAxis;
			const int32 I3 = I2 + 1;

			// Unreal front faces are clockwise seen from above; reverse of Unity's.
			Triangles.Add(I0);
			Triangles.Add(I2);
			Triangles.Add(I1);
			Triangles.Add(I1);
			Triangles.Add(I2);
			Triangles.Add(I3);
		}
	}

	Mesh->ClearAllMeshSections();
	Mesh->SetRelativeLocation(FVector(
		(-Half + ChunkX * CellsPerChunk * Cell) * MetresToCentimetres,
		(Half - (ChunkY * CellsPerChunk + CellsPerChunk) * Cell) * MetresToCentimetres,
		0.f));
	Mesh->bUseComplexAsSimpleCollision = Lod == 0;
	Mesh->SetCollisionEnabled(Lod == 0 ? ECollisionEnabled::QueryAndPhysics : ECollisionEnabled::NoCollision);
	Mesh->SetCollisionObjectType(ECC_WorldStatic);
	Mesh->SetCollisionResponseToAllChannels(Lod == 0 ? ECR_Block : ECR_Ignore);
	Mesh->SetCastShadow(Lod == 0);
	Mesh->CreateMeshSection_LinearColor(0, Vertices, Triangles, Normals, UVs, Colors, Tangents, Lod == 0, false);
	if (TerrainMaterial != nullptr)
	{
		Mesh->SetMaterial(0, TerrainMaterial);
	}
}

void AWorldTerrainActor::UpdateStreaming(bool bInitial)
{
	if (World.Resolution < 2) return;

	APawn* Player = UGameplayStatics::GetPlayerPawn(this, 0);
	if (Player == nullptr) return;

	const int32 CellsPerChunk = (World.Resolution - 1) / ChunksPerAxis;
	const float Half = World.SizeMeters * 0.5f;
	const float Cell = World.CellSize;
	const float ChunkSizeCm = CellsPerChunk * Cell * 100.f;
	const FVector PlayerLocation = Player->GetActorLocation();
	const double StartMs = FPlatformTime::Seconds() * 1000.0;

	int32 Budget = bInitial ? Chunks.Num() : 4;
	int32 Visible = 0;

	for (int32 ChunkY = 0; ChunkY < ChunksPerAxis; ++ChunkY)
	{
		for (int32 ChunkX = 0; ChunkX < ChunksPerAxis; ++ChunkX)
		{
			const int32 Index = ChunkY * ChunksPerAxis + ChunkX;
			const FVector ChunkCenter(
				(-Half + ChunkX * CellsPerChunk * Cell) * 100.f + ChunkSizeCm * 0.5f,
				(Half - (ChunkY * CellsPerChunk + CellsPerChunk) * Cell) * 100.f - ChunkSizeCm * 0.5f,
				0.f);

			const float Distance = FVector::Dist2D(PlayerLocation, ChunkCenter);
			const int32 Lod = DesiredLod(Distance);

			UProceduralMeshComponent* Mesh = Chunks[Index];
			if (Lod < 0)
			{
				if (Mesh != nullptr) Mesh->SetVisibility(false);
				continue;
			}

			if (ChunkLods[Index] != Lod && Budget > 0)
			{
				if (Mesh == nullptr)
				{
					Mesh = NewObject<UProceduralMeshComponent>(this);
					Mesh->SetMobility(EComponentMobility::Movable);
					Mesh->RegisterComponent();
					Mesh->AttachToComponent(Root, FAttachmentTransformRules::KeepRelativeTransform);
					Chunks[Index] = Mesh;
				}
				const bool bWasBuilt = ChunkLods[Index] >= 0;
				BuildChunk(ChunkX, ChunkY, Lod, Mesh);
				ChunkLods[Index] = Lod;
				if (bWasBuilt) ++ChunksRebuilt; else ++ChunksBuilt;
				--Budget;
			}

			if (Mesh != nullptr)
			{
				Mesh->SetVisibility(true);
				++Visible;
			}
		}
	}

	ChunksVisible = Visible;
	LastUpdateMs = static_cast<float>(FPlatformTime::Seconds() * 1000.0 - StartMs);
}

void AWorldTerrainActor::Tick(float DeltaSeconds)
{
	Super::Tick(DeltaSeconds);

	NextUpdate -= DeltaSeconds;
	if (NextUpdate <= 0.f)
	{
		NextUpdate = UpdateInterval;
		UpdateStreaming(ChunksBuilt == 0);
	}

	StatsTimer += DeltaSeconds;
	if (StatsTimer >= 2.f)
	{
		StatsTimer = 0.f;
		UE_LOG(LogTemp, Display,
			TEXT("[AI003] terrain stream: built %d, rebuilt %d, visible %d, last update %.2f ms, view %.0f m"),
			ChunksBuilt, ChunksRebuilt, ChunksVisible, LastUpdateMs, ViewDistanceCm / 100.f);
	}
}
