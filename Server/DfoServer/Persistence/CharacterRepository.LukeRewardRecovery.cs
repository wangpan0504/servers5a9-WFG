using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : ILukeRewardRecoveryStore
{
    public LukeRewardRecoverySnapshot ReadLukeRewardRecovery(AccountOwners.Lease owner, Character character,
        LukeRewardRecoveryTarget target) => owner.Execute(() =>
    {
        target.Validate();
        if (target.Character != character.Id) throw new InvalidDataException("目标角色ID与当前账号角色不一致");
        using var db = database.Open(); using var tx = db.BeginRead();
        var current = Require(db, tx, owner.Account, character.Name);
        if (current.Id != character.Id || current.Revision != character.Revision)
            throw new InvalidDataException("目标角色版本已变化");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", character.Id); command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$instance", target.Instance.ToString("N"));
        command.Parameters.AddWithValue("$phase", target.Phase); command.Parameters.AddWithValue("$kind", target.Kind);
        string prefix = target.Phase == 0 ? "phase0" : "phase1";
        // 精确一行、单只读快照；不扫描历史、不建默认行、不写state或资格。
        command.CommandText = $"SELECT {prefix}_receipt,{prefix}_event,{prefix}_anchor FROM character_raid_runs WHERE character_id=$id AND account=$account AND instance_id=$instance AND raid_type=1 AND length({prefix}_receipt)<=262144";
        RaidPhaseEligibility eligibility;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read() || row.IsDBNull(0)) throw new InvalidDataException("没有该明确目标的已提交阶段资格");
            eligibility = Decode<RaidPhaseEligibility>(row.GetString(0));
            if (!eligibility.Eligible || eligibility.Instance != target.Instance || eligibility.Character != character.Id ||
                eligibility.RaidType != RaidKind.Luke || eligibility.Phase != target.Phase || eligibility.Event == Guid.Empty ||
                row.IsDBNull(1) || eligibility.Event.ToString("N") != row.GetString(1) || row.IsDBNull(2) ||
                eligibility.Anchor != row.GetInt64(2) || string.IsNullOrWhiteSpace(eligibility.Source) ||
                eligibility.Offers.IsDefaultOrEmpty || eligibility.Offers.Length > 32 || eligibility.Offers.Any(x => x is null) ||
                eligibility.Offers.Select(x => (x.Kind, x.Card)).Distinct().Count() != eligibility.Offers.Length)
                throw new InvalidDataException("阶段资格身份或牌面损坏");
        }
        var offer = eligibility.Offers.SingleOrDefault(x => x.Kind == target.Kind && x.Card == target.Card)
            ?? throw new InvalidDataException("原资格没有该牌面");
        if (offer.Grants.IsDefaultOrEmpty || offer.Grants.Length > 32 || offer.Grants.Any(x => x is null))
            throw new InvalidDataException("原奖励计划损坏");
        var prepared = new PreparedRaidReward(target.Instance, target.Phase, target.Kind, target.Card, character.Id,
            eligibility.Source, offer.Grants, target.Phase == 1, eligibility.Week) { RaidType = RaidKind.Luke };
        command.CommandText = "SELECT plan_json,mail_ids,account,raid_type FROM character_raid_reward_receipts WHERE character_id=$id AND instance_id=$instance AND phase=$phase AND kind=$kind";
        bool claimed = false; ImmutableArray<uint> mails = [];
        using (var row = command.ExecuteReader())
            if (row.Read())
            {
                if (row.GetString(0).Length > 262144 || row.GetString(1).Length > 65536 ||
                    row.GetString(2) != owner.Account || row.GetInt32(3) != (int)RaidKind.Luke ||
                    !RecoveryPlanMatches(row.GetString(0), prepared))
                    throw new InvalidDataException("该权益已选其它牌面或原领取回执不一致");
                mails = Decode<ImmutableArray<uint>>(row.GetString(1));
                if (mails.IsDefault) throw new InvalidDataException("原领取邮件回执损坏");
                claimed = true;
            }
        return new LukeRewardRecoverySnapshot(prepared, eligibility.Event, eligibility.Anchor, claimed, mails);
    });

    static bool RecoveryPlanMatches(string json, PreparedRaidReward prepared)
    {
        try { return RaidRewardPlanMatches(json, JsonSerializer.Serialize(prepared), RaidKind.Luke); }
        catch (JsonException ex) { throw new InvalidDataException("历史领取回执JSON损坏", ex); }
    }

    static T Decode<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("历史攻坚记录为空"); }
        catch (JsonException ex) { throw new InvalidDataException("历史攻坚记录JSON损坏", ex); }
    }
}
