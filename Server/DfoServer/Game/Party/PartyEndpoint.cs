namespace DfoServer.Game.Party;

// IPv4 保存原始四字节的小端值；端口在领域内统一使用主机字节序。
internal sealed record PartyEndpoint(byte Nat, uint InnerAddress, uint OuterAddress, ushort Port, uint Mtu);
internal sealed record PartyEndpointRow(ushort Actor, PartyEndpoint Endpoint, uint Account);
