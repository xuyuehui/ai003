#pragma once

#include "CoreMinimal.h"
#include "Engine/EngineTypes.h"
#include "GameFramework/GameModeBase.h"
#include "WorldGameMode.generated.h"

class AWorldTerrainActor;
class AWorldCharacter;
struct FAI003WorldData;

/**
 * Loads the shared world, spawns terrain + sky + sun, and places the player at
 * the world's spawn point instead of needing a hand-placed PlayerStart.
 *
 * Optional automation flags:
 *   -AI003WorldSmoke   verify the player spawned on the terrain and can run, then exit
 *   -AI003WorldShot    save artifacts/world-ue5.png and exit
 *   -AI003DemoShot     fixed-camera frame for the Unity/UE comparison, then exit
 */
UCLASS()
class UE5GAMECLIENT_API AWorldGameMode : public AGameModeBase
{
	GENERATED_BODY()

public:
	AWorldGameMode();

	virtual void InitGame(const FString& MapName, const FString& Options, FString& ErrorMessage) override;
	virtual void StartPlay() override;
	virtual void HandleStartingNewPlayer_Implementation(APlayerController* NewPlayer) override;

private:
	void SpawnEnvironment();

	void BeginAutomation();
	void BeginDemoShot();
	void BeginRoomShot();
	void RunSmokeStage1();
	void RunSmokeStage2();
	void ExitWith(int32 Code);

	TSharedPtr<FAI003WorldData> WorldData;

	UPROPERTY(Transient)
	TObjectPtr<AWorldTerrainActor> TerrainActor;

	FVector SpawnLocation = FVector::ZeroVector;

	bool bWorldSmoke = false;
	bool bWorldShot = false;
	bool bDemoShot = false;
	bool bRoomShot = false;
	FString ShotPath;
	FString DemoShotPath;
	FString RoomShotPath;
	FTimerHandle AutomationTimer;
	FVector SmokeStartLocation = FVector::ZeroVector;
	TWeakObjectPtr<AWorldCharacter> SmokePlayer;
};
