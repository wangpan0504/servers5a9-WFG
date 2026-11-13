using DfoServer.Game.Raids;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static partial class RaidCombatProjection
{
    internal static bool Movie(GameConnectionContext context, RaidActor actor, AntonRaidBattle battle,
        AntonRaidBattleView view, Guid movieEvent, bool completed)
    {
        var identity = RaidCombatDisplayIdentity.Capture(actor, view);
        bool Guard() => context.Session.CanPublishRaidMovie(actor, battle, view.Phase, movieEvent) && context.RaidDisplay == identity;
        // 最后一名无资格成员确认可同步进入等待；已接受的659仍需ACK，但不能投到新actor或新阶段。
        bool Acknowledge() => actor.IsCurrent && context.Session.CurrentRaidBattle(actor) == battle &&
            battle.View.Phase == view.Phase && context.RaidDisplay == identity;
        var confirmation = context.Session.ConfirmRaidMovie(actor, battle, view, movieEvent, completed,
            () => context.RaidDisplay == identity,
            () => context.Sender.TryPost(1, (ushort)CmdPacketType.RAID_MOVIE_SKIP, [1], "raid_movie_ack", Acknowledge));
        if (confirmation is null) return true;
        view = confirmation.View;
        // 完成事实与ACK已在同一owner/Battle边界接受，广播始终在锁外。
        if (confirmation.Changed) battle.Publish(battle.View);
        if (!confirmation.Acknowledged) return false;
        if (!completed)
        {
            if (battle.HasAntonRewards && view.AntonCardsReady) return true;
            // 01C63430发送0仅请求跳过；599通知原电影控件，不能提前领取或结束阶段。
            return context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_MOVIE_SKIP, [], "raid_movie_skip_prompt",
                () => Guard() && context.RaidMoviePrompt != identity && context.RaidMovieCards != identity,
                () => context.RaidMoviePrompt = identity);
        }
        if (battle.HasAntonRewards) return true;
        // 本人电影结束即可开牌；慢成员与断线成员只影响最终整团收尾。
        if (battle.Kind == RaidKind.Fiendwar) return PostFiendwarMovieCards(context, actor, battle, view);
        if (battle.Kind == RaidKind.Prey)
        {
            return PostPreyResults(context, actor, battle, view);
        }
        if (view.Phase == 0 || battle.LukeEligibility(actor.Character, view.Phase) is { Eligible: false })
            return true;
        return PostLukeMovieCards(context, actor, battle, view, movieEvent);
    }

    static bool PostPreyResults(GameConnectionContext context, RaidActor actor, AntonRaidBattle battle,
        AntonRaidBattleView view)
    {
        if (!battle.PreyMovieCardsReady(actor, view) ||
            battle.LukeEligibility(actor.Character, view.Phase) is not { Eligible: true } ||
            battle.RewardPolicy(actor, view.Phase) is not { Offers.IsEmpty: false } policy ||
            battle.PreyTeamCommittedRewards(actor, view) is not { } team) return true;
        var identity = RaidCombatDisplayIdentity.Capture(actor, view) with { State = AntonRaidBattleState.Reward };
        bool Current()
        {
            var latest = battle.View;
            return context.Session.CanPublishRaidBattle(actor, latest) && context.RaidDisplay == identity &&
                latest.Phase == view.Phase && battle.PreyMovieCardsReady(actor, latest) && context.RaidMovieCards != identity;
        }
        byte Written() => context.PreyResultDisplay is { } display && display.Identity == identity ? display.Mask : (byte)0;
        void Record(byte bit) => context.PreyResultDisplay = (identity, (byte)(Written() | bit));
        // 原生0x10C只揭示结果，不发送660；全部实际个人数据和完整kind3表必须先于开窗。
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_REWARD_LIST,
            PreyRewardPackets.Personal(actor.Character, policy.Offers), "prey_result_personal",
            () => Current() && (Written() & 1) == 0, () => Record(1))) return false;
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_REWARD_LIST, PreyRewardPackets.Team(team),
            "prey_result_team", () => Current() && (Written() & 4) == 0, () => Record(4))) return false;
        // type3的0x10C只在显示阶段1开启；不能为开窗改写引导的真实阶段及原资格。
        byte displayPhase = battle.PreyGuide ? (byte)1 : checked((byte)view.Phase);
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_STATE, [4, displayPhase], "prey_result_open",
            () => Current() && (Written() & 5) == 5, () => context.RaidMovieCards = identity)) return false;
        return true;
    }

    // 引导P1领取后会立即进入真实P2；须在原阶段仍有效时写完已提交结果，不能等新阶段广播补旧牌面。
    internal static async Task PublishPreyGuideResultsAsync(GameConnectionContext context, RaidRewardReceipt receipt)
    {
        if (!context.Session.CanPublishRaidReward(receipt) ||
            context.Session.RaidStatusSnapshot() is not { Battle.Kind: RaidKind.Prey, Battle.PreyGuide: true } snapshot ||
            snapshot.View.State != AntonRaidBattleState.Reward || snapshot.View.Phase != receipt.Prepared.Phase ||
            snapshot.View.Instance != receipt.Prepared.Instance) return;
        if (!PostPreyResults(context, snapshot.Actor, snapshot.Battle, snapshot.View))
            throw new IOException("Prey guide result queue unavailable");
        await context.Sender.DrainAsync();
    }

    // 开窗587实际写出后才迁。调用方须先Drain，使RaidMovieCards已记当前真实阶段。
    // 保存失败留在会话义务里，这里不抛，避免自动领奖把它当成展示失败。可重试的忙锁才启动有界主动重试。
    internal static async Task ReturnCompletedPreyGuideWaitingAsync(GameConnectionContext context, TownProjection town, RaidRewardReceipt receipt)
    {
        if (context.RaidMovieCards is not { State: AntonRaidBattleState.Reward } cards ||
            cards.Phase != receipt.Prepared.Phase || cards.Instance != receipt.Prepared.Instance ||
            cards.Character != receipt.Prepared.Character) return;
        var status = await ApplyPreyGuideWaitingAsync(context, town, null, null);
        if (status == GameSession.PreyGuideWaitingStatus.Retryable &&
            context.Session.CopyPreyGuideWaiting() is { } duty && PreyGuideWindowCovers(context, duty))
        {
            var window = context.RaidMovieCards;
            context.PreyGuideWaiting.Arm(duty, captured => ApplyPreyGuideWaitingAsync(context, town, captured, window));
        }
    }

    internal static void SchedulePreyGuideWaitingRetry(GameConnectionContext context, TownProjection town)
    {
        if (context.Session.CopyPreyGuideWaiting() is not { } duty || !PreyGuideWindowCovers(context, duty)) return;
        var window = context.RaidMovieCards;
        context.PreyGuideWaiting.Schedule(duty, captured => ApplyPreyGuideWaitingAsync(context, town, captured, window));
    }

    static bool PreyGuideWindowCovers(GameConnectionContext context, GameSession.PreyGuideWaitingDuty duty)
    {
        if (context.RaidMovieCards is not { State: AntonRaidBattleState.Reward } cards) return false;
        return cards.Session == context.Session && cards.Owner == context.Owner && cards.SelectionEpoch == duty.Epoch &&
            cards.Character == duty.Character && cards.Instance == duty.Instance && cards.Phase == duty.Phase;
    }

    static bool PreyGuideWindowMatches(GameConnectionContext context, GameSession.PreyGuideWaitingDuty duty, RaidCombatDisplayIdentity window) =>
        context.RaidMovieCards is { } cards && cards.Session == window.Session && cards.Owner == window.Owner &&
        cards.SelectionEpoch == window.SelectionEpoch && cards.Character == window.Character &&
        cards.Instance == window.Instance && cards.Phase == window.Phase && cards.State == window.State &&
        PreyGuideWindowCovers(context, duty);

    static async Task<GameSession.PreyGuideWaitingStatus> ApplyPreyGuideWaitingAsync(GameConnectionContext context, TownProjection town,
        GameSession.PreyGuideWaitingDuty? retryOf, RaidCombatDisplayIdentity? window)
    {
        GameSession.PreyGuideWaitingAttempt attempt;
        // 带了原义务就不能改走无绑定入口，否则会按当前状态新建或接管另一份。
        if (retryOf is { } bound)
            attempt = context.Session.ReturnPreyGuideCompletedWaiting(bound,
                () => window is { } frozen && PreyGuideWindowMatches(context, bound, frozen));
        else
            attempt = context.Session.ReturnPreyGuideCompletedWaiting();
        if (attempt.Status == GameSession.PreyGuideWaitingStatus.Faulted)
            context.PreyGuideWaiting.Report(attempt.Detail ?? "prey guide waiting fault");
        if (attempt.Status != GameSession.PreyGuideWaitingStatus.Moved || attempt.Receipt is not { } moved)
            return attempt.Status;
        await town.TravelAsync(context, moved);
        if (moved.RaidBattleChange is not null)
            context.Session.PublishRaidCombat(moved.RaidBattleChange);
        return GameSession.PreyGuideWaitingStatus.Moved;
    }

    static bool PostLukeMovieCards(GameConnectionContext context, RaidActor actor, AntonRaidBattle battle,
        AntonRaidBattleView view, Guid? acceptedMovieEvent = null)
    {
        if (battle.CaptureLukeMovieCards(actor, view.Phase, acceptedMovieEvent) is not { } snapshot ||
            snapshot.View.Instance != view.Instance || view.Phase != 0 && snapshot.Team.IsDefaultOrEmpty) return true;
        view = snapshot.View;
        var identity = RaidCombatDisplayIdentity.Capture(actor, view);
        bool Cards() => context.Session.CanPublishRaidMovie(actor, battle, view.Phase, snapshot.Event) && context.RaidDisplay == identity &&
            battle.LukeMovieCardsReady(actor, view.Phase, snapshot.Event) && context.RaidMovieCards != identity &&
            (view.Phase == 0 || context.LukeResults.MovieTeamWritten(identity));
        context.LukeResults.PostTeam(context, actor, battle, view, movie: snapshot);
        // 02396DE0 state4→036D2770→01C99AA0显示翻牌组合对象；第二字节写入真实阶段。
        // 这是演出显示推进，Battle仍在Reward，只有实际资产提交确认才能转阶段。
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_STATE, [4, checked((byte)view.Phase)], "raid_movie_cards", Cards)) return false;
        var offers = snapshot.Policy.Offers;
        for (int index = 0; index < offers.Length; index++)
        {
            var offer = offers[index];
            // 尚未选择时不登记本人owner：600有本人行会写控件已选哨兵，阻止660自然发送。
            if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_REWARD_LIST, [offer.Kind, 0], "raid_card_choices", Cards,
                index == offers.Length - 1 ? () => context.RaidMovieCards = identity : null)) return false;
        }
        return true;
    }

    static bool PostFiendwarMovieCards(GameConnectionContext context, RaidActor actor, AntonRaidBattle battle,
        AntonRaidBattleView view)
    {
        if (!battle.FiendwarMovieCardsReady(view, actor) || battle.LukeEligibility(actor.Character, view.Phase) is not { Eligible: true } ||
            battle.RewardPolicy(actor, view.Phase) is not { Offers.IsEmpty: false } policy) return true;
        var identity = RaidCombatDisplayIdentity.Capture(actor, view);
        bool Cards() => context.Session.CanPublishRaidBattle(actor, view) && context.RaidDisplay == identity &&
            battle.FiendwarMovieCardsReady(view, actor) && context.RaidMovieCards != identity;
        var team = battle.FiendwarTeamEntitlements(view);
        if (team.IsDefaultOrEmpty) return true;
        // 原生第二页按分类2枚举完整owner表，须在state4装配子页之前准备。
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_REWARD_LIST,
            PreyRewardPackets.FiendwarTeamResults(team), "fiendwar_team_reward_entitlements", Cards)) return false;
        // 00D30460读取byte2并传给036D2770→01C99AA0；0沿原生普通开牌分支，清014926D0的特殊展示状态。
        // 1523必须保留三字节，不能把587的阶段字节误当此布尔参数。
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.FIEND_WAR_STATE, [4, 0, 0], "fiendwar_movie_cards", Cards)) return false;
        for (int index = 0; index < policy.Offers.Length; index++)
        {
            var offer = policy.Offers[index];
            if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_REWARD_LIST, [offer.Kind, 0], "fiendwar_card_choices", Cards,
                index == policy.Offers.Length - 1 ? () => context.RaidMovieCards = identity : null)) return false;
        }
        return true;
    }
}

