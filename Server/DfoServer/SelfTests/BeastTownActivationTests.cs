using DfoServer.Game.Characters;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Testing;
using PvfLib;

namespace DfoServer;

internal static class BeastTownActivationTests
{
    static readonly ChannelEntry TestChannel = new(new(2, 46), 40, "[beast]");

    public static void RunCore()
    {
        const string script = "[server]\n2\n10 `<chn_channel_info_028>` 40 `[beast]` 5 0 0 0 0 0 0 0 0 0 0 ``\n[/server]";
        var advertised = script;
        TestCheck.That(ChannelCatalog.Parse(script).Entry(10).Type == 40 &&
            ChannelCatalog.Parse(advertised).Entry(10) is { Type: 40, Group: "[beast]" },
            "频道目录、列表与登录保留独立类型40，不再借用普通卡片");
        TestCheck.That(BeastChannel.IsEntry(TestChannel) &&
            !BeastChannel.IsEntry(new(new(2, 10), 22, "[beast]")) &&
            !BeastChannel.IsEntry(new(new(2, 46), 40, "[other]")), "魔兽城镇实验必须同时匹配类型和实验分组");
        TestCheck.That(ChannelPackets.Endpoint("127.0.0.1", 10011, channelType: ChannelPackets.LoginChannelType(TestChannel))[3] == 40,
            "登录响应投影原生频道类型40");
        var row = new CharacterRosterEntry(1, 0, "BeastTown", 0, 0, 95, []);
        TestCheck.That((row with { BeastEligible = true }).ForBeastChannel(true).BeastEligible &&
            !row.ForBeastChannel(true).BeastEligible &&
            (row with { BeastEligible = true }).ForBeastChannel(false).BeastEligible &&
            !(row with { BeastEligible = true, Level = 89 }).ForBeastChannel(true).BeastEligible &&
            (row with { BeastEligible = true, Level = 90 }).ForBeastChannel(true).BeastEligible &&
            (row with { BeastEligible = true, IsPvpPrivate = true }).ForBeastChannel(true).BeastEligible,
            "名册频道资格要求至少90级及5927完成态，其余既有标记不影响该资格位");
        TestCheck.That(BeastChannel.EntryEvents(true, [282]).SequenceEqual(new ushort[]{282,333}) &&
            BeastChannel.EntryEvents(false, [282]).SequenceEqual(new ushort[]{282,333}) &&
            BeastChannel.EntryEvents(true, [333]).SequenceEqual(new ushort[]{333}), "魔兽开放事件始终保留，不丢失既有事件且无重复");
        TestCheck.That(!ChannelAreaLanding.SameSnapshotPredicates(40, 22) &&
            !ChannelAreaLanding.SameSnapshotPredicates(22, 40) &&
            ChannelAreaLanding.SameSnapshotPredicates(40, 40) &&
            ChannelAreaLanding.SameSnapshotPredicates(22, 22), "进出魔兽频道不沿用普通城镇快照，同类频道仍保留快照");
        var plain = CharacterPackets.Roster([row]);
        var probe = CharacterPackets.Roster([row with { BeastEligible = true }]);
        int bit = plain.Length - 10 - 48 + CharacterPackets.BeastEligibleTailOffset;
        TestCheck.That(plain[bit] == 0 && probe[bit] == 1 &&
            plain.Where((_, i) => i != bit).SequenceEqual(probe.Where((_, i) => i != bit)), "临时准入只改变名册魔兽资格字节");
    }

    public static void RunResources(string path)
    {
        using var pvf = PvfArchive.Open(path);
        RunResources(TownCatalog.Load(pvf));
    }

    public static void RunResources(TownCatalog towns)
    {
        var none = new HashSet<int>();
        var room = ChannelAreaLanding.DecideArea(towns, 51, 0, 95, none).Spot;
        TestCheck.That(room is not null && towns.IsRoom(room, none), "51/0必须有可站立赛丽亚房间");
        TestCheck.That(ChannelAreaLanding.DecideArea(towns, 51, 0, 89, none).Spot is null, "房间保留PVF最低90级门槛");
        var street = ChannelAreaLanding.RoomExit(towns, TestChannel, room!, 95, none);
        TestCheck.That(street is { Town: 51, Area: 1 } && towns.IsPosition(street, none), "专属房间出门解析51/1可站门口");
        var role = new Character(1, 0, "BeastTown", 0, room!, towns.InitialReturn) { Level = 95 };
        TestCheck.That(TownTravel.LeaveRoom(role, towns, none, TestChannel) is { Accepted: true, Position: var exit } && exit == street,
            "空正文出房入口也进入51/1而非普通存档地区");
        TestCheck.That(towns.Areas[(51, 1)].Default.Links.Any(x => x.Town == 51 && x.Area == 2) &&
            towns.Areas[(51, 2)].DungeonWorldMap == 46, "街道连候场，候场使用魔兽WDM46");
        TestCheck.That(ChannelAreaLanding.RoomExit(towns, new(new(2, 10), 22, "[granfloris]"), towns.Room, 95, none) is null,
            "普通频道不被魔兽城镇接管");
        TestCheck.That(ChannelAreaLanding.IsChannelBoundTown(towns, 51), "魔兽城镇不得覆盖普通返回地区");
        var kept = TownTravel.LeaveRoom(role, towns, none, TestChannel);
        TestCheck.That(kept.Accepted && kept.Return == role.Return, "进入魔兽街道仍保留普通返回地区");
        var polluted = role with { Return = street, Position = street! };
        TestCheck.That(BeastChannel.OrdinaryReturn(towns, polluted, none).Town != 51,
            "旧存档特殊地区返回点修复为可用普通城镇");
        TestCheck.That(!ChannelAreaLanding.ClientKeepsTownSnapshot(towns, new(new(2, 10), 22, "[granfloris]"), street!),
            "服务端重启后也不得从51号地区恢复普通频道快照");
        Console.WriteLine($"PASS beast town activation room={room} street={street} worldmap=46");
    }
}
