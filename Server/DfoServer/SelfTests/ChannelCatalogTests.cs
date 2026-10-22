using System.Buffers.Binary;
using DfoServer.GameWorld.Channels;
using DfoServer.Network.Builders;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ChannelCatalogTests
{
    public static void Run()
    {
        int before = Count;
        const string row = "10 `<chn_channel_info_028>` 1 `[granfloris]` 5 0 0 0 0 0 0 0 0 0 0 ``";
        string source = "[server]\n2\n" + row + "\n[/server]\n";
        string root = Path.Combine(Path.GetTempPath(), "S5A9-channel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "channel.txt");
            var embedded = Infrastructure.ServerRuntime.LoadChannels(path);
            That(embedded.ServerIndex == 2 && embedded.Channels.Count == 30 && embedded.Entry(10).Identity == new ChannelIdentity(2, 10),
                "无外置资源时从发行包内置脚本恢复全部入口");
            File.WriteAllText(path, source);
            That(Infrastructure.ServerRuntime.LoadChannels(path).SourceText == source, "目录部署优先使用外置频道脚本");
            File.WriteAllText(path, "invalid");
            Reject<InvalidDataException>(() => Infrastructure.ServerRuntime.LoadChannels(path), "外置脚本错误仍拒绝，不静默覆盖配置");
        }
        finally { Directory.Delete(root, true); }
        var catalog = ChannelCatalog.Parse(source);
        That(catalog.ServerIndex == 2 && catalog.Channels.Count == 1 && catalog.Channels[0] ==
            new ChannelDefinition(10, "<chn_channel_info_028>", 1, "[granfloris]"), "频道分组、频道号、类型与标签分开保留");
        That(catalog.Entry(10) == new ChannelEntry(new(2, 10), 1, "[granfloris]"), "会话入口保留频道身份及资源分类，不另建业务实例");
        Reject<InvalidDataException>(() => catalog.Entry(256), "无法放入Actor身份的频道号不能建立监听入口");
        That(ChannelCatalog.Parse(source.Replace("<chn_channel_info_028>", "<4::channel_info_dname_1>")).Channels[0].Number == 10,
            "允许已有的带索引字符串引用");
        foreach (string bad in new[]
        {
            source.Replace("`<chn_channel_info_028>`", "`plain name`"),
            source.Replace("`<chn_channel_info_028>`", "`<bad/key>`"),
            source.Replace(" 1 `[granfloris]`", " 201 `[granfloris]`"),
            source.Replace(" 1 `[granfloris]`", " 57 `[granfloris]`"),
            source.Replace(" 5 0", " 256 0"),
            source.Replace(" 5 0", " 5 NaN"),
            source.Replace(" 5 0", " 5"),
            source.Replace("``", "0 ``"),
            source.Replace("``", "`"),
            source.Replace("10 `<", "11 `<"),
            source.Replace("\n2\n", "\n250\n"),
            source.Replace("[/server]", row + "\n[/server]"),
            source + source,
            source + "\0"
        })
            Reject<InvalidDataException>(() => ChannelCatalog.Parse(bad), "畸形或无法在本版使用的频道声明被拒绝");
        byte[] body = LoginPackets.ChannelInfo(catalog.GameIdentity);
        for (int i = 0; i < body.Length; i++)
            body[i] = (byte)((((body[i] << 6) | (body[i] >> 2)) ^ 0xb5) & 0xff);
        That(BinaryPrimitives.ReadInt32LittleEndian(body) == body.Length - 4, "完整CHANNELINFO长度前缀包含时钟字段");
        Bytes(body.AsSpan(343, 4), [0x38, 2, 0x40, 10], "登录安装共享的服务器2/游戏频道10，不使用点击的分类号");
        using (var reader = new BinaryReader(new MemoryStream(body, 347, body.Length - 347)))
        {
            That(reader.ReadByte() == 88 && Math.Abs(reader.Read7BitEncodedInt64() - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) <= 5,
                "CHANNELINFO field11为服务器Unix秒，绝对期限不能落到零时钟");
            Bytes(reader.ReadBytes(10), [0xa0, 0xd3, 0x0e, 0xd4, 0x0d, 0xa8, 0xd3, 0x0e, 0x94, 0x0e], "频道身份不截断协议数量");
            That(reader.BaseStream.Position == reader.BaseStream.Length, "握手时钟及枚举数量完整消费，无多余尾部");
        }
        var character = new Character(1, 0, "A", 0, new(38, 1, 450, 234), null);
        var stats = new CharacterStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        byte[] actor = TownPackets.Actor(character, catalog.GameIdentity);
        byte[] player = TownPackets.Player(character, stats, 1, catalog.GameIdentity);
        Bytes(actor.AsSpan(0, 5), [0, 1, 0, 2, 10], "Actor按NOTI1身份进入本地角色表，不能沿用0/0");
        Bytes(player.AsSpan(0, 5), [1, 1, 0, 2, 10], "Player延续Actor的服务器和频道上下文");
        Bytes(actor.AsSpan(5), TownPackets.Actor(character).AsSpan(5), "身份修正不改变Actor其余字段");
        Bytes(player.AsSpan(5), TownPackets.Player(character, stats, 1).AsSpan(5), "身份修正不改变Player属性与装备布局");
        That(actor[3] == body[344] && actor[4] == body[346] && player[3] == body[344] && player[4] == body[346],
            "复现原生本地/跨频道比较，登录和角色包选择同一个角色存储上下文");
        Console.WriteLine($"PASS {Count - before} 频道资源字段、原生名称引用与共享身份断言");
    }
}
