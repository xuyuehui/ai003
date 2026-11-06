#include "Net/RudpTransport.h"

#include "Common/UdpSocketBuilder.h"
#include "Interfaces/IPv4/IPv4Endpoint.h"
#include "Sockets.h"
#include "SocketSubsystem.h"
#include "HAL/PlatformTime.h"

namespace
{
	void WriteU32(TArray<uint8>& B, int32 O, uint32 V)
	{
		B[O] = uint8(V);
		B[O + 1] = uint8(V >> 8);
		B[O + 2] = uint8(V >> 16);
		B[O + 3] = uint8(V >> 24);
	}
	uint32 ReadU32(const uint8* B, int32 O)
	{
		return uint32(B[O]) | (uint32(B[O + 1]) << 8) | (uint32(B[O + 2]) << 16) | (uint32(B[O + 3]) << 24);
	}
}

uint32 FRudpTransport::Crc32(const uint8* Data, int32 Count)
{
	static uint32 Table[256];
	static bool Built = false;
	if (!Built)
	{
		for (uint32 i = 0; i < 256; i++)
		{
			uint32 C = i;
			for (int32 K = 0; K < 8; K++) C = (C & 1) ? (0xEDB88320 ^ (C >> 1)) : (C >> 1);
			Table[i] = C;
		}
		Built = true;
	}
	uint32 Crc = 0xFFFFFFFF;
	for (int32 i = 0; i < Count; i++) Crc = Table[(Crc ^ Data[i]) & 0xFF] ^ (Crc >> 8);
	return ~Crc;
}

bool FRudpTransport::Connect(const FString& Address, const FString& Token)
{
	Close();
	FString Host = Address;
	int32 Port = 0;
	int32 Colon = INDEX_NONE;
	if (Address.FindLastChar(TEXT(':'), Colon))
	{
		Host = Address.Left(Colon);
		Port = FCString::Atoi(*Address.Mid(Colon + 1));
	}
	if (Port <= 0) return false;

	Remote = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM)->CreateInternetAddr();
	bool bValid = false;
	Remote->SetIp(*Host, bValid);
	Remote->SetPort(Port);
	if (!bValid) return false;

	Socket = FUdpSocketBuilder(TEXT("AI003RUDP")).AsNonBlocking().Build();
	if (!Socket) return false;

	Session = uint32(FMath::RandRange(1, 0x7FFFFFFF));
	SendSeq = 0;
	RecvNext = 1;
	LastAck = 0;
	Unacked.Reset();
	Reorder.Reset();
	Fragments.Reset();

	TArray<uint8> Hello;
	FTCHARToUTF8 Utf8(*FString::Printf(TEXT("{\"token\":\"%s\"}"), *Token));
	Hello.Append(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length());
	SendPacket(3, Hello);
	return true;
}

void FRudpTransport::Close()
{
	if (Socket)
	{
		Socket->Close();
		ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM)->DestroySocket(Socket);
		Socket = nullptr;
	}
}

void FRudpTransport::SendPacket(uint8 Type, const TArray<uint8>& Payload)
{
	if (!Socket || !Remote.IsValid()) return;
	SendSeq++;
	TArray<uint8> Packet;
	Packet.SetNumZeroed(HeaderSize + Payload.Num());
	WriteU32(Packet, 0, Magic);
	Packet[4] = Version;
	Packet[5] = Type;
	WriteU32(Packet, 6, Session);
	WriteU32(Packet, 10, SendSeq);
	WriteU32(Packet, 14, LastAck);
	WriteU32(Packet, 18, 0);
	Packet[22] = uint8(Payload.Num() & 0xFF);
	Packet[23] = uint8((Payload.Num() >> 8) & 0xFF);
	if (Payload.Num() > 0) FMemory::Memcpy(Packet.GetData() + HeaderSize, Payload.GetData(), Payload.Num());
	WriteU32(Packet, 24, Crc32(Packet.GetData(), 24));
	int32 Sent = 0;
	Socket->SendTo(Packet.GetData(), Packet.Num(), Sent, *Remote);
	FRudpPacket& P = Unacked.Add(SendSeq);
	P.Data = MoveTemp(Packet);
	P.SentAt = FPlatformTime::Seconds();
}

void FRudpTransport::Send(const FString& Json)
{
	if (!IsActive() || Json.IsEmpty()) return;
	FTCHARToUTF8 Utf8(*Json);
	const int32 Chunk = MaxPayload - HeaderSize - 4;
	const int32 Count = FMath::Max(1, FMath::DivideAndRoundUp(Utf8.Length(), Chunk));
	if (Count > 255) return;
	const uint16 MsgId = uint16(SendSeq);
	for (int32 i = 0; i < Count; i++)
	{
		const int32 Start = i * Chunk;
		const int32 Len = FMath::Min(Chunk, Utf8.Length() - Start);
		TArray<uint8> Payload;
		Payload.SetNumZeroed(4 + Len);
		Payload[0] = uint8(MsgId & 0xFF);
		Payload[1] = uint8(MsgId >> 8);
		Payload[2] = uint8(i);
		Payload[3] = uint8(Count);
		FMemory::Memcpy(Payload.GetData() + 4, reinterpret_cast<const uint8*>(Utf8.Get()) + Start, Len);
		SendPacket(1, Payload);
	}
}

