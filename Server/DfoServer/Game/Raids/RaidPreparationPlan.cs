using System.Collections.Immutable;
using DfoServer.Game.Party;

namespace DfoServer.Game.Raids;

internal sealed record RaidSquadPreparation(byte Squad, ImmutableArray<RaidMember> Members)
{
    public RaidActor Anchor => Members[0].Actor;
}

// 只引用原招募快照和会话；不是正式实例、资产预留或开战授权。
internal sealed class RaidPreparationPlan
{
    RaidPreparationPlan(RaidParty source, ImmutableArray<RaidSquadPreparation> squads)
    { Source = source; Squads = squads; }

    public RaidParty Source { get; }
    public ImmutableArray<RaidSquadPreparation> Squads { get; }

    // 594单成员路径写出的原生CMD12完整设置；未知raid成员字段不借此赋义。
    public static PartySettings NativePartySettings(byte squad) => new(0,
        "Party : " + squad.ToString(System.Globalization.CultureInfo.InvariantCulture), 4, uint.MaxValue, 5, 0, 0, 0, 0, 7);

    public static RaidPreparationPlan? Capture(RaidParty source, out string? rejection)
    {
        rejection = RosterRejection(source);
        if (rejection is not null) return null;
        // 591/op3发包按Slot排序；Luke最多8人走客户端稳定插入排序，同分队且未知排序字段为0时保留此顺序。
        var squads = source.Members.OrderBy(m => m.Slot).GroupBy(m => m.Squad).OrderBy(g => g.Key)
            .Select(g => new RaidSquadPreparation(g.Key, [..g])).ToImmutableArray();
        return new(source, squads);
    }

    public bool IsCurrent(RaidRegistry raids) => raids.IsCurrent(Source) && RosterRejection(Source) is null;

    static string? RosterRejection(RaidParty source)
    {
        if (source.Type != 1 || source.Capacity is < 1 or > 8) return "unsupported-raid-preparation";
        if (source.Started is not null) return "raid-already-started";
        if (source.Members.Any(m => m.Actor.Session.StartedRaid is not null)) return "raid-member-already-started";
        if (source.Members.IsEmpty || source.Members.Length > source.Capacity) return "invalid-raid-roster";
        if (source.Members.Select(m => m.Slot).Distinct().Count() != source.Members.Length ||
            source.Members.Any(m => m.Slot >= source.Capacity) ||
            source.Members.Select(m => m.Actor.Character).Distinct().Count() != source.Members.Length ||
            source.Members.Select(m => m.Actor.Owner.Account).Distinct(StringComparer.Ordinal).Count() != source.Members.Length ||
            !source.Members.Any(m => m.Actor.Character == source.Leader)) return "invalid-raid-roster";
        // CanManage读取当前Selected资格，不把入团时的显示字段或旧资产revision当作准入凭据。
        if (source.Members.Any(m => !m.Actor.CanManage || m.Actor.Channel != source.Channel ||
            m.Actor.Session.ChannelEntry?.Type != 0x20)) return "raid-member-not-current";
        if (source.Members.Any(m => m.Squad == 0)) return "raid-squad-unassigned";
        if (source.Members.Any(m => m.Squad > (source.Capacity + 3) / 4) ||
            source.Members.GroupBy(m => m.Squad).Any(g => g.Count() > 4)) return "invalid-raid-squad";
        return null;
    }

    // 只比较已有普通队伍成员/队长，不比较594 settings，不代表原生握手幂等就绪或开战许可。
    public string InspectOrdinaryParties(RaidRegistry raids, PartyService parties)
    {
        if (!IsCurrent(raids)) return "raid-preparation-stale";
        var ordinary = parties.Snapshot();
        var matched = new List<PartySnapshot>();
        foreach (var squad in Squads)
        {
            var related = ordinary.Where(p => p.Members.Any(m =>
                squad.Members.Any(r => ReferenceEquals(r.Actor.Session, m.Actor.Session)))).ToArray();
            if (related.Length == 0) return "ordinary-party-missing";
            if (related.Length != 1) return "ordinary-party-conflict";
            var party = related[0];
            if (party.Members.Length != squad.Members.Length || party.Members.Any(m => !m.IsCurrent() ||
                !squad.Members.Any(r => ReferenceEquals(r.Actor.Session, m.Actor.Session) &&
                    ReferenceEquals(r.Actor.Owner, m.Actor.Owner) && r.Actor.Epoch == m.Actor.Epoch &&
                    r.Actor.Character == m.Actor.Character.Id))) return "ordinary-party-conflict";
            if (!party.Members.Any(m => m.Slot == party.Leader &&
                ReferenceEquals(m.Actor.Session, squad.Anchor.Session))) return "ordinary-party-anchor-mismatch";
            matched.Add(party);
        }
        // 两个owner各自保留版本；只读观察不替代正式动作所需的命令门禁与提交前全员复核。
        return IsCurrent(raids) && matched.All(parties.IsCurrent) ? "ordinary-party-members-match" : "raid-preparation-stale";
    }
}
