using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Party;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    AntonRaidDisconnectFact? preparedAntonDisconnect;

    internal RaidActor? CaptureRaidRejoinActor(RaidRegistry registry) => owner.Execute(() =>
        raidCombat is { Actor.IsCurrent: true } current ? current.Actor : CaptureRaidActor(registry));

    // 清理共享Run/普通分队之前保存原身份；不保存Selected或另一份角色资产。
    internal void PrepareAntonRejoinDisconnect()
    {
        if (preparedAntonDisconnect is not null || raidRegistry is not { } registry || raidCombat is not { Battle.Kind: RaidKind.Anton } combat) return;
        try
        {
            owner.Execute(() =>
            {
                var fact = registry.CaptureAntonDisconnect(combat.Actor, combat.Battle, PartyDungeon?.Id, Phase == SessionPhase.Loading);
                if (fact is null || CurrentParty?.Invoke() is not { } party ||
                    party.Members.SingleOrDefault(m => ReferenceEquals(m.Actor.Session, this)) is not { } own ||
                    AntonRaidBattle.AntonMemberReservation.Capture(combat.Battle, combat.Actor, fact.Run) is not { } member ||
                    formalDungeon is { } dungeon && (ScriptedNavigationBlocked(dungeon) || dungeon.PendingDeathReward is not null ||
                        dungeon.PendingQuestDrop is not null || dungeon.PendingInventoryDiscard is not null)) return false;
                var state = new AntonRejoinState(this);
                preparedAntonDisconnect = fact with { Anton = new(combat.Actor, combat.Battle, member, state, party.Id,
                    party.Commands, own.Slot, party.Leader,
                    party.Members.Select(m => (m.Actor.Character.Id, m.Slot)).ToImmutableArray(), PartyDungeon) };
                return true;
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { }
    }

    // 原上下文在旧Session清理后由候选唯一持有；只有原Run仍存活且没有错过战斗事实时才能迁移。
    internal sealed class AntonRejoinState
    {
        readonly RaidCombatBinding combat;
        readonly FormalDungeon? dungeon;
        readonly RaidDungeonAdmission? admission;
        readonly SessionPhase phase;
        readonly Location position;
        readonly Location? returnPoint;
        internal AntonRejoinState(GameSession original)
        {
            combat = original.raidCombat!; dungeon = original.formalDungeon; admission = original.raidDungeonAdmission;
            phase = original.Phase; position = original.Selected!.Position; returnPoint = original.Selected.Return;
        }

        internal void CommitPosition(GameSession target)
        {
            // 原位置是断线时已验证的服务端事实。仅保存新owner当前角色的位置，不回写旧资产。
            // SQL失败不安装成员或消费候选；目录/分队/Battle锁均不跨SQL。
            var character = target.Selected!;
            if (character.Position != position || character.Return != returnPoint)
                target.Selected = target.SaveTownPosition(character, position, returnPoint)
                    ?? throw new InvalidOperationException("原攻坚候战室位置无法保存");
        }

        internal bool CanInstall(GameSession target, PartySnapshot party, PartyDungeonInstance? run,
            AntonRaidBattle.AntonMemberReservation reservation)
        {
            if (!target.CanChangeTownParty || target.raidCombat is not null || target.formalDungeon is not null || target.PartyDungeon is not null ||
                target.raidRegistry is null || target.CurrentParty?.Invoke() is not null) return false;
            if (dungeon is null) return run is null && phase == SessionPhase.Town;
            if (run is null || run.Id != dungeon.Run.Snapshot.Identity.InstanceId || run.Members.Length == 0) return false;
            var snapshot = dungeon.Run.Snapshot;
            return party.Members.All(m => ReferenceEquals(m.Actor.Session.PartyDungeon, run) &&
                m.Actor.Session.DungeonSnapshot is { } current && current.Identity.InstanceId == run.Id &&
                current.CurrentMap == snapshot.CurrentMap && (current.Phase == snapshot.Phase ||
                    snapshot.Phase == SinglePlayerDungeonPhase.Loading && current.Phase == SinglePlayerDungeonPhase.Dungeon) &&
                reservation.MatchesRunProgress(m.Actor.Character.Id, current.Identity.Epoch, snapshot.Identity.Epoch,
                    snapshot.Phase == SinglePlayerDungeonPhase.Loading && current.Phase == SinglePlayerDungeonPhase.Dungeon) &&
                current.LivingMonsters.SequenceEqual(snapshot.LivingMonsters)) && run.CanRestoreAntonMember();
        }

        internal bool Install(GameSession target, RaidActor actor, PartySnapshot restored, PartyDungeonInstance? run)
        {
            // 调用方已持本次新owner、原分队和原Battle；不复制旧Selected，不修改数据库或票据。
            if (!target.MatchesRaidCombatActor(actor) || !target.CanChangeTownParty || target.raidCombat is not null) return false;
            bool InstallContext()
            {
                var binding = new RaidCombatBinding(actor, combat.Battle) { RewardWeek = combat.RewardWeek };
                foreach (var row in combat.Pending) binding.Pending.Add(row.Key, row.Value);
                foreach (var row in combat.Receipts)
                    binding.Receipts.Add(row.Key, row.Value with { Generation = actor.Owner.Generation, Epoch = actor.Epoch,
                        Inventory = new(true, 0) });
                target.raidCombat = binding;
                if (dungeon is not null)
                {
                    target.raidDungeonAdmission = admission is null ? null : admission with
                    {
                        Actor = actor, IsCurrent = () => actor.IsCurrent && target.StartedRaid?.InstanceId == combat.Battle.Instance
                    };
                    target.formalDungeon = dungeon;
                    target.PartyDungeon = run;
                    target.partyDungeonRun = run!.Id;
                    // 恢复已有Run的合法阶段，不调用会清理Run的普通城镇切换，也不伪造加载ACK。
                    _ = UserStatePolicy.Read(phase, true, true, false, dungeon.Run.Phase, false);
                    target.Phase = phase;
                }
                return true;
            }
            return run is null ? InstallContext() : run.RestoreAntonMember(restored.Members.Single(m => m.Actor.Session == target), InstallContext);
        }
    }
}
