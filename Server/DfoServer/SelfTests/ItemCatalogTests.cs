using static DfoServer.Testing.TestCheck;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer;

internal static class ItemCatalogTests
{
    public static void Run(PvfArchive pvf)
    {
        var catalog = StarterEquipmentCatalog.Load(pvf);
        EquipmentCatalogTests.Run(pvf, catalog.Resources);
        int[] weapons = [101010912, 102040571, 104010559, 106020596, 108000525, 104010559, 109000493, 102040571,
            106020596, 101010912, 106040493, 101010912, 101030782, 114000248, 108000525, 116000011];
        int checks = Count;
        for (byte job = 0; job < 16; job++)
        {
            var items = catalog.GetStarterEquipment(job);
            That(items.Count == 5 && items.Single(item => item.Slot == StarterEquipmentSlot.Weapon).TemplateId == weapons[job], "职业初始武器");
            That(items.Single(item => item.Slot == StarterEquipmentSlot.Coat).TemplateId == 10400, "初始上衣");
            That(items.Single(item => item.Slot == StarterEquipmentSlot.Pants).TemplateId == 12400, "初始下装");
            That(items.Single(item => item.Slot == StarterEquipmentSlot.Waist).TemplateId == 16400, "初始腰带");
            That(items.Single(item => item.Slot == StarterEquipmentSlot.Shoes).TemplateId == 18400, "初始鞋");
            foreach (var item in items)
            {
                var definition = catalog.GetEquipment(item.TemplateId);
                That(definition.MinimumLevel == 1 && definition.MaximumDurability > 0, "模板等级及耐久上限");
                That(pvf.Contains(definition.ScriptPath), "模板资源存在");
            }
        }
        That(catalog.GetEquipment(10400).MaximumDurability == 33, "上衣模板耐久");
        That(catalog.GetEquipment(12400).MaximumDurability == 27, "下装模板耐久");
        That(catalog.GetEquipment(16400).MaximumDurability == 20 && catalog.GetEquipment(18400).MaximumDurability == 20, "腰带及鞋模板耐久");
        try
        {
            catalog.GetStarterEquipment(16);
            throw new Exception("FAIL: 未知职业被接受");
        }
        catch (InvalidDataException)
        {
            checks++;
        }
        string[] labels = ["[create equipment list]", "[weapon]", "[coat]", "[pants]", "[waist]", "[shoes]", "[/create equipment list]", "[unknown]"];
        PvfToken[] valid = [new(3, 0), new(6, 1), new(0, 1), new(6, 2), new(0, 2), new(6, 3), new(0, 3), new(6, 4), new(0, 4), new(6, 5), new(0, 5), new(3, 6)];
        That(StarterEquipmentCatalog.ParseStarterEquipment(valid, id => labels[id]).Count == 5, "独立声明解析");
        Reject(valid[..^1], "缺结束标记");
        Reject([.. valid, new(3, 0)], "重复块");
        Reject([.. valid, new(3, 6)], "重复结束标记");
        Reject([.. valid[..9], new(3, 6)], "短声明");
        Reject([.. valid[..^1], new(6, 1), new(0, 6), new(3, 6)], "长声明");
        var duplicate = valid.ToArray();
        duplicate[3] = new(6, 1);
        Reject(duplicate, "重复领域槽位");
        var unknown = valid.ToArray();
        unknown[1] = new(6, 7);
        Reject(unknown, "未知领域槽位");
        var negative = valid.ToArray();
        negative[2] = new(0, -1);
        Reject(negative, "负模板 ID");
        var wrongType = valid.ToArray();
        wrongType[2] = new(6, 1);
        Reject(wrongType, "模板 token 类型变化");

        void Reject(PvfToken[] tokens, string name)
        {
            try
            {
                StarterEquipmentCatalog.ParseStarterEquipment(tokens, id => labels[id]);
                throw new Exception("FAIL: " + name);
            }
            catch (InvalidDataException)
            {
                checks++;
            }
        }

        Console.WriteLine($"PASS {Count - checks} item catalog assertions");

    }
}
