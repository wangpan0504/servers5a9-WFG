using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed partial class SinglePlayerDungeonRun
{
    // 原生ACT内部参数5最终写普通45的kind0；只恢复英雄活Boss两房往返，不授予清怪或阶段符号事实。
    private bool CanEvildomWarChallengeWarp(DungeonPoint target)
    {
        if (entry.DungeonId != EvildomWarChallengeEntryPolicy.DungeonId || entry.Difficulty != 2 ||
            !entry.IsOrdinary || entry.IsPractice || entry.IsClassExperience || entry.IsQuestReplay ||
            !entry.IndividualMapMovement || entry.PartyMemberCount != 4 || !entry.DisableFatigueConsumption ||
            !entry.DisableMonsterAndClearExperience || entry.HellParty is not null || entry.RaidEntry is not null ||
            entry.Maze.Width != 13 || entry.Maze.Height != 3 || entry.Maze.MapSpecification?.Count != 15 ||
            !entry.Maze.WarpMaps.Any(warp => warp.Source == current.Plan.Room.Map.Position && warp.Destination == target) ||
            !rooms.TryGetValue(new(12, 0), out var boss) || !rooms.TryGetValue(new(12, 1), out var seal) ||
            boss.Plan.Room.Map != entry.BossMap || !boss.Loaded ||
            !Matches(boss, 400000524, true, 407000556, "sarpoza.mob", "action/seal_start.act", new(12, 1)) ||
            !Matches(seal, 400000525, false, 407000558, "sarpoza_seal_dummy1.mob", "action/proc_0.act", new(12, 0))) return false;
        var actor = boss.Plan.Monsters.SingleOrDefault(monster => monster.MonsterId == 407000556 && monster.IsBoss &&
            monster.CanReportDeath && !monster.IsFriendly && monster.AiCharacter is null);
        if (actor is null || boss.Dead.Contains(actor.ObjectKey) || boss.Culled.Contains(actor.ObjectKey)) return false;
        return ReferenceEquals(current, boss) && target == seal.Plan.Room.Map.Position ||
            ReferenceEquals(current, seal) && seal.Loaded && target == boss.Plan.Room.Map.Position;

        static bool Matches(Room room, int map, bool boss, int monster, string mob, string action, DungeonPoint destination)
        {
            if (room.Plan.Room.Map.Id != map || room.Plan.Room.Map.Kind != (boss ? "boss" : "map") ||
                room.Plan.Room.Map.Path != DungeonEvildomWarChallengeWarpRules.MapPath(boss) ||
                room.Plan.Room.EvildomWarChallengeWarp is not { } evidence || evidence.MapId != map ||
                evidence.MapPath != room.Plan.Room.Map.Path || evidence.MonsterId != monster ||
                evidence.MonsterPath != DungeonEvildomWarChallengeWarpRules.Root + mob ||
                evidence.ActionPath != DungeonEvildomWarChallengeWarpRules.Root + action || evidence.Destination != destination ||
                evidence.MonsterOrdinal < 0 || evidence.MonsterOrdinal >= room.Plan.Room.Monsters.Count) return false;
            var source = room.Plan.Room.Monsters[evidence.MonsterOrdinal];
            return source.MonsterId == monster && source.Path == evidence.MonsterPath &&
                room.Plan.Monsters.Count(row => row.SourceOrdinal == evidence.MonsterOrdinal && row.MonsterId == monster &&
                    row.Source == source && row.AiCharacter is null && row.CanReportDeath) == 1;
        }
    }
}
