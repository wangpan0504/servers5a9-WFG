using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassUndefeatedSongBoosterTests
{
    static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    static readonly Location Room = new(0, 0, 0, 0);

    public static void RunCore()
    {
        int before = Count;
        var plan = Plan(); var fact = BattlePassUndefeatedSongBoosterSource.Prepare(1, 0, plan)!;
        That(fact.Amount == 25 && fact.At == At && fact.SourceId.Length == 32 &&
            fact.SourceId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "原开箱25实物形成确定32hex来源及原取得时间");
        That(BattlePassUndefeatedSongBoosterSource.Prepare(1, 0, plan) == fact, "同一原冻结计划重试不重抽或换来源");
        That(BattlePassUndefeatedSongBoosterSource.Prepare(1, 0, Plan(old: 10, added: 25))!.Amount == 25,
            "合堆只计本笔25，不把原余额10再次计入");
        That(BattlePassUndefeatedSongBoosterSource.Prepare(1, 0, Plan(old: 10, added: 25, accountMaterial: true))!.Amount == 25,
            "真实账号材料余额10增至35只计取得25");
        foreach (var invalid in new[] { Plan(old: 10, added: 24, accountMaterial: true),
            Plan(old: 10, added: -1, accountMaterial: true) })
            Reject<InvalidDataException>(() => BattlePassUndefeatedSongBoosterSource.Prepare(1, 0, invalid),
                "账号材料增量与原奖池不符或减少时拒绝");
        That(BattlePassUndefeatedSongBoosterSource.Prepare(1, 0, Plan(added: 10, overflow: 15))!.Amount == 10,
            "部分投邮只计实际入包10，保留另15待邮件真正提取");
        That(BattlePassUndefeatedSongBoosterSource.Prepare(1, 0, Plan(added: 0, overflow: 25)) is null,
            "原箱全部投邮尚非实物入包，不生成来源");
        That(plan.WithAfter(plan.After).BoosterSongAcquisition == plan.BoosterSongAcquisition,
            "原WithAfter在同事务后补状态时完整保留开箱事实");
        That(BattlePassUndefeatedSongBoosterSource.Prepare(1, 0, Plan(remaining: 1, sourceCount: 2))!.Amount == 25,
            "批量源只消费原一次，剩余同UID完整核心仍保留");
        var unrelated = new InventoryPlan(plan.Before, plan.After, "reward_grant");
        That(BattlePassUndefeatedSongBoosterSource.Prepare(1, 0, unrelated) is null,
            "初始Grant或普通转移的正余额无typed原开箱事实，不计任务");
        foreach (var invalid in new[]
        {
            Plan(character: 2), Plan(revision: 1), Plan(reason: "inventory_move"), Plan(sourceUid: 0),
            Plan(added: 24), Plan(added: 0), Plan(overflow: 1), Plan(sourceCount: 2),
            Plan(kind: ItemKind.Consumable), Plan(declared: 24), Plan(noPrecondition: true),
            Plan(openCount: 0), Plan(openCount: 2), Plan(remaining: 1), Plan(sourceCount: 2, remaining: 1, changedSourceCore: true)
        }) Reject<InvalidDataException>(() => BattlePassUndefeatedSongBoosterSource.Prepare(1, 0, invalid),
            "错误actor/revision/原UID核心/扣源次数/授权实物差量不能冒真实开箱取得");
        Reject<InvalidDataException>(() => BattlePassUndefeatedSongBoosterSource.Prepare(0, 0, plan), "空角色不注册来源");
        foreach (var other in new[] { Plan(sourceUid: 2), Plan(sourceId: 10304817), Plan(sourceCount: 2, remaining: 1, declared: 100, added: 100) })
            That(BattlePassUndefeatedSongBoosterSource.Prepare(1, 0, other)!.SourceId != fact.SourceId,
                "真实不同源UID/盒ID/奖池数量身份独立");
        Console.WriteLine($"PASS {Count - before} BattlePassUndefeatedSongBoosterTests core");
    }

    static InventoryPlan Plan(int old = 0, int added = 25, int overflow = 0, int declared = 25,
        ushort character = 1, long revision = 0, long sourceUid = 1, int sourceId = 10308804,
        int sourceCount = 1, int remaining = 0, int openCount = 1, string reason = "booster_open",
        ItemKind kind = ItemKind.Material, bool noPrecondition = false, bool changedSourceCore = false, bool accountMaterial = false)
    {
        var source = new InventoryItem(sourceUid, new(InventoryListType.Main, 65), new(sourceId, ItemKind.Consumable, sourceCount));
        InventoryItem Song(int count) => new(7, new(InventoryListType.Main, 121), new(10308807, kind, count));
        var before = new InventorySnapshot(old == 0 || accountMaterial ? [source] : [source, Song(old)]);
        if (accountMaterial) before = before with
            { AccountWallet = before.AccountWallet.SetMaterial(10308807, new AccountMaterialBalance(old)) };
        var afterItems = new List<InventoryItem>();
        if (remaining > 0) afterItems.Add(source with { Core = source.Core with { Value = remaining, Attr = changedSourceCore ? (byte)1 : (byte)0 } });
        if (!accountMaterial && old + added > 0) afterItems.Add(Song(old + added));
        var after = new InventorySnapshot([.. afterItems]);
        if (accountMaterial) after = after with
            { AccountWallet = after.AccountWallet.SetMaterial(10308807, new AccountMaterialBalance(old + added)) };
        var actor = new Character(character, 0, "来源", 0, Room, Room) { Revision = revision, Inventory = before };
        return new(before, after, reason, noPrecondition ? null : new InventoryCharacterPrecondition(actor),
            overflow == 0 ? [] : [Song(overflow) with { Uid = 0 }])
        {
            UsedItems = [new(sourceId, openCount)],
            BoosterSongAcquisition = new(character, revision, source, openCount, declared, At)
        };
    }
}
