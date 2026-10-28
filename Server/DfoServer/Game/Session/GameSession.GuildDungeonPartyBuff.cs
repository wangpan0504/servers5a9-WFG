namespace DfoServer.Game.Session;

internal sealed record GuildDungeonPartyBuffReceipt(uint[] Removed, uint Added, Func<bool> IsCurrent);

internal sealed partial class GameSession
{
    uint projectedDungeonPartyBuff;
    Guid projectedDungeonPartyInstance;
    long projectedDungeonPartyEpoch;
    long dungeonPartyBuffRevision;

    public GuildDungeonPartyBuffReceipt? PlanGuildDungeonPartyBuff(bool clear = false)
    {
        // 先快照实例成员，再拿账号锁；不在实例锁中等待其它账号。
        ushort[] active = clear ? [] : PartyDungeon?.Members.Select(row => row.Actor.Character.Id).ToArray() ?? [];
        return owner.Execute<GuildDungeonPartyBuffReceipt?>(() =>
        {
            var dungeon = formalDungeon;
            uint desired = 0;
            if (!clear && Ready && Selected is { } character && Phase is SessionPhase.Dungeon or SessionPhase.Settlement &&
                dungeon is not null && guildDungeons?.Find(dungeon.Entry.DungeonId) is not null)
            {
                int count = repository.ReadGuildDungeonPartyCount(owner, character.Id, dungeon.GuildMemberships, active);
                desired = guildDungeons.PartyBuff(count);
            }
            Guid instance = desired == 0 ? Guid.Empty : dungeon!.Run.Snapshot.Identity.InstanceId;
            uint previous = projectedDungeonPartyEpoch == SelectionEpoch ? projectedDungeonPartyBuff : 0;
            if (previous == desired && projectedDungeonPartyInstance == instance) return null;
            projectedDungeonPartyBuff = desired; projectedDungeonPartyInstance = instance; projectedDungeonPartyEpoch = SelectionEpoch;
            long revision = ++dungeonPartyBuffRevision, epoch = SelectionEpoch;
            ushort? actor = Selected?.Id;
            bool Current() => owner.Execute(() => owner.IsCurrent && SelectionEpoch == epoch && Selected?.Id == actor &&
                dungeonPartyBuffRevision == revision && (desired == 0 || Ready && formalDungeon?.Run.Snapshot.Identity.InstanceId == instance));
            uint[] removed = [.. Enumerable.Range(2, 3).Select(count => guildDungeons?.PartyBuff(count) ?? 0)
                .Append(previous).Where(id => id != 0).Distinct()];
            return new(removed, desired, Current);
        });
    }
}
