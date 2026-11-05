#include "World/WorldData.h"

#include "Dom/JsonObject.h"
#include "HAL/FileManager.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace
{
	bool ReadFloatArray(const FString& Path, int32 ExpectedCount, TArray<float>& Out, FString& OutError)
	{
		TArray<uint8> Bytes;
		if (!FFileHelper::LoadFileToArray(Bytes, *Path))
		{
			OutError = FString::Printf(TEXT("cannot read %s"), *Path);
			return false;
		}
		if (Bytes.Num() != ExpectedCount * 4)
		{
			OutError = FString::Printf(TEXT("%s has %d bytes, expected %d"), *Path, Bytes.Num(), ExpectedCount * 4);
			return false;
		}

		Out.SetNumUninitialized(ExpectedCount);
		FMemory::Memcpy(Out.GetData(), Bytes.GetData(), Bytes.Num());
		return true;
	}
}

bool FAI003WorldData::Load(const FString& Directory, FString& OutError)
{
	const FString JsonPath = FPaths::Combine(Directory, TEXT("world.json"));
	FString Json;
	if (!FFileHelper::LoadFileToString(Json, *JsonPath))
	{
		OutError = FString::Printf(TEXT("cannot read %s (run scripts/generate-world.cmd)"), *JsonPath);
		return false;
	}

	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		OutError = FString::Printf(TEXT("invalid json in %s"), *JsonPath);
		return false;
	}

	double Number = 0.0;
	if (Root->TryGetNumberField(TEXT("version"), Number)) Version = static_cast<int32>(Number);
	Root->TryGetStringField(TEXT("name"), Name);
	if (Root->TryGetNumberField(TEXT("seed"), Number)) Seed = static_cast<int64>(Number);
	if (Root->TryGetNumberField(TEXT("sizeMeters"), Number)) SizeMeters = static_cast<float>(Number);
	if (Root->TryGetNumberField(TEXT("resolution"), Number)) Resolution = static_cast<int32>(Number);
	if (Root->TryGetNumberField(TEXT("cellSize"), Number)) CellSize = static_cast<float>(Number);
	if (Root->TryGetNumberField(TEXT("seaLevel"), Number)) SeaLevel = static_cast<float>(Number);

	const TSharedPtr<FJsonObject>* SpawnObject = nullptr;
	if (Root->TryGetObjectField(TEXT("spawn"), SpawnObject) && SpawnObject != nullptr)
	{
		double X = 0.0, Y = 0.0, Z = 0.0;
		(*SpawnObject)->TryGetNumberField(TEXT("x"), X);
		(*SpawnObject)->TryGetNumberField(TEXT("y"), Y);
		(*SpawnObject)->TryGetNumberField(TEXT("z"), Z);
		SpawnMeters = FVector(X, Y, Z);
	}

	if (Resolution < 2)
	{
		OutError = TEXT("world resolution is invalid");
		return false;
	}

	const int32 Count = Resolution * Resolution;
	if (!ReadFloatArray(FPaths::Combine(Directory, TEXT("heightmap.f32")), Count, Heights, OutError))
	{
		return false;
	}

	TArray<uint8> ColorBytes;
	if (!FFileHelper::LoadFileToArray(ColorBytes, *FPaths::Combine(Directory, TEXT("colors.rgba8"))))
	{
		OutError = TEXT("cannot read colors.rgba8");
		return false;
	}
	if (ColorBytes.Num() != Count * 4)
	{
		OutError = FString::Printf(TEXT("colors.rgba8 has %d bytes, expected %d"), ColorBytes.Num(), Count * 4);
		return false;
	}

	Colors.SetNumUninitialized(Count);
	for (int32 i = 0; i < Count; ++i)
	{
		Colors[i] = FColor(ColorBytes[i * 4 + 0], ColorBytes[i * 4 + 1], ColorBytes[i * 4 + 2], ColorBytes[i * 4 + 3]);
	}

	if (!FFileHelper::LoadFileToArray(Biomes, *FPaths::Combine(Directory, TEXT("biomes.r8"))))
	{
		OutError = TEXT("cannot read biomes.r8");
		return false;
	}
	if (Biomes.Num() != Count)
	{
		OutError = FString::Printf(TEXT("biomes.r8 has %d bytes, expected %d"), Biomes.Num(), Count);
		return false;
	}

	return true;
}

float FAI003WorldData::HeightAt(float X, float Y) const
{
	const float Half = SizeMeters * 0.5f;
	const float Fx = (X + Half) / CellSize;
	const float Fy = (Y + Half) / CellSize;
	if (Fx < 0.f || Fy < 0.f || Fx > Resolution - 1 || Fy > Resolution - 1)
	{
		return SeaLevel - 50.f;
	}

	const int32 X0 = FMath::FloorToInt(Fx);
	const int32 Y0 = FMath::FloorToInt(Fy);
	const int32 X1 = FMath::Min(X0 + 1, Resolution - 1);
	const int32 Y1 = FMath::Min(Y0 + 1, Resolution - 1);
	const float Tx = Fx - X0;
	const float Ty = Fy - Y0;

	const float H00 = Heights[Y0 * Resolution + X0];
	const float H10 = Heights[Y0 * Resolution + X1];
	const float H01 = Heights[Y1 * Resolution + X0];
	const float H11 = Heights[Y1 * Resolution + X1];

	return FMath::Lerp(FMath::Lerp(H00, H10, Tx), FMath::Lerp(H01, H11, Tx), Ty);
}

FVector FAI003WorldData::NormalAt(int32 Ix, int32 Iy) const
{
	const int32 X0 = FMath::Max(Ix - 1, 0);
	const int32 X1 = FMath::Min(Ix + 1, Resolution - 1);
	const int32 Y0 = FMath::Max(Iy - 1, 0);
	const int32 Y1 = FMath::Min(Iy + 1, Resolution - 1);

	const float Dhdx = (Heights[Iy * Resolution + X1] - Heights[Iy * Resolution + X0]) / ((X1 - X0) * CellSize);
	const float Dhdy = (Heights[Y1 * Resolution + Ix] - Heights[Y0 * Resolution + Ix]) / ((Y1 - Y0) * CellSize);

	// UE: X and Y are the horizontal axes, Z is up. UE Y = -Unity z, so the
	// gradient along Y flips sign compared with the heightmap rows.
	return FVector(-Dhdx, Dhdy, 1.f).GetSafeNormal();
}
