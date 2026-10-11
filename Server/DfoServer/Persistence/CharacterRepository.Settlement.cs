using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 通关经验、免费牌资产、满背包邮件和永久回执共用一个事务。
    public DungeonSettlementCommit CommitDungeonSettlement(AccountOwners.Lease owner, Character expected,
        SinglePlayerDungeonRun run, PreparedDungeonSettlement prepared, CharacterGrowthCatalog catalog,
        InventoryPlan? cardInventory = null, bool mailCardItem = false, DungeonCardDelivery? premiumDelivery = null) => owner.Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(catalog);
        if (prepared.Options is not null && premiumDelivery is null)
            throw new InvalidDataException("付费及会员通关必须提供完整免费批次");
        if (!run.IsPendingSettlement(prepared) || prepared.Fact.Identity.CharacterId != expected.Id ||
            prepared.Fact.DungeonId != run.DungeonId || prepared.Fact.ConfirmedDeathEvents.Count == 0)
            throw new InvalidDataException("通关提交需要本角色当前实例的冻结清图事实与首份报告");
        var identity = prepared.Fact.Identity;
        var reward = prepared.Reward;
        byte[] deaths = prepared.Fact.ConfirmedDeathEvents.SelectMany(value => value.ToByteArray()).ToArray();
        using var db = database.Open();
        // 同份冻结报告的恢复只读账本；新报告在同一事务保存经验、双树SP和receipt。
        using var tx = db.BeginTransaction(deferred: true);
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.DungeonState?.LastAttempt != identity.TaskAttempt)
            throw new InvalidOperationException("通关角色、账号或副本尝试已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
        command.CommandText = """
            SELECT account,event_id,quest_id,task_attempt,epoch,dungeon_id,map_id,death_events,report_body,
              base_experience,rank_experience,total_experience,rank_bonus_index,rule_id,resource_source,before_experience,after_experience
            FROM character_dungeon_settlement_receipts WHERE character_id=$id AND instance_id=$instance
            """;
        bool duplicate;
        uint gained = 0;
        using (var reader = command.ExecuteReader())
        {
            duplicate = reader.Read();
            if (duplicate && (reader.GetString(0) != owner.Account || reader.GetString(1) != prepared.EventId.ToString("N") ||
                reader.GetInt64(2) != identity.QuestId || reader.GetInt64(3) != identity.TaskAttempt ||
                reader.GetInt64(4) != identity.Epoch || reader.GetInt64(5) != prepared.Fact.DungeonId ||
                reader.GetInt64(6) != prepared.MapId || !((byte[])reader[7]).AsSpan().SequenceEqual(deaths) ||
                !((byte[])reader[8]).AsSpan().SequenceEqual(prepared.ReportBody) ||
                reader.GetInt64(9) != reward.BaseExperience || reader.GetInt64(10) != reward.RankExperience ||
                reader.GetInt64(11) != reward.TotalExperience || reader.GetInt64(12) != reward.RankBonusIndex ||
                reader.GetString(13) != reward.RuleId || reader.GetString(14) != reward.ResourceSource))
                throw new InvalidDataException("同一通关实例的事实、报告或奖励被修改");
            if (duplicate) gained = checked((uint)(reader.GetInt64(16) - reader.GetInt64(15)));
        }
        if (duplicate)
        {
            var restored = ReadCardCommit(db, tx, owner.Account, prepared, current, expected, true, gained);
            tx.Commit();
            return restored;
        }
        var result = CharacterGrowthPlan.AddExperience(expected, reward.TotalExperience, catalog);
        if (!result.Success)
            throw new InvalidDataException("通关成长候选被拒绝: " + result.Failure);
        var plan = result.Plan!;
        if (current.Revision != plan.Revision || current.Level != plan.PreviousLevel || current.Job != plan.Job ||
            current.GrowType != plan.GrowType || !SameLearning(current.Learning, plan.Before) ||
            !current.Skills.OrderBy(skill => skill.Id).SequenceEqual(expected.Skills.OrderBy(skill => skill.Id)))
            throw new InvalidOperationException("通关成长revision或完整学习前提已变化");
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$revision", plan.Revision);
        command.Parameters.AddWithValue("$level", plan.TargetLevel);
        command.Parameters.AddWithValue("$experience", checked((long)plan.Learning.Experience));
        command.CommandText = """
            UPDATE characters SET level=$level,experience=$experience,revision=revision+1
            WHERE id=$id AND account=$account AND revision=$revision
            """;
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("通关成长revision或账号已变化");
        WriteLearning(db, tx, expected.Id, plan.Learning);
        WriteCanonicalSkills(db, tx, expected.Id, plan.Learning);
        if (quests is not null)
            WriteQuestConditions(db,tx,current.Id,current.ActiveMainQuests,DfoServer.Game.Quests.QuestStateProgress.Refresh(
                current.ActiveMainQuests,current with { Level = plan.TargetLevel, Learning = plan.Learning },quests));
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$account", owner.Account);
        command.Parameters.AddWithValue("$event", prepared.EventId.ToString("N"));
        command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
        command.Parameters.AddWithValue("$quest", identity.QuestId);
        command.Parameters.AddWithValue("$attempt", identity.TaskAttempt);
        command.Parameters.AddWithValue("$epoch", identity.Epoch);
        command.Parameters.AddWithValue("$dungeon", prepared.Fact.DungeonId);
        command.Parameters.AddWithValue("$map", prepared.MapId);
        command.Parameters.AddWithValue("$deaths", deaths);
        command.Parameters.AddWithValue("$report", prepared.ReportBody.ToArray());
        command.Parameters.AddWithValue("$generation", owner.Generation);
        command.Parameters.AddWithValue("$base", reward.BaseExperience);
        command.Parameters.AddWithValue("$rank", reward.RankExperience);
        command.Parameters.AddWithValue("$total", reward.TotalExperience);
        command.Parameters.AddWithValue("$bonus", reward.RankBonusIndex);
        command.Parameters.AddWithValue("$rule", reward.RuleId);
        command.Parameters.AddWithValue("$resource", reward.ResourceSource);
        command.Parameters.AddWithValue("$beforeLevel", plan.PreviousLevel);
        command.Parameters.AddWithValue("$afterLevel", plan.TargetLevel);
        command.Parameters.AddWithValue("$beforeExperience", checked((long)plan.Before.Experience));
        command.Parameters.AddWithValue("$afterExperience", checked((long)plan.Learning.Experience));
        command.Parameters.AddWithValue("$revision", checked(plan.Revision + 1));
        command.CommandText = """
            INSERT INTO character_dungeon_settlement_receipts(character_id,account,event_id,instance_id,quest_id,
              task_attempt,epoch,dungeon_id,map_id,death_events,report_body,generation,base_experience,rank_experience,
              total_experience,rank_bonus_index,rule_id,resource_source,before_level,after_level,
              before_experience,after_experience,revision)
            VALUES($id,$account,$event,$instance,$quest,$attempt,$epoch,$dungeon,$map,$deaths,$report,$generation,
              $base,$rank,$total,$bonus,$rule,$resource,$beforeLevel,$afterLevel,$beforeExperience,$afterExperience,$revision)
            """;
        command.ExecuteNonQuery();
        var committed = Require(db, tx, owner.Account, expected.Name);
        if (committed.Level != plan.TargetLevel || committed.Revision != plan.Revision + 1 || !SameLearning(committed.Learning, plan.Learning))
            throw new InvalidOperationException("持久通关成长结果与候选不一致");
        DungeonCardReward? card = null;
        uint? mailId = null;
        if (prepared.Card is { } frozen)
        {
            if (premiumDelivery is not null)
            {
                if (prepared.Options is null) throw new InvalidDataException("会员奖励缺少冻结资格");
                DungeonCardReward[] requested = prepared.Options.BlackDiamond is { } bonus ? [frozen,bonus] : [frozen];
                ValidateCardDelivery(premiumDelivery,requested,0);
                cardInventory = premiumDelivery.Inventory;
            }
            else if (cardInventory is null || cardInventory.Reason != "dungeon_card_reward" ||
                cardInventory.ChangedCurrencies.Any(kind => kind != Game.Currency.CurrencyKind.Gold) ||
                cardInventory.After.Gold < cardInventory.Before.Gold ||
                (long)cardInventory.After.Gold - cardInventory.Before.Gold > frozen.Gold ||
                mailCardItem && frozen.Item is null)
                throw new InvalidDataException("免费牌需要同一冻结奖励的库存规划");
            if (premiumDelivery is null) ValidateCardInventory(cardInventory!, frozen, mailCardItem);
            committed = CommitInventoryInTransaction(db, tx, owner, committed, cardInventory!);
            card = premiumDelivery?.Rewards[0] ?? frozen with { Gold = checked((int)(committed.Inventory.Gold - expected.Inventory.Gold)) };
            if (premiumDelivery is not null)
                mailId = DeliverCardItems(db,tx,owner,committed,premiumDelivery.MailItems,prepared,"free");
            else if (mailCardItem)
            {
                var recipient = new MailboxRecipient(committed.Id, owner.Account, committed.Name, committed.Job, committed.GrowType, committed.Level);
                var delivery = new MailboxDelivery("dungeon-card:" + prepared.EventId.ToString("N"), recipient,
                    "副本奖励", "背包空间不足，免费翻牌物品已通过永久邮件保存。", 0,
                    [new(0, new(InventoryListType.Main, 9), frozen.Item!)], frozen.CreatedAt);
                var delivered = DeliverMailInTransaction(db, tx, delivery);
                if (!delivered.Accepted) throw new InvalidOperationException("翻牌溢出邮件提交失败: " + delivered.Error);
                mailId = delivered.MailId;
            }
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
            command.Parameters.AddWithValue("$plan", JsonSerializer.Serialize(frozen));
            command.Parameters.AddWithValue("$reward", JsonSerializer.Serialize(card));
            command.Parameters.AddWithValue("$mail", (object?)mailId ?? DBNull.Value);
            command.Parameters.AddWithValue("$revision", committed.Revision);
            command.CommandText = """
                INSERT INTO character_dungeon_card_rewards(character_id,account,instance_id,plan_json,reward_json,mail_id,revision)
                VALUES($id,$account,$instance,$plan,$reward,$mail,$revision)
                """;
            command.ExecuteNonQuery();
            if (prepared.Options is { } options)
            {
                command.Parameters.AddWithValue("$options",JsonSerializer.Serialize(options));
                command.Parameters.AddWithValue("$bonus",premiumDelivery is { Rewards.Length:2 } ? JsonSerializer.Serialize(premiumDelivery.Rewards[1]) : (object)DBNull.Value);
                command.CommandText = """
                    INSERT INTO character_dungeon_card_options(character_id,account,instance_id,options_json,black_diamond_reward_json,bonus_mail_id)
                    VALUES($id,$account,$instance,$options,$bonus,$mail)
                    """;
                command.ExecuteNonQuery();
            }
        }
        else if (cardInventory is not null || mailCardItem)
            throw new InvalidDataException("没有冻结免费牌时不能提交翻牌资产");
        tx.Commit();
        return new DungeonSettlementCommit(committed, false, reward)
        {
            GainedExperience = checked((uint)plan.GainedExperience),
            Card = card, MailId = mailId, Options = prepared.Options,
            BlackDiamond = premiumDelivery is { Rewards.Length:2 } ? premiumDelivery.Rewards[1] : null,
            Inventory = card is null ? null : new(expected.Inventory, committed.Inventory, "dungeon_card_reward")
        };
    });

    static DungeonSettlementCommit ReadCardCommit(SqliteConnection db, SqliteTransaction tx, string account,
        PreparedDungeonSettlement prepared, Character current, Character expected, bool duplicate, uint gained)
    {
        if (prepared.Card is null) return new(current, duplicate, prepared.Reward) { GainedExperience = gained };
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$id", current.Id);
        query.Parameters.AddWithValue("$instance", prepared.Fact.Identity.InstanceId.ToString("N"));
        query.CommandText = "SELECT account,plan_json,reward_json,mail_id FROM character_dungeon_card_rewards WHERE character_id=$id AND instance_id=$instance";
        using var reader = query.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != account || reader.GetString(1) != JsonSerializer.Serialize(prepared.Card))
            throw new InvalidDataException("通关免费牌回执缺失或冻结奖励被修改");
        var card = JsonSerializer.Deserialize<DungeonCardReward>(reader.GetString(2)) ?? throw new InvalidDataException("翻牌回执为空");
        card.Validate();
        if (card.Gold > prepared.Card.Gold || card with { Gold = prepared.Card.Gold } != prepared.Card)
            throw new InvalidDataException("翻牌实际回执与冻结来源不一致");
        uint? mail = reader.IsDBNull(3) ? null : checked((uint)reader.GetInt64(3));
        reader.Close();
        DungeonCardReward? bonus = null;
        if (prepared.Options is { } options)
        {
            query.CommandText = "SELECT options_json,black_diamond_reward_json FROM character_dungeon_card_options WHERE character_id=$id AND instance_id=$instance";
            using var extra = query.ExecuteReader();
            if (!extra.Read() || extra.GetString(0) != JsonSerializer.Serialize(options)) throw new InvalidDataException("付费及会员冻结计划缺失或变化");
            if (!extra.IsDBNull(1))
            {
                bonus = JsonSerializer.Deserialize<DungeonCardReward>(extra.GetString(1)) ?? throw new InvalidDataException("黑钻回执为空");
                VerifyActualCard(bonus,options.BlackDiamond ?? throw new InvalidDataException("没有黑钻冻结资格"));
            }
        }
        return new(current, duplicate, prepared.Reward)
        {
            GainedExperience = gained,
            Card = card, MailId = mail, Options = prepared.Options, BlackDiamond = bonus,
            Inventory = new(expected.Inventory, current.Inventory, "dungeon_card_reward")
        };
    }

    static void ValidateCardInventory(InventoryPlan plan, DungeonCardReward reward, bool mailItem)
    {
        if (plan.After.Expansion != plan.Before.Expansion || plan.After.CargoCapacity != plan.Before.CargoCapacity ||
            plan.After.GoldCarryLimit != plan.Before.GoldCarryLimit || plan.After.AuctionGoldLimit != plan.Before.AuctionGoldLimit ||
            plan.After.AccountCargo != plan.Before.AccountCargo || !plan.After.EpicPieces.Same(plan.Before.EpicPieces) ||
            !ShopPurchaseCount.Same(plan.After.ShopPurchases,plan.Before.ShopPurchases))
            throw new InvalidDataException("翻牌不能修改无关资产资格");
        if (mailItem || reward.Item is null)
        {
            if (plan.ChangedSlots.Length != 0) throw new InvalidDataException("邮件或空牌不能修改背包物品");
            return;
        }
        if (plan.ChangedSlots.Length != 1) throw new InvalidDataException("普通免费牌必须完整发放同一冻结物品");
        var slot = plan.ChangedSlots[0];
        var before = plan.BeforeIndex.Find(slot);
        var after = plan.AfterIndex.Find(slot);
        if (after is null || after.Core with { Value = reward.Item.Value } != reward.Item ||
            before is not null && (before.Uid != after.Uid || before.Core with { Value = reward.Item.Value } != reward.Item) ||
            after.Core.Count - (before?.Core.Count ?? 0) != reward.Item.Count)
            throw new InvalidDataException("翻牌新增量、完整核心或原UID与冻结奖励不一致");
    }
}
