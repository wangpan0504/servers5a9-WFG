using System.Buffers.Binary;
using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class GuildNpcTests
{
    public static void RunCore()
    {
        int before = Count;
        byte[] body = new byte[69];
        body.AsSpan(0, 13).Fill(0xA7);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(17), GuildNpcCatalog.Guardian);
        That(GuildNpcRequests.TryParse(body, out var parsed) && parsed is { Action: 1, NpcId: GuildNpcCatalog.Guardian, Argument: 0 },
            "现代NPC69字节结构从真实13/17/21偏移读取，不把旧前缀当角色身份");
        That(!GuildNpcRequests.TryParse(body.AsSpan(13), out _) && !GuildNpcRequests.TryParse(body.AsSpan(0, 68), out _),
            "不接受去前缀或截断的旧NPC正文");
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), 7);
        That(!GuildNpcRequests.TryParse(body, out _), "NPC动作越界拒绝");
        byte[] error = GuildNpcPackets.Result(new(2, 1));
        That(error.Length == 9 && error[0] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(error.AsSpan(1)) == 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(error.AsSpan(5)) == 18,
            "NPC升级失败包含动作i32和本版正在升级错误18");
        byte[] success = GuildNpcPackets.Result(new(0, 1));
        That(success.Length == 9 && success[0] == 1 && BinaryPrimitives.ReadInt32LittleEndian(success.AsSpan(5)) == 0,
            "NPC成功也提供动作与零业务错误，满足消费者固定8字节读取");
        var rows = Enumerable.Range(0, 8).Select(index => new GuildNpcProgress(GuildNpcCatalog.Guardian + (uint)index,
            index % 4 + 1, index * 100, index * 200)).ToArray();
        byte[] all = GuildNpcPackets.All(rows);
        That(all.Length == 128 && BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(16)) == GuildNpcCatalog.Guardian + 1 &&
            BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan(24)) == 100 &&
            BinaryPrimitives.ReadInt32LittleEndian(all.AsSpan(28)) == 200,
            "NPC全量是8行16字节且升级/契约时间各有独立字段");
        Reject<InvalidDataException>(() => GuildNpcPackets.All(rows[..7]), "NPC全量不发送缺失名册");
        Repository();
        GuildNpcCraftTests.RunCore();
        Console.WriteLine($"PASS {Count - before} modern guild NPC wire assertions");
    }
    public static void RunResources(PvfArchive pvf)
    {
        int before = Count;
        var catalog = new GuildNpcCatalog(pvf);
        GuildNpcCraftTests.RunResources(pvf);
        That(catalog.Definitions.Count == 8 && catalog.Find(GuildNpcCatalog.Guardian)?.At(1) is
            { FirstAmount: 100000, SecondAmount: 1, UpgradeDays: 5, RequiredLevel: 10 },
            "纯净公会守护石首级升级使用声明公款/结晶/5天，不误读为分钟或个人币");
        That(catalog.Contract(1, 0) is { Days: 1, PublicFunds: 3000 } &&
            catalog.Contract(1, 2) is { Days: 15, PublicFunds: 45000 } && catalog.Contract(1, 3) is null,
            "契约按当前NPC级别及从0起选项查真实费用，越界选项不存在");
        Console.WriteLine($"PASS {Count - before} modern guild NPC resource assertions");
    }
}