// 本连接一份终局回候战执行器。不拥有奖励收据，也不在持有 Battle 锁的通知里等待搬迁。
// 主动重试有次数上限和退避；取消只打断下一次等待，已经进入门禁的那一次仍要做身份复核。
// 次数按不可变义务身份分开记。成功、取消、身份失效和旧任务收尾都不清掉别的义务的预算。
// 同一份已经用尽的义务保持用尽，场景就绪或换角事件不能把它重置。连接上最多记住 16 份，
// 先丢掉未用尽且不是当前槽位的旧身份；只有表满了才会丢掉一份已用尽的旧身份。
internal sealed class PreyGuideWaitingRetry
{
    internal const int MaxAttempts = 5;
    internal const int MaxTrackedDuties = 16;
    static readonly int[] DelaysMs = [25, 50, 100, 200, 200];
    readonly object gate = new();
    readonly Dictionary<GameSession.PreyGuideWaitingDuty, int> spent = new();
    CancellationToken stop;
    CancellationTokenSource? linked;
    Task pending = Task.CompletedTask;
    Func<GameSession.PreyGuideWaitingDuty, Task<GameSession.PreyGuideWaitingStatus>>? work;
    GameSession.PreyGuideWaitingDuty? duty;
    int runId;
    bool running;
    bool again;
    bool armed;

