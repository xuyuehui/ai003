// Copyright AI003. All rights reserved.

#pragma once

#include "CoreMinimal.h"
#include "Containers/Ticker.h"
#include "Subsystems/GameInstanceSubsystem.h"

#include "NetSyncSubsystem.generated.h"

class IWebSocket;
class ARemotePlayer;
class FJsonObject;

/**
 * Gateway state sync for the UE5 client (protocol v2).
 *
 * Owns one WebSocket connection: joins as a player, reports the local pawn at
 * 10 Hz (metres, canonical Unity-style axes), and mirrors every snapshot player
 * as an ARemotePlayer. Runs automatically in game worlds; disable with
 * -AI003NoSync.
 */
UCLASS()
class UE5GAMECLIENT_API UNetSyncSubsystem : public UGameInstanceSubsystem
{
	GENERATED_BODY()

public:
	virtual void Initialize(FSubsystemCollectionBase& Collection) override;
	virtual void Deinitialize() override;

	bool IsJoined() const { return !PlayerId.IsEmpty(); }
	int32 GetRemoteCount() const { return Remotes.Num(); }
	const FString& GetPlayerId() const { return PlayerId; }

private:
	void Connect();
	void HandleConnected();
	void HandleConnectionError(const FString& Error);
	void HandleMessage(const FString& Message);
	void SendJoin();
	void SendMove();
	void Send(const FString& Type, const TSharedPtr<FJsonObject>& Body);
	void ApplySnapshot(const TSharedPtr<FJsonObject>& Body);
	void RemoveStale(const TSet<FString>& Seen);
	bool Tick(float DeltaSeconds);

	// Combat room (protocol v3).
	struct FRoomCommand
	{
		int32 DX = 0;
		int32 DZ = 0;
		bool bAttack = false;
	};

	void SendRoomEnter();
	void SendRoomInput(int32 Tick, const FRoomCommand& Command);
	void EnterRoom(const TSharedPtr<FJsonObject>& Body);
	void ApplyRoomFrame(const TSharedPtr<FJsonObject>& Body);
	void HandleRoomEvents(const TSharedPtr<FJsonObject>& Body);
	void PredictTick();
	void ApplyPrediction();
	void Reconcile(const FVector& ServerPosition, int32 ServerTick);
	void StepPrediction(const FRoomCommand& Command);
	void PrunePrediction(int32 ServerTick);
	FRoomCommand ReadRoomCommand() const;
	static FString LoadOrCreateClientId();
	class AWorldCharacter* LocalCharacter() const;
	FVector RoomToWorld(double XMeters, double ZMeters) const;

	TSharedPtr<IWebSocket> Socket;
	FTSTicker::FDelegateHandle TickerHandle;

	TMap<FString, TWeakObjectPtr<ARemotePlayer>> Remotes;
	TMap<FString, TWeakObjectPtr<ARemotePlayer>> RoomRemotes;
	TMap<FString, FVector> RoomLastPositions;

	FString Endpoint = TEXT("ws://127.0.0.1:8080/ws");
	FString PlayerId;
	FString ClientID;
	int32 NextSeq = 0;

	double NextSendAt = 0.0;
	double NextJoinAt = 0.0;
	double NextConnectAt = 0.0;
	bool bConnecting = false;
	bool bConnected = false;
	bool bEnabled = true;

	bool bRoomEnabled = false;
	bool bRoomActive = false;
	bool bRoomBot = false;
	FString RoomId;
	int32 RoomTick = 0;
	int32 RoomHP = 100;
	int32 RoomMaxHP = 100;
	int32 RoomFighters = 0;
	double RoomLocalX = 0.0;
	double RoomLocalZ = 0.0;
	double RoomSendInterval = 0.05;
	double NextRoomSendAt = 0.0;
	double NextRoomEnterAt = 0.0;
	double NextHudAt = 0.0;
	float RoomYaw = 0.f;
	FVector RoomCenter = FVector::ZeroVector;

	TMap<int32, FRoomCommand> PendingCommands;
	TMap<int32, FVector> PredictedHistory;
	FVector PredictedPos = FVector::ZeroVector;
	int32 PredictedTick = 0;
	int32 ServerTick = 0;
	int32 Corrections = 0;
	double NextPredictAt = 0.0;
	float LastErrorCm = 0.f;
	float RoomSpeedCm = 25.f;
	float RoomDiagCm = 17.7f;
};
