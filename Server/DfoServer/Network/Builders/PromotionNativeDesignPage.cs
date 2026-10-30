using System.Globalization;
using System.Text;
using DfoServer.Game.CashShopPromotions;

namespace DfoServer.Network.Builders;

// 只读活动页使用已提交快照；不推进累计、不授予购买资格或奖励。
internal static class PromotionNativeDesignPage
{
    public const ushort EventId = 10999;

    public static EventInfoDesignPage? Create(PromotionPageSnapshot snapshot)
    {
        if (snapshot is null || snapshot.Campaign != "spring-2020" || snapshot.Period != "replica-1" ||
            snapshot.RulesVersion != "v1" || snapshot.Realm != "default" || snapshot.Group != "package" ||
            snapshot.CatalogRevision < 0 || snapshot.ProgressRevision is < 0 || snapshot.Count is < 0 ||
            snapshot.Count.HasValue != snapshot.ProgressRevision.HasValue || snapshot.MaximumPurchases != 5000)
            return null;

        if (snapshot.Status == "unavailable")
        {
            if (snapshot.AwardMask27 is not null || snapshot.OverflowAwardCount is not null ||
                !snapshot.Rewards.IsDefaultOrEmpty || snapshot.OverflowReward is not null) return null;
            string known = snapshot.Count?.ToString(CultureInfo.InvariantCulture) ?? "未知";
            // 缺少已核奖项时只显示状态；两文本落在原生初始窗口内部。
            return Page("[text_control] 2 ``` 累计：" + known +
                " ``` 8 40 `COLOR_WHITE` ``` 奖励状态暂不可核，请稍后重开活动页 ``` 8 65 `COLOR_WHITE`\n");
        }

        if (snapshot.Status is not ("ready" or "disabled") || snapshot.Count is not long count ||
            snapshot.AwardMask27 is not uint mask || snapshot.OverflowAwardCount is not long overflow ||
            snapshot.Rewards.IsDefault || snapshot.Rewards.Length != 9 ||
            snapshot.OverflowReward is not { ItemId: 490705415, Count: 1 }) return null;
        uint expectedMask = 0;
        for (int round = 0; round < 3; round++)
            for (int tier = 2; tier <= 10; tier++)
                if (count >= tier + round * 10) expectedMask |= 1u << (round * 9 + tier - 2);
        if (mask != expectedMask || overflow != (count > 30 ? count - 30 : 0)) return null;
        for (int index = 0; index < 9; index++)
        {
            var reward = snapshot.Rewards[index];
            if (reward is null || reward.Count != 1 || reward.ItemId != 490705441 + index) return null;
        }

        int[] columns = [54, 125, 196], rows = [135, 203, 279];
        var content = new StringBuilder("[image_with_itemcontrol] `Interface2/cs_shop/cs_calendar.img` 12 9");
        for (int index = 0; index < 9; index++)
        {
            var reward = snapshot.Rewards[index];
            content.Append(CultureInfo.InvariantCulture,
                $" {reward.ItemId} {reward.Count} {columns[index % 3]} {rows[index / 3]}");
        }
        string digits = count.ToString(CultureInfo.InvariantCulture);
        string next = count >= snapshot.MaximumPurchases ? "已达累计上限" :
            "距下档" + (count < 30 && count % 10 == 0 ? "2" : "1") + "套";
        content.Append('\n').Append("[text_control] 2 ``` ").Append(digits.Length <= 5 ? digits : "见下")
            .Append(" ``` 196 344 `COLOR_WHITE` ``` ");
        if (snapshot.Status == "disabled") content.Append("活动未开启 ");
        content.Append("累计：").Append(digits).Append(' ').Append(next).Append(" ``` 8 445 `COLOR_WHITE`\n");
        return Page(content.ToString());
    }

    static EventInfoDesignPage Page(string content) => new(EventId, 1, "", "", 2, 4,
        "众神降世礼包 多重购买有惊喜", "众神降世礼包", "", 0, 0, "design popup window/2/0", content, true);
}
