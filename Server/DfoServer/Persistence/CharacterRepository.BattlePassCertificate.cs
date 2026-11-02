using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository : IBattlePassCertificateStore
{
    public BattlePassCertificateCommit CommitBattlePassCertificate(AccountOwners.Lease owner, Character expected,
        BattlePassCertificatePlan plan, DateTimeOffset now) => owner.Execute(() =>
    {
        plan.Before.Validate();
        if (battlePassMissionPolicy is not { } policy || policy.SeasonKey != plan.Before.SeasonKey ||
            plan.Before.PassType != 0 || policy.Active(now).IsEmpty || now < plan.PlannedAt || plan.OrderId == Guid.Empty ||
            !BattlePassCertificatePurchase.IsRequest(plan.Request) || plan.Inventory.Reason != "cash_shop_buy" ||
            plan.Inventory.CharacterPrecondition is null || plan.Receipt.Products is not
            [{ CommodityId: BattlePassCertificatePurchase.Commodity, ItemId: BattlePassCertificatePurchase.Item,
                Count: 1, CeraPrice: BattlePassCertificatePurchase.Cera, GoldPrice: 0, OtherPrice: 0, Section: "package", IsMileage: false }])
            throw new BattlePassCertificateChangedException();
        var before = plan.Inventory.Before; var after = plan.Inventory.After;
        if (CurrencyWallet.Get(before, CurrencyKind.Cera) - CurrencyWallet.Get(after, CurrencyKind.Cera) != BattlePassCertificatePurchase.Cera ||
            CurrencyWallet.Get(before, CurrencyKind.TokenCera) != CurrencyWallet.Get(after, CurrencyKind.TokenCera) ||
            CurrencyWallet.Get(before, CurrencyKind.HappyTokenCera) != CurrencyWallet.Get(after, CurrencyKind.HappyTokenCera) ||
            !before.Items.Where(row => row.Core.ItemId == BattlePassCertificatePurchase.Item).SequenceEqual(after.Items.Where(row => row.Core.ItemId == BattlePassCertificatePurchase.Item)) ||
            plan.Inventory.OverflowItems.Any(row => row.Core.ItemId == BattlePassCertificatePurchase.Item))
            throw new BattlePassCertificateChangedException();
        using var db = database.Open(); using var write = db.BeginWrite("CharacterRepository.CommitBattlePassCertificate");
        using var tx = write.Transaction;
        // 等待原SQLite writer期间可能跨季结束；获取writer后重新采样同一服务端时钟。
        now = plan.Clock.GetUtcNow();
        if (now < plan.PlannedAt || policy.Active(now).IsEmpty) throw new BattlePassCertificateChangedException();
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", owner.Account); command.Parameters.AddWithValue("$season", policy.SeasonKey);
        command.CommandText = "SELECT rules_json FROM battle_pass_season_rules WHERE season_key=$season";
        if (command.ExecuteScalar() is not string rules || rules != policy.RulesJson) throw new BattlePassCertificateChangedException();
        var current = ReadBattlePassMissionState(db, tx, owner.Account, policy, now);
        if (!current.Active || current.Season.PassType != 0 || current.Season.Revision != plan.Before.Revision ||
            current.Season.TotalExperience != plan.Before.TotalExperience || !current.Season.Claims.SetEquals(plan.Before.Claims))
            throw new BattlePassCertificateChangedException();
        command.CommandText = "SELECT 1 FROM account_battle_pass_certificate_orders WHERE account=$account AND season_key=$season";
        if (command.ExecuteScalar() is not null) throw new BattlePassCertificateChangedException();
        // 原库存唯一实现承担余额实读/版本/期限/event、UID、附赠/邮件与钱包审计；仅参与此同库事务。
        var character = CommitInventoryInTransaction(db, tx, owner, expected, plan.Inventory);
        // 通用库存同事务已记真实消费XP；资格仍核原快照，只合并本事务的新季版本。
        var paidState = ReadBattlePassMissionState(db, tx, owner.Account, policy, now);
        if (!paidState.Active || paidState.Season.PassType != 0 ||
            !paidState.Season.Claims.SetEquals(current.Season.Claims)) throw new BattlePassCertificateChangedException();
        long next = checked(paidState.Season.Revision + 1);
        command.CommandText = "UPDATE account_battle_pass_seasons SET pass_type=1,revision=$next WHERE account=$account AND season_key=$season AND pass_type=0 AND revision=$before";
        command.Parameters.AddWithValue("$next", next); command.Parameters.AddWithValue("$before", paidState.Season.Revision);
        if (command.ExecuteNonQuery() != 1) throw new BattlePassCertificateChangedException();
        command.CommandText = """
            INSERT INTO account_battle_pass_certificate_orders
              (account,season_key,order_id,commodity_id,item_id,cera,recipient_character_id,
               before_season_revision,resulting_season_revision,business_snapshot,committed_at)
            VALUES($account,$season,$order,105659,490705043,4900,$character,$original,$next,$business,$at)
            """;
        command.Parameters.AddWithValue("$original", current.Season.Revision);
        command.Parameters.AddWithValue("$order", plan.OrderId.ToString("N")); command.Parameters.AddWithValue("$character", character.Id);
        command.Parameters.AddWithValue("$business", JsonSerializer.Serialize(new { plan.Request, plan.Receipt }));
        command.Parameters.AddWithValue("$at", now.ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        // type1使已有真实高级任务进度重新计算ready，不新写任务source/XP或领取事实。
        var committed = ReadBattlePassMissionState(db, tx, owner.Account, policy, now);
        tx.Commit();
        return new BattlePassCertificateCommit(character, committed, plan.OrderId);
    });
}
