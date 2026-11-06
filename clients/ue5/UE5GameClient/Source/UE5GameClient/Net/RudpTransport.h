// Reliable UDP transport matching the Go gateway RUDP protocol.
#pragma once

#include "CoreMinimal.h"
#include "Containers/Map.h"

struct FRudpPacket
{
	TArray<uint8> Data;
	double SentAt = 0.0;
	int32 Tries = 0;
};

class FRudpTransport
{
public:
	bool Connect(const FString& Address, const FString& Token);
	void Close();
	bool IsActive() const { return Socket != nullptr; }
	void Send(const FString& Json);
	/** Polls the socket, handles acks/retransmits, returns decoded messages. */
	void Tick(float DeltaSeconds, TArray<FString>& OutMessages);

private:
	void SendPacket(uint8 Type, const TArray<uint8>& Payload);
	void HandlePacket(const TArray<uint8>& Packet, TArray<FString>& OutMessages);
	void Accept(const TArray<uint8>& Packet, int32 Length, TArray<FString>& OutMessages);
	void Deliver(const TArray<uint8>& Data, int32 Offset, int32 Length, TArray<FString>& OutMessages);

	FSocket* Socket = nullptr;
	TSharedPtr<FInternetAddr> Remote;
	uint32 Session = 0;
	uint32 SendSeq = 0;
	uint32 RecvNext = 1;
	uint32 LastAck = 0;
	double LastTick = 0.0;

	TMap<uint32, FRudpPacket> Unacked;
	TMap<uint32, TArray<uint8>> Reorder;
	TMap<uint16, TArray<TArray<uint8>>> Fragments;

	static constexpr uint32 Magic = 0x41335255;
	static constexpr uint8 Version = 1;
	static constexpr int32 HeaderSize = 28;
	static constexpr int32 MaxPayload = 1200;
	static constexpr int32 MaxTries = 12;

	static uint32 Crc32(const uint8* Data, int32 Count);
};