    internal Task Completion { get { lock (gate) return pending; } }
    internal int Attempts { get { lock (gate) return duty is { } current ? Count(current) : 0; } }
    internal bool Exhausted { get { lock (gate) return duty is { } current && Count(current) >= MaxAttempts; } }
    internal int AttemptsFor(GameSession.PreyGuideWaitingDuty? key) { lock (gate) return key is { } found ? Count(found) : 0; }
    internal bool ExhaustedFor(GameSession.PreyGuideWaitingDuty? key) { lock (gate) return key is { } found && Count(found) >= MaxAttempts; }
    // 每次排队尝试在调用会话之前等待。同步的首次保存不走这里。
    internal Func<Task>? BeforeAttempt { get; set; }
    internal Action<string>? Diagnostic { get; set; }

    int Count(GameSession.PreyGuideWaitingDuty key) => spent.TryGetValue(key, out var count) ? count : 0;

    int Spend(GameSession.PreyGuideWaitingDuty key)
    {
        if (!spent.ContainsKey(key))
        {
            if (spent.Count >= MaxTrackedDuties)
            {
                GameSession.PreyGuideWaitingDuty? drop = null;
                foreach (var pair in spent)
                {
                    if (pair.Key == duty || pair.Key == key) continue;
                    if (pair.Value < MaxAttempts) { drop = pair.Key; break; }
                    drop ??= pair.Key;
                }
                if (drop is { } found) spent.Remove(found);
            }
            spent[key] = 0;
        }
        int next = spent[key] + 1;
        spent[key] = next;
        return next;
    }

