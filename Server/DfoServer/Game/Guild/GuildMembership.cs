namespace DfoServer.Game.Guild;

// 从公会与成员表读取的角色投影；在线时只保存在当前角色快照中。
internal sealed record GuildMembership(uint Id, string Name, byte Level, ushort MemberCount,
    string MasterName, string Notice, uint Experience, uint CreatedAtUnix)
{
    public GuildAppearance Appearance { get; init; } = new();
    public byte Tier { get; init; }
    public uint Crystals { get; init; }
    public byte Grade { get; init; } = 4;
}

internal sealed record GuildMembershipReceipt(DfoServer.Game.Characters.Character Character, long Generation, long SelectionEpoch);
