using System.Collections.Frozen;
using System.Text.RegularExpressions;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 只解析本版商城引用的契约和指定任务助手目录，不扫描全部物品内容。
internal sealed class DevilContractCatalog
{
    public FrozenDictionary<int, int[]> PackageSlots { get; }
    public FrozenDictionary<int, RecoveryConsumableDefinition> Assistants { get; }
    public int[] RecoveryPrices { get; }

    public DevilContractCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> stackable, CashShopCatalog shop)
    {
        var slots = shop.Products.Values.Where(row => row.Section == "selectable character premium")
            .GroupBy(row => row.ItemId).ToDictionary(group => group.Key, group => group.Select(row => row.ContractType - 580).Distinct().Single());
        var packages = new Dictionary<int, int[]>();
        foreach (var product in shop.Products.Values.Where(row => row.Section == "charac premium package"))
        {
            if (!stackable.TryGetValue(product.ItemId, out var path)) continue;
            string text = archive.ReadText("stackable/" + path);
            int[] pairs = PvfScript.Integers(PvfScript.Block(PvfScript.Block(text, "booster info"), "cera"));
            if (pairs.Length == 0 || pairs.Length % 2 != 0) continue;
            var resolved = new List<int>();
            for (int i = 0; i < pairs.Length; i += 2)
            {
                if (!slots.TryGetValue(pairs[i], out int slot) || slot is < 0 or > 7 || pairs[i + 1] != 1)
                    throw new InvalidDataException("魔王契约套餐包含未知子项或数量");
                resolved.Add(slot);
            }
            if (resolved.Distinct().Count() != resolved.Count) throw new InvalidDataException("魔王契约套餐子项重复");
            packages[product.ItemId] = resolved.ToArray();
        }
        PackageSlots = packages.ToFrozenDictionary();
        var assistants = new Dictionary<int, RecoveryConsumableDefinition>();
        foreach (var (id, path) in stackable.Where(pair => pair.Value.StartsWith("cash/chn_contract_devil/chn_quest_helper_", StringComparison.OrdinalIgnoreCase)))
        {
            string text = archive.ReadText("stackable/" + path);
            int[] summon = PvfScript.Integers(PvfScript.Section(text, "summon apc"));
            if (summon.Length != 5 || summon[0] <= 0 || summon[3] <= 0 || summon[4] != 1 ||
                !Regex.IsMatch(PvfScript.Section(text, "stackable type"), @"\[waste\]`\s+0"))
                throw new InvalidDataException("任务助手召唤道具声明无效: " + path);
            assistants.Add(id, new(id, Number(text, "minimum level"), Number(text, "cool time"),
                Number(text, "cooltime group"), null, null, SummonsApc: true));
        }
        Assistants = assistants.ToFrozenDictionary();
        string parameters = archive.ReadText(archive.Contains("etc/(r)serverparameter.etc") ? "etc/(r)serverparameter.etc" : "etc/serverparameter.etc");
        RecoveryPrices = PvfScript.Integers(PvfScript.Section(parameters, "stamina recovery cost"));
        if (RecoveryPrices.Length == 0 || RecoveryPrices.Any(price => price < 0)) throw new InvalidDataException("PVF虚弱恢复价格表无效");
    }

    static int Number(string text, string tag) => PvfScript.Integers(PvfScript.Section(text, tag)).Single();
}
