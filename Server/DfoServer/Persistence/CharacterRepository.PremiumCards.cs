using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    public DungeonPaidCardCommit CommitPaidDungeonCard(AccountOwners.Lease owner,Character expected,
        PreparedDungeonSettlement prepared,DungeonCardDelivery delivery,DateTimeOffset now) => owner.Execute<DungeonPaidCardCommit>(() =>
    {
        var options = prepared.Options ?? throw new InvalidDataException("本次通关没有付费牌计划");
        if (options.PaidDisabled) return new(false);
        if (prepared.Fact.Identity.CharacterId != expected.Id) throw new InvalidDataException("付费牌不是当前角色实例");
        using var db = database.Open();
        using var write = db.BeginWrite("CharacterRepository.CommitPaidDungeonCard", deferred: true);
        using var tx = write.Transaction;
        var current = Require(db,tx,owner.Account,expected.Name);
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$id",expected.Id);
        query.Parameters.AddWithValue("$instance",prepared.Fact.Identity.InstanceId.ToString("N"));
        query.CommandText = "SELECT account,options_json,paid_reward_json,paid_mail_id FROM character_dungeon_card_options WHERE character_id=$id AND instance_id=$instance";
        DungeonCardReward? previous = null;
        uint? previousMail = null;
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != owner.Account || reader.GetString(1) != JsonSerializer.Serialize(options))
                throw new InvalidDataException("付费牌计划不存在或已变化");
            if (!reader.IsDBNull(2))
            {
                previous = JsonSerializer.Deserialize<DungeonCardReward>(reader.GetString(2)) ?? throw new InvalidDataException("付费回执为空");
                previousMail = reader.IsDBNull(3) ? null : checked((uint)reader.GetInt64(3));
            }
        }
        if (previous is not null)
        {
            VerifyActualCard(previous,options.Paid);
            tx.Commit();
            return new(true,current,previous,new(expected.Inventory,current.Inventory,"dungeon_card_reward"),previousMail,true);
        }
        if (current.Id != expected.Id || current.Revision != expected.Revision || current.DungeonState?.LastAttempt != prepared.Fact.Identity.TaskAttempt)
            throw new InvalidOperationException("付费牌角色或实例前提已变化");
        if (delivery.Cost != options.Price || current.Inventory.Gold < options.Price)
            return new(false);
        if (options.UseGoldCard)
        {
            var membership = ReadCardMembership(db,tx,owner.Account,current.Id,now);
            // 已广告免费的牌失去资格时拒绝，不暗中改成金币扣费。
            if (!membership.GoldCardAvailable(now.ToUnixTimeSeconds())) return new(false);
            query.Parameters.AddWithValue("$account",owner.Account);
            query.Parameters.AddWithValue("$day",membership.Day);
            query.CommandText = """
                INSERT INTO character_gold_card_uses(character_id,account,service_day,used) VALUES($id,$account,$day,1)
                ON CONFLICT(character_id,service_day) DO UPDATE SET used=used+1 WHERE used<10
                """;
            if (query.ExecuteNonQuery() != 1) throw new InvalidOperationException("黄金牌次数已耗尽");
        }
        ValidateCardDelivery(delivery,[options.Paid],options.Price);
        current = CommitInventoryInTransaction(db,tx,owner,current,delivery.Inventory);
        uint? mail = DeliverCardItems(db,tx,owner,current,delivery.MailItems,prepared,"paid");
        query.Parameters.AddWithValue("$reward",JsonSerializer.Serialize(delivery.Rewards[0]));
        query.Parameters.AddWithValue("$mail",(object?)mail ?? DBNull.Value);
        query.Parameters.AddWithValue("$cost",options.Price);
        query.Parameters.AddWithValue("$revision",current.Revision);
        query.Parameters.AddWithValue("$claimDay",CardMembership.ServiceDay(now));
        query.CommandText = """
            UPDATE character_dungeon_card_options SET paid_reward_json=$reward,paid_mail_id=$mail,paid_cost=$cost,
              paid_revision=$revision,paid_day=$claimDay WHERE character_id=$id AND instance_id=$instance AND paid_reward_json IS NULL
            """;
        if (query.ExecuteNonQuery() != 1) throw new InvalidOperationException("付费牌资格已被消费");
        ApplyBattlePassGoldenPaidCard(db,tx,owner.Account,prepared,current.Id,now);
        tx.Commit();
        return new(true,current,delivery.Rewards[0],new(expected.Inventory,current.Inventory,"dungeon_card_reward"),mail);
    });

    static uint? DeliverCardItems(SqliteConnection db,SqliteTransaction tx,AccountOwners.Lease owner,Character current,
        ImmutableArray<ItemCore> cores,PreparedDungeonSettlement prepared,string side,long? createdAt = null)
    {
        if (cores.IsEmpty) return null;
        var recipient = new MailboxRecipient(current.Id,owner.Account,current.Name,current.Job,current.GrowType,current.Level);
        var items = cores.Select(core => new InventoryItem(0,new(InventoryListType.Main,9),core)).ToImmutableArray();
        var delivery = new MailboxDelivery("dungeon-card:"+side+":"+prepared.EventId.ToString("N"),recipient,"副本奖励",
            "背包空间不足，翻牌物品已通过永久邮件保存。",0,items,createdAt ?? prepared.Card!.CreatedAt);
        var (mailError, mailId) = DeliverMailInTransaction(db,tx,delivery);
        if (mailError != MailboxError.None) throw new InvalidOperationException("翻牌邮件失败: "+mailError);
        return mailId;
    }

    static void VerifyActualCard(DungeonCardReward actual,DungeonCardReward frozen)
    {
        actual.Validate();
        if (actual.Gold > frozen.Gold || actual with { Gold = frozen.Gold } != frozen)
            throw new InvalidDataException("实际牌面与冻结来源不一致");
    }

    static void ValidateCardDelivery(DungeonCardDelivery delivery,IReadOnlyList<DungeonCardReward> frozen,int cost)
    {
        var plan = delivery.Inventory;
        if (delivery.Rewards.Length != frozen.Count || delivery.Cost != cost || plan.Reason != "dungeon_card_reward" ||
            plan.ChangedCurrencies.Any(kind => kind != Game.Currency.CurrencyKind.Gold) ||
            (long)plan.After.Gold-plan.Before.Gold+cost != delivery.Rewards.Sum(card => (long)card.Gold) ||
            plan.Before.Expansion != plan.After.Expansion || plan.Before.CargoCapacity != plan.After.CargoCapacity ||
            plan.Before.AccountCargo != plan.After.AccountCargo || plan.Before.GoldCarryLimit != plan.After.GoldCarryLimit ||
            plan.Before.AuctionGoldLimit != plan.After.AuctionGoldLimit || !plan.Before.EpicPieces.Same(plan.After.EpicPieces) ||
            !ShopPurchaseCount.Same(plan.Before.ShopPurchases,plan.After.ShopPurchases))
            throw new InvalidDataException("翻牌费用、奖励或无关资产规划不匹配");
        var remaining = new List<ItemCore>();
        for (int index = 0; index < frozen.Count; index++)
        {
            VerifyActualCard(delivery.Rewards[index],frozen[index]);
            remaining.AddRange(frozen[index].Items());
        }
        foreach (var mailed in delivery.MailItems)
        {
            int at = remaining.FindIndex(core => core == mailed);
            if (at < 0) throw new InvalidDataException("溢出邮件不是冻结奖励");
            remaining.RemoveAt(at);
        }
        ItemCore Key(ItemCore core) => core.IsStackable ? core with { Value = 1 } : core;
        var expected = remaining.GroupBy(Key).ToDictionary(group => group.Key,group => group.Sum(core => core.Count));
        foreach (var slot in plan.ChangedSlots)
        {
            var before = plan.BeforeIndex.Find(slot);
            var after = plan.AfterIndex.Find(slot);
            if (after is null || before is not null && (before.Uid != after.Uid || Key(before.Core) != Key(after.Core)))
                throw new InvalidDataException("翻牌不能删除或更换已有完整核心");
            var key = Key(after.Core);
            int added = after.Core.Count-(before?.Core.Count ?? 0);
            if (added <= 0 || !expected.TryGetValue(key,out int needed) || needed < added)
                throw new InvalidDataException("翻牌新增物品量超过冻结批次");
            expected[key] = needed-added;
        }
        if (expected.Values.Any(value => value != 0)) throw new InvalidDataException("翻牌批次遗漏物品");
    }
}
