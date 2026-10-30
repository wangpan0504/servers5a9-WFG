using System.Text.Json;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Infrastructure;

// 只读资源预览，复用正式基础掉落准备器；不创建角色或实例，不推进随机数，不发奖励。
internal sealed class DungeonDropInspection(DungeonCatalog dungeons, DungeonRoomCatalog rooms,
    DungeonResourceDropPolicy policy, DungeonDropControlSettings settings)
{
    public string Query(int dungeonId, DungeonDropScope scope, byte difficulty, int monsterIndex, int page)
    {
        if (difficulty > 4 || monsterIndex < 0 || page is < 0 or > 100000 || !Enum.IsDefined(scope))
            throw new InvalidDataException("掉落预览参数越界");
        var definition = dungeons.Get(dungeonId);
        var maps = definition.Mazes.SelectMany(maze => (maze.MapSpecification ?? []).Concat(maze.LayeredMaps)
            .Concat(maze.MapAlternatives).Concat(maze.StoryStages)).DistinctBy(map => map.Id).ToArray();
        if (maps.Length > 2048) throw new InvalidDataException("副本地图过多，请使用更小的资源范围");
        var monsters = new List<DungeonPlannedMonster>();
        var warnings = new List<string>();
        foreach (var map in maps)
        {
            try
            {
                var plan = DungeonMonsterPlan.FromRoom(rooms.Get(map, requirePassiveDefinitions: false), 1, 2, definition.BasisLevel);
                monsters.AddRange(plan.Monsters.Where(monster => monster.SpawnType <= 3 && monster.CanReportDeath && !monster.IsFriendly));
            }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or KeyNotFoundException)
            { warnings.Add("地图 " + map.Id + "：" + error.Message); }
        }
        var samples = monsters.DistinctBy(monster => (monster.MonsterId, monster.CandidateLevel, monster.RewardType)).ToArray();
        if (samples.Length == 0) throw new InvalidDataException("该副本没有可预览的普通怪物；祭坛及脚本专用演员使用独立生成规则");
        if (monsterIndex >= samples.Length) throw new InvalidDataException("怪物索引超出本副本清单");
        var sample = samples[monsterIndex];
        var snapshot = settings.Current;
        bool Matches(DungeonDropControlRule rule) => rule.DungeonIds.IsEmpty || rule.DungeonIds.Contains(dungeonId);
        var rate = snapshot.Enabled ? snapshot.Rules.FirstOrDefault(rule => rule.Scope == scope && Matches(rule))?.Multipliers ??
            snapshot.Rules.FirstOrDefault(rule => rule.Scope == DungeonDropScope.All && Matches(rule))?.Multipliers : null;
        var original = policy.Prepare(sample.MonsterId, sample.CandidateLevel, sample.RewardType, difficulty, definition.BasisLevel);
        var adjusted = DungeonDropControlSnapshot.Apply(original, rate);
        var rows = DungeonDropListing.Rows(adjusted).Skip(checked(page * 64)).Take(65).ToArray();
        return "OK " + JsonSerializer.Serialize(new { dungeonId, scope = scope.ToString(), difficulty, page,
            monsters = samples.Select((monster, index) => new { index, id = monster.MonsterId, level = monster.CandidateLevel,
                rank = monster.RewardType, map = monster.SourceMap?.Id }), rows = rows.Take(64), hasNext = rows.Length > 64,
            note = "已保存范围规则下的基础掉落预览：每次抽样概率。含基础通道、怪物声明及基础独立/世界掉落；深渊等入场专用扩展、药剂、成长契约、活动期限和每日资格以真实入场为准。",
            warnings });
    }
}