void FRudpTransport::Tick(float DeltaSeconds, TArray<FString>& OutMessages)
{
	if (!Socket) return;
	uint8 Buffer[2048];
	TSharedRef<FInternetAddr> From = ISocketSubsystem::Get(PLATFORM_SOCKETSUBSYSTEM)->CreateInternetAddr();
	for (int32 Guard = 0; Guard < 32; Guard++)
	{
		int32 Read = 0;
		if (!Socket->RecvFrom(Buffer, sizeof(Buffer), Read, *From) || Read <= 0) break;
		TArray<uint8> Packet;
		Packet.Append(Buffer, Read);
		HandlePacket(Packet, OutMessages);
	}

	const double Now = FPlatformTime::Seconds();
	TArray<uint32> Expired;
	for (const TPair<uint32, FRudpPacket>& Pair : Unacked)
	{
		const double Wait = 0.12 * (Pair.Value.Tries + 1);
		if (Now - Pair.Value.SentAt < Wait) continue;
		if (Pair.Value.Tries >= MaxTries) { Expired.Add(Pair.Key); continue; }
		FRudpPacket& P = Unacked.FindChecked(Pair.Key);
		P.Tries++;
		P.SentAt = Now;
		int32 Sent = 0;
		Socket->SendTo(P.Data.GetData(), P.Data.Num(), Sent, *Remote);
	}
	for (uint32 Key : Expired) Unacked.Remove(Key);
}

void FRudpTransport::HandlePacket(const TArray<uint8>& Packet, TArray<FString>& OutMessages)
{
	if (Packet.Num() < HeaderSize) return;
	if (ReadU32(Packet.GetData(), 0) != Magic || Packet[4] != Version) return;
	if (ReadU32(Packet.GetData(), 6) != Session) return;
	if (ReadU32(Packet.GetData(), 24) != Crc32(Packet.GetData(), 24)) return;

	const uint8 Type = Packet[5];
	const uint32 Seq = ReadU32(Packet.GetData(), 10);
	const uint32 Ack = ReadU32(Packet.GetData(), 14);
	const int32 Len = Packet[22] | (Packet[23] << 8);
	if (Ack != 0) Unacked.Remove(Ack);
	if (Type == 2) return;

	LastAck = Seq;
	if (Seq == RecvNext)
	{
		Accept(Packet, Len, OutMessages);
		while (TArray<uint8>* Next = Reorder.Find(RecvNext))
		{
			TArray<uint8> Copy = *Next;
			Reorder.Remove(RecvNext);
			Accept(Copy, Copy.Num() - HeaderSize, OutMessages);
		}
	}
	else if (Seq > RecvNext)
	{
		Reorder.Add(Seq, Packet);
	}
	SendPacket(2, TArray<uint8>());
}

void FRudpTransport::Accept(const TArray<uint8>& Packet, int32 Length, TArray<FString>& OutMessages)
{
	RecvNext++;
	if (Length < 4) return;
	const uint8* P = Packet.GetData() + HeaderSize;
	const uint16 MsgId = uint16(P[0] | (P[1] << 8));
	const int32 Index = P[2];
	const int32 Count = P[3];
	if (Count <= 1)
	{
		Deliver(Packet, HeaderSize + 4, Length - 4, OutMessages);
		return;
	}
	TArray<TArray<uint8>>& Parts = Fragments.FindOrAdd(MsgId);
	if (Parts.Num() != Count) Parts.SetNum(Count);
	if (Index >= Count || Parts[Index].Num() > 0) return;
	Parts[Index].Append(P + 4, Length - 4);
	for (const TArray<uint8>& Part : Parts) if (Part.Num() == 0) return;
	TArray<uint8> Joined;
	for (const TArray<uint8>& Part : Parts) Joined.Append(Part);
	Fragments.Remove(MsgId);
	Deliver(Joined, 0, Joined.Num(), OutMessages);
}

void FRudpTransport::Deliver(const TArray<uint8>& Data, int32 Offset, int32 Length, TArray<FString>& OutMessages)
{
	FUTF8ToTCHAR Utf8(reinterpret_cast<const ANSICHAR*>(Data.GetData() + Offset), Length);
	FString Message;
	Message.Reserve(Length);
	for (int32 i = 0; i < Utf8.Length(); i++) Message.AppendChar(Utf8.Get()[i]);
	OutMessages.Add(MoveTemp(Message));
}
