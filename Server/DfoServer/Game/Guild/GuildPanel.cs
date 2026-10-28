using DfoServer.Game.Accounts;
using DfoServer.Game.Session;

namespace DfoServer.Game.Guild;

internal sealed record GuildPanelMember(ushort Id, string Account, string Name, ushort Level, byte Job,
    byte GrowType, byte Grade, string Memo, uint Contribution, long LastLoginUnix)
{
    public byte Channel { get; init; } = byte.MaxValue;
    public byte Region { get; init; }
    public uint WeeklyContribution { get; init; }
}

internal sealed record GuildPanel(GuildMembership Guild, ushort MasterId, GuildPanelMember[] Members)
{
    public GuildManagementState? Management { get; init; }
    public GuildEconomyState? Economy { get; init; }
    public GuildDungeonProgress? DungeonProgress { get; init; }
    public uint HongbaoSpentCera { get; init; }
    public ushort OnlineCount => checked((ushort)Members.Count(member => member.Channel != byte.MaxValue));
}

internal sealed record GuildPanelQuery(ushort CharacterId, Func<bool> IsCurrent);

internal sealed class GuildPanelService(IGuildDirectoryStore store, OnlineSessionDirectory online)
{
    public GuildPanel? Read(AccountOwners.Lease owner, ushort characterId, byte region)
    {
        var panel = store.ReadGuildPanel(owner, characterId);
        if (panel is null) return null;
        // 查询完成后才逐个捕获在线身份，不能持请求者账号锁访问另一个账号。
        var members = panel.Members.ToDictionary(member => member.Id, member => member with { Region = region });
        foreach (var identity in online.Snapshot())
        {
            if (!identity.Ready || !members.TryGetValue(identity.CharacterId, out var member) ||
                member.Account != identity.Account) continue;
            var current = online.Find(identity.Account)?.PublicCharacter(member.Id);
            if (current is null || !current.IsCurrent()) continue;
            members[member.Id] = member with { Channel = current.Channel.Number, Region = current.Channel.ServerIndex };
        }
        return panel with { Members = panel.Members.Select(member => members[member.Id]).ToArray() };
    }
}
