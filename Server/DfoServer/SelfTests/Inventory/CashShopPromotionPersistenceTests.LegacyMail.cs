using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static partial class CashShopPromotionPersistenceTests
{
    public static void RunLegacyMailCore()
    {
        int before = Count;
        using var f = PageFixture();
        var plan = f.Plan(4);
        That(plan.Awards.Select(award => award.PurchaseNumber).SequenceEqual([2L, 3L, 4L]) &&
            plan.Deliveries.Length == 3 && plan.Deliveries.All(delivery => delivery.Items.Length == 1),
            "四套新订单先生成第2至4档的三封单附件赠礼");
        var committed = f.Store.Commit(f.Owner, plan, f.Now);
        f.Character = committed.Character;
        That(committed.MailIds.Length == 3, "四套订单提交三封独立赠礼邮件");

        uint legacyMailId = committed.MailIds[0];
        string ids = string.Join(",", committed.MailIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
        var business = JsonNode.Parse(ReadPromotionBusiness(f, plan.Order.Id))!.AsObject();
        var deliveries = business["Deliveries"]!.AsArray();
        That(deliveries.Count == 3, "新订单冻结快照包含三份真实投递记录");
        var mergedDelivery = deliveries[0]!.DeepClone().AsObject();
        mergedDelivery["Items"] = MergeItems(deliveries.Select(delivery => delivery!).ToArray());
        string firstKey = mergedDelivery["Key"]!.GetValue<string>();
        business["Deliveries"] = new JsonArray(mergedDelivery);
        string mergedBusiness = business.ToJsonString();

        string mergedContent = MergeStoredItems(f.Sql, "mailbox_messages", "delivery_content", committed.MailIds);
        string mergedAudit = MergeStoredItems(f.Sql, "mailbox_audit_log", "assets", committed.MailIds,
            "action_name='system'");
        f.Sql.Execute($"UPDATE cash_shop_promotion_orders SET business_snapshot={SqlText(mergedBusiness)}," +
            $"mail_ids={SqlText(JsonSerializer.Serialize(new[] { legacyMailId }))} WHERE order_id={SqlText(plan.Order.Id.ToString("N"))}");
        f.Sql.Execute($"UPDATE mailbox_messages SET delivery_content={SqlText(mergedContent)} WHERE mail_id={legacyMailId}");
        f.Sql.Execute($"UPDATE mailbox_attachments SET mail_id={legacyMailId},ordinal=CASE mail_id " +
            string.Join(" ", committed.MailIds.Select((id, index) =>
                $"WHEN {id.ToString(CultureInfo.InvariantCulture)} THEN {index.ToString(CultureInfo.InvariantCulture)}")) +
            $" ELSE ordinal END WHERE mail_id IN ({ids})");
        f.Sql.Execute($"UPDATE cash_shop_promotion_awards SET mail_id={legacyMailId} WHERE order_id={SqlText(plan.Order.Id.ToString("N"))}");
        f.Sql.Execute($"UPDATE mailbox_audit_log SET assets={SqlText(mergedAudit)} WHERE mail_id={legacyMailId} AND action_name='system'");
        f.Sql.Execute($"DELETE FROM mailbox_audit_log WHERE mail_id IN ({string.Join(",", committed.MailIds.Skip(1))})");
        f.Sql.Execute($"DELETE FROM mailbox_messages WHERE mail_id IN ({string.Join(",", committed.MailIds.Skip(1))})");

        That((string)f.Sql.Query($"SELECT delivery_key FROM mailbox_messages WHERE mail_id={legacyMailId}")! == firstKey &&
            f.Sql.Query($"SELECT count(*) FROM mailbox_messages WHERE mail_id IN ({ids})") is 1L &&
            f.Sql.Query($"SELECT count(*) FROM mailbox_attachments WHERE mail_id={legacyMailId} AND ordinal BETWEEN 0 AND 2") is 3L &&
            f.Sql.Query($"SELECT count(*) FROM cash_shop_promotion_awards WHERE order_id={SqlText(plan.Order.Id.ToString("N"))} AND mail_id={legacyMailId}") is 3L,
            "旧格式将三个真实附件和奖项归并到首封原ID，删除其余空邮件");

        var page = PageRead(f);
        That(page.Verified && page.Progress == committed.Progress && page.Progress.Count == 4 &&
            page.AwardMask27 == 7 && page.OverflowAwardCount == 0,
            "原十档页面读取核验旧四套一封三附件快照及2至4档位");
        string beforeRestore = f.Sql.Snapshot();
        var restored = f.Store.RestoreCommitted(f.Owner, plan.Order.Id);
        That(restored is not null && restored.Progress == committed.Progress &&
            restored.MailIds.SequenceEqual([legacyMailId]) && f.Sql.Snapshot() == beforeRestore,
            "原订单GUID恢复单一旧mailId且不补发或改动任何资产");

        f.Sql.Receiver = f.Character;
        var mail = f.Store.LoadMailbox(f.Owner, f.Character.Id, f.Now.ToUnixTimeSeconds()).Entries.Single();
        var expectedItems = mail.Attachments.Select(attachment => attachment.Item).ToArray();
        That(mail.Id == legacyMailId && mail.Attachments.Length == 3 &&
            expectedItems.Select(item => item.Core.ItemId).SequenceEqual([490705441, 490705442, 490705443]) &&
            expectedItems.All(item => item.Uid > 0) && expectedItems.Select(item => item.Uid).Distinct().Count() == 3,
            "一封旧赠礼邮件实际承载三档各自的持久实例UID");
        That(f.Sql.Claim([legacyMailId]).Accepted, "原邮件整封领取成功");
        f.Character = f.Sql.Receiver;
        That(expectedItems.All(item => f.Character.Inventory.Items.Any(actual => actual.Uid == item.Uid && actual.Core.ItemId == item.Core.ItemId)) &&
            f.Sql.Query($"SELECT count(*) FROM mailbox_attachments WHERE mail_id={legacyMailId} AND claimed=0") is 0L,
            "整封领取将三个原UID实例逐一转入角色库存且不遗留未领取附件");
        That(f.Store.ChangeMailState(f.Owner, f.Character.Id, [legacyMailId], 0,
            f.Now.ToUnixTimeSeconds()).Accepted, "三档领取后按原邮件路径删除已领取邮件");
        var afterClaim = PageRead(f);
        That(afterClaim.Verified && afterClaim.Progress == committed.Progress &&
            afterClaim.AwardMask27 == 7 && afterClaim.OverflowAwardCount == 0,
            "旧合封邮件领取并删除后仍以已提交账本核验全部三个奖档");
        Console.WriteLine($"Cash shop legacy promotion mail: {Count - before} checks passed.");
    }

    static string ReadPromotionBusiness(Fixture f, Guid orderId) =>
        (string)f.Sql.Query($"SELECT business_snapshot FROM cash_shop_promotion_orders WHERE order_id={SqlText(orderId.ToString("N"))}")!;

    static JsonArray MergeItems(IReadOnlyList<JsonNode> documents)
    {
        var merged = new JsonArray();
        foreach (var document in documents)
        {
            var items = document["Items"]?.AsArray() ?? throw new InvalidDataException("存储投递记录缺Items");
            if (items.Count != 1) throw new InvalidDataException("购物车单套投递应恰有一个附件");
            merged.Add(items[0]!.DeepClone());
        }
        return merged;
    }

    static string MergeStoredItems(MailboxPersistenceTests.Fixture sql, string table, string column,
        ImmutableArray<uint> mailIds, string filter = "1=1")
    {
        var documents = new List<JsonNode>(mailIds.Length);
        foreach (uint id in mailIds)
        {
            string json = (string)sql.Query($"SELECT {column} FROM {table} WHERE mail_id={id.ToString(CultureInfo.InvariantCulture)}" +
                $" AND {filter}")!;
            documents.Add(JsonNode.Parse(json)!);
        }
        var merged = documents[0]!.DeepClone().AsObject();
        merged["Items"] = MergeItems(documents);
        return merged.ToJsonString();
    }

    static string SqlText(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
