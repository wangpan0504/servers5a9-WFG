namespace DfoServer.Game.Dungeons;

// 改版魔兽专属实体：PVF etc/beastdungeon/beastdungeon.etc 声明三档，
// beastdungeon.etc 的5335项是改版魔兽固定锚点及Boss资源；每日图由独立发布状态映射，
// 不能把该etc行误读成五张日图列表。三档并不读取普通DGN的 difficulty size=1。
internal static class RevisedBeastSelection
{
    internal const int FirstDungeon = 5335;
    internal const int LastDungeon = 5339;
    internal const byte MinDifficulty = 0; // 王者
    internal const byte NightmareDifficulty = 1; // 噩梦
    internal const byte BeastDifficulty = 2; // THE BEAST
    internal const byte MaxDifficulty = BeastDifficulty;
    internal const int EntryQuest = 5927;
    internal const int Town = 51;
    internal static readonly int[] Bosses = [400365, 400383, 400384];
    internal static readonly IReadOnlyDictionary<int, string> DungeonPaths =
        new Dictionary<int, string>
        {
            [5335] = "dungeon/beast_metrocenter/beast_canyon_of_beast.dgn",
            [5336] = "dungeon/beast_metrocenter/beast_forest_of_redwitch.dgn",
            [5337] = "dungeon/beast_metrocenter/beast_hideout_of_fear.dgn",
            [5338] = "dungeon/beast_metrocenter/beast_gust_zone.dgn",
            [5339] = "dungeon/beast_metrocenter/beast_time_plaza.dgn"
        };
    // 权限快照需要完整实体集合；每日单图选择仍由 Published/IsDaily 控制。
    internal static IReadOnlyList<int> AllDungeons => DungeonPaths.Keys.Order().ToArray();

    internal static bool IsQuestContext(uint questId) => questId is 0 or 5970;

    public static int DailyDungeon(DateTimeOffset now)
    {
        // 同一业务日、所有角色和服务重启后保持同图；独立种子不读取首版轮换状态。
        int day = DungeonIncountPolicy.Period(now).Day;
        byte[] seed = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("revised-beast-daily-v1:" + day.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return FirstDungeon + (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(seed) % 5);
    }
    internal readonly record struct Publication(int DungeonId, string Path, int WorldMapPattern)
    {
        public static Publication For(DateTimeOffset now)
        {
            int id = DailyDungeon(now);
            return new(id, PathFor(id), id switch
            {
                5335 => 2,
                5336 => 8,
                5337 => 4,
                5338 => 6,
                5339 => 0,
                _ => throw new InvalidDataException("改版魔兽实体索引未注册")
            });
        }
    }

    public static Publication Published(DateTimeOffset now) => Publication.For(now);
    public static bool IsDaily(int dungeonId, DateTimeOffset now) => dungeonId == DailyDungeon(now);
    public static string PathFor(int dungeonId) => DungeonPaths.TryGetValue(dungeonId, out var path)
        ? path : throw new ArgumentOutOfRangeException(nameof(dungeonId));
    public static bool IsDifficulty(byte difficulty) => difficulty <= MaxDifficulty;
    public static bool IsRequest(int dungeonId, byte difficulty, DateTimeOffset now) =>
        IsDaily(dungeonId, now) && IsDifficulty(difficulty);
}
