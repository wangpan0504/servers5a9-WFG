using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Raids;
using Microsoft.Data.Sqlite;

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
        command.CommandText = $"SELECT {prefix}_receipt,{prefix}_event,{prefix}_anchor,source FROM character_raid_runs WHERE character_id=$id AND account=$account AND instance_id=$instance AND raid_type=$raidType AND length({prefix}_receipt)<=262144";
        command.Parameters.AddWithValue("$raidType", (byte)target.RaidType);
        RaidPhaseEligibility eligibility;
        using (var row = command.ExecuteReader())
        {
            if (!row.Read() || row.IsDBNull(0)) throw new InvalidDataException("没有该明确目标的已提交阶段资格");
            eligibility = DecodeLukeEligibility(row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1),
                row.IsDBNull(2) ? null : row.GetInt64(2), target.Instance, target.Phase, character.Id,
                validateAllGrants: target.RaidType == RaidKind.Prey, raidType: target.RaidType);
            if (target.RaidType == RaidKind.Fiendwar && (eligibility.Source != row.GetString(3) || eligibility.Week <= 0))
                throw new InvalidDataException("漩涡历史资格来源或周期不属于原开战实例");
            if (target.RaidType == RaidKind.Prey && (eligibility.Source is not (PreyGuideRewards.TeamSource or PreyGuideRewards.Source) ||
                eligibility.Source != row.GetString(3) || eligibility.Week <= 0 ||
                eligibility.Offers.Any(offer => offer.Kind is not (0 or 1) || offer.Card != 0)))
                throw new InvalidDataException("普雷历史资格来源或牌面不属于原开战实例");
            if (!eligibility.Eligible) throw new InvalidDataException("没有该明确目标的已提交阶段资格");
        }
        bool formalLuke = target.RaidType == RaidKind.Luke && LukeResourceRewards.IsFormalSource(eligibility.Source);
        if (formalLuke && target.Card > 3) throw new InvalidDataException("历史卢克牌面越界");
        // 正式权益冻结在0号授权牌；选择位置仍留在真实领取回执，不影响其它策略的精确牌面。
        var offer = eligibility.Offers.SingleOrDefault(x => x.Kind == target.Kind &&
            x.Card == (target.RaidType == RaidKind.Fiendwar || formalLuke ? 0 : target.Card))
            ?? throw new InvalidDataException("原资格没有该牌面");
        if (offer.Grants.IsDefaultOrEmpty || offer.Grants.Length > 32 || offer.Grants.Any(x => x is null))
            throw new InvalidDataException("原奖励计划损坏");
        var prepared = new PreparedRaidReward(target.Instance, target.Phase, target.Kind, target.Card, character.Id,
            eligibility.Source, offer.Grants, target.RaidType == RaidKind.Fiendwar || target.Phase == 1, eligibility.Week)
            { RaidType = target.RaidType };
        command.CommandText = "SELECT plan_json,mail_ids,account,raid_type,card FROM character_raid_reward_receipts WHERE character_id=$id AND instance_id=$instance AND phase=$phase AND kind=$kind";
        bool claimed = false; ImmutableArray<uint> mails = [];
        using (var row = command.ExecuteReader())
            if (row.Read())
            {
                if (target.RaidType == RaidKind.Fiendwar)
                {
                    if (row.GetString(0).Length > 262144 || Decode<PreparedRaidReward>(row.GetString(0)) is not { Card: <= 3 } selected)
                        throw new InvalidDataException("漩涡原领取卡位损坏");
                    // 只恢复同kind真实已提交的展示卡位；其余字段仍逐字段严格匹配原资格。
                    prepared = prepared with { Card = selected.Card };
                }
                if (row.GetString(0).Length > 262144 || row.GetString(1).Length > 65536 ||
                    row.GetString(2) != owner.Account || row.GetInt32(3) != (int)target.RaidType ||
                    target.RaidType == RaidKind.Prey && row.GetInt32(4) != 0 || formalLuke && row.GetInt32(4) != target.Card ||
                    !RecoveryPlanMatches(row.GetString(0), prepared))
                    throw new InvalidDataException("该权益已选其它牌面或原领取回执不一致");
                mails = Decode<ImmutableArray<uint>>(row.GetString(1));
                if (mails.IsDefault) throw new InvalidDataException("原领取邮件回执损坏");
                claimed = true;
            }
        return new LukeRewardRecoverySnapshot(prepared, eligibility.Event, eligibility.Anchor, claimed, mails);
    });

    // 只读发现不接收资源目录，不自动选择牌面，也不调用领取或恢复Battle。
    public LukeRewardRecoveryPage ListLukeRewardRecovery(AccountOwners.Lease owner, Character character,
        Guid? afterInstance = null, int limit = 16) => owner.Execute(() =>
    {
        if (limit is < 1 or > 16 || afterInstance == Guid.Empty)
            throw new InvalidDataException("历史资格分页参数无效");
        using var db = database.Open(); using var tx = db.BeginRead();
        var current = Require(db, tx, owner.Account, character.Name);
        if (current.Id != character.Id || current.Revision != character.Revision)
            throw new InvalidDataException("目标角色版本已变化");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$id", character.Id); command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$after", afterInstance?.ToString("N") ?? "");
        command.Parameters.AddWithValue("$limit", limit);
        // 现有(character_id,instance_id)主键支持keyset；先取有界身份，避免加载所有历史JSON。
        command.CommandText = "SELECT instance_id FROM character_raid_runs WHERE character_id=$id AND account=$account AND raid_type=1 AND instance_id>$after ORDER BY instance_id LIMIT $limit";
        var instances = new List<Guid>(limit);
        using (var rows = command.ExecuteReader())
            while (rows.Read())
            {
                string raw = rows.GetString(0);
                if (!Guid.TryParseExact(raw, "N", out var instance) || instance == Guid.Empty ||
                    !string.Equals(raw, instance.ToString("N"), StringComparison.Ordinal))
                    throw new InvalidDataException("历史实例主键损坏");
                instances.Add(instance);
            }
        var entries = ImmutableArray.CreateBuilder<LukeRewardRecoveryEntry>();
        command.Parameters.Add("$instance", SqliteType.Text); command.Parameters.Add("$phase", SqliteType.Integer);
        foreach (var instance in instances)
        {
            command.Parameters["$instance"].Value = instance.ToString("N");
            for (int phase = 0; phase < 2; phase++)
            {
                string prefix = phase == 0 ? "phase0" : "phase1";
                command.CommandText = $"SELECT CASE WHEN length({prefix}_receipt)<=262144 THEN {prefix}_receipt END,{prefix}_event,{prefix}_anchor,length({prefix}_receipt) FROM character_raid_runs WHERE character_id=$id AND account=$account AND instance_id=$instance AND raid_type=1";
                RaidPhaseEligibility eligibility;
                using (var row = command.ExecuteReader())
                {
                    if (!row.Read() || row.IsDBNull(3)) continue;
                    if (row.GetInt64(3) > 262144) throw new InvalidDataException("历史阶段资格超过读取上限");
                    eligibility = DecodeLukeEligibility(row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1),
                        row.IsDBNull(2) ? null : row.GetInt64(2), instance, phase, character.Id, validateAllGrants: true);
                }
                if (!eligibility.Eligible) continue;
                var claimed = ImmutableArray.CreateBuilder<LukeRewardRecoverySnapshot>();
                command.Parameters["$phase"].Value = phase;
                command.CommandText = "SELECT kind,card,CASE WHEN length(plan_json)<=262144 THEN plan_json END,CASE WHEN length(mail_ids)<=65536 THEN mail_ids END,account,raid_type FROM character_raid_reward_receipts WHERE character_id=$id AND instance_id=$instance AND phase=$phase ORDER BY kind LIMIT 33";
                using (var rows = command.ExecuteReader())
                    while (rows.Read())
                    {
                        int card = rows.GetInt32(1);
                        bool formalLuke = LukeResourceRewards.IsFormalSource(eligibility.Source);
                        if (formalLuke && card is < 0 or > 3) throw new InvalidDataException("历史卢克牌面越界");
                        var offer = eligibility.Offers.SingleOrDefault(x => x.Kind == rows.GetInt32(0) && x.Card == (formalLuke ? 0 : card));
                        if (claimed.Count >= 32 || offer is null || rows.IsDBNull(2) || rows.IsDBNull(3) ||
                            rows.GetString(4) != owner.Account || rows.GetInt32(5) != (int)RaidKind.Luke)
                            throw new InvalidDataException("历史领取回执身份或牌面损坏");
                        var prepared = new PreparedRaidReward(instance, phase, offer.Kind, checked((byte)card), character.Id,
                            eligibility.Source, offer.Grants, phase == 1, eligibility.Week) { RaidType = RaidKind.Luke };
                        if (!RecoveryPlanMatches(rows.GetString(2), prepared)) throw new InvalidDataException("原领取回执不一致");
                        var mails = Decode<ImmutableArray<uint>>(rows.GetString(3));
                        if (mails.IsDefault) throw new InvalidDataException("原领取邮件回执损坏");
                        claimed.Add(new(prepared, eligibility.Event, eligibility.Anchor, true, mails));
                    }
                entries.Add(new(eligibility, claimed.ToImmutable()));
            }
        }
        return new LukeRewardRecoveryPage(entries.ToImmutable(), instances.Count == limit ? instances[^1] : null);
    });

    static RaidPhaseEligibility DecodeLukeEligibility(string json, string? savedEvent, long? savedAnchor,
        Guid instance, int phase, ushort character, bool validateAllGrants = false, RaidKind raidType = RaidKind.Luke)
    {
        var eligibility = Decode<RaidPhaseEligibility>(json);
        if (eligibility.Instance != instance || eligibility.Character != character || eligibility.RaidType != raidType ||
            eligibility.Phase != phase || eligibility.Event == Guid.Empty || eligibility.Event.ToString("N") != savedEvent ||
            eligibility.Anchor != savedAnchor || string.IsNullOrWhiteSpace(eligibility.Source) ||
            eligibility.Offers.IsDefaultOrEmpty || eligibility.Offers.Length > 32 || eligibility.Offers.Any(x => x is null) ||
            eligibility.Offers.Select(x => (x.Kind, x.Card)).Distinct().Count() != eligibility.Offers.Length ||
            (validateAllGrants && eligibility.Offers.Any(x => x.Grants.IsDefaultOrEmpty || x.Grants.Length > 32 || x.Grants.Any(g => g is null))))
            throw new InvalidDataException("阶段资格身份或牌面损坏");
        return eligibility;
    }

    static bool RecoveryPlanMatches(string json, PreparedRaidReward prepared)
    {
        try { return RaidRewardPlanMatches(json, JsonSerializer.Serialize(prepared), prepared.RaidType); }
        catch (JsonException ex) { throw new InvalidDataException("历史领取回执JSON损坏", ex); }
    }

    static T Decode<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("历史攻坚记录为空"); }
        catch (JsonException ex) { throw new InvalidDataException("历史攻坚记录JSON损坏", ex); }
    }
}
