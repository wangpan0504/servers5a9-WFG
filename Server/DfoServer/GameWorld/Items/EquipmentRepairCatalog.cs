using System.Globalization;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed class EquipmentRepairCatalog
{
    readonly float repairRate;
    readonly float quickRate;
    readonly float[] upgradeRates;

    public EquipmentRepairCatalog(PvfArchive pvf)
    {
        string prices = pvf.ReadText("equipment/pricetable.tbl");
        repairRate = PvfScript.Number(prices, "repair cost", float.NaN);
        quickRate = PvfScript.Number(prices, "quick repair cost rate", float.NaN) / 100;
        upgradeRates = PvfScript.Section(pvf.ReadText("etc/upgrade.etc"), "repair cost rate by upgrade level")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => float.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        if (!float.IsFinite(repairRate) || repairRate < 0 || !float.IsFinite(quickRate) || quickRate < 0 ||
            upgradeRates.Length == 0 || upgradeRates.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("PVF修理费用表无效");
    }

    public bool TryPrice(ItemTemplate template, ushort durability, byte upgrade, bool quick, out int cost)
    {
        cost = 0;
        if (template.RepairPrice <= 0 || template.Durability == 0 || durability >= template.Durability)
            return true;
        float rate = upgradeRates[Math.Min(upgrade, upgradeRates.Length - 1)];
        if (rate <= 0)
            rate = 1;
        // 等级折算先做整数除法，其余保持PVF单精度运算及逐件截断；中间乘法不能溢出为免费。
        long scaled = (long)template.RepairPrice * ((long)template.Grade + 5) / 10;
        float price = (float)scaled * repairRate / template.Durability * (template.Durability - durability) * rate;
        if (quick)
            price *= quickRate;
        if (!float.IsFinite(price) || price < 0 || (double)price > int.MaxValue)
            return false;
        cost = (int)price;
        return true;
    }
}
