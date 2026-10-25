using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    Character ReadDungeonGrowthCharacter(SqliteConnection db, SqliteTransaction tx, string account,
        Character expected, QuestDropPlan? drops)
    {
        // 命中掉落声明但未中奖/已满上限也依赖库存；寻物任务会在击杀末尾按实时持有量刷新。
        // 保守回退原完整读取，不让空奖励或同revision的工具修改绕过资产前提。
        if (drops is { ReadsInventory: true } || drops?.Inventory.Changed == true ||
            drops is not null && quests is not null && expected.ActiveMainQuests.Any(active =>
                quests.TryGet(active.QuestId, out var quest) && quest.Condition is "[seeking]" or "[seek n meet npc]"))
            return Require(db, tx, account, expected.Name);

        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.Parameters.AddWithValue("$account", account);
        query.Parameters.AddWithValue("$name", expected.Name);
        query.CommandText = """
            SELECT c.*,COALESCE(w.revision,0) AS wallet_revision,COALESCE(a.revision,0) AS cargo_revision,
              COALESCE(e.revision,0) AS epic_revision
            FROM characters c LEFT JOIN account_wallets w ON w.account=c.account
            LEFT JOIN account_cargo a ON a.account=c.account LEFT JOIN account_epic_books e ON e.account=c.account
            WHERE c.account=$account AND c.name=$name
            """;
        Character stored;
        bool reload;
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidDataException("角色不存在");
            stored = Read(reader);
            long Value(string name) => reader.GetInt64(reader.GetOrdinal(name));
            var inventory = expected.Inventory;
            // 正常角色资产写入推进角色revision，共享资产另有账号版本。任一不符都读实际状态再复验。
            reload = stored.Id != expected.Id || stored.Revision != expected.Revision ||
                Value("wallet_revision") != inventory.AccountWallet.Revision ||
                Value("cargo_revision") != inventory.AccountCargo.Revision || Value("epic_revision") != inventory.EpicPieces.Revision ||
                Value("gold") != inventory.Gold || Value("revive_coins") != inventory.ReviveCoins ||
                Value("win_points") != inventory.WinPoints || Value("gold_carry_limit") != inventory.GoldCarryLimit ||
                Value("auction_gold_limit") != inventory.AuctionGoldLimit || Value("inventory_expansion") != inventory.Expansion ||
                Value("avatar_expansion") != inventory.AvatarExpansion || Value("cargo_capacity") != inventory.CargoCapacity ||
                Value("stamina_until") != inventory.StaminaUntil || stored.EquipmentSlotFlags != expected.EquipmentSlotFlags;
        }
        if (reload) return Require(db, tx, account, expected.Name);

        // 未变资产沿用本owner的在线快照，不新增缓存。GM进度不保证推进revision，相关前提仍逐项实读。
        var current = expected with
        {
            Slot = stored.Slot, Name = stored.Name, Position = stored.Position, Return = stored.Return,
            Job = stored.Job, GrowType = stored.GrowType, Level = stored.Level,
            AcceptedStoryLevel = stored.AcceptedStoryLevel, QuestCycleDay = stored.QuestCycleDay,
            Emotion = stored.Emotion, CloneTitleItemId = stored.CloneTitleItemId, BlacklistCount = stored.BlacklistCount,
            Learning = ReadLearning(db, expected.Id, tx), Skills = ReadCharacterSkills(db, expected.Id, tx),
            ActiveMainQuests = ReadActiveMainQuests(db, expected.Id, tx), DungeonState = ReadDungeonState(db, expected.Id, tx),
            // 公会可由其他成员改变，不能用本角色revision证明其未变；此处保留单成员查询。
            Guild = ReadGuildMembership(db, expected.Id, tx)
        };
        return ReadQuestProgress(db, current, tx);
    }
}
