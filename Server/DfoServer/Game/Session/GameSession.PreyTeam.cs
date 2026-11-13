using DfoServer.Game.Characters;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 原生46只收尾本人的当前已清图Run；资格仍由正式死亡事实与Battle确认，不由评分请求签发。
    internal Guid? ClearedPreyTeamResultRun() => owner.Execute<Guid?>(() =>
    {
        if (!Ready || raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid ||
            formalDungeon is not { Entry.RaidEntry.Kind: RaidKind.Prey, Run.ClearFact: { } fact } dungeon ||
            dungeon.Entry.RaidEntry.InstanceId != raid.Battle.Instance ||
            dungeon.Run.Snapshot.Phase != Dungeons.SinglePlayerDungeonPhase.Settlement ||
            fact.Identity != dungeon.Run.Snapshot.Identity ||
            !raid.Battle.HasDungeonContribution(raid.Actor, fact.Identity.InstanceId)) return null;
        return fact.Identity.InstanceId;
    });

    // 失败保留原攻坚与标记，只将已经退出原Run/选图的本人送回资源声明的备战区。
    internal Town.TownTravelReceipt? ReturnFailedPreyWaiting(RaidActor actor, Guid instance) => owner.Execute<Town.TownTravelReceipt?>(() =>
    {
        if (!actor.IsCurrent || !ReferenceEquals(actor.Session, this) || !ReferenceEquals(actor.Owner, owner) ||
            !CanChangeTownParty || formalDungeon is not null || PartyDungeon is not null || ChannelEntry?.Type != 50 ||
            raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid ||
            raid.Actor != actor || raid.Battle.Instance != instance || !raid.Battle.View.PhaseExpired ||
            raidRegistry?.Find(actor) is not { Type: 3, Guide: false, Started: { } started } || started.InstanceId != instance ||
            raid.Battle.LukeWaitingRoom is not { } room || Selected is not { } character) return null;
        var target = towns.Spawn(new(checked((byte)room.Town), checked((byte)room.Area), 0, 0), completed);
        if (target is null) return null;
        var saved = character.Position.Town == room.Town && character.Position.Area == room.Area
            ? character : SaveTownPosition(character, target, character.Return);
        if (saved is null) return null;
        Selected = saved;
        return new Town.TownTravelReceipt(saved, owner.Generation, SelectionEpoch) { RaidBattleChange = RecordPreyTownLocation() };
    });

    // 同一 owner 代数、选角、角色和实例只留一份终局回候战义务。奖励收据不在这里登记。
    // 59/2 是攻坚实例区，不能当选角快照；断线、换角、离开普雷频道或会话结束只放弃这份内存义务。
    internal sealed record PreyGuideWaitingDuty(long Generation, long Epoch, ushort Character, Guid Instance, int Phase, int Town, int Area);
    internal enum PreyGuideWaitingStatus { None, Moved, Retryable, Faulted }
    internal readonly record struct PreyGuideWaitingAttempt(PreyGuideWaitingStatus Status, Town.TownTravelReceipt? Receipt, string? Detail)
    {
        internal static PreyGuideWaitingAttempt None() => new(PreyGuideWaitingStatus.None, null, null);
        internal static PreyGuideWaitingAttempt Moved(Town.TownTravelReceipt receipt) => new(PreyGuideWaitingStatus.Moved, receipt, null);
        internal static PreyGuideWaitingAttempt Retryable(string detail) => new(PreyGuideWaitingStatus.Retryable, null, detail);
        internal static PreyGuideWaitingAttempt Faulted(string detail) => new(PreyGuideWaitingStatus.Faulted, null, detail);
    }
    PreyGuideWaitingDuty? preyGuideWaiting;
    // 生产路径为空。重读先用 0 毫秒预算探路，探到忙就抛 SQLITE_BUSY，不再进入会按 30 秒重试的 LoadCharacter。
    // 测试用来让 revision 冲突之后的重读单独失败；外部写锁会先让保存变成忙锁，到不了这一步。
    internal Func<ushort, DfoServer.Game.Characters.Character?>? PreyGuideCharacterReload;
    // 只取消连接上的重试等待。在 owner 锁内调用，不能再进入 owner，也不能等待任务。
    internal Action? PreyGuideWaitingReleased { get; set; }
    internal bool PreyGuideWaitingPending => owner.Execute(() => preyGuideWaiting is not null);
    internal PreyGuideWaitingDuty? CopyPreyGuideWaiting() => owner.Execute(() => preyGuideWaiting);
    void AbandonPreyGuideWaiting()
    {
        preyGuideWaiting = null;
        try { PreyGuideWaitingReleased?.Invoke(); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    bool OwnsPreyGuideWaiting(PreyGuideWaitingDuty duty) =>
        owner.IsCurrent && duty.Generation == owner.Generation && duty.Epoch == SelectionEpoch &&
        Selected?.Id == duty.Character && ChannelEntry?.Type == 50 &&
        raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: true } raid &&
        raid.Battle.Instance == duty.Instance;

    bool SamePreyGuideWaiting(PreyGuideWaitingDuty duty)
    {
        if (!OwnsPreyGuideWaiting(duty) || Phase != SessionPhase.Town || !CanChangeTownParty ||
            formalDungeon is not null || PartyDungeon is not null || raidCombat is not { } raid) return false;
        var view = raid.Battle.View;
        if (view.Phase != duty.Phase || view.PhaseExpired || view.ClockFaulted ||
            view.State is not (AntonRaidBattleState.Reward or AntonRaidBattleState.Completed) ||
            view.Phase + 1 != raid.Battle.PhaseCount || raid.Battle.PreyGuideWaitingRoom is not { } room) return false;
        return room.Town == duty.Town && room.Area == duty.Area;
    }

    // 只在引导最终阶段、翻牌窗已经写出之后调用。一阶段仍停鸟背，退场24也不改成候战区。
    // retryOf 为空才允许按当前状态建立义务。排队任务只重试捕获到的那一份，对不上就返回，不新建、不接管。
    // 忙锁留下义务并标成可重试；其它数据库错误留下义务并标成故障，不伪装成已经搬迁。
    // 保存和冲突重读都在这次 owner 临界区里，但数据库等待预算是 0：写闸门和 SQLite 忙锁都不等。
    // 身份、revision 和 COMMIT 仍在同一临界区，成功之后才改 Selected、才返回可以发布的回执。
    internal PreyGuideWaitingAttempt ReturnPreyGuideCompletedWaiting(PreyGuideWaitingDuty? retryOf = null, Func<bool>? windowStill = null) =>
        owner.Execute(() =>
        {
            if (retryOf is { } expected)
            {
                if (preyGuideWaiting != expected) return PreyGuideWaitingAttempt.None();
                if (!SamePreyGuideWaiting(expected) || windowStill?.Invoke() == false)
                {
                    if (!SamePreyGuideWaiting(expected)) preyGuideWaiting = null;
                    return PreyGuideWaitingAttempt.None();
                }
            }
            else if (preyGuideWaiting is { } armed && !OwnsPreyGuideWaiting(armed))
                preyGuideWaiting = null;
            if (Phase != SessionPhase.Town || !CanChangeTownParty || formalDungeon is not null || PartyDungeon is not null ||
                ChannelEntry?.Type != 50 ||
                raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: true } raid ||
                raid.Battle.View is not { PhaseExpired: false, ClockFaulted: false } view ||
                view.State is not (AntonRaidBattleState.Reward or AntonRaidBattleState.Completed) ||
                view.Phase + 1 != raid.Battle.PhaseCount || raid.Battle.PreyGuideWaitingRoom is not { } room ||
                Selected is not { } character) return PreyGuideWaitingAttempt.None();
            if (character.Position.Town == room.Town && character.Position.Area == room.Area)
            {
                preyGuideWaiting = null;
                return PreyGuideWaitingAttempt.None();
            }
            if (retryOf is null)
                preyGuideWaiting = new(owner.Generation, SelectionEpoch, character.Id, raid.Battle.Instance, view.Phase, room.Town, room.Area);
            var target = towns.Spawn(new(checked((byte)room.Town), checked((byte)room.Area), 0, 0), completed);
            if (target is null) return PreyGuideWaitingAttempt.Faulted("waiting room spawn missing");
            return SavePreyGuideWaiting(character, target);
        });

    PreyGuideWaitingAttempt SavePreyGuideWaiting(DfoServer.Game.Characters.Character character, DfoServer.GameWorld.Towns.Location target)
    {
        try { return CommitPreyGuideWaiting(character, target); }
        catch (System.Data.Common.DbException error) { return ClassifyPreyGuideWaiting(error, "save"); }
        catch (InvalidOperationException error) when (error.Message == "Character revision or owner changed")
        {
            DfoServer.Game.Characters.Character latest;
            try { latest = ReloadPreyGuideCharacter(character.Slot); }
            catch (System.Data.Common.DbException reload) { return ClassifyPreyGuideWaiting(reload, "reload"); }
            catch (Exception reload) { return PreyGuideWaitingAttempt.Faulted("reload " + reload.GetType().Name + " " + reload.Message); }
            if (latest.Id != character.Id) return PreyGuideWaitingAttempt.Faulted("reload character mismatch");
            if (latest.Position.Town == target.Town && latest.Position.Area == target.Area)
            {
                Selected = latest;
                preyGuideWaiting = null;
                return PreyGuideWaitingAttempt.None();
            }
            try { return CommitPreyGuideWaiting(latest, target); }
            catch (System.Data.Common.DbException again) { return ClassifyPreyGuideWaiting(again, "resave"); }
            catch (InvalidOperationException again) when (again.Message == "Character revision or owner changed")
            {
                return PreyGuideWaitingAttempt.Faulted("revision conflict after reload");
            }
        }
    }

    PreyGuideWaitingAttempt CommitPreyGuideWaiting(DfoServer.Game.Characters.Character character, DfoServer.GameWorld.Towns.Location target)
    {
        var saved = SavePreyGuideTown(character, target);
        if (saved is null) return PreyGuideWaitingAttempt.Faulted("town save declined");
        Selected = saved;
        preyGuideWaiting = null;
        return PreyGuideWaitingAttempt.Moved(new Town.TownTravelReceipt(saved, owner.Generation, SelectionEpoch)
            { RaidBattleChange = RecordPreyTownLocation() });
    }

    // 候战区不是公会大厅。立即保存失败会抛出，由外层分成可重试忙锁或故障。
    DfoServer.Game.Characters.Character? SavePreyGuideTown(DfoServer.Game.Characters.Character character, DfoServer.GameWorld.Towns.Location target) =>
        towns.IsGuildHall(target, completed) || repository is not DfoServer.Game.Characters.IImmediateTownSave immediate
            ? SaveTownPosition(character, target, character.Return)
            : immediate.SaveTownImmediate(owner, character, target, character.Return);

    DfoServer.Game.Characters.Character ReloadPreyGuideCharacter(ushort slot)
    {
        if (PreyGuideCharacterReload is { } hook)
            return hook(slot) ?? throw new InvalidOperationException("prey guide reload missing");
        return repository is DfoServer.Game.Characters.IImmediateTownSave immediate
            ? immediate.LoadCharacterImmediate(owner, slot)
            : repository.LoadCharacter(owner, slot);
    }

    // SQLITE_BUSY=5、SQLITE_LOCKED=6 才可重试。其它 DbException 留义务并标故障，避免无差别吞掉。
    static PreyGuideWaitingAttempt ClassifyPreyGuideWaiting(System.Data.Common.DbException error, string stage)
    {
        int? code = null;
        var value = error.GetType().GetProperty("SqliteErrorCode")?.GetValue(error);
        if (value is not null) code = Convert.ToInt32(value);
        string detail = stage + " sqlite=" + (code?.ToString() ?? "none") + " " + error.GetType().Name + " " + error.Message;
        return code is 5 or 6 ? PreyGuideWaitingAttempt.Retryable(detail) : PreyGuideWaitingAttempt.Faulted(detail);
    }

    // 离团先提交公共落点；调用方随后才解除原攻坚与队伍绑定，SQL失败仍可重试原请求。
    internal Town.TownTravelReceipt? ReturnPreyLobby(RaidActor actor, RaidParty expected) => owner.Execute<Town.TownTravelReceipt?>(() =>
    {
        if (!actor.IsCurrent || !ReferenceEquals(actor.Session, this) || !ReferenceEquals(actor.Owner, owner) ||
            !CanChangeTownParty || ChannelEntry?.Type != 50 || expected is not { Type: 3 } ||
            raidRegistry?.Find(actor) is not { Type: 3 } current || current.Key != expected.Key ||
            current.Guide != expected.Guide ||
            current.Started?.InstanceId != expected.Started?.InstanceId ||
            Selected is not { Position.Town: 59, Position.Area: >= 2 and <= 5 } character) return null;
        if (expected.Started is { } started && (raidCombat is not
            { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey } raid ||
            raid.Battle.PreyGuide != expected.Guide ||
            raid.Battle.Instance != started.InstanceId)) return null;
        var target = ChannelAreaLanding.RoomExit(towns, ChannelEntry, towns.Room, character.Level, completed);
        if (target is null) return null;
        var saved = SaveTownPosition(character, target, character.Return);
        if (saved is null) return null;
        Selected = saved;
        RecordPreyBoardingArrival();
        return new(saved, owner.Generation, SelectionEpoch);
    });

    internal AntonRaidBattleChange? SetPreyMarker(byte group, int target) => owner.Execute(() =>
    {
        if (!Ready || ChannelEntry?.Type != 50 || raidCombat is not { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey } raid ||
            raid.Battle.PreyGuide || raidRegistry?.FindCurrent(this, SelectionEpoch) is not { Type: 3, Guide: false, Leader: var leader } party ||
            Selected?.Id != leader || party.Started?.InstanceId != raid.Battle.Instance) return null;
        return raid.Battle.SetPreyMarker(raid.Actor, group, target);
    });
    bool HasPreyTeamTownAccess => ChannelEntry?.Type == 50 &&
        raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey } raid &&
        raid.Battle.View is { PhaseExpired: false, ClockFaulted: false } view &&
        view.Symbols.GetValueOrDefault("[prepare raid]") == 1 &&
        raidRegistry?.FindCurrent(this, SelectionEpoch) is { Type: 3, Started: { } started } party &&
        party.Guide == raid.Battle.PreyGuide &&
        started.InstanceId == raid.Battle.Instance;

    // 1614只设置本人当前攻坚的报告阈值，不构造伤害、死亡或通关事实。
    internal bool SetPreyBossHpAlarm(int percent) => owner.Execute(() =>
    {
        if (percent is < -1 or > 100 || !Ready || raidCombat is not
            { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid ||
            raid.Battle.View is not { State: AntonRaidBattleState.Fighting, PhaseExpired: false, ClockFaulted: false }) return false;
        var view = raid.Battle.View;
        return view.Symbols.ContainsKey("[hp alarm]") &&
            raid.Battle.SetMechanism(view.Instance, view.Revision, "[hp alarm]", percent) is not null;
    });

    internal (RaidActor Actor, AntonRaidBattle Battle, AntonRaidBattleChange Change)? ReportPreyBossHp(int percent) =>
        owner.Execute<(RaidActor, AntonRaidBattle, AntonRaidBattleChange)?>(() =>
        {
            if (percent is < 0 or > 100 || !Ready || Phase != SessionPhase.Dungeon || raidCombat is not
                { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid ||
                formalDungeon is not { Entry.RaidEntry.Kind: RaidKind.Prey } dungeon ||
                dungeon.Entry.RaidEntry.InstanceId != raid.Battle.Instance || !CanMoveWithParty()) return null;
            var change = raid.Battle.ReportPreyTeamBossPercent(raid.Actor, dungeon.Run.Snapshot, percent);
            return change is null ? null : (raid.Actor, raid.Battle, change);
        });

    // 原本体每两秒上报当前HP/最大HP的一万分比，原Run仍负责最终死亡与清图。
    internal (RaidActor Actor, AntonRaidBattle Battle, AntonRaidBattleChange Change)? ReportPreyBossHpUnits(int value) =>
        owner.Execute<(RaidActor, AntonRaidBattle, AntonRaidBattleChange)?>(() =>
        {
            if (value is < 0 or > 10000 || !Ready || Phase != SessionPhase.Dungeon || raidCombat is not
                { Actor.IsCurrent: true, Battle.Kind: RaidKind.Prey, Battle.PreyGuide: false } raid ||
                formalDungeon is not { Entry.RaidEntry.Kind: RaidKind.Prey } dungeon ||
                dungeon.Entry.RaidEntry.InstanceId != raid.Battle.Instance || !CanMoveWithParty()) return null;
            var change = raid.Battle.ReportPreyTeamBossValue(raid.Actor, dungeon.Run.Snapshot, value);
            return change is null ? null : (raid.Actor, raid.Battle, change);
        });
}
