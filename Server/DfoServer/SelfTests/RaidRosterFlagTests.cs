using System.Text.RegularExpressions;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Testing;
using PvfLib;

namespace DfoServer;

// 名册尾部 tail[37]安徒恩 / tail[38]卢克 / tail[39]保留零字节 / tail[41]普雷·伊希斯。tail[40]超时空漩涡仍看公会。
// 证据 evidence/roster-raid-flags-20261021.txt（ARAD 03B55580 频道类型分支 + 02676A00 读序 + dstr 66087/66088/66089/32116）。
internal static class RaidRosterFlagTests
{
    const int RosterTrailer = 10;
    const int TailLength = 48;

    public static void Run()
    {
        int checks = TestCheck.Count;
        TestCheck.That(CharacterPackets.AntonEligibleTailOffset == 37 && CharacterPackets.LukeEligibleTailOffset == 38 &&
            CharacterPackets.ReservedSpecialChannelTailOffset == 39 && CharacterPackets.FiendWarEligibleTailOffset == 40 &&
            CharacterPackets.PreyEligibleTailOffset == 41, "tail[37..41] 依次为安徒恩/卢克/保留零/超时空漩涡/普雷");
        TestCheck.That(CharacterRosterEntry.IsPreyEligible(true, 95) && CharacterRosterEntry.IsPreyEligible(true, 100) &&
            !CharacterRosterEntry.IsPreyEligible(true, 94) && !CharacterRosterEntry.IsPreyEligible(false, 100), "普雷要求任务 8935 且 Lv95 以上");
        RunPacket();
        RunLoginType();
        RunRepository();
        RunAdmission();
        Console.WriteLine($"PASS {TestCheck.Count - checks} 安徒恩/卢克/普雷名册资格位断言");
    }

    static void RunAdmission()
    {
        var channel = new DfoServer.GameWorld.Channels.ChannelEntry(new(2, 19), 23, "[raid]");
        var point = new Location(1, 0, 10, 10);
        var role = new Character(1, 0, "Admission", 0, point, point) { Level = 85, CompletedQuests = [12829, 3779] };
        string? Check(Character value) => DfoServer.Game.Raids.RaidAdmissionPlan.Rejection(channel, value);
        TestCheck.That(Check(role) is null && Check(role with { Level = 99 }) is null,
            "安徒恩按PVF接受85至99级，不采用未证90级比较");
        TestCheck.That(Check(role with { Level = 84 }) == "anton-level-required" &&
            Check(role with { Level = 100 }) == "anton-level-required", "安徒恩等级上下界");
        TestCheck.That(Check(role with { CompletedQuests = [3779] }) == "anton-entry-quest-required" &&
            Check(role with { CompletedQuests = [12829] }) == "anton-prerequisite-required",
            "名册任务完成不替代前置完成，接受态不等于完成态");
        channel = channel with { Type = 0x20, Group = "[luke_raid]" };
        role = role with { Level = 90, CompletedQuests = [2903, 4872] };
        TestCheck.That(Check(role) is null && Check(role with { Level = 99 }) is null,
            "卢克按入场任务资源接受90至99级，不以城镇86级门槛代替");
        TestCheck.That(Check(role with { Level = 89 }) == "luke-level-required" &&
            Check(role with { Level = 100 }) == "luke-level-required", "卢克等级上下界");
        TestCheck.That(Check(role with { CompletedQuests = [4872] }) == "luke-entry-quest-required" &&
            Check(role with { CompletedQuests = [2903] }) == "luke-prerequisite-required",
            "卢克完成2903及4872分别复核，不以名册资格位代替前置");
        TestCheck.That(DfoServer.Game.Raids.RaidAdmissionPlan.Rejection(channel with { Type = 22 },
            role with { Level = 1, CompletedQuests = [] }) is null, "安徒恩门禁不污染普通频道");
    }

