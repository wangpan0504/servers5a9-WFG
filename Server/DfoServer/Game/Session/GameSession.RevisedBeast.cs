using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Dungeons;
using DfoServer.Game.Characters;
namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal DateTimeOffset DungeonBusinessNow => IncountNow();

    public RevisedBeastCountsReceipt? CaptureRevisedBeastCounts(DungeonRoomCatalog rooms)
    {
        // 配置读取在owner锁外；账本查询只读取当前角色的小计。
        if (!BeastChannel.IsEntry(ChannelEntry) || repository is not IRevisedBeastRewardStore store) return null;
        var rules=rooms.RevisedBeastEncounter(0);
        return owner.Execute(() =>
        {
            // 选角初始化先发布HUD，随后才MarkReady；只读次数也须在此阶段投影。
            if (Selected is not { } character) return null;
            var period=DungeonIncountPolicy.Period(IncountNow());
            var entry=repository.LoadDungeonIncount(owner,character.Id,RevisedBeastSelection.FirstDungeon,period);
            var reward=store.LoadRevisedBeastRewardUsage(owner,character.Id,period);
            var counts=new RevisedBeastCounts(checked((uint)Math.Max(0,entry.DailyUsed)),
                checked((uint)Math.Max(0,entry.WeeklyUsed)),
                checked((byte)Math.Max(0,rules.DailyRewardLimit-reward.DailyUsed)),
                checked((byte)Math.Max(0,rules.WeeklyRewardLimit-reward.WeeklyUsed)));
            return new RevisedBeastCountsReceipt(character,owner.Generation,SelectionEpoch,period,counts);
        });
    }

    public bool CanPublishRevisedBeastCounts(RevisedBeastCountsReceipt receipt) => owner.Execute(() =>
        BeastChannel.IsEntry(ChannelEntry) && owner.Generation==receipt.Generation && SelectionEpoch==receipt.SelectionEpoch &&
        ReferenceEquals(Selected,receipt.Character) && DungeonIncountPolicy.Period(IncountNow())==receipt.Period);

    public RevisedBeastStageReceipt? SettleRevisedBeastAnne(ItemCatalog items,GoldLimitCatalog gold,EpicPieceCatalog pieces)
    {
        var captured=owner.Execute(()=>(Character:Ready?Selected:null,Dungeon:formalDungeon,Epoch:SelectionEpoch));
        if (captured.Character is not { } expected || captured.Dungeon is not { Entry.IsRevisedBeast:true } dungeon ||
            dungeon.Run.RevisedBeastAnneStage is not { } stage || dungeon.Run.RevisedBeastTerminal is not null ||
            dungeon.Entry.RevisedBeastEncounter is not { } rules || repository is not IRevisedBeastRewardStore store) return null;
        if (dungeon.RevisedBeastStageReceipt is { } completed) return completed;
        var now=(timeProvider ?? TimeProvider.System).GetUtcNow();
        var rewards=System.Collections.Immutable.ImmutableArray.Create(new InventoryReward(rules.AnneRewardItem,1));
        var plan=dungeon.RevisedBeastStageReward;
        if (plan is null)
        {
            var inventory=InventoryRewardGrantService.TryGrantBatch(expected,rewards,items,gold,pieces,now,allowOverflowMail:true);
            if (!inventory.Accepted || inventory.Plan is null) return null;
            plan=new(expected,stage,new(expected.Inventory,inventory.Plan.After,"revised_beast_anne",new(expected),inventory.Plan.OverflowItems),
                rewards,DungeonIncountPolicy.Period(now),rules.DailyRewardLimit,rules.WeeklyRewardLimit);
        }
        return owner.Execute(()=>
        {
            if (!Ready || SelectionEpoch!=captured.Epoch || !ReferenceEquals(formalDungeon,dungeon) ||
                !ReferenceEquals(Selected,expected) || Phase!=SessionPhase.Dungeon ||
                !ReferenceEquals(dungeon.Run.RevisedBeastAnneStage,stage) || dungeon.Run.RevisedBeastTerminal is not null) return null;
            dungeon.RevisedBeastStageReward ??= plan;
            var commit=store.CommitRevisedBeastStageReward(owner,dungeon.Run,dungeon.RevisedBeastStageReward);
            if (!commit.ProjectionCurrent) return null;
            Selected=commit.Character;
            return dungeon.RevisedBeastStageReceipt=new(FormalReceipt(),stage,commit);
        });
    }

    public bool CanPublishRevisedBeastStage(RevisedBeastStageReceipt receipt) => owner.Execute(()=>Ready &&
        formalDungeon?.RevisedBeastStageReceipt==receipt && SelectionEpoch==receipt.Scene.SelectionEpoch &&
        ReferenceEquals(Selected,receipt.Commit.Character) && formalDungeon.Run.Snapshot.Identity==receipt.Stage.Identity &&
        Phase==SessionPhase.Dungeon);

    public RevisedBeastSettlementReceipt? SettleRevisedBeast(ItemCatalog items,GoldLimitCatalog gold,EpicPieceCatalog pieces)
    {
        var captured=owner.Execute(()=>(Character:Ready?Selected:null,Dungeon:formalDungeon,Epoch:SelectionEpoch));
        if (captured.Character is not { } expected || captured.Dungeon is not { Entry.IsRevisedBeast:true } dungeon ||
            dungeon.Run.RevisedBeastTerminal is not { } terminal || dungeon.Entry.RevisedBeastEncounter is not { } rules ||
            repository is not IRevisedBeastRewardStore store) return null;
        if (dungeon.RevisedBeastSettlement is { } completed) return completed;
        var now=(timeProvider ?? TimeProvider.System).GetUtcNow();
        // 安妮只在阶段发奖；终局胜利才新增歇罗奖励，死亡终局不新增资产。
        if (dungeon.RevisedBeastStageReceipt is not { } stage) return null;
        var rewards=terminal.Success
            ? System.Collections.Immutable.ImmutableArray.Create(new InventoryReward(rules.HierroRewardItem,1))
            : System.Collections.Immutable.ImmutableArray<InventoryReward>.Empty;
        var plan=dungeon.RevisedBeastReward;
        if (plan is null)
        {
            var inventory=InventoryRewardGrantService.TryGrantBatch(expected,rewards,items,gold,pieces,now,allowOverflowMail:true);
            if (!inventory.Accepted || inventory.Plan is null) return null;
            plan=new(expected,terminal,dungeon.Entry.DungeonId,dungeon.Entry.Difficulty,
                new(expected.Inventory,inventory.Plan.After,"revised_beast_terminal",new(expected),inventory.Plan.OverflowItems),
                rewards,DungeonIncountPolicy.Period(now),rules.DailyRewardLimit,rules.WeeklyRewardLimit);
        }
        return owner.Execute(()=>
        {
            if (!Ready || SelectionEpoch != captured.Epoch || !ReferenceEquals(formalDungeon,dungeon) ||
                !ReferenceEquals(Selected,expected) || Phase != SessionPhase.Settlement ||
                !ReferenceEquals(dungeon.Run.RevisedBeastTerminal,terminal)) return null;
            dungeon.RevisedBeastReward ??= plan;
            var commit=store.CommitRevisedBeastReward(owner,dungeon.Run,dungeon.RevisedBeastReward);
            if (!commit.ProjectionCurrent) return null;
            Selected=commit.Character;
            return dungeon.RevisedBeastSettlement=new(FormalReceipt(),terminal,commit)
            { DisplayRewards=stage.Commit.Rewards.AddRange(commit.Rewards),
                Points=rules.PointsFor(terminal.Success,terminal.ElapsedMilliseconds),
                BestRecords=store.LoadRevisedBeastBestRecords(owner,expected.Id,dungeon.Entry.DungeonId,dungeon.Entry.Difficulty),
                ElapsedSeconds=dungeon.RevisedBeastElapsedSeconds ?? throw new InvalidOperationException("改版终局未冻结计时") };
        });
    }

    public RevisedBeastSettlementReceipt? CurrentRevisedBeastSettlement() => owner.Execute(() =>
        formalDungeon?.RevisedBeastSettlement is { } receipt && CanPublishRevisedBeast(receipt) ? receipt : null);

    public bool CanPublishRevisedBeast(RevisedBeastSettlementReceipt receipt) => owner.Execute(()=>Ready &&
        formalDungeon?.RevisedBeastSettlement == receipt && SelectionEpoch == receipt.Scene.SelectionEpoch &&
        ReferenceEquals(Selected,receipt.Commit.Character) && formalDungeon.Run.Snapshot.Identity == receipt.Terminal.Identity);

    public FormalDungeonReceipt? FinishRevisedBeastDeathTimeout(DungeonDeathReturnReceipt scheduled) => owner.Execute(()=>
    {
        if (!Ready || Selected?.Id != scheduled.CharacterId || owner.Generation != scheduled.Generation ||
            SelectionEpoch != scheduled.SelectionEpoch || formalDungeon is not { CharacterDead:true,Entry.IsRevisedBeast:true } dungeon ||
            dungeon.DeathSerial != scheduled.DeathSerial || dungeon.Run.Snapshot.Identity.InstanceId != scheduled.InstanceId ||
            dungeon.RevisedBeastDeathDue is not { } due ||
            (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds() < due ||
            !dungeon.Run.FinishRevisedBeastOnDeathTimeout()) return null;
        FreezeRevisedBeastElapsed(dungeon);
        SetState(SessionPhase.Settlement);
        return FormalReceipt();
    });

    // 使用本实例单调时钟；在终局事实成立时冻结，奖励提交或重发不能延长对局。
    static void FreezeRevisedBeastElapsed(FormalDungeon dungeon) =>
        dungeon.RevisedBeastElapsedSeconds ??= (dungeon.Run.RevisedBeastTerminal ??
            throw new InvalidOperationException("改版魔兽尚未终局")).ElapsedMilliseconds / 1000;

    public TimeSpan DungeonDeathDelay => owner.Execute(()=>TimeSpan.FromMilliseconds(
        formalDungeon?.RevisedBeastDeathDue is { } due ? Math.Max(0,due-(timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds()) : 10000));
}
