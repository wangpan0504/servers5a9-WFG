using DfoServer.GameWorld.Channels;

namespace DfoServer.Game.Raids;

// 进程内攻坚队表，不写库。每个角色最多是一支攻坚队的队长；重复创建替换旧队，退出删除。
// key = (服务器序号 << 24) | (频道号 << 16) | low16：客户端 023C8260 只要求高两字节等于 CHANNELINFO field 7/8，
// low16 在 0x24E/0x24F/ACK664 路径中只参与整 key 查表比较（KB-20261021T063522-c523cdf490、raid-static-deep S3）。
internal sealed record RaidParty(uint Key, byte Type, string Title, byte[] TitleBytes, ChannelIdentity Channel, ushort Leader, string LeaderName);

internal sealed class RaidRegistry
{
    readonly object gate = new();
    readonly Dictionary<ushort, RaidParty> byLeader = [];
    readonly Dictionary<ChannelIdentity, ushort> next = [];

    public int Count { get { lock (gate) return byLeader.Count; } }

    public RaidParty Create(ChannelIdentity channel, byte type, string title, byte[] titleBytes, ushort leader, string leaderName)
    {
        lock (gate)
        {
            byLeader.Remove(leader);
            ushort low = Allocate(channel);
            var party = new RaidParty(((uint)channel.ServerIndex << 24) | ((uint)channel.Number << 16) | low,
                type, title, titleBytes, channel, leader, leaderName);
            byLeader[leader] = party;
            return party;
        }
    }

    public RaidParty? Find(ushort leader) { lock (gate) return byLeader.GetValueOrDefault(leader); }

    // MODIFY_RAID_INFO：只改标题，key/类型/队长不变。
    public RaidParty? Rename(ushort leader, string title, byte[] titleBytes)
    {
        lock (gate)
        {
            if (!byLeader.TryGetValue(leader, out var party)) return null;
            return byLeader[leader] = party with { Title = title, TitleBytes = titleBytes };
        }
    }

    // SET_RAID_WAITING：进程内待机名单，只用于拒绝重复登记（ACK667 0x12）；待机列表的下发包未恢复，名单不对外显示。
    readonly HashSet<ushort> waiting = [];

    public bool AddWaiting(ushort character) { lock (gate) return waiting.Add(character); }

    public void RemoveWaiting(ushort character) { lock (gate) waiting.Remove(character); }

    public RaidParty? Leave(ushort character)
    {
        lock (gate) return byLeader.Remove(character, out var party) ? party : null;
    }

    // low16 从 1 开始，跳过同频道仍在用的值；0 不用。
    ushort Allocate(ChannelIdentity channel)
    {
        ushort low = next.GetValueOrDefault(channel);
        for (int attempt = 0; attempt < ushort.MaxValue; attempt++)
        {
            low = (ushort)(low == ushort.MaxValue ? 1 : low + 1);
            uint high = ((uint)channel.ServerIndex << 24) | ((uint)channel.Number << 16);
            if (!byLeader.Values.Any(party => party.Key == (high | low))) { next[channel] = low; return low; }
        }
        throw new InvalidOperationException("频道攻坚队编号已用尽");
    }
}
