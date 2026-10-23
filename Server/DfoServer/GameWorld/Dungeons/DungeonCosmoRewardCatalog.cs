using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 仅冻结超星空终点的真实领主来源；不把几何或奖励声明当作准入、扣票与祭坛已实现。
internal sealed class DungeonCosmoRewardCatalog
{
    public const uint DungeonId = 7191;
    public int RewardRolls { get; }
    public byte Difficulty { get; }
    public ImmutableDictionary<int, DungeonRoomDefinition> BossRooms { get; }

    internal DungeonCosmoRewardCatalog(int rolls, byte difficulty, IEnumerable<DungeonRoomDefinition> rooms)
    {
        if (rolls is < 1 or > ushort.MaxValue || difficulty > 4)
            throw new InvalidDataException("超星空奖励缺少有效档位骰数或固定难度");
        RewardRolls = rolls;
        Difficulty = difficulty;
        BossRooms = rooms.ToImmutableDictionary(room => room.Map.Id);
        if (BossRooms.Count == 0 || BossRooms.Values.Any(room => room.Monsters.Count(row => row.RankKind == "[boss]" &&
            row.Modifiers.Count == 0 && row.Team is not (>= 0 and < 100)) != 1))
            throw new InvalidDataException("超星空每个终点须有唯一真实领主");
    }

    public static DungeonCosmoRewardCatalog Load(PvfArchive pvf, DungeonCatalog dungeons, DungeonRoomCatalog rooms)
    {
        var definition = dungeons.Get((int)DungeonId);
        if (!definition.RawSections.Any(row => row.Tag == "cosmo dungeon") || !definition.TryGetDesignatedDifficulty(out byte difficulty))
            throw new InvalidDataException("超星空缺少模式或固定难度声明");
        var paths = DungeonCatalog.ParseDirectory(pvf.ReadTokens("map/map.lst"), pvf.ResolveString, "map");
        var tokens = pvf.ReadTokens(definition.Path);
        var maps = new Dictionary<int, DungeonMap>();
        for (int at = 0; at < tokens.Length; at++)
        {
            if (tokens[at].Type != 6 || pvf.ResolveString(tokens[at].Value) != "boss_selection_probability") continue;
            var values = tokens.Skip(at + 1).TakeWhile(token => token.Type == 0).Select(token => token.Value).ToArray();
            if (values.Length < 4 || values.Length % 2 != 0 || values[0] < 0 || values[1] < 0)
                throw new InvalidDataException("超星空终点候选声明残缺");
            for (int index = 2; index < values.Length; index += 2)
            {
                if (values[index] <= 0 || values[index + 1] <= 0 || !paths.TryGetValue(values[index], out string? path))
                    throw new InvalidDataException("超星空终点候选编号或权重非法");
                var map = new DungeonMap("boss", new(values[0], values[1]), values[index], path);
                if (maps.TryGetValue(map.Id, out var previous) && previous != map)
                    throw new InvalidDataException("超星空终点地图身份冲突");
                maps[map.Id] = map;
            }
        }
        return new(rooms.HellParty.RewardRollCount(1), difficulty, maps.Values.Select(map => rooms.Get(map)));
    }

    public bool Matches(uint dungeonId, DungeonMap map, DungeonRoomMonster? source) => dungeonId == DungeonId && source is not null &&
        BossRooms.TryGetValue(map.Id, out var room) && map.Position == room.Map.Position && map.Path == room.Map.Path &&
        room.Monsters.Any(row => row.MonsterId == source.MonsterId && row.Path == source.Path && row.RankKind == source.RankKind &&
            row.RawValues.SequenceEqual(source.RawValues) && row.Modifiers.SequenceEqual(source.Modifiers));
}
