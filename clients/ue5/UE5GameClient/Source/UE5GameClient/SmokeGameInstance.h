// Copyright AI003. All rights reserved.

#pragma once

#include "CoreMinimal.h"
#include "Containers/Ticker.h"
#include "Engine/GameInstance.h"

#include "SmokeGameInstance.generated.h"

class IWebSocket;

/**
 * Headless connectivity check for the UE5 client.
 *
 * Run it with (see scripts/smoke-ue5.ps1):
 *
 *   UnrealEditor-Cmd.exe <project> -game -nullrhi -nosplash -unattended \
 *       -AI003Smoke -AI003Url=ws://127.0.0.1:8080/ws
 *
 * The instance connects to the Go gateway, waits for the welcome frame, sends
 * three pings, requires three matching pongs, then exits the process with
 * code 0 (success) or 1 (failure).
 */
UCLASS()
class UE5GAMECLIENT_API USmokeGameInstance : public UGameInstance
{
	GENERATED_BODY()

public:
	virtual void Init() override;
	virtual void Shutdown() override;

private:
	void StartSmoke();
	void HandleConnected();
	void HandleConnectionError(const FString& Error);
	void HandleMessage(const FString& Message);
	void SendPing();
	bool Tick(float DeltaSeconds);
	void Finish(bool bSuccess, const FString& Reason);

	TSharedPtr<IWebSocket> Socket;
	FTSTicker::FDelegateHandle TickerHandle;

	FString Endpoint;
	double StartedAt = 0.0;
	int32 NextSeq = 0;
	int32 Pongs = 0;
	int32 RequiredPongs = 3;
	bool bFinished = false;

	static constexpr double TimeoutSeconds = 30.0;
};
