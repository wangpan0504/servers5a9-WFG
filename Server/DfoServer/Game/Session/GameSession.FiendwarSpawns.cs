using DfoServer.Game.Dungeons;
using DfoServer.Game.Raids;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 当前连接只保留最近一个合法房间流。断线随Session退出，不另建在线角色或全局流目录。
    sealed record FiendwarSpawnStream(DungeonRunIdentity Identity, uint Serial, FiendwarSpawnReport Last);
    FiendwarSpawnStream? fiendwarSpawnStream;

    internal DungeonRunOutcome ReportFiendwarSpawn(FormalDungeonReceipt receipt, uint serial,
        FiendwarSpawnReport report, PartyDungeonInstance? expectedParty)
    {
        if (!ReferenceEquals(PartyDungeon, expectedParty)) return DungeonRunOutcome.Rejected;
        // 不能先拿本人owner再升级全组；正式分派先持原队伍Commands及全员命令门禁。
        return expectedParty is null ? owner.Execute(() => Register([this])) :
            expectedParty.Execute(members =>
            {
                if (!ReferenceEquals(PartyDungeon, expectedParty) ||
                    !members.Any(member => member.Slot == expectedParty.Leader && ReferenceEquals(member.Actor.Session, this)))
                    return DungeonRunOutcome.Rejected;
                return Register(members.Select(member => member.Actor.Session).ToArray());
            });

        DungeonRunOutcome Register(GameSession[] sessions)
        {
            if (!report.Valid || serial != report.Serial || !report.Matches(receipt) ||
                receipt.Generation != owner.Generation || receipt.SelectionEpoch != SelectionEpoch ||
                receipt.Character.Id != Selected?.Id || formalDungeon is not { } reporter ||
                reporter.Run.LastLoadingFact is not { } loaded || loaded.Identity != receipt.Snapshot.Identity ||
                loaded.Map != receipt.Snapshot.CurrentMap || reporter.Run.Scene.Map != receipt.Snapshot.CurrentMap ||
                reporter.MapSeed != receipt.MapSeed ||
                sessions.Length is < 1 or > 4 || sessions.Any(session => !ReferenceEquals(session.PartyDungeon, expectedParty)))
                return DungeonRunOutcome.Rejected;
            if (fiendwarSpawnStream is { } prior && prior.Identity == receipt.Snapshot.Identity && prior.Serial == serial &&
                (report.Sequence < prior.Last.Sequence || report.Sequence == prior.Last.Sequence && report != prior.Last))
                return DungeonRunOutcome.Rejected;

            // 各成员有独立CharacterId/owner/epoch，不能套用报告人的回执身份。
            var scenes = sessions.Select(session => session.CurrentFormalDungeonScene()).ToArray();
            if (scenes.Any(scene => scene is null) || sessions.Any(session => session.Selected?.Id == report.ObjectKey))
                return DungeonRunOutcome.Rejected;
            var keys = reporter.Run.FiendwarObjectKeys;
            if (keys is null || report.LeaseId == 0 || sessions.Any(session =>
                    !ReferenceEquals(session.formalDungeon?.Run.FiendwarObjectKeys, keys))) return DungeonRunOutcome.Rejected;
            bool replay = reporter.Run.FindCurrentFiendwarDynamic(reporter.Run.Scene.Identity, report.Spawn.ObjectKey, includeRetired: true) == report.Spawn;
            if (!keys.OwnsKey(receipt.Character.Id, report.LeaseId, report.Spawn.ObjectKey) ||
                !replay && !keys.CanUse(receipt.Character.Id, report.LeaseId, report.Spawn.ObjectKey)) return DungeonRunOutcome.Rejected;
            var checks = new DungeonRunOutcome[sessions.Length];
            for (int index = 0; index < sessions.Length; index++)
            {
                var scene = scenes[index]!;
                if (scene.Snapshot.Identity.InstanceId != receipt.Snapshot.Identity.InstanceId ||
                    scene.Snapshot.CurrentMap != receipt.Snapshot.CurrentMap || scene.MapSeed != receipt.MapSeed ||
                    scene.Entry.DungeonId != receipt.Entry.DungeonId ||
                    (checks[index] = sessions[index].CanRegisterFiendwarSpawn(scene, report.Spawn)) == DungeonRunOutcome.Rejected)
                    return DungeonRunOutcome.Rejected;
            }
            // 部分成员已经安装而另一部分没有属于被破坏的共享状态，不能以补安装掩盖它。
            if (checks.Any(check => check != checks[0])) return DungeonRunOutcome.Rejected;
            if (checks[0] == DungeonRunOutcome.Accepted)
            {
                for (int index = 0; index < sessions.Length; index++)
                    sessions[index].InstallFiendwarSpawn(scenes[index]!, report.Spawn);
                keys.Consume(receipt.Character.Id, report.LeaseId, report.Spawn.ObjectKey);
            }
            fiendwarSpawnStream = new(receipt.Snapshot.Identity, serial, report);
            return checks[0];
        }
    }

    DungeonRunOutcome CanRegisterFiendwarSpawn(FormalDungeonReceipt receipt, FiendwarDynamicSpawn spawn) => owner.Execute(() =>
    {
        if (!CanMutatePartyDungeon || !Ready || Phase != SessionPhase.Dungeon || Selected is not { } character ||
            receipt.Generation != owner.Generation || receipt.SelectionEpoch != SelectionEpoch || receipt.Character.Id != character.Id ||
            formalDungeon is not { } dungeon || dungeon.Run.Scene.Identity != receipt.Snapshot.Identity ||
            dungeon.Run.Scene.Map != receipt.Snapshot.CurrentMap || dungeon.MapSeed != receipt.MapSeed ||
            dungeon.HasPendingFatigue || dungeon.Ground.HasPendingOperation || HasPendingFormalCard ||
            !CanMoveWithParty() || !(dungeon.Run.IsEvildomWarChallengeDynamic &&
                ChannelEntry?.Type == 64 && HasCurrentEvildomWarChallengeSeat(CurrentParty?.Invoke()) ||
                raidCombat is { Actor.IsCurrent: true, Battle.Kind: RaidKind.Fiendwar } raid &&
                    raid.Battle.CanRegisterFiendwarSpawn(raid.Actor, dungeon.Run)))
            return DungeonRunOutcome.Rejected;
        var outcome = dungeon.Run.CanRegisterFiendwarDynamic(receipt.Snapshot.Identity, spawn);
        if (outcome == DungeonRunOutcome.Accepted && !dungeon.Ground.HasFreeDynamicKey(spawn.ObjectKey))
            return DungeonRunOutcome.Rejected;
        return outcome;
    });

    void InstallFiendwarSpawn(FormalDungeonReceipt receipt, FiendwarDynamicSpawn spawn) => owner.Execute(() =>
    {
        if (formalDungeon is not { } dungeon || dungeon.Run.Scene.Identity != receipt.Snapshot.Identity ||
            dungeon.Run.Scene.Map != receipt.Snapshot.CurrentMap || receipt.Generation != owner.Generation ||
            receipt.SelectionEpoch != SelectionEpoch || receipt.Character.Id != Selected?.Id)
            throw new InvalidOperationException("漩涡动态召唤已经核验的成员快照发生变化");
        // 两者均已在全员dry-run中通过；不做SQL、不等待网络、不修改怪物奖励集合。
        // Battle计时器可独立推进，安装阶段不二次业务拒绝而留下部分成员已安装。
        dungeon.Ground.ReserveDynamicKey(spawn.ObjectKey);
        if (dungeon.Run.RegisterFiendwarDynamic(receipt.Snapshot.Identity, spawn) != DungeonRunOutcome.Accepted)
            throw new InvalidOperationException("漩涡动态召唤已经核验的对象键发生变化");
        return true;
    });
}
