using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
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
    // 通关只冻结牌面；经验、幸运星和冻结计划共事务，资产留到实际选牌。
    public DungeonSettlementCommit CommitDungeonSettlement(AccountOwners.Lease owner, Character expected,
        SinglePlayerDungeonRun run, PreparedDungeonSettlement prepared, CharacterGrowthCatalog catalog,
        InventoryPlan? cardInventory = null, bool mailCardItem = false, DungeonCardDelivery? premiumDelivery = null,
        bool grantLuckyStar = false, int luckyStarLimit = 0, bool recommendedClear = false) => owner.Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(catalog);
        if (cardInventory is not null || mailCardItem || premiumDelivery is not null)
            throw new InvalidDataException("通关不能提前提交翻牌资产");
        if (!run.IsPendingSettlement(prepared) || prepared.Fact.Identity.CharacterId != expected.Id ||
            prepared.Fact.DungeonId != run.DungeonId ||
            prepared.Fact.ConfirmedDeathEvents.Count == 0 && prepared.Fact.PassiveSource is null)
            throw new InvalidDataException("通关提交需要本角色当前实例的冻结清图事实与首份报告");
        var identity = prepared.Fact.Identity;
        var reward = prepared.Reward;
        // 合并后账本仍保留秘药与冒险团分项列，CHECK 为六项之和：
        // other_bonus 只能放除成长契约、秘药、冒险团之外的分项，否则会重复计入秘药。
        uint otherBonus = checked(reward.ExtraExperience - reward.GrowthContractExperience -
            reward.PotionExperience - reward.AdventureExperience);
        string bonusJson = reward.BonusParts.IsDefault ? "[]" : JsonSerializer.Serialize(reward.BonusParts);
        byte[] deaths = prepared.Fact.ConfirmedDeathEvents.SelectMany(value => value.ToByteArray()).ToArray();
        string? sceneOperation = prepared.Fact.PassiveSource?.OperationId.ToString("N");
        using var db = database.Open();
        // 同份冻结报告的恢复只读账本；新报告在同一事务保存经验、双树SP和receipt。
        using var write = db.BeginWrite("CharacterRepository.CommitDungeonSettlement", deferred: true);
        using var tx = write.Transaction;
        VerifySettlementSceneClear(db,tx,owner,prepared.Fact);
        var current = Require(db, tx, owner.Account, expected.Name);
        if (current.Id != expected.Id || current.DungeonState?.LastAttempt != identity.TaskAttempt)
            throw new InvalidOperationException("通关角色、账号或副本尝试已变化");
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$id", expected.Id);
        command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
        command.CommandText = """
            SELECT account,event_id,quest_id,task_attempt,epoch,dungeon_id,map_id,death_events,report_body,
              base_experience,rank_experience,total_experience,rank_bonus_index,rule_id,resource_source,before_experience,after_experience,
              scene_operation_id,growth_contract_experience,potion_experience,adventure_experience,
              other_bonus_experience,bonus_json,proficiency_gain
            FROM character_dungeon_settlement_receipts WHERE character_id=$id AND instance_id=$instance
            """;
        bool duplicate;
        uint gained = 0, proficiencyGained = 0;
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
                reader.GetString(13) != reward.RuleId || reader.GetString(14) != reward.ResourceSource ||
                (reader.IsDBNull(17) ? null : reader.GetString(17)) != sceneOperation || reader.GetInt64(18) != reward.GrowthContractExperience ||
                reader.GetInt64(19) != reward.PotionExperience || reader.GetInt64(20) != reward.AdventureExperience ||
                reader.GetInt64(21) != otherBonus || reader.GetString(22) != bonusJson))
                throw new InvalidDataException("同一通关实例的事实、报告或奖励被修改");
            if (duplicate)
            {
                gained = checked((uint)(reader.GetInt64(16) - reader.GetInt64(15)));
                proficiencyGained = checked((uint)reader.GetInt64(23));
            }
        }
        if (duplicate)
        {
            var restored = ReadCardCommit(db, tx, owner.Account, prepared, current, expected, true, gained, proficiencyGained);
            restored = restored with { LuckyStarTotal = ReadAccountWallet(db, owner.Account, tx).RentalPoints,
                GuildMileageGuildId = ReadGuildDungeonMileage(db, tx, prepared),
                GuildDungeon = RestoreGuildDungeonProgress(db, tx, prepared) };
            if (prepared.Grief is { } grief)
                restored = restored with { GriefReward = grief.Reward, MailId = ReadGriefClear(db,tx,owner,prepared),
                    Inventory = new(expected.Inventory,current.Inventory,"grief_tower_reward") };
            if (prepared.BossRush is { } rush)
                restored = restored with { BossRushReward = rush,
                    Inventory = rush.Delivery.Inventory.Changed
                        ? new(expected.Inventory,current.Inventory,"boss_rush_reward") : restored.Inventory };
            if (ReadGuildDungeonReward(db, tx, owner, prepared) is { } guildMail)
                restored = restored with { MailId = guildMail };
            tx.Commit();
            return restored;
        }
        if (prepared.EkernDay is { } ekernDay)
        {
            if (run.DungeonId is not (5097 or 5098) || run.Entry.IsPractice || !run.Entry.IsOrdinary)
                throw new InvalidDataException("艾肯计数缺少正式通关事实");
            WriteEkernClear(db,tx,owner,expected.Id,run.DungeonId,ekernDay);
        }
        var creatureInventory = prepared.CreatureInventory;
        if (creatureInventory is not null)
        {
            ValidateInventoryPlan(expected, creatureInventory);
            var slot = Game.Creatures.CreatureRules.EquippedSlot;
            if (creatureInventory.Reason != "creature_experience" || creatureInventory.ChangedSlots.Any(value => value != slot) ||
                creatureInventory.ChangedCurrencies.Length != 0 || creatureInventory.AccountCargoChanged ||
                creatureInventory.BeforeIndex.Find(slot) is not { Creature: not null } source ||
                creatureInventory.AfterIndex.Find(slot) is not { Creature: not null } target || source.Uid != target.Uid ||
                target.Creature.Experience < source.Creature.Experience || target.Creature.Level < source.Creature.Level ||
                !SameInventory(current.Inventory, creatureInventory) || creatureInventory.CharacterPrecondition?.Matches(current) == false)
                throw new InvalidDataException("宠物通关成长不是同一角色当前装备实例的冻结计划");
            WriteInventory(db, tx, owner, expected.Id, creatureInventory, creatureInventory.After);
        }
        if (prepared.AncientGate is { } ancient)
        {
            if (prepared.Fact.DungeonId is < 7101 or > 7107 || run.Entry.IsPractice ||
                !DailyBoosterUsage.Same(current.Inventory.DailyBoosterUses, ancient.Before))
                throw new InvalidOperationException("远古奖励进度的角色快照已变化");
        }
        var result = reward.TotalExperience == 0 && run.Entry.DisableMonsterAndClearExperience
            ? CharacterGrowthPlan.WithoutExperience(expected, catalog) : CharacterGrowthPlan.AddExperience(expected, reward.TotalExperience, catalog);
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
        command.Parameters.AddWithValue("$dailyBooster", JsonSerializer.Serialize(prepared.AncientGate?.After ?? current.Inventory.DailyBoosterUses));
        command.CommandText = """
            UPDATE characters SET level=$level,experience=$experience,revision=revision+1,daily_booster_usage=$dailyBooster
            WHERE id=$id AND account=$account AND revision=$revision
            """;
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("通关成长revision或账号已变化");
        WriteLearning(db, tx, expected.Id, plan.Learning);
        WriteCanonicalSkills(db, tx, expected.Id, plan.Learning);
        // 通关结算经验计入冒险团；同一实例的重复报告在事务开头已返回，不会重复计入。
        AdventureProgressionCommit? adventureCommit = null;
        if (CreditAdventure(db, tx, owner.Account, expected.Id, AdventureCreditSource.Earned, reward.TotalExperience,
                EffectivePosition(current.Level, current.Learning.Experience), current.Level, plan.TargetLevel,
                DateTimeOffset.UtcNow) is { } credit)
        {
            WriteAdventureCredit(db, tx, owner.Account, credit, AdventureCreditSource.Earned, adventureCatalog!);
            if (credit.After != credit.Before)
                adventureCommit = new(credit.After with { Revision = credit.Before.Revision + 1 }, credit.RawOverflow);
        }
        // 熟练度与通关经验同源：满级溢出全额、跨过95级入口线只计入口线以上部分。
        // 回执保存这笔经验里真正记入熟练度的额度，重复报告与显示层都按同一有效经验回放。
        if (current.DungeonState is { } dungeonState)
        {
            var proficiencyBefore = new ProficiencyProgress(dungeonState.ProficiencyLevel, dungeonState.ProficiencyExperience);
            var proficiencyGain = ProficiencyProgression.Gain(plan.PreviousLevel, plan.Before.Experience, reward.TotalExperience, catalog);
            if (ProficiencyProgression.Advance(proficiencyBefore, proficiencyGain, catalog) is { } proficiency)
            {
                WriteProficiency(db, tx, owner.Account, expected.Id, proficiency.Before, proficiency.After);
                proficiencyGained = checked((uint)proficiency.Credited);
            }
        }
        // 推荐地下城通关按角色累计到u16上限，账号面板读取时求和；同一实例的重复报告已在上面提前返回。
        if (recommendedClear)
        {
            RecordWeeklyAttendanceClear(db, tx, owner);
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.CommandText = """
                UPDATE character_dungeon_state SET recommended_dungeon_clears=min(recommended_dungeon_clears+1,65535)
                WHERE character_id=$id AND account=$account
                """;
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("推荐地下城计数缺少角色副本状态");
        }
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
        command.Parameters.AddWithValue("$sceneOperation", (object?)sceneOperation ?? DBNull.Value);
        command.Parameters.AddWithValue("$growthContract", reward.GrowthContractExperience);
        command.Parameters.AddWithValue("$potion", reward.PotionExperience);
        command.Parameters.AddWithValue("$adventure", reward.AdventureExperience);
        command.Parameters.AddWithValue("$otherBonus", otherBonus);
        command.Parameters.AddWithValue("$bonusJson", bonusJson);
        command.Parameters.AddWithValue("$proficiencyGained", proficiencyGained);
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
              before_experience,after_experience,revision,scene_operation_id,growth_contract_experience,potion_experience,adventure_experience,
              other_bonus_experience,bonus_json,proficiency_gain)
            VALUES($id,$account,$event,$instance,$quest,$attempt,$epoch,$dungeon,$map,$deaths,$report,$generation,
              $base,$rank,$total,$bonus,$rule,$resource,$beforeLevel,$afterLevel,$beforeExperience,$afterExperience,$revision,$sceneOperation,$growthContract,$potion,$adventure,$otherBonus,$bonusJson,$proficiencyGained)
            """;
        command.ExecuteNonQuery();
        ApplyBattlePassTayberrsClear(db, tx, owner.Account, run, prepared);
        ApplyBattlePassFormalClear(db, tx, owner.Account, run, prepared);
        ApplyBattlePassDazzlementClear(db, tx, owner.Account, run, prepared);
        ClearOrdinaryLuke(db, tx, owner, run);
        ClearPreyNormal(db, tx, owner, run);
        ClearAntonNormal(db, tx, owner, run);
        uint? griefMail = CommitGriefClear(db,tx,owner,expected,prepared);
        uint? bossRushMail = CommitBossRushClear(db,tx,owner,expected,prepared);
        ApplyBattlePassBossRushReward(db,tx,owner.Account,run,prepared,bossRushMail);
        long guildNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var guildReward = CommitGuildDungeonReward(db, tx, owner, expected, prepared, guildNow);
        uint guildMileageGuildId = CommitGuildDungeonMileage(db, tx, owner, run, prepared);
        var guildContribution = CommitGuildDungeonContribution(db, tx, owner, prepared, guildReward.Contribution);
        var guildDungeon = CommitGuildDungeonProgress(db, tx, prepared, guildNow);
        CommitGuildRankingParty(db, tx, prepared);
        int endlessAdventureAmount = GrantEndlessAdventure(db, tx, owner, expected, prepared);
        var committed = Require(db, tx, owner.Account, expected.Name);
        if (committed.Level != plan.TargetLevel || committed.Revision != plan.Revision + 1 || !SameLearning(committed.Learning, plan.Learning))
            throw new InvalidOperationException("持久通关成长结果与候选不一致");
        if (prepared.Card is { } frozen)
        {
            frozen.Validate();
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$id", expected.Id);
            command.Parameters.AddWithValue("$account", owner.Account);
            command.Parameters.AddWithValue("$instance", identity.InstanceId.ToString("N"));
            command.Parameters.AddWithValue("$plan", JsonSerializer.Serialize(frozen));
            command.Parameters.AddWithValue("$reward", JsonSerializer.Serialize(frozen));
            command.Parameters.AddWithValue("$revision", committed.Revision);
            command.CommandText = """
                INSERT INTO character_dungeon_card_rewards(character_id,account,instance_id,plan_json,reward_json,revision,claimed)
                VALUES($id,$account,$instance,$plan,$reward,$revision,0)
                """;
            command.ExecuteNonQuery();
            if (prepared.Options is { } options)
            {
                command.Parameters.AddWithValue("$options",JsonSerializer.Serialize(options));
                command.CommandText = """
                    INSERT INTO character_dungeon_card_options(character_id,account,instance_id,options_json)
                    VALUES($id,$account,$instance,$options)
                    """;
                command.ExecuteNonQuery();
            }
        }
        // 幸运星仍在通关事务发放；随后选牌使用实际更新后的钱包快照。
        var luckyStar = grantLuckyStar ? GrantDungeonLuckyStar(db, tx, owner.Account, luckyStarLimit) :
            new DungeonLuckyStarGrant(false, ReadAccountWallet(db, owner.Account, tx).RentalPoints);
        if (luckyStar.Granted)
            committed = committed with { Inventory = committed.Inventory with { AccountWallet = ReadAccountWallet(db, owner.Account, tx) } };
        uint attendanceMail = prepared.DailyAttendance is { } attendance
            ? ApplyDailyAttendance(db, tx, owner.Account, committed, attendance, claim: false).MailId : 0;
        tx.Commit();
        return new DungeonSettlementCommit(committed, false, reward)
        {
            DailyAttendanceMailId = attendanceMail,
            GainedExperience = checked((uint)plan.GainedExperience),
            ProficiencyGained = proficiencyGained,
            Adventure = adventureCommit,
            EndlessAdventureAmount = endlessAdventureAmount,
            LuckyStarGained = luckyStar.Granted,
            LuckyStarTotal = luckyStar.Total,
            GuildMileageGuildId = guildMileageGuildId,
            GuildContribution = guildContribution,
            GuildDungeon = guildDungeon,
            Card = prepared.Card, Options = prepared.Options,
            GriefReward = prepared.Grief?.Reward, MailId = guildReward.Mail ?? griefMail ?? bossRushMail,
            BossRushReward = prepared.BossRush,
            BlackDiamond = prepared.Options?.BlackDiamond,
            Inventory = prepared.AncientGate is not null || prepared.Grief?.Delivery.Inventory.Changed == true ||
                prepared.BossRush?.Delivery.Inventory.Changed == true || creatureInventory is { Changed: true } || endlessAdventureAmount > 0
                ? new(expected.Inventory, committed.Inventory, prepared.Grief is not null ? "grief_tower_reward" :
                    prepared.BossRush is not null ? "boss_rush_reward" : endlessAdventureAmount > 0 ? "endless_adventure" : "creature_experience") : null
        };
    });

    static DungeonLuckyStarGrant GrantDungeonLuckyStar(SqliteConnection db, SqliteTransaction tx, string account, int limit)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.CommandText = "INSERT INTO account_wallets(account) VALUES($account) ON CONFLICT(account) DO NOTHING";
        command.ExecuteNonQuery();
        var wallet = ReadAccountWallet(db, account, tx);
        var grant = DungeonLuckyStarPolicy.Apply(wallet, limit);
        if (!grant.Granted) return grant;
        command.Parameters.AddWithValue("$revision", wallet.Revision);
        command.Parameters.AddWithValue("$points", grant.Total);
        command.CommandText = "UPDATE account_wallets SET revision=revision+1,rental_points=$points WHERE account=$account AND revision=$revision AND rental_points < $points";
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("通关幸运星账号钱包版本变化");
        return grant;
    }

    static DungeonSettlementCommit ReadCardCommit(SqliteConnection db, SqliteTransaction tx, string account,
        PreparedDungeonSettlement prepared, Character current, Character expected, bool duplicate, uint gained, uint proficiencyGained)
    {
        if (prepared.Card is null) return new(current, duplicate, prepared.Reward)
            { GainedExperience = gained, ProficiencyGained = proficiencyGained };
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
            GainedExperience = gained, ProficiencyGained = proficiencyGained,
            Card = card, MailId = mail, Options = prepared.Options, BlackDiamond = bonus,
            Inventory = null
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
