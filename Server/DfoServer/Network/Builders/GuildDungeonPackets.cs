using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;

namespace DfoServer.Network.Builders;

internal static class GuildDungeonPackets
{
    public static byte[] Progress(GuildDungeonCatalog catalog, GuildDungeonProgress progress)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((byte)1);
        // 本服从资源中首个无前置图打开选图；这不是原服入口字段的常量推断。
        writer.Write(catalog.Definitions.Where(row => row.Prerequisites.Length == 0).Min(row => row.Id));
        writer.Write(0u); // 当前不声明周期截止时间，不将未知同步时钟冒充Unix时间。
        writer.Write(0u); // 01698BD0读取到+3C，业务语义待证。
        writer.Write(checked((uint)catalog.Definitions.Count));
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var counts = progress.Counts.ToDictionary(row => row.DungeonId, row => row.Effective(catalog, now).Units);
        foreach (var row in catalog.Definitions.OrderBy(row => row.Id))
        {
            writer.Write(row.Id);
            writer.Write(counts.GetValueOrDefault(row.Id));
        }
        return stream.ToArray();
    }
}
