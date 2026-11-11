using System.Collections.Immutable;
using DfoServer.Development;
using DfoServer.Game.Events;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Events;
using PvfLib;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AdventurerBenefitTests
{
    public static void RunCore()
    {
        var character = new DfoServer.Game.Characters.Character(1, 0, "ReturnActor", 0, new(0, 0, 0, 0), null);
        byte[] ordinary = TownPackets.Actor(character, 0, utcSeconds: 1000);
        foreach (ushort type in new ushort[] { 1, 2 })
        {
            byte[] returning = TownPackets.Actor(character, 0, utcSeconds: 1000, returningAdventurerType: type);
            That(returning.Length == ordinary.Length &&
                Enumerable.Range(0, ordinary.Length).Where(at => ordinary[at] != returning[at]).SequenceEqual([ordinary.Length - 37]) &&
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(returning.AsSpan(returning.Length - 37)) == type,
                "新回归头像只修改原生u16身份，不增字节、不覆盖熟练进度或旧活动身份");
        }
        Reject<InvalidDataException>(() => TownPackets.Actor(character, 0, returningAdventurerType: 3), "拒绝未定义的新回归身份");
        Bytes(AdventurerBenefitPackets.Returning(0), new byte[16], "原生正常身份清空16B状态");
        Bytes(AdventurerBenefitPackets.Returning(0x12345678), Convert.FromHexString("01000000010000000000000078563412"),
            "原生回归身份1和绝对期限独立金样，提示已展示不等于领取奖励");
        foreach (string action in new[] { "lucky-profession", "returning-adventurer" })
        {
            var request = DevelopmentRequest.Parse("account other-1 " + action + " Hero", "default");
            request.Validate();
            That(request.Account == "other-1" && request.Name == "Hero" && request.Writes, "活动GM保留显式账号和角色身份");
        }
        string root = Path.Combine(Path.GetTempPath(), "adventurer-benefit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AdventurerBenefitStore(root);
            That(store.Read() == AdventurerBenefitConfiguration.Default, "新配置不伪造回归账号或个人幸运资格");
            var value = new AdventurerBenefitConfiguration(7, ImmutableDictionary<string, long>.Empty.Add("other-1", 12345678));
            store.Write(value);
            var restored = new AdventurerBenefitStore(root).Read();
            That(restored.ProfessionJob == 7 && restored.ReturningUntil.Count == 1 && restored.ReturningUntil["other-1"] == 12345678,
                "职业及账号回归期限重启后完整恢复");
            Reject<InvalidDataException>(() => store.Write(value with { ProfessionJob = 17 }), "非法职业不落盘");
            That(store.Read().ProfessionJob == 7, "拒绝写入保留原配置");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static void RunResources(PvfArchive pvf)
    {
        int before = Count;
        var catalog = new AdventurerBenefitCatalog(pvf);
        That(catalog.Professions.SequenceEqual([new EventProfession(1, -1)]) && catalog.ReturningPeriodSeconds == 28 * 86400 &&
            catalog.ReturningBuff == 1273 && catalog.ExistingBuff == 1273, "累计资源原生女格斗全转职和28天回归目录");
        That(catalog.KillExperiencePercent(1087) == 20 && catalog.KillExperiencePercent(1088) == 50 &&
            catalog.KillExperiencePercent(1089) == 100 && catalog.KillExperiencePercent(1273) == 0, "击杀经验比例从真实Buff读取而非硬编码");
        var service = new AdventurerBenefitService(catalog, AdventurerBenefitConfiguration.Default, _ => { });
        var hero = new AdventurerBenefitMember("hero", 0, 0);
        var fighter = new AdventurerBenefitMember("fighter", 1, 0x24);
        That(service.Select(hero, [hero], 1000).Buffs.IsEmpty && service.Select(fighter, [fighter], 1000).Buffs.IsEmpty,
            "单人及原生空职业自身表不伪造组队Buff");
        for (int count = 1; count <= 3; count++)
        {
            AdventurerBenefitMember[] members = [hero, .. Enumerable.Range(0, count).Select(index => fighter with { Account = "fighter" + index })];
            uint expected = count switch { 1 => 1087, 2 => 1088, _ => 1089 };
            That(service.Select(hero, members, 1000).Buffs.SequenceEqual([expected]), "按匹配职业成员数选择原生队友档位" + count);
        }
        var advanced = fighter with { GrowType = 0x25 };
        That(service.Select(hero, [hero, advanced], 1000).Buffs.SequenceEqual([1087u]), "全转职声明覆盖觉醒编码和第五分支");
        long until = service.SetReturning("hero", 1000);
        That(until == 1000 + 28 * 86400 && service.ReturningUntil("other", 1000) == 0, "回归仅设置目标账号且期限来自资源");
        That(service.Select(hero, [hero], 1000).Buffs.IsEmpty, "回归身份不把组队Buff变为单人永久属性");
        That(service.Select(hero, [hero, fighter], 1000).Buffs.SequenceEqual([1087u, 1273u]) &&
            service.Select(fighter, [hero, fighter], 1000).Buffs.SequenceEqual([1273u]), "回归与现役队友获得各自原生组队Buff且同索引不重复");
        That(service.Select(hero, [hero, fighter], until - 1).Buffs.Contains(1273u) &&
            !service.Select(hero, [hero, fighter], until).Buffs.Contains(1273u) && service.ReturningUntil("hero", until) == 0,
            "期限边界立即失效且不续期");
        service.SetProfession(7);
        var male = fighter with { Job = 7 };
        That(service.Select(hero, [hero, fighter], 1000).Buffs.SequenceEqual([1273u]) &&
            service.Select(hero, [hero, male], 1000).Buffs.SequenceEqual([1087u, 1273u]), "GM按基础职业替换目标，不按单个角色授予");
        var failed = new AdventurerBenefitService(catalog, AdventurerBenefitConfiguration.Default, _ => throw new IOException("fixture persist failure"));
        Reject<IOException>(() => failed.SetProfession(7), "职业配置落盘失败不发布");
        Reject<IOException>(() => failed.SetReturning("hero", 1000), "账号配置落盘失败不发布");
        That(failed.Configuration == AdventurerBenefitConfiguration.Default, "失败保留完整旧快照");
        Console.WriteLine($"PASS {Count - before} adventurer benefit resource assertions");
    }
}
