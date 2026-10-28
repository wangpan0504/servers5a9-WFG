using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using DfoServer.Network.Projection.Inventory;

namespace DfoServer.Network.Projection;

// 每条连接最多一个计时任务；使用命令门串行发布，取消等待不反向持有账号owner。
internal sealed class DungeonAltarProjection(GameConnectionContext context, ServerLog log, TownProjection? town = null,
    InventoryProjection? inventory = null, CharacterLevelProjection? levels = null, TimeProvider? time = null) : IAsyncDisposable
{
    CancellationTokenSource? cancellation;
    Task pending = Task.CompletedTask;

    public async Task WakeAsync(DungeonAltarReceipt receipt)
        => await WakeAsync(receipt, null);

    public async Task WakeAfterDeathAsync(DungeonAltarReceipt receipt, Func<DungeonAltarReceipt?> complete)
        => await WakeAsync(receipt, complete);

    async Task WakeAsync(DungeonAltarReceipt receipt, Func<DungeonAltarReceipt?>? complete)
    {
        await StopAsync();
        await PublishAsync(receipt);
        if (receipt.Due is null || receipt.Completed && receipt.Result is null || !context.Session.CanPublishDungeonAltar(receipt)) return;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Stop);
        pending = RunAsync(receipt, complete, cancellation.Token);
    }

    async Task RunAsync(DungeonAltarReceipt scheduled, Func<DungeonAltarReceipt?>? complete, CancellationToken stop)
    {
        try
        {
            while (scheduled.Due is { } due)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, due - scheduled.Clock)), time ?? TimeProvider.System, stop);
                await context.CommandGate.WaitAsync(stop);
                try
                {
                    DungeonAltarReceipt? next;
                    if (scheduled.WaitingDeathSerial is { } serial)
                    {
                        if (!context.Session.CanPublishDungeonAltar(scheduled)) return;
                        if (context.Session.CanFinishDungeonAltarDeath(scheduled)) next = complete?.Invoke();
                        else
                        {
                            next = context.Session.WaitDungeonAltarRevival();
                            if (next?.WaitingDeathSerial != serial) return;
                        }
                    }
                    else next = context.Session.TickDungeonAltar(scheduled);
                    if (next is not { } receipt) return;
                    // 提前唤醒只续等，不重发同阶段的排名或奖励。
                    if (receipt.Returned is not null || receipt.Result is null || receipt.Result.Stage != scheduled.Result?.Stage)
                        await PublishAsync(receipt);
                    scheduled = receipt;
                }
                finally { context.CommandGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || context.Stop.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.Data.Common.DbException)
        {
            log.Write("formal_altar_timer_failed", new { Connection = context.Id, scheduled.InstanceId, Error = error.Message });
        }
    }

    async Task PublishAsync(DungeonAltarReceipt receipt)
    {
        if (receipt.WaitingDeathSerial is not null) return;
        if (receipt.Returned is { } returned)
        {
            if (town is not null) await town.ReturnFromFormalDungeonAsync(context, returned, "formal_altar_timeout_town");
            return;
        }
        bool Guard() => context.Session.CanPublishDungeonAltar(receipt);
        if (receipt.Result is { } result)
        {
            await PublishResultAsync(receipt, result, Guard);
            return;
        }
        if (receipt.DifficultySelected && receipt.State.Choices.Length > 1)
            await context.Notify(NotiPacketType.ULTIMATE_DIFFICULTY_UI, DungeonAltarPackets.SelectedDifficulty(receipt.Difficulty),
                "formal_altar_difficulty", Guard);
        if (receipt.RoundAdvanced && receipt.State.WaitingForDifficulty && receipt.State.Choices.Length > 1)
            await context.Notify(NotiPacketType.ULTIMATE_DIFFICULTY_UI, DungeonAltarPackets.DifficultyCountdown(receipt.DisplayRound, 10),
                "formal_altar_choice", Guard);
        if (receipt.RoundStarted)
        {
            var round = receipt.State.Plan.Rounds.Single(row => row.Round == receipt.Round && row.Difficulty == receipt.Difficulty);
            await context.Notify(NotiPacketType.BLOOD_ROUND_INTERVAL_TIME,
                DungeonAltarPackets.RoundInterval(receipt.DisplayRound, checked((uint)round.Batches[0].DelayMilliseconds)),
                "formal_altar_round", Guard);
            var special = round.Batches.SelectMany(batch => batch.Actors).SingleOrDefault(actor => actor.Kind < 19);
            if (special is not null)
                await context.Notify(NotiPacketType.BLOOD_CRAZY_MONSTER_SPAWN, DungeonAltarPackets.SpecialMonster(special),
                    "formal_altar_special_monster", Guard);
            log.Write("formal_altar_round", new { Connection = context.Id, receipt.InstanceId, receipt.Map.Id, receipt.DisplayRound, receipt.Difficulty });
        }
        if (receipt.Batch is { } batch)
            await context.Notify(NotiPacketType.BLOOD_MONSTER_SPAWN, DungeonAltarPackets.Spawn(batch), "formal_altar_spawn", Guard);
    }

    async Task PublishResultAsync(DungeonAltarReceipt receipt, DungeonAltarResultReceipt result, Func<bool> guard)
    {
        var pending = result.Pending;
        var commit = result.Commit;
        var fact = pending.Fact;
        if (result.Stage == 0)
            await context.Notify(NotiPacketType.BLOOD_DUNGEON_STATE_RANKING,
                DungeonAltarPackets.Ranking(fact.PlayMilliseconds, checked((uint)fact.CompletedRounds), commit.BestMilliseconds,
                    checked((uint)commit.BestRounds), checked((uint)fact.MaximumRounds), checked(commit.GainedExperience + commit.ProficiencyGained)),
                "formal_altar_ranking", guard);
        else if (result.Stage == 1)
        {
            await context.Notify(NotiPacketType.BLOOD_DUNGEON_STATE_REWARD,
                DungeonAltarPackets.ClearRewards(checked((byte)fact.CompletedRounds), checked((byte)fact.MaximumRounds), [pending.Reward.Draws, [], [], []]),
                "formal_altar_reward", guard);
        }
        else if (result.Stage == 2)
        {
            var character = commit.Character;
            var expected = pending.Expected;
            var state = character.DungeonState ?? throw new InvalidOperationException("祭坛结算缺少成长附属状态");
            if (CharacterExperienceNotice.ShouldPublish(expected.Level))
                await context.Notify(NotiPacketType.EXP, GrowthPackets.Experience(character.Level, character.Learning,
                    new(checked((uint)character.Inventory.WinPoints), state.GrowthCapsuleExperience, state.ProficiencyLevel, state.ProficiencyExperience),
                    CharacterExperienceDisplay.Ordinary), "formal_altar_experience", guard);
            if (inventory is not null)
                await inventory.RefreshAsync(context, new(true, 0, commit.Inventory)
                {
                    Receipt = new(character, receipt.Generation, receipt.SelectionEpoch)
                    {
                        Phase = SessionPhase.Settlement, DungeonIdentity = pending.Fact.Identity,
                        PreviousQuests = expected.ActiveMainQuests, PreviousSkillSwitchInventory = expected.SkillSwitchInventory
                    }
                });
            if (levels is not null)
                await levels.RefreshAsync(context, character, expected.Level, "formal_altar_level", guard, previousLearning: expected.Learning);
        }
        log.Write("formal_altar_result", new { Connection = context.Id, receipt.InstanceId, result.Stage, fact.CompletedRounds,
            fact.MaximumRounds, fact.Cleared, commit.Duplicate, commit.GainedExperience, commit.ProficiencyGained });
    }

    public async Task StopAsync()
    {
        if (cancellation is null) return;
        await cancellation.CancelAsync();
        await pending;
        cancellation.Dispose();
        cancellation = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
