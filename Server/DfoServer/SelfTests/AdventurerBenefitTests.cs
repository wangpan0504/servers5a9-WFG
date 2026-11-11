using System.Collections.Immutable;
using DfoServer.Development;
using DfoServer.Game.Events;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Events;
using PvfLib;
using DfoServer.Network.Builders;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
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
        Bytes(AdventurerBenefitPackets.LuckyProfessions([new(7, 0x24), new(15, 0x12)]),
            Convert.FromHexString("0200000007000000240000000F00000012000000"), "幸运名单使用独立原生u32数量和8B职业编码金样");
        Bytes(AdventurerBenefitPackets.LuckyProfessions([]), new byte[4], "幸运名单清除使用零数量，不伪造个人身份");
        Reject<InvalidDataException>(() => AdventurerBenefitPackets.LuckyProfessions([new(16, 0)]), "拒绝未知幸运职业");
        Reject<InvalidDataException>(() => AdventurerBenefitPackets.LuckyProfessions([new(7, 0), new(7, 0)]), "拒绝重复幸运职业编码");
        foreach (string action in new[] { "lucky-profession", "returning-adventurer" })
        {
            var request = DevelopmentRequest.Parse("account other-1 " + action + " Hero", "default");
            request.Validate();
            That(request.Account == "other-1" && request.Name == "Hero" && request.Writes, "活动GM保留显式账号和角色身份");
        }
        foreach (string mode in new[] { "on", "off", "status" })
        {
            var request = DevelopmentRequest.Parse("lucky-professions " + mode, "default");
            That(request.Action == DevelopmentAction.LuckyProfession && request.Name.Length == 0 && request.Args.SequenceEqual([mode]),
                "全局幸运命令严格保留开关或只读动作：" + mode);
        }
        foreach (string invalid in new[] { "lucky-professions toggle", "lucky-professions on off", "lucky-profession Hero invalid" })
            Reject<InvalidDataException>(() => DevelopmentRequest.Parse(invalid, "default"), "非法幸运参数拒绝：" + invalid);
        That(AdventurerBenefitConfiguration.Default.LuckyEnabled == false && !AdventurerBenefitConfiguration.Default.AllProfessions,
            "首次服务端默认关闭全职业幸运");
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
            store.Write(value with { ProfessionJob = null, AllProfessions = true });
            var all = new AdventurerBenefitStore(root).Read();
            That(all.AllProfessions && all.ProfessionJob is null && all.ReturningUntil["other-1"] == 12345678,
                "全职业开关重启恢复且保留原回归账号期限");
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
        That(catalog.Lucky is { KillExperiencePercent: 10, ClearExperiencePercent: 15, QuestExperiencePercent: 10,
                PartyExperiencePercent: 10, FatigueAccelerationPercent: 100 } && service.LuckyProfessions(1000).IsEmpty,
            "四叶草独立资源包含原生比例，未启用的零日期日程不凭空授予幸运资格");
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
        That(service.Select(hero, [hero, fighter], 1000).Buffs.SequenceEqual([1087u, 1273u]) &&
            service.Select(hero, [hero, male], 1000).Buffs.SequenceEqual([1273u]), "幸运职业GM不改写独立女格斗组队活动的目标和专属Buff");
        That(service.LuckyProfessions(1000).All(row => row.Job == 7) && service.IsLucky(7, 0x24, 1000) &&
            service.IsLucky(7, 0, 1000) && !service.IsLucky(1, 0x24, 1000) && !service.IsLucky(7, 0x2f, 1000),
            "幸运GM按合法基础职业全部转职及觉醒生效，未设置职业及非法成长分支不获资格");
        That(service.Select(hero, [hero, male], 1000).LuckyPartyClearExperiencePercent == 10 &&
            service.Select(male, [hero, male], 1000).LuckyPartyClearExperiencePercent == 0 &&
            service.Select(male, [male], 1000).LuckyPartyClearExperiencePercent == 0 &&
            service.Select(hero, [hero, male, male with { Account = "lucky-other" }], 1000).LuckyPartyClearExperiencePercent == 10,
            "幸运队友通关奖励排除本人和单人，多名幸运队友也只给资源10%而不叠倍");
        service.SetProfession(15);
        That(service.IsLucky(15, 0x24, 1000) && service.LuckyProfessions(1000).All(row => row.Job == 15) &&
            !service.IsLucky(7, 0x24, 1000), "新职业使用实际CHR合法分支，覆盖原生日程缺少枪剑士的情况");
        var failed = new AdventurerBenefitService(catalog, AdventurerBenefitConfiguration.Default, _ => throw new IOException("fixture persist failure"));
        Reject<IOException>(() => failed.SetProfession(7), "职业配置落盘失败不发布");
        Reject<IOException>(() => failed.SetAllProfessions(), "全职业配置落盘失败不发布");
        Reject<IOException>(() => failed.SetReturning("hero", 1000), "账号配置落盘失败不发布");
        That(failed.Configuration == AdventurerBenefitConfiguration.Default, "失败保留完整旧快照");
        service.SetAllProfessions();
        var allProfessions = service.LuckyProfessions(1000);
        That(allProfessions.SequenceEqual(catalog.Lucky!.Professions.OrderBy(row => row.Key).SelectMany(row => row.Value)) &&
            allProfessions.All(row => service.IsLucky(row.Job, row.GrowType, 1000)) && !service.IsLucky(7, 0x2f, 1000),
            "全职业仅包含实际CHR全部合法转职觉醒，不授予非法分支");
        var allConfiguration = service.Configuration;
        service.SetAllProfessions();
        That(ReferenceEquals(service.Configuration, allConfiguration) && service.ReturningUntil("hero", 1000) == until,
            "换职业重复开启全职业不清旧职业、不重复持久化且不改回归期限");
        var restoredAll = new AdventurerBenefitService(catalog, service.Configuration, _ => { });
        That(restoredAll.LuckyProfessions(1000).SequenceEqual(allProfessions), "服务重建仍保持完整全职业名单");
        service.SetAllProfessions(false);
        That(service.LuckyProfessions(1000).IsEmpty && !service.Configuration.AllProfessions && service.Configuration.LuckyEnabled == false &&
            service.ReturningUntil("hero", 1000) == until, "关闭全职业清除名单并阻断日程恢复，保留独立回归期限");
        That(new AdventurerBenefitService(catalog, service.Configuration, _ => { }).LuckyProfessions(1000).IsEmpty,
            "关闭状态服务重建后保留，首次配置默认关闭");
        LuckyExperienceAndSession(pvf, catalog);
        Console.WriteLine($"PASS {Count - before} adventurer benefit resource assertions");
    }

    static void LuckyExperienceAndSession(PvfArchive pvf, AdventurerBenefitCatalog catalog)
    {
        var benefit = new AdventurerBenefitService(catalog, AdventurerBenefitConfiguration.Default, _ => { });
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var experience = new ExperienceBonusService(items, new RecoveryConsumableCatalog([]), new(0, 0), adventurerBenefits: benefit);
        var hero = new Character(1, 0, "Lucky", 0, new(1, 0, 10, 10), null) { Job = 7, GrowType = 0x24 };
        benefit.SetProfession(7);
        foreach (var (kind, amount) in new[] { (ExperienceEvent.Kill, 10u), (ExperienceEvent.Clear, 15u), (ExperienceEvent.Quest, 10u) })
        {
            var result = experience.Plan(new(hero, kind, 3, 1000, false), 101, 20, null);
            That(result.Amount(ExperienceBonusSource.LuckyCharacter) == amount && result.Total == 121 + amount &&
                result.Display(result.Total).BuffExperience == amount && result.Display(result.Total).DeductedExperienceComponent == amount,
                "幸运本人经验按事件独立取整且到账与显示守恒：" + kind);
            That(experience.Plan(new(hero with { Job = 0 }, kind, 3, 1000, false), 101, 20, null).Total == 121,
                "未命中职业不获得幸运经验：" + kind);
        }
        byte[] clear = DungeonSettlementPackets.EmptyCardReward(new(100, 20, 145, 1, "lucky-test", "fixture")
            { BonusParts = [new(ExperienceBonusSource.LuckyCharacter, 15), new(ExperienceBonusSource.LuckyParty, 10)] }, 0);
        That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(clear.AsSpan(123)) == 15 &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(clear.AsSpan(131)) == 10,
            "结果页幸运本人及队伍奖励使用独立原生字段，不挤占职业组队Buff");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-lucky-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            using var owner = new AccountOwners().Acquire("lucky-session", () => { });
            var towns = DfoServer.GameWorld.Towns.TownCatalog.Load(pvf);
            repository.Create(owner, new("LuckySession", towns.Room, towns.InitialReturn) { Job = 7, GrowType = 0x24 });
            var settings = new GameDebugSettings { AdventurerBenefits = benefit };
            var session = new GameSession(repository, towns, owner, debugSettings: settings);
            session.Select(0); session.MarkReady();
            var initial = session.PlanLuckyProfessions()!;
            That(!initial.Professions.IsEmpty && initial.IsCurrent() && session.PlanLuckyProfessions() is null,
                "同一选角配置只投影一次幸运名单");
            That(!session.PremiumPcRoom && session.ChangeFatigueAcceleration(true) is { Error: 0, Active: true },
                "幸运职业不伪造网吧身份即可选择原生免费燃烧");
            benefit.SetProfession(15);
            var changed = session.PlanLuckyProfessions()!;
            That(!initial.IsCurrent() && changed.IsCurrent() && changed.Professions.All(row => row.Job == 15) &&
                session.FatigueAccelerationState() is { Selected: true, Active: false },
                "职业切换废弃慢队列旧名单并停止原职业燃烧资格，保留玩家选择");
            session.ReturnToRoster(); session.Select(0); session.MarkReady();
            That(!changed.IsCurrent() && session.PlanLuckyProfessions() is not null,
                "重新选角重发当前名单，旧epoch不能发回旧资格");
            That(session.ChangeFatigueAcceleration(true) is { Error: 179 } &&
                session.ChangeFatigueAcceleration(false) is { Error: 0, Active: false },
                "普通职业不能开启幸运燃烧，仍可关闭已保存的选择");
            benefit.SetAllProfessions();
            var all = session.PlanLuckyProfessions()!;
            That(all.IsCurrent() && all.Professions.Any(row => row.Job == 7) && all.Professions.Any(row => row.Job == 15) &&
                session.ChangeFatigueAcceleration(true) is { Error: 0, Active: true }, "全职业开启后本人重新获得名单和免费燃烧资格");
            benefit.SetAllProfessions(false);
            var disabled = session.PlanLuckyProfessions()!;
            That(!all.IsCurrent() && disabled.IsCurrent() && disabled.Professions.IsEmpty &&
                session.FatigueAccelerationState() is { Selected: true, Active: false }, "关闭废弃旧名单、发送原生零数量并停止幸运燃烧");
            var ordinary = new GameSession(repository, towns, owner,
                debugSettings: new GameDebugSettings { AdventurerBenefits = new(catalog, AdventurerBenefitConfiguration.Default, _ => { }) });
            ordinary.Select(0);
            That(ordinary.PlanLuckyProfessions() is { Professions.IsEmpty: true },
                "首次普通选角也必须发送空名单，清掉前一次连接的客户端全局资格");
        }
        finally { if (Directory.Exists(root)) TestFiles.DeleteDirectory(root, true); }
    }
}
