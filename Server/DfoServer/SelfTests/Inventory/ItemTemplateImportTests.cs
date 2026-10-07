using DfoServer.Infrastructure;
using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using PvfLib;

namespace DfoServer.Testing.Inventory;

internal static class ItemTemplateImportTests
{
    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            checks++;
            if (!value)
                throw new InvalidOperationException("FAIL item template import: " + name);
        }
        void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, name);
        }
        var scripts = new Dictionary<string, string>
        {
            ["equipment/base.equ"] = "[name]\n`Base`\n[value]\n500\n[durability]\n28\n[usable period]\n14\n" +
                "[expiration date]\n`2026-12-01`\n[need material]\n3037 2\n[/need material]\n[price]\n100\n[add price]\n-100\n",
            ["equipment/middle.equ"] = "[value]\n0\n[usable period]\n0\n[expiration date]\n0\n[import script]\n`base.equ`\n",
            ["equipment/child.equ"] = "[name]\n`Child`\n[import script]\n`./MIDDLE.equ`\n[impossible contents]\n`disjoint`\n[/impossible contents]\n"
        };
        string Read(string path) => scripts.TryGetValue(path, out string? text) ? text : throw new FileNotFoundException(path);
        var resolved = EquipmentFile.ReadScript(Read, "equipment/child.equ");
        Check(resolved.Section("name").Trim() == "`Child`" && resolved.Section("durability").Trim() == "28" &&
            resolved.Section("value").Trim() == "0", "root-relative normalized chained imports keep child declarations before and after import");
        Check(ItemExpiration.Parse(resolved) == new ItemExpiration(), "explicit zero overrides inherited relative and absolute expiry");
        var price = ItemShopPrice.Parse(resolved, true, 200);
        Check(price.Gold == 0 && price.SellGold == 0 && price.Materials.SequenceEqual(new ItemMaterialCost[] { new(3037, 2) }),
            "inherited shop materials and signed price adjustment coexist with local zero sale value");
        scripts["equipment/child.equ"] = "[name]\n\n[import script]\n`middle.equ`\n";
        Check(EquipmentFile.ReadScript(Read, "equipment/child.equ").Section("name") == "", "empty local field is not a missing declaration or the following tag");
        foreach (string reference in new[] { "`missing.equ`", "`./CHILD.equ`", "", "`base.equ` `middle.equ`", "`base.equ`\n[import script]\n`middle.equ`" })
        {
            scripts["equipment/child.equ"] = "[import script]\n" + reference + "\n";
            Reject(() => EquipmentFile.ReadScript(Read, "equipment/child.equ"), "missing, recursive or malformed import cannot create a partial template");
        }
        scripts["equipment/child.equ"] = "[import script]\n`middle.equ`\n";
        scripts["equipment/base.equ"] = "[import script]\n`child.equ`\n";
        Reject(() => EquipmentFile.ReadScript(Read, "equipment/child.equ"), "indirect normalized cycle fails before template publication");

        var catalog = itemResources.Items;
        var template = catalog.Get(100050791);
        Check(template is { Id: 100050791, Name: "name_100050791", Kind: ItemKind.Equipment, EquipmentSlot: 14,
            MinimumLevel: 85, Durability: 28, StackLimit: 1, Rarity: 6, Grade: 90, Weight: 1400, RepairPrice: 64800 },
            "matched temporary coat keeps its own ID and inherits the complete base equipment definition");
        Check(template.Expiration == new ItemExpiration(Days: 14) && template.ShopPrice.SellGold == 0 && template.ShopPrice.Gold == 0 &&
            template.ShopPrice.Materials.SequenceEqual(new ItemMaterialCost[] { new(10088421, 600) }) &&
            template.DisjointForbidden && !template.PersonalCargoForbidden && !template.AccountCargoForbidden,
            "matched temporary overrides preserve expiry, zero price and PVF disjoint rule without extra cargo bans");
        Check(catalog.Get(100050666).Expiration == new ItemExpiration() && catalog.Get(100050666).ShopPrice.SellGold > 0 &&
            !catalog.Get(100050666).DisjointForbidden, "loading a derived item never mutates its permanent parent");
        int imported = 0;
        var indexed = PvfScript.List(pvf.ReadText("equipment/equipment.lst"))
            .ToLookup(row => "equipment/" + row.Value, StringComparer.OrdinalIgnoreCase);
        var importTags = new Dictionary<int, bool>();
        // 全索引发现样本时按块读取，不把所有装备文本和解压块永久留在在线查询缓存中。
        pvf.ScanRawFiles(indexed.Select(group => pvf.FindFile(group.Key)).OfType<PvfFile>(), (file, read) =>
        {
            byte[] raw = read();
            bool hasImport = file.DataType == 3 && new PvfDocument(Encoding.Unicode.GetString(raw)).Has("import script");
            if (file.DataType is not (1 or 3) || file.DataType == 1 && raw.Length % 5 != 0)
                throw new InvalidDataException("装备样本扫描遇到非法脚本格式");
            for (int offset = 0; file.DataType == 1 && !hasImport && offset < raw.Length; offset += 5)
            {
                if (raw[offset] != 3)
                    continue;
                int key = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(offset + 1));
                if (!importTags.TryGetValue(key, out hasImport))
                    importTags[key] = hasImport = string.Equals(pvf.ResolveString(key), "[import script]", StringComparison.OrdinalIgnoreCase);
            }
            if (!hasImport)
                return;
            foreach (var (id, _) in indexed[file.Path])
            {
                var item = catalog.Get(id);
                Check(item.Id == id && item.Kind == ItemKind.Equipment && item.Durability > 0 && item.Expiration.Days > 0 &&
                    item.ShopPrice.SellGold == 0 && item.DisjointForbidden, "matched imported equipment resolves completely " + id);
                imported++;
            }
        });
        Check(imported == 311, "matched PVF imported equipment coverage");

        var now = DateTimeOffset.UtcNow;
        var current = new Character(1, 0, "Import", 0, towns.Room, towns.InitialReturn)
        {
            Job = 15, Inventory = new([new(1, Bag(3), InventoryRewardGrantService.Create(catalog.Get(10304530), utcNow: now))], Gold: 500)
        };
        var booster = new InventoryBoosterService(catalog, itemResources.Boosters, itemResources.GoldLimits, itemResources.EpicPieces);
        var request = new BoosterOpenRequest(3, 1, new(0, 0, [100050791, 100100692, 100150661]));
        var opened = booster.Open(current, request, out var receipt, now);
        Check(opened.Accepted && receipt!.Rewards.Length == 3 && opened.Plan!.After.Find(Bag(3)) is null &&
            opened.Plan.After.Items.Select(item => item.Core.ItemId).SequenceEqual(request.Selection!.ItemIds),
            "ordinary selected box grants all three imported items without output wear-level or job gates");
        int expires = checked((int)now.ToUnixTimeSeconds() + 14 * 86400);
        Check(opened.Plan!.After.Items.All(item => item.Core.ExpireTime == expires && item.Core.Value == InventoryRewardGrantService.InitialQuality &&
            item.Core.Durability == catalog.Get(item.Core.ItemId).Durability && item.Core.SealFlag == 0 && item.Core.TradeRestriction == 0),
            "imported rewards use one creation time, full durability and their own attachment policy");
        var coat = opened.Plan.After.Items[0] with { Uid = 99 };
        current = current with { Level = 85, Inventory = new([coat], Gold: 500) };
        var service = new InventoryService(catalog, itemResources.GoldLimits, itemResources.EpicPieces);
        var quick = service.Move(current, Move(coat, Bag(3)));
        Check(quick.Accepted && quick.Plan!.After.Find(Bag(3)) == coat with { Position = Bag(3) }, "imported equipment remains a real quickbar instance");
        var worn = service.Move(current with { Inventory = quick.Plan!.After }, Move(coat with { Position = Bag(3) }, new(InventoryListType.Equipment, 14)));
        Check(worn.Accepted && worn.Plan!.After.Find(new(InventoryListType.Equipment, 14)) == coat with { Position = new(InventoryListType.Equipment, 14) },
            "inherited equipment slot permits wearing with unchanged UID and full core");
        var cargo = service.Move(current, Move(coat, new(InventoryListType.PersonalCargo, 0)));
        Check(cargo.Accepted && cargo.Plan!.After.Find(new(InventoryListType.PersonalCargo, 0))!.Core == coat.Core,
            "imported temporary equipment retains existing personal warehouse access");
        var sale = new InventoryShopService(catalog, itemResources.Shops, itemResources.GoldLimits, itemResources.EpicPieces).Sell(current, new(Bag(9), 1, 1, 2, 75));
        Check(sale.Accepted && sale.Plan!.After.Items.IsEmpty && sale.Plan.After.Gold == 500,
            "explicit zero sale value is not replaced by permanent equipment value or a one-gold minimum");
        Check(!new InventoryDisjointService(catalog, itemResources.Disjoint, itemResources.GoldLimits, itemResources.EpicPieces).Disjoint(current, new(Bag(9), -1, 0), out _).Accepted && current.Inventory.Find(Bag(9)) == coat,
            "PVF disjoint prohibition keeps the original temporary equipment");
        var occupied = Enumerable.Range(9, 55).Select(slot => new InventoryItem(slot, Bag((short)slot), coat.Core));
        current = current with { Inventory = new([.. occupied, new(100, Bag(3), InventoryRewardGrantService.Create(catalog.Get(10304530), utcNow: now))]) };
        Check(!booster.Open(current, request, out _, now).Accepted && current.Inventory.Items.Length == 56 && current.Inventory.Find(Bag(3)) is not null,
            "insufficient equipment slots retain the whole selection box and grant no partial imported result");
        Console.WriteLine($"PASS {checks} item template import assertions");
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    static InventoryMoveRequest Move(InventoryItem source, InventorySlot destination) => new(source.Position, source.Core.ItemId, 1, destination, 0, 0, -1, 0, 0);
}
