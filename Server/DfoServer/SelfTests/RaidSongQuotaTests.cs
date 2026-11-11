using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Raids;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class RaidSongQuotaTests
{
    static readonly AccountMaterialDefinition Cap = new(4000, 4000, 1300);

    internal static void RunCore()
    {
        int before = Count;
        ExpectLuke("卢克满额", Held(1300, 1300, 1300), 0, 0);
        ExpectLuke("卢克部分余额", Held(1260, 1260, 1260), 25, 40);
        ExpectLuke("卢克未满", Held(0, 0, 0), 25, 100);
        ExpectLuke("卢克持有已满", Held(4000, 0, 0), 0, 0);
        var vortexFull = Clip([Vortex(100)], Held(1300, 1300, 1300));
        var vortexPart = Clip([Vortex(100)], Held(1260, 1260, 1260));
        var vortexOpen = Clip([Vortex(100)], Held(0, 0, 0));
        That(SongCount(vortexFull[0]) == 0 && Display(vortexFull[0]) == 0, "漩涡满额牌面为0");
        That(SongCount(vortexPart[0]) == 40 && Display(vortexPart[0]) == 40, "漩涡部分余额只显示能入账的40");
        That(SongCount(vortexOpen[0]) == 100 && Display(vortexOpen[0]) == 100, "漩涡未满保持抽出的100");
        var preyFull = Clip([Prey(0, 50), Prey(1, 150)], Held(4000, 4000, 100));
        var preyPart = Clip([Prey(0, 50), Prey(1, 150)], Held(1260, 1260, 1260));
        var preyOpen = Clip([Prey(0, 50), Prey(1, 150)], Held(0, 0, 0));
        That(SongCount(preyFull[0]) == 0 && SongCount(preyFull[1]) == 0 && Pupil(preyFull[0]) == 5 && Pupil(preyFull[1]) == 5, "普雷账号周满额只去掉不渝之咏");
        That(SongCount(preyPart[0]) == 40 && SongCount(preyPart[1]) == 40 && Pupil(preyPart[0]) == 5 && Pupil(preyPart[1]) == 5, "普雷部分余额两阶段各自按当时剩余计算，不预扣一阶段");
        That(SongCount(preyOpen[0]) == 50 && SongCount(preyOpen[1]) == 150 && Pupil(preyOpen[1]) == 5, "普雷未满保持50和150");
        That(PreyGuideRewards.ValidDisplay(preyFull[0].Offers[0]) && PreyGuideRewards.ValidDisplay(preyPart[0].Offers[0]), "数量0和部分数量都仍是合法普雷展示");
        var zero = new LukeRewardResultSpec(0, (uint)RaidSongQuota.ItemId, 0, 0, 0, 0);
        That(zero.Matches(new InventoryReward(RaidSongQuota.ItemId, 0)), "不渝之咏数量0与展示一致");
        That(!zero.Matches(new InventoryReward(RaidSongQuota.ItemId, 1)) && !(zero with { ItemId = 1 }).Matches(new InventoryReward(1, 0)), "数量不符或其他物品的0不放行");
        That(!(zero with { RawTail1 = null }).Matches(new InventoryReward(RaidSongQuota.ItemId, 0)), "缺字段不等于显式0");
        Bytes(LukeRewardPackets.Result(new(1, 7, 0, zero)),
            [1, 1, 7, 0, 0, 1, 0, 0xC7, 0x4C, 0x9D, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            "满额沿用600既有数量u16的0，不新增标志");
        Reject<InvalidDataException>(() => LukeRewardPackets.Result(new(1, 7, 0, zero with { ItemId = 1 })), "其他物品数量0仍拒绝");
        var personal = PreyRewardPackets.Personal(7, preyFull[0].Offers);
        That(personal[5] == 2 && personal[11] == 0 && personal[12] == 0, "普雷满额个人600保留不渝之咏行且数量为0");
        var frozen = ImmutableArray.Create(new InventoryReward(RaidSongQuota.ItemId, 100), new InventoryReward(3037, 5));
        var partial = RaidSongQuota.ClipGrantList(frozen, Held(1260, 1260, 1260), 7, Cap, 20_000, out var credited);
        That(partial[0].Count == 40 && partial[1].Count == 5 && credited.Balance == 1300 && credited.EarnedBy(7, 20_000) == 1300, "提交按数据库剩余裁到40并入账40");
        var none = RaidSongQuota.ClipGrantList(frozen, Held(1300, 1300, 1300), 7, Cap, 20_000, out var same);
        That(none[0].Count == 0 && same.Same(Held(1300, 1300, 1300)), "提交满额入账0，不改已有余额");
        var holdingFull = RaidSongQuota.ClipGrantList(frozen, new AccountMaterialBalance(4000), 7, Cap, 20_000, out var stamped);
        That(holdingFull[0].Count == 0 && holdingFull[1].Count == 5 && stamped.Balance == 4000 && stamped.RaidWeek == 20_000 &&
            stamped.RaidEarned == 0 && stamped.CharacterEarned.IsEmpty, "正数牌面裁到0时盖上攻坚周，持有和已获得不加");
        var kept = RaidSongQuota.ClipGrantList([new(RaidSongQuota.ItemId, 0)], Held(0, 0, 0), 7, Cap, 20_000, out var untouched);
        That(kept[0].Count == 0 && untouched.Same(Held(0, 0, 0)), "已发出的数量0在领取时不加发");
        var incoming = new PreparedRaidReward(Guid.Parse("00000000-0000-0000-0000-000000000121"), 0, 1, 0, 7, "src", frozen, false, 1) { RaidType = RaidKind.Luke };
        var stored = incoming with { Grants = partial };
        That(RaidSongQuota.IsSongCapSettlement(JsonSerializer.Serialize(stored), JsonSerializer.Serialize(incoming)), "重放承认不渝之咏向下裁剪");
        That(!RaidSongQuota.IsSongCapSettlement(JsonSerializer.Serialize(incoming), JsonSerializer.Serialize(stored)), "不允许把已裁数量加回");
        That(!RaidSongQuota.IsSongCapSettlement(JsonSerializer.Serialize(incoming with { Grants = frozen.SetItem(1, frozen[1] with { Count = 1 }) }), JsonSerializer.Serialize(incoming)), "其他奖励数量变化不是额度结算");
        var silent = new PreparedRaidReward(Guid.Parse("00000000-0000-0000-0000-000000000122"), 1, 1, 0, 7, LukeResourceRewards.Source, [new(RaidSongQuota.ItemId, 0)], true, 1) { RaidType = RaidKind.Luke };
        That(BattlePassUndefeatedSongSource.DirectAmount(silent, new(InventorySnapshot.Empty, InventorySnapshot.Empty, "raid_reward")) == 0, "数量0不申报战令");
        var at = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var wallet = InventorySnapshot.Empty with { AccountWallet = InventorySnapshot.Empty.AccountWallet.SetMaterial(RaidSongQuota.ItemId, new(10)) };
        var loose = new AccountMaterialDefinition(100);
        var granted = AccountMaterialService.Grant(wallet, 7, RaidSongQuota.ItemId, 25, loose, at, true);
        var plan = new InventoryPlan(wallet, granted.Plan!.After, "raid_reward")
        {
            AccountMaterialRaidDefinitions = ImmutableDictionary<int, AccountMaterialDefinition>.Empty.Add(RaidSongQuota.ItemId, loose)
        };
        That(BattlePassUndefeatedSongSource.DirectAmount(silent with { Grants = [new(RaidSongQuota.ItemId, 0), new(RaidSongQuota.ItemId, 25)], Character = 7 }, plan, at) == 25,
            "同批数量0不让正数量的战令校验失败");
        var restored = Clip([Luke(0, 25), Luke(1, 100)], Held(3900, 0, 0));
        That(SongCount(restored[0]) == 25 && SongCount(restored[1]) == 100, "消耗持有后两阶段各自按恢复的额度计算");
        var nextWeek = Clip([Luke(1, 100)], Held(0, 1260, 1260), 20_007);
        That(SongCount(nextWeek[0]) == 100, "跨周后旧获得额度不占用新周牌面");
        var claimed = RaidSongQuota.ClipGrantList([new(RaidSongQuota.ItemId, 25)], Held(1260, 1260, 1260), 7, Cap, 20_000, out var afterClaim);
        var phaseAfterClaim = RaidSongQuota.ClipOffersToRemaining(Luke(1, 100).Offers, afterClaim, 7, Cap, 20_000);
        That(claimed[0].Count == 25 && phaseAfterClaim[0].Grants[0].Count == 15, "另一次领取入账后，后续阶段按新余额计算");
        That(SongCount(Clip([Luke(1, 100)], Held(1260, 1260, 1260))[0]) == 40, "一阶段未领取时二阶段不预扣那25");
        var tight = new AccountMaterialDefinition(4000, 4000, 50);
        var weekly1300 = new AccountMaterialDefinition(4000, 4000, 1300);
        var tightFace = RaidSongQuota.ClipOffersToRemaining(Luke(1, 100).Offers, Held(0, 0, 0), 7, tight, 20_000);
        var weekly1300Face = RaidSongQuota.ClipOffersToRemaining(Luke(1, 100).Offers, Held(0, 0, 0), 7, weekly1300, 20_000);
        That(tightFace[0].Grants[0].Count == 50 && weekly1300Face[0].Grants[0].Count == 100 && DisplayOf(tightFace[0]) == 50,
            "牌面使用传入的账号材料定义，不写死1300");
        var reduced = RaidSongQuota.ApplyGrants(Luke(1, 100).Offers[0], [new(RaidSongQuota.ItemId, 40)]);
        That(RaidSongQuota.IsOfferReduction([reduced], Luke(1, 100).Offers) && !RaidSongQuota.IsOfferReduction(Luke(1, 100).Offers, [reduced]),
            "已发出的更小数量可重放，领取时不能加回");
        ExpectWeeklyEarned();
        ExpectPvfPathSelection();
        Console.WriteLine($"PASS {Count - before} 不渝之咏上限牌面");
    }

    // 周上限按本周累计获得。消费持有不回升周获得；跨周才回升。持有 4000 仍按当前持有单独限制。
    static void ExpectWeeklyEarned()
    {
        int before = Count;
        var spentCharacter = Held(0, 1300, 1300);
        That(RaidSongQuota.Remaining(spentCharacter, 7, Cap, 20_000) == 0, "角色周已获得 1300 后把持有消费到 0，周获得不回升");
        var spentFace = Clip([Luke(1, 100)], spentCharacter);
        That(SongCount(spentFace[0]) == 0, "消费持有后角色周已满，密封牌面仍是 0");
        var spentClaim = RaidSongQuota.ClipGrantList([new(RaidSongQuota.ItemId, 100)], spentCharacter, 7, Cap, 20_000, out var afterSpend);
        That(spentClaim[0].Count == 0 && afterSpend.Balance == 0 && afterSpend.RaidEarned == 1300 && afterSpend.EarnedBy(7, 20_000) == 1300,
            "领取时消费持有不回升角色周获得，也不改已获得");
        var spentAccount = Held(0, 4000, 0);
        That(RaidSongQuota.Remaining(spentAccount, 7, Cap, 20_000) == 0 &&
            SongCount(Clip([Luke(1, 100)], spentAccount)[0]) == 0, "账号周已获得 4000 后消费持有，账号周不回升");
        var crossed = Clip([Luke(1, 100)], Held(0, 1300, 1300), 20_007);
        That(SongCount(crossed[0]) == 100, "跨周后旧获得不再占用，牌面回到原始 100");
        var crossedClaim = RaidSongQuota.ClipGrantList([new(RaidSongQuota.ItemId, 100)], Held(0, 1300, 1300), 7, Cap, 20_007, out var afterWeek);
        That(crossedClaim[0].Count == 100 && afterWeek.Balance == 100 && afterWeek.RaidEarned == 100 && afterWeek.EarnedBy(7, 20_007) == 100,
            "领取时跨周按新周获得入账 100");
        That(RaidSongQuota.Remaining(Held(4000, 0, 0), 7, Cap, 20_000) == 0, "本周获得为 0 但持有已满 4000，持有上限仍拒绝");
        That(RaidSongQuota.Remaining(Held(3950, 0, 0), 7, Cap, 20_000) == 50, "本周获得为 0 时只剩持有空位 50");
        var holdClaim = RaidSongQuota.ClipGrantList([new(RaidSongQuota.ItemId, 100)], Held(3950, 0, 0), 7, Cap, 20_000, out var afterHold);
        That(holdClaim[0].Count == 50 && afterHold.Balance == 4000 && afterHold.RaidEarned == 50, "领取时持有上限按当前持有裁到 50，并计入本周获得");
        var tight = Held(3500, 3800, 1260);
        That(RaidSongQuota.Remaining(tight, 7, Cap, 20_000) == 40, "剩余同时受持有、账号周和角色周约束，取角色周剩余 40");
        Console.WriteLine($"PASS {Count - before} 不渝之咏周获得不随持有回升");
    }

    // 已经存在的显式路径优先，再回退工作目录和程序旁 Data/Pvf/Script.pvf。
    // 都不存在则返回 null，调用方打印 SKIP，不记为通过；不读取开发者固定盘符上的资源。
    internal static string? SelectCoreSongPvf(string? configuredPath, Func<string, bool> exists, string localFallback, string besideProgram)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && exists(configuredPath)) return configuredPath;
        if (exists(localFallback)) return localFallback;
        return exists(besideProgram) ? besideProgram : null;
    }

    static void ExpectPvfPathSelection()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "song-pvf-path-selection");
        string local = Path.Combine(root, "working", "Data", "Pvf", "Script.pvf");
        string beside = Path.Combine(root, "server", "Data", "Pvf", "Script.pvf");
        string configured = Path.Combine(root, "explicit", "Script.pvf");
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Exists(string path) => present.Contains(path);
        present.Add(local);
        present.Add(configured);
        That(SelectCoreSongPvf(configured, Exists, local, beside) == configured, "显式路径和工作目录回退都存在时选显式路径");
        present.Clear();
        present.Add(local);
        That(SelectCoreSongPvf(null, Exists, local, beside) == local &&
            SelectCoreSongPvf("  ", Exists, local, beside) == local, "未指定路径时使用工作目录回退");
        present.Clear();
        present.Add(beside);
        That(SelectCoreSongPvf(null, Exists, local, beside) == beside, "未指定且工作目录回退不存在时使用程序旁 Script.pvf");
        present.Clear();
        That(SelectCoreSongPvf(null, Exists, local, beside) is null &&
            SelectCoreSongPvf(configured, Exists, local, beside) is null, "显式路径、工作目录回退和程序旁都不存在时不选择文件");
        present.Add(local);
        That(SelectCoreSongPvf(configured, Exists, local, beside) == local, "显式路径缺失时回退到工作目录文件");
        Console.WriteLine($"PASS {Count - before} 不渝之咏PVF路径选择");
    }

    // 已存在的 --pvf-path 优先。未指定或不存在时回退工作目录，再回退程序旁 Data/Pvf/Script.pvf。
    // 命令：dotnet build/check/bin/DfoServer/debug/DfoServer.dll --selftest-core
    internal static void RunCoreResources(string? configuredPath)
    {
        string? path = SelectCoreSongPvf(configuredPath, File.Exists, Path.GetFullPath(Path.Combine("Data", "Pvf", "Script.pvf")),
            Path.Combine(AppContext.BaseDirectory, "Data", "Pvf", "Script.pvf"));
        if (path is null)
        {
            Console.WriteLine("SKIP 不渝之咏PVF上限：未指定可用 --pvf-path，且工作目录与程序旁 Script.pvf 都不存在，未执行不记为通过");
            return;
        }
        using var archive = PvfArchive.Open(path);
        RunResources(archive, new ItemResources(archive));
    }

    internal static void RunResources(PvfArchive archive, ItemResources items)
    {
        int before = Count;
        var template = items.Items.Get(RaidSongQuota.ItemId);
        var material = template.AccountMaterial ?? throw new InvalidDataException("真实目录缺少不渝之咏账号材料");
        That(template.StackLimit == 4000 && material.HoldingLimit == template.StackLimit &&
            material.WeeklyAccountLimit == 4000 && material.WeeklyCharacterLimit == 1300 &&
            ReferenceEquals(RaidSongQuota.Limit(items.Items), material),
            "真实PVF经目录读出不渝之咏持有4000、账号周4000、角色周1300");
        var catalog = RaidCatalog.Load(archive);
        var preyTokens = archive.ReadTokens(PreyTeamScript.Path);
        var preySymbols = archive.ReadTokens(PreyTeamScript.Path + ".symbol");
        var preyTeam = new PreyGuideRewards(PreyTeamScript.Parse(preyTokens, preySymbols, archive.ResolveString), items.Items, items.Boosters);
        var preyGuide = new PreyGuideRewards(PreyGuideScript.Parse(preyTokens, preySymbols, archive.ResolveString), items.Items, items.Boosters);
        var fiendwarPath = catalog.Find(2)!.Path;
        var fiendwarTokens = archive.ReadTokens(fiendwarPath);
        var fiendwarSymbols = archive.Contains(fiendwarPath + ".symbol") ? archive.ReadTokens(fiendwarPath + ".symbol") : [];
        var fiendwarTeam = FiendwarScript.Parse(fiendwarPath, fiendwarTokens, fiendwarSymbols, archive.ResolveString);
        var fiendwarGuide = FiendwarScript.ParseGuide(fiendwarPath, fiendwarTokens, fiendwarSymbols, archive.ResolveString);
        var fiendwar = new FiendwarRewards(fiendwarTeam, items.Items, items.Boosters, fiendwarGuide);
        var fiendwarGuideRewards = new FiendwarRewards(fiendwarGuide, items.Items, items.Boosters);
        var lukeRaid = catalog.Find(1)!;
        var lukeScript = RaidScriptParser.Parse(lukeRaid.Path, archive.ReadTokens(lukeRaid.Path),
            archive.Contains(lukeRaid.Path + ".symbol") ? archive.ReadTokens(lukeRaid.Path + ".symbol") : [],
            archive.ResolveString, RaidScriptKind.Luke);
        That(lukeScript.Phases.Length == 2, "卢克资源声明两阶段");
        if (!lukeScript.Issues.IsEmpty) lukeScript = lukeScript with { Issues = [] };
        var luke = new LukeResourceRewards(lukeScript, items.Items, items.Boosters);
        That(ReferenceEquals(preyTeam.UndefeatedSongLimit, material) && ReferenceEquals(preyGuide.UndefeatedSongLimit, material) &&
            ReferenceEquals(fiendwar.UndefeatedSongLimit, material) && ReferenceEquals(fiendwarGuideRewards.UndefeatedSongLimit, material) &&
            ReferenceEquals(luke.UndefeatedSongLimit, material),
            "卢克、漩涡和普雷使用目录中的同一份不渝之咏定义");
        Console.WriteLine($"PASS {Count - before} 不渝之咏PVF上限");
    }

    static void ExpectLuke(string name, AccountMaterialBalance balance, int first, int second)
    {
        var phases = Clip([Luke(0, 25), Luke(1, 100)], balance);
        That(SongCount(phases[0]) == first && Display(phases[0]) == first && phases[0].Offers[0].LukeResult!.Matches(phases[0].Offers[0]), name + "一阶段");
        That(SongCount(phases[1]) == second && Display(phases[1]) == second && phases[1].Offers[0].LukeResult!.Matches(phases[1].Offers[0]), name + "二阶段");
    }

    static ImmutableArray<RaidPhaseRewardPolicy> Clip(RaidPhaseRewardPolicy[] phases, AccountMaterialBalance balance, int week = 20_000) =>
        phases.Select(phase => phase with { Offers = RaidSongQuota.ClipOffersToRemaining(phase.Offers, balance, 7, Cap, week) }).ToImmutableArray();

    static RaidPhaseRewardPolicy Luke(int phase, int count)
    {
        var spec = new LukeRewardResultSpec(0, (uint)RaidSongQuota.ItemId, checked((ushort)count), 0, 0, 0);
        return new(LukeResourceRewards.Source, [new RaidRewardOffer(1, 0, [new(RaidSongQuota.ItemId, count)], []) { LukeResult = spec }], new(0, checked((byte)phase), 0, 0, 2, 0));
    }

    static RaidPhaseRewardPolicy Vortex(int count)
    {
        var spec = new LukeRewardResultSpec(0, (uint)RaidSongQuota.ItemId, checked((ushort)count), 0, 0, 0);
        return new(FiendwarRewards.Source, [new RaidRewardOffer(1, 0, [new(RaidSongQuota.ItemId, count)], []) { PreyDisplay = [spec] }], new(0, 0, 0, 0, 2, 0));
    }

    static RaidPhaseRewardPolicy Prey(int phase, int song)
    {
        var grants = ImmutableArray.Create(new InventoryReward(RaidSongQuota.ItemId, song), new InventoryReward(3037, 5));
        var display = grants.Select(grant => new LukeRewardResultSpec(0, checked((uint)grant.ItemId), checked((ushort)grant.Count), 0, 0, 0)).ToImmutableArray();
        return new(PreyGuideRewards.TeamSource, [new RaidRewardOffer(1, 0, grants, []) { PreyDisplay = display }], new(0, checked((byte)phase), 0, 0, 2, 0));
    }

    static int SongCount(RaidPhaseRewardPolicy phase) => phase.Offers[0].Grants.Single(row => row.ItemId == RaidSongQuota.ItemId).Count;

    static int Display(RaidPhaseRewardPolicy phase) => DisplayOf(phase.Offers[0]);

    static int DisplayOf(RaidRewardOffer offer)
    {
        var spec = offer.LukeResult ?? offer.PreyDisplay.Single(row => row.ItemId == (uint)RaidSongQuota.ItemId);
        return spec.DisplayNumber!.Value;
    }

    static int Pupil(RaidPhaseRewardPolicy phase) => phase.Offers[0].Grants.Single(row => row.ItemId == 3037).Count;

    static AccountMaterialBalance Held(int balance, int accountEarned, int characterEarned, int week = 20_000, ushort character = 7)
    {
        if (accountEarned == 0 && characterEarned == 0) return new(balance);
        var characters = characterEarned == 0 ? ImmutableSortedDictionary<ushort, int>.Empty :
            ImmutableSortedDictionary<ushort, int>.Empty.SetItem(character, characterEarned);
        return new(balance, week, accountEarned, characters);
    }
}
