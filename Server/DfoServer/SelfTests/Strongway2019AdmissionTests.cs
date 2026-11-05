using DfoServer.Game.Characters;
using DfoServer.Game.Events;
using DfoServer.Game.Strongway2019;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class Strongway2019AdmissionTests
{
    public static void RunCore()
    {
        int before = Count;
        var catalog = Strongway2019CatalogTests.CreateCatalog();
        var character = new Character(1, 3, "SWEntry", 0, new(65, 1, 300, 300), null) { Level = 95 };
        var channel = new ChannelEntry(new(2, 49), 55, "[dpl_pve]");
        var now = DateTimeOffset.Parse("2026-11-01T12:00:00+08:00");
        var schedule = new ServerEventSchedule(312, 7, now.ToUnixTimeSeconds(), now.AddHours(1).ToUnixTimeSeconds());
        Strongway2019Admission? Plan(uint id = 5359, int count = 1, int antiEvil = 4000,
            Character? actor = null, ChannelEntry? entry = null, ServerEventSchedule? active = null,
            DateTimeOffset? at = null, bool matched = false) =>
            Strongway2019EntryPolicy.Prepare(catalog, actor ?? character, entry ?? channel, id, count,
                antiEvil, active ?? schedule, at ?? now, matched);
        var simple = Plan();
        That(simple is { Mode: Strongway2019Mode.Simple, DungeonId: 5359, MemberCount: 1, AntiEvil: 4000 } &&
            ReferenceEquals(simple.Character, character) && simple.Schedule == schedule,
            "首期简单模式在真实频道城镇、95级和4000抗魔边界冻结原角色及完整活动版本");
        foreach (int members in Enumerable.Range(1, 4))
            That(Plan(count: members) is not null && Plan(5363, members, 4000) is { Mode: Strongway2019Mode.Hero } &&
                Plan(5360, members, 5400, matched: true) is { Mode: Strongway2019Mode.Matching },
                "简单、英雄与匹配授权支持原队伍一至四人：" + members);
        That(Plan(5363, antiEvil: 4000) is { Mode: Strongway2019Mode.Hero, AntiEvil: 4000 } &&
            Plan(5363, antiEvil: 4415) is { Mode: Strongway2019Mode.Hero, AntiEvil: 4415 },
            "英雄普通挑战使用4000抗魔门槛，4000边界与4415均可准入");
        foreach (var invalid in new[]
        {
            Plan(count: 0), Plan(count: 5), Plan(antiEvil: 3999), Plan(5363, 0, 4000), Plan(5363, 5, 4000), Plan(5363, 1, 3999),
            Plan(5360, 4, 5400), Plan(5360, 0, 5400, matched: true), Plan(5360, 5, 5400, matched: true),
            Plan(5360, 1, 5399, matched: true), Plan(5364, 4, 9999), Plan(5362, 4, 9999),
            Plan(actor: character with { Level = 94 }), Plan(actor: character with { Position = new(65, 0, 0, 0) }),
            Plan(entry: channel with { Type = 22 }), Plan(active: schedule with { OverrideActive = false }),
            Plan(active: schedule with { EventId = 64 }), Plan(at: now.AddSeconds(-1)), Plan(at: now.AddHours(1)),
            Plan(matched: true)
        }) That(invalid is null, "首期准入拒绝错误人数、抗魔、季模式、频道地点和失效时段");
        That(Strongway2019EntryPolicy.Contains(5359) && Strongway2019EntryPolicy.Contains(5364) &&
            !Strongway2019EntryPolicy.Contains(5362), "隐藏首期图保持专属门禁，其他旧赛图不借本季准入");
        Console.WriteLine($"PASS {Count - before} 首期强者之路模式、频道、人数、抗魔与活动准入");
    }
}