    // 03B55580 只在 +0x1010 == 0x17 / 0x20 时读 tail[37] / tail[38]，ACK LOGIN 第 3 字节必须发安徒恩/卢克频道真实类型。
    static void RunLoginType()
    {
        var anton = new DfoServer.GameWorld.Channels.ChannelEntry(new(2, 19), 23, "[raid]");
        var luke = new DfoServer.GameWorld.Channels.ChannelEntry(new(2, 20), 32, "[luke_raid]");
        var normal = new DfoServer.GameWorld.Channels.ChannelEntry(new(2, 1), 22, "[elven_guard]");
        byte[] plain = ChannelPackets.Endpoint("127.0.0.1", 10011, 7);
        byte[] antonBody = ChannelPackets.Endpoint("127.0.0.1", 10011, 7, ChannelPackets.LoginChannelType(anton));
        byte[] lukeBody = ChannelPackets.Endpoint("127.0.0.1", 10011, 7, ChannelPackets.LoginChannelType(luke));
        TestCheck.That(ChannelPackets.LoginChannelType(anton) == 0x17 && ChannelPackets.LoginChannelType(luke) == 0x20 &&
            ChannelPackets.LoginChannelType(normal) == 22 && antonBody[3] == 0x17 && lukeBody[3] == 0x20 &&
            antonBody.Where((b, i) => i != 3).SequenceEqual(plain.Where((b, i) => i != 3)) &&
            lukeBody.Where((b, i) => i != 3).SequenceEqual(plain.Where((b, i) => i != 3)),
            "安徒恩/卢克频道 ACK LOGIN 第 3 字节发 0x17/0x20，普通频道发 22 以启用冒险团页，其余字节不变");
    }

    static void RunPacket()
    {
        var position = new Location(38, 0, 100, 100);
        Character Role(int[] quests, byte level = 95) => new Character(123, 2, "剑影", 7, position, position)
            { Level = level, GrowType = 37, CompletedQuests = quests };
        byte[] none = Tail(Role([]));
        byte[] anton = Tail(Role([12829]));
        byte[] luke = Tail(Role([2903]));
        byte[] prey = Tail(Role([8935]));
        byte[] preyLow = Tail(Role([8935], 94));
        byte[] beast = Tail(Role([5927]));
        byte[] all = Tail(Role([12829, 2903, 8935, 5927]));
        TestCheck.That(none[37] == 0 && none[38] == 0 && none[39] == 0 && none[40] == 0 && none[41] == 0, "无任务时五个资格位均为 0");
        TestCheck.That(anton[37] == 1 && OnlyDiffers(anton, none, 37), "完成 12829：只有 tail[37]=1");
        TestCheck.That(luke[38] == 1 && OnlyDiffers(luke, none, 38), "完成 2903：只有 tail[38]=1");
        TestCheck.That(prey[41] == 1 && OnlyDiffers(prey, none, 41), "完成 8935 且 Lv95：只有 tail[41]=1");
        TestCheck.That(preyLow[41] == 0 && preyLow.SequenceEqual(none), "完成 8935 但 Lv94：tail[41]=0");
        TestCheck.That(beast[39] == 0 && beast.SequenceEqual(none), "旧5927完成历史不能激活已退役入口，tail[39]固定0");
        TestCheck.That(all[37] == 1 && all[38] == 1 && all[39] == 0 && all[41] == 1 && all[40] == 0, "三项现行任务打开对应资格位，退役位保持零，超时空漩涡仍需公会");
    }

    static bool OnlyDiffers(byte[] tail, byte[] baseline, int offset) =>
        Enumerable.Range(0, TailLength).All(i => i == offset || tail[i] == baseline[i]);

    static byte[] Tail(Character character)
    {
        byte[] body = CharacterPackets.Roster([CharacterRosterEntry.FromCharacter(character)]);
        return body.AsSpan(body.Length - RosterTrailer - TailLength, TailLength).ToArray();
    }