    internal void Bind(CancellationToken token) => stop = token;

    internal void Report(string detail)
    {
        try { Diagnostic?.Invoke(detail); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    internal void Cancel()
    {
        CancellationTokenSource? source;
        lock (gate)
        {
            armed = false;
            again = false;
            duty = null;
            source = linked;
        }
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal void Arm(GameSession.PreyGuideWaitingDuty captured, Func<GameSession.PreyGuideWaitingDuty, Task<GameSession.PreyGuideWaitingStatus>> attempt)
    {
        lock (gate)
        {
            if (Count(captured) >= MaxAttempts) return;
            if (duty == captured && (running || armed)) return;
            duty = captured;
            work = attempt;
            int seen = Spend(captured);
            if (seen >= MaxAttempts)
            {
                armed = false;
                again = false;
                Report("exhausted attempts=" + seen);
                return;
            }
            armed = true;
            StartLocked();
        }
    }

    internal void Schedule(GameSession.PreyGuideWaitingDuty captured, Func<GameSession.PreyGuideWaitingDuty, Task<GameSession.PreyGuideWaitingStatus>> attempt)
    {
        lock (gate)
        {
            if (Count(captured) >= MaxAttempts) return;
            if (duty == captured && (running || again)) return;
            duty = captured;
            work = attempt;
            again = true;
            StartLocked();
        }
    }

    void StartLocked()
    {
        if (running) return;
        running = true;
        int id = ++runId;
        linked?.Dispose();
        linked = CancellationTokenSource.CreateLinkedTokenSource(stop);
        pending = Drain(id);
    }

    async Task Drain(int id)
    {
        await Task.Yield();
        GameSession.PreyGuideWaitingDuty? runningSnapshot = null;
        try
        {
            while (true)
            {
                GameSession.PreyGuideWaitingDuty snapshot;
                Func<GameSession.PreyGuideWaitingDuty, Task<GameSession.PreyGuideWaitingStatus>> attempt;
                bool immediate;
                int spentNow;
                CancellationToken token;
                lock (gate)
                {
                    if (runId != id) return;
                    token = linked?.Token ?? CancellationToken.None;
                    if (work is null || duty is not { } current) { FinishLocked(id, null, GameSession.PreyGuideWaitingStatus.None); return; }
                    snapshot = current;
                    runningSnapshot = snapshot;
                    attempt = work;
                    immediate = again;
                    again = false;
                    spentNow = Count(snapshot);
                    if (!immediate && !armed) { FinishLocked(id, snapshot, GameSession.PreyGuideWaitingStatus.None); return; }
                    if (!immediate && token.IsCancellationRequested) { FinishLocked(id, snapshot, GameSession.PreyGuideWaitingStatus.None); return; }
                }
                if (!immediate)
                {
                    // 同步的那一次已经计入这份义务的次数，退避从 25ms 起，对应 25/50/100/200。
                    try { await Task.Delay(DelaysMs[Math.Clamp(spentNow - 1, 0, DelaysMs.Length - 1)], token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { lock (gate) FinishLocked(id, snapshot, GameSession.PreyGuideWaitingStatus.None); return; }
                }
                var wait = BeforeAttempt?.Invoke();
                if (wait is not null) await wait.ConfigureAwait(false);
                var status = await attempt(snapshot).ConfigureAwait(false);
                if (status != GameSession.PreyGuideWaitingStatus.Retryable)
                {
                    lock (gate) FinishLocked(id, snapshot, status);
                    return;
                }
                bool stopLoop;
                bool ranOut;
                int seen;
                lock (gate)
                {
                    if (runId != id) return;
                    seen = Spend(snapshot);
                    ranOut = seen >= MaxAttempts;
                    stopLoop = ranOut || duty != snapshot;
                    if (stopLoop) FinishLocked(id, snapshot, status);
                }
                if (!stopLoop) continue;
                if (ranOut && seen > 0) Report("exhausted attempts=" + seen);
                return;
            }
        }
        catch (Exception error)
        {
            Report(error.GetType().Name + ": " + error.Message);
            lock (gate) FinishLocked(id, runningSnapshot, GameSession.PreyGuideWaitingStatus.Faulted);
        }
    }

    void FinishLocked(int id, GameSession.PreyGuideWaitingDuty? snapshot, GameSession.PreyGuideWaitingStatus status)
    {
        if (runId != id) return;
        running = false;
        // 槽位已经换成另一份义务时，只收掉这次快照，不能清新义务，也不能动它的次数。
        if (snapshot is not null && duty is not null && duty != snapshot)
        {
            StartLocked();
            return;
        }
        armed = false;
        again = false;
        if (snapshot is not null && duty == snapshot &&
            status is GameSession.PreyGuideWaitingStatus.Moved or GameSession.PreyGuideWaitingStatus.None
                or GameSession.PreyGuideWaitingStatus.Faulted)
            duty = null;
    }
}
