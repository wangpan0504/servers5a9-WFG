using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public Character ConsumeOperationHopeAreaFatigue(AccountOwners.Lease owner, Character expected, SinglePlayerDungeonRun run, TimeProvider? battlePassClock = null) => owner.Execute(() =>
    {
        var snapshot = run.Snapshot;
        var identity = snapshot.Identity;
        var state = expected.Fatigue ?? throw new InvalidOperationException("普通副本疲劳owner未初始化");
        state.Validate();
        var catalog = run.HopeCatalog;
        int area = run.HopeAreaId ?? throw new InvalidDataException("希望加载地图不属于声明区域");
        bool ordinary = catalog is not null && run.Entry.Hope is not null;
        string rule = "operation-hope-first-area-v1";
        ushort amount = checked((ushort)(catalog?.FatigueValue ?? 0));
        if (!ordinary || run.Entry.Maze.QuestConnectionTokens.Count != 0 ||
            run.Entry.Maze.MapSpecification?.Contains(snapshot.CurrentMap) != true && !run.Entry.Maze.DummyStages.Contains(snapshot.CurrentMap) ||
            snapshot.Phase != SinglePlayerDungeonPhase.Loading || identity.CharacterId != expected.Id ||
            identity.InstanceId == Guid.Empty || identity.TaskAttempt != expected.DungeonState?.LastAttempt)
            throw new InvalidDataException("疲劳扣点需要本服已准入实例的真实首次房间加载");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.ConsumeOperationHopeAreaFatigue", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.DungeonState != expected.DungeonState)
            throw new InvalidOperationException("扣点实例或角色已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        var receipts = DungeonTransientReceipts.Get(owner, identity);
        if (receipts.HopeAreaFatigue.TryGetValue(area, out var saved) && saved.Committed)
        {
            if (saved.Rule != rule || saved.After != saved.Before.ConsumeAtEntry(amount))
                throw new InvalidDataException("房间扣点缓存的原始规则或余额变化");
            if (current.Revision == expected.Revision && current.Fatigue == state) return expected;
            if (expected.Revision != saved.BeforeRevision || current.Revision != saved.Revision ||
                saved.Before != state || current.Fatigue != saved.After)
                throw new InvalidOperationException("已提交扣点的原快照精确恢复失效");
            return current;
        }
        if (current.Revision != expected.Revision || current.Fatigue != state)
            throw new InvalidOperationException("扣点的原revision或疲劳owner已变化");
        var target = state.ConsumeAtEntry(amount);
        command.Parameters.AddWithValue("$revision", expected.Revision);
        command.CommandText = "UPDATE characters SET revision=revision+1 WHERE id=$id AND account=$account AND revision=$revision";
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("首次房间扣点revision冲突");
        command.Parameters.AddWithValue("$used", target.Used);
        command.Parameters.AddWithValue("$remaining", target.Remaining);
        command.CommandText = "UPDATE character_fatigue SET used=$used,remaining=$remaining WHERE character_id=$id AND account=$account";
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("首次房间扣点缺少原疲劳owner");
        ApplyBattlePassDungeonActivity(db,tx,owner.Account,DfoServer.Game.BattlePass.BattlePassDungeonActivity.PrepareFatigue(
            identity,"hope_area",area,state,target,battlePassClock ?? TimeProvider.System));
        var committed = Require(db, tx, owner.Account, expected.Name);
        var cached = new DungeonTransientFatigue(rule, expected.Revision, state, target);
        receipts.HopeAreaFatigue[area] = cached;
        tx.Commit();
        cached.Committed = true;
        return committed;
    });
}