    static void RunRepository()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-raid-roster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("raid-roster", () => { });
            var position = new Location(1, 0, 10, 10);
            NewCharacter Role(string name, int[] quests, byte level) => new(name, position, position) { CompletedQuests = quests, Level = level };
            var anton = repository.Create(owner, Role("RrAnton", [12829], 90));
            var luke = repository.Create(owner, Role("RrLuke", [2903], 90));
            var prey = repository.Create(owner, Role("RrPrey", [8935], 95));
            var preyLow = repository.Create(owner, Role("RrPreyLow", [8935], 94));
            var none = repository.Create(owner, Role("RrNone", [10], 95));
            var roster = repository.ListRoster(owner);
            CharacterRosterEntry Row(Character character) => roster.Single(row => row.Id == character.Id);
            TestCheck.That(Row(anton) is { AntonEligible: true, LukeEligible: false, PreyEligible: false } &&
                Row(luke) is { AntonEligible: false, LukeEligible: true, PreyEligible: false } &&
                Row(prey) is { AntonEligible: false, LukeEligible: false, PreyEligible: true } &&
                Row(preyLow) is { PreyEligible: false } && Row(none) is { AntonEligible: false, LukeEligible: false, PreyEligible: false },
                "名册查询按 character_completed_quests 与等级计算安徒恩/卢克/普雷资格");
            var full = repository.List(owner);
            TestCheck.That(CharacterPackets.Roster(roster).SequenceEqual(CharacterPackets.Roster(full.Select(CharacterRosterEntry.FromCharacter).ToArray())),
                "轻量名册与完整角色的资格字节一致");
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
    }

    // 资源核对：常量必须与 PVF etc/enterablespecialchannel.etc 及任务文件一致；退役保留位不依赖资源资格。
    public static void RunResources(TownCatalog towns, PvfArchive pvf)
    {
        int checks = TestCheck.Count;
        int Quest(string key) => towns.SpecialChannelQuests.TryGetValue(key, out int quest) ? quest : -1;
        TestCheck.That(Quest("anton") == CharacterRosterEntry.AntonEntryQuest && Quest("luke") == CharacterRosterEntry.LukeEntryQuest &&
            Quest("prey") == CharacterRosterEntry.PreyEntryQuest && Quest("fiendwar") == CharacterRosterEntry.FiendWarEntryQuest,
            "enterablespecialchannel.etc：anton=12829 luke=2903 prey=8935 fiendwar=8672");
        string list = pvf.ReadText("n_quest/quest.lst");
        string? File(int id) => Regex.Match(list, $@"(?<!\d){id}\s+`([^`]+)`") is { Success: true } m ? m.Groups[1].Value : null;
        TestCheck.That(File(12829) is { } a && a.Contains("Anton_dimension_enter", StringComparison.OrdinalIgnoreCase) &&
            File(2903) is { } l && l.Contains("Luke_enter_01", StringComparison.OrdinalIgnoreCase) &&
            File(8935) is { } p && p.Contains("Isys_normal_clear_03", StringComparison.OrdinalIgnoreCase), "入场任务均在 quest.lst 注册");
        string antonQuest = pvf.ReadText("n_quest/" + File(12829)!.Replace('\\', '/'));
        TestCheck.That(Regex.IsMatch(antonQuest, @"\[level\]\s+85\s+99") &&
            Regex.IsMatch(antonQuest, @"\[pre required quest\]\s+3779"), "安徒恩权威准入常量匹配PVF等级及前置");
        string lukeQuest = pvf.ReadText("n_quest/" + File(2903)!.Replace('\\', '/'));
        TestCheck.That(Regex.IsMatch(lukeQuest, @"\[level\]\s+90\s+99") &&
            Regex.IsMatch(lukeQuest, @"\[pre required quest\]\s+4872"), "卢克权威准入常量匹配PVF等级及前置");
        string preyQuest = pvf.ReadText("n_quest/" + File(8935)!.Replace('\\', '/'));
        TestCheck.That(Regex.IsMatch(preyQuest, @"\[level\]\s+95\s+100"), "前往夜之摩天楼 [level] 95 100，与 dstr 32116 的 Lv95 一致");
        Console.WriteLine($"PASS {TestCheck.Count - checks} 团本入场资格 PVF 核对断言");
    }
}
