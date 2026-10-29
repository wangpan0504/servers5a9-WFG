using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class MileageShopFileTests
{
    public static void Run()
    {
        const string normal = "[specific product mileage]\n10 7 11 0\n[/specific product mileage]\n" +
            "[mileage rate by category]\n`item etc` 3 `package` 1\n[/mileage rate by category]\n[buy only cera]\n20 20 21\n[/buy only cera]";
        const string shop = "[item]\n100 200 10 0 50 0 `10个` 101 0\n[/item]\n" +
            "[purchasing limit]\n100 2 0 0 0 0\n[/purchasing limit]\n[limit mileage by cash]\n20000\n[/limit mileage by cash]";
        var file = new MileageShopFile(normal, shop);
        That(file.Items.Single() == new MileageShopEntry(100, 200, 10, 0, 50, 0, 101, 0), "积分商城九列保留数量、积分价及分类，不读取字符串中的数字");
        That(file.Limits.Single() == new MileageShopLimit(100, 2, 0, 0, 0, 0), "六列限购原值保留，不猜测周期");
        That(file.SpecificMileage[10] == 7 && file.SpecificMileage[11] == 0 && file.CategoryPercent["item etc"] == 3 &&
            file.CeraOnly.SetEquals(new[] { 20, 21 }) && file.CashMileageLimit == 20000, "指定零返分、分类百分比、仅点券名册及消费额度独立解析");
        That(new MileageShopFile("", null).Items.Count == 0, "可选积分商城缺席不制造商品");
        foreach (string invalid in new[]
        {
            shop.Replace("100 200 10", "100 200 -1"), shop.Replace("0 50 0", "0 -1 0"),
            shop.Replace("`10个` 101 0", "`10个` 101"), shop.Replace("[/item]", ""),
            shop.Replace("[/item]", "100 200 10 0 50 0 `重复` 101 0\n[/item]"),
            shop.Replace("100 2 0 0 0 0", "100 0 0 0 0 0"), shop.Replace("20000", "-1")
        })
        {
            bool rejected = false;
            try { _ = new MileageShopFile(normal, invalid); } catch (InvalidDataException) { rejected = true; }
            That(rejected, "畸形、负数、重复或未闭合的积分声明拒绝");
        }
    }
}
