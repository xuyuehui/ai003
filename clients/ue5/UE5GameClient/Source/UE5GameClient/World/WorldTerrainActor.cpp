#include "World/WorldTerrainActor.h"

#include "Components/StaticMeshComponent.h"
#include "Engine/StaticMesh.h"
#include "Materials/MaterialInterface.h"
#include "ProceduralMeshComponent.h"
#include "World/WorldData.h"

AWorldTerrainActor::AWorldTerrainActor()
{
	PrimaryActorTick.bCanEverTick = false;

	Root = CreateDefaultSubobject<USceneComponent>(TEXT("Root"));
	RootComponent = Root;
}

void AWorldTerrainActor::Build(const FAI003WorldData& Data)
{
	UMaterialInterface* TerrainMaterial = LoadObject<UMaterialInterface>(
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

	const int32 CellsPerChunk = (Data.Resolution - 1) / ChunksPerAxis;
	if (CellsPerChunk * ChunksPerAxis != Data.Resolution - 1)
	{
		UE_LOG(LogTemp, Error, TEXT("[AI003] resolution %d does not split into %d chunks"), Data.Resolution, ChunksPerAxis);
		return;
	}

	const int32 VertsPerAxis = CellsPerChunk + 1;
	const float Half = Data.SizeMeters * 0.5f;
	const float Cell = Data.CellSize;
	constexpr float MetresToCentimetres = 100.f;

	Chunks.Reserve(ChunksPerAxis * ChunksPerAxis);

	for (int32 ChunkY = 0; ChunkY < ChunksPerAxis; ++ChunkY)
	{
		for (int32 ChunkX = 0; ChunkX < ChunksPerAxis; ++ChunkX)
		{
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
			Triangles.Reserve(CellsPerChunk * CellsPerChunk * 6);

			const int32 BaseX = ChunkX * CellsPerChunk;
			const int32 BaseY = ChunkY * CellsPerChunk;

			for (int32 LocalY = 0; LocalY < VertsPerAxis; ++LocalY)
			{
				const int32 Gy = BaseY + LocalY;
				for (int32 LocalX = 0; LocalX < VertsPerAxis; ++LocalX)
				{
					const int32 Gx = BaseX + LocalX;
					const int32 Index = Gy * Data.Resolution + Gx;

					Vertices.Add(FVector(
						LocalX * Cell * MetresToCentimetres,
						LocalY * Cell * MetresToCentimetres,
						Data.Heights[Index] * MetresToCentimetres));
					Normals.Add(Data.NormalAt(Gx, Gy));
					UVs.Add(FVector2D(
						static_cast<float>(Gx) / (Data.Resolution - 1),
						static_cast<float>(Gy) / (Data.Resolution - 1)));
					// The world stores linear colour bytes (matching Unity). Feed them
					// straight through with bSRGBConversion=false so UE does not decode
					// them a second time.
					const FColor VertexColor = Data.Colors[Index];
					Colors.Add(FLinearColor(
						VertexColor.R / 255.f,
						VertexColor.G / 255.f,
						VertexColor.B / 255.f,
						1.f));
				}
			}

			for (int32 LocalY = 0; LocalY < CellsPerChunk; ++LocalY)
			{
				for (int32 LocalX = 0; LocalX < CellsPerChunk; ++LocalX)
				{
					const int32 I0 = LocalY * VertsPerAxis + LocalX;
					const int32 I1 = I0 + 1;
					const int32 I2 = I0 + VertsPerAxis;
					const int32 I3 = I2 + 1;

					Triangles.Add(I0);
					Triangles.Add(I1);
					Triangles.Add(I2);
					Triangles.Add(I1);
					Triangles.Add(I3);
					Triangles.Add(I2);
				}
			}

			UProceduralMeshComponent* Mesh = NewObject<UProceduralMeshComponent>(this);
			Mesh->SetMobility(EComponentMobility::Movable);
			Mesh->RegisterComponent();
			Mesh->AttachToComponent(Root, FAttachmentTransformRules::KeepRelativeTransform);
			Mesh->SetRelativeLocation(FVector(
				(-Half + ChunkX * CellsPerChunk * Cell) * MetresToCentimetres,
				(-Half + ChunkY * CellsPerChunk * Cell) * MetresToCentimetres,
				0.f));

			Mesh->bUseComplexAsSimpleCollision = true;
			Mesh->SetCollisionEnabled(ECollisionEnabled::QueryAndPhysics);
			Mesh->SetCollisionObjectType(ECC_WorldStatic);
			Mesh->SetCollisionResponseToAllChannels(ECR_Block);
			Mesh->SetCastShadow(true);
			Mesh->CreateMeshSection_LinearColor(0, Vertices, Triangles, Normals, UVs, Colors, Tangents, true, false);

			if (TerrainMaterial != nullptr)
			{
				Mesh->SetMaterial(0, TerrainMaterial);
			}

			Chunks.Add(Mesh);
		}
	}

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

	Ocean->SetRelativeLocation(FVector(0.f, 0.f, Data.SeaLevel * MetresToCentimetres));
	const float OceanScale = Data.SizeMeters * 4.f;
	Ocean->SetRelativeScale3D(FVector(OceanScale, OceanScale, 1.f));

	UE_LOG(LogTemp, Display, TEXT("[AI003] terrain built: %d chunks, %dx%d vertices, %.0f m"),
		Chunks.Num(), Data.Resolution, Data.Resolution, Data.SizeMeters);
}
