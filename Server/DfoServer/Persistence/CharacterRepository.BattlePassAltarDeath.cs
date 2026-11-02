using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IBattlePassAltarDeathStore
{
    public bool CommitBattlePassAltarDeath(AccountOwners.Lease owner, Character expected,
        SinglePlayerDungeonRun run, PreparedDungeonDeath prepared) => owner.Execute(() =>
    {
        if (battlePassMissionPolicy is null) return false;
        if (quests is null || prepared.BattlePassActivity is not {} fact ||
            !fact.MatchesAltarNonTarget(run,prepared,expected,quests))
            throw new InvalidDataException("祭坛独立战令提交需要原已发布非目标死亡、角色与接取目录的冻结事实");
        using var db=database.Open();
        using var write=db.BeginWrite("CharacterRepository.CommitBattlePassAltarDeath",deferred:true);
        using var tx=write.Transaction;
        var current=Require(db,tx,owner.Account,expected.Name);
        if (current.Id!=expected.Id || current.Revision!=expected.Revision ||
            !current.ActiveMainQuests.SequenceEqual(expected.ActiveMainQuests))
            throw new InvalidOperationException("祭坛独立战令提交的角色revision或原接取已变化");
        using var command=db.CreateCommand();command.Transaction=tx;
        command.CommandText="SELECT season_key FROM account_battle_pass_source_events WHERE account=$account AND source_kind=$kind AND source_id=$source";
        command.Parameters.AddWithValue("$account",owner.Account);command.Parameters.AddWithValue("$kind",fact.SourceKind);
        command.Parameters.AddWithValue("$source",fact.SourceId);
        if (command.ExecuteScalar() is string season)
        {
            if (battlePassMissionPolicy is null || season!=battlePassMissionPolicy.SeasonKey)
                throw new InvalidDataException("原祭坛独立死亡来源的冻结赛季不一致");
            tx.Commit();
            return true;
        }
        // 仅原来源、任务进度与季版本同事务提交；不写角色、原任务或普通经验回执。
        ApplyBattlePassDungeonDeath(db,tx,owner.Account,run,prepared);
        bool recorded=command.ExecuteScalar() is string;
        tx.Commit();
        return recorded;
    });
}
