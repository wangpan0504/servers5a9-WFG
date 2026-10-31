using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 复用正式清图参与账本，按当前阶段投影；显示宽度u8饱和，不改变奖励门槛的原始计数。
    internal IReadOnlyDictionary<ushort, byte>? RosterClears(AntonRaidBattleView? view = null)
    {
        lock (gate) return view is null || IsCurrent(view) ? lukeParticipation.Where(row => row.Key.Phase == current.Phase)
            .ToDictionary(row => row.Key.Character, row => (byte)Math.Clamp(row.Value, 0, byte.MaxValue)) : null;
    }

    void RecordRosterClear(int phase, ushort character)
    {
        var key = (phase, character);
        int count = lukeParticipation.GetValueOrDefault(key);
        lukeParticipation[key] = count == int.MaxValue ? count : count + 1;
    }

    // 安徒恩共享Run的全部成员SQL已确认后才记录；个人显示不依赖谁发送117，也不重复扣图HP。
    internal AntonRaidBattleChange? RecordAntonPartyClears((RaidActor Actor, DungeonClearFact Fact)[] facts)
    {
        lock (gate)
        {
            if (!antonPhaseProgress || Kind != RaidKind.Anton || facts.Length is < 1 or > 4 ||
                !dungeonRuns.TryGetValue(facts[0].Fact.Identity.InstanceId, out var run) || !run.Installed ||
                facts.Length != run.Active.Count || facts.Select(row => row.Actor).Distinct().Count() != facts.Length) return null;
            foreach (var row in facts)
                if (!row.Actor.IsCurrent || !run.Active.Contains(row.Actor) || row.Fact.DungeonId != run.Dungeon ||
                    row.Fact.ClearMap is null || row.Fact.ConfirmedDeathEvents.Count == 0 ||
                    !entries.TryGetValue((run.Id, row.Actor.Character), out var entry) || !ReferenceEquals(entry.Run, run) ||
                    row.Fact.Identity.CharacterId != entry.Identity.CharacterId || row.Fact.Identity.QuestId != entry.Identity.QuestId ||
                    row.Fact.Identity.TaskAttempt != entry.Identity.TaskAttempt || row.Fact.Identity.Epoch < entry.Identity.Epoch) return null;
            var before = current;
            bool changed = false;
            if (!run.GuildPartyRecorded)
            {
                RecordGuildClearParty(facts.Select(row => row.Actor.Character));
                run.GuildPartyRecorded = true;
            }
            foreach (var row in facts)
                if (run.RosterContributions.Add(row.Actor.Character))
                { RecordRosterClear(run.Phase, row.Actor.Character); changed = true; }
            // 只改展示版本；阶段终结与奖励仍沿既有权威清图和结算入口。
            if (changed) current = current with { Revision = current.Revision + 1 };
            return new(before, current);
        }
    }
}
