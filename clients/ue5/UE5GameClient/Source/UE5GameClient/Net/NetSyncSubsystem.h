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

	TSharedPtr<IWebSocket> Socket;
	FTSTicker::FDelegateHandle TickerHandle;

	TMap<FString, TWeakObjectPtr<ARemotePlayer>> Remotes;

	FString Endpoint = TEXT("ws://127.0.0.1:8080/ws");
	FString PlayerId;
	int32 NextSeq = 0;

	double NextSendAt = 0.0;
	double NextJoinAt = 0.0;
	double NextConnectAt = 0.0;
	bool bConnecting = false;
	bool bConnected = false;
	bool bEnabled = true;
};
