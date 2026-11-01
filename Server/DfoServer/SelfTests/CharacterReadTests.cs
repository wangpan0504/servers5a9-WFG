using static DfoServer.Testing.TestCheck;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static class CharacterReadTests
{
    public static void Run()
    {
        int checks = TestCheck.Count;
        RosterAuraVisibility();
        string root = Path.Combine(Path.GetTempPath(), "s5a9-character-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("read-first", () => { });
            using var other = owners.Acquire("read-other", () => { });
            var position = new Location(1, 0, 10, 10);
            TestCheck.That(repository.ListRoster(owner).Length == 0 && repository.List(owner).Length == 0, "空账号查询不要求共享资产行");
            NewCharacter Role(string name) => new(name, position, position)
            {
                CompletedQuests = [10, 20], Skills = [new(5, 1)], ActiveMainQuests = [new(0, 30, 1)],
                Inventory = new([new(0, new(InventoryListType.Main, 121), new(1001, ItemKind.Material, 3)),
                    new(0, new(InventoryListType.Equipment, 12), new(2001, ItemKind.Equipment, 123))])
            };
            var first = repository.Create(owner, Role("ReadFirst"));
            var second = repository.Create(owner, Role("ReadSecond"));
            var outsider = repository.Create(other, Role("ReadOther"));
            var pieces = new EpicPieceCatalog("[piece list]\n2001 3001\n[/piece list]");
            var paid = first.Inventory with
            {
                AccountWallet = first.Inventory.AccountWallet.Set(CurrencyKind.Cera, 77),
                AccountCargo = new(0, 8, 9), EpicPieces = EpicPieceBook.Empty.Set(pieces, 0, 12),
                ShopPurchases = [new(1, 1001, true, false, 0, 2), new(1, 1001, false, true, 1, 3)],
                Items = first.Inventory.Items.Add(new(0, new(InventoryListType.AccountCargo, 0), new(1001, ItemKind.Material, 7)))
            };
            first = repository.CommitInventory(owner, first, new(first.Inventory, paid, "read-fixture"));
            var full = repository.List(owner);
            var roster = repository.ListRoster(owner);
            TestCheck.That(roster.Select(row => row.Id).SequenceEqual(new[] { first.Id, second.Id }), "名册按账号与槽位有序返回");
            TestCheck.That(CharacterPackets.Roster(roster).SequenceEqual(CharacterPackets.Roster(full.Select(CharacterRosterEntry.FromCharacter).ToArray())), "轻量名册与完整角色生成完全相同包体");
            TestCheck.That(roster.All(row => row.Equipment.Length == 1 && row.Equipment[0].Position.List == InventoryListType.Equipment), "名册只加载穿戴外观");
            foreach (var character in full)
                TestCheck.That(JsonSerializer.Serialize(repository.LoadCharacter(owner, character.Slot)) == JsonSerializer.Serialize(character), "单角色加载保留完整任务、技能和资产");
            TestCheck.That(full.All(row => row.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 77 && row.Inventory.AccountCargo.Gold == 9 && row.Inventory.EpicPieces.Get(0) == 12), "批次内共享资产数值一致");
            TestCheck.That(ReferenceEquals(full[0].Inventory.AccountWallet, full[1].Inventory.AccountWallet) &&
                ReferenceEquals(full[0].Inventory.AccountCargo, full[1].Inventory.AccountCargo) &&
                ReferenceEquals(full[0].Inventory.EpicPieces, full[1].Inventory.EpicPieces) &&
                ReferenceEquals(full[0].Inventory.Items.Last(), full[1].Inventory.Items.Last()), "完整列表只实例化一次账号共享快照和仓库物品");
            TestCheck.That(full[0].Inventory.ShopPurchases.Length == 2 && full[1].Inventory.ShopPurchases.Length == 1 &&
                full[0].Inventory.ShopPurchases[0].AccountWide && !full[0].Inventory.ShopPurchases[1].AccountWide, "账号限购共享且角色限购仍隔离并保持顺序");
            TestCheck.That(repository.LoadCharacter(other, 0).Id == outsider.Id && repository.ListRoster(other).Single().Id == outsider.Id, "相同槽位不能跨账号读角色");
            Reject<InvalidDataException>(() => repository.LoadCharacter(owner, 31), "不存在的槽位明确拒绝");

            using var external = new SqliteConnection("Data Source=" + path + ";Pooling=False");
            external.Open();
            using var command = external.CreateCommand();
            TestCheck.That(first.BlacklistCount == 0 && second.BlacklistCount == 0 && outsider.BlacklistCount == 0,
                "新角色被拉黑计分默认为0，不从账号或装备称号推导");
            command.CommandText = "UPDATE characters SET blacklist_count=201 WHERE id=$blackId";
            command.Parameters.AddWithValue("$blackId", first.Id);
            command.ExecuteNonQuery();
            var counted = repository.LoadCharacter(owner, first.Slot);
            TestCheck.That(counted.BlacklistCount == 201 && repository.List(owner).Single(row => row.Id == first.Id).BlacklistCount == 201 &&
                repository.LoadCharacter(owner, second.Slot).BlacklistCount == 0 && repository.LoadCharacter(other, outsider.Slot).BlacklistCount == 0,
                "完整角色和按需加载读取各自有效计分，不跨角色或账号共享");
            var moved = repository.Save(owner, counted, counted.Position with { X = 11 }, counted.Return);
            TestCheck.That(moved.BlacklistCount == 201 && new CharacterRepository(path).LoadCharacter(owner, first.Slot).BlacklistCount == 201,
                "无关位置保存及重开库保留被拉黑计分");
            using (var writer = external.BeginTransaction(deferred: false))
            {
                command.Transaction = writer;
                command.CommandText = "UPDATE account_currency_balances SET balance=999 WHERE account='read-first'";
                command.ExecuteNonQuery();
                TestCheck.That(repository.ListRoster(owner).Length == 2 && repository.LoadCharacter(owner, 0).Inventory.AccountWallet.Get(CurrencyKind.Cera) == 77 &&
                    repository.List(owner).All(row => row.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 77), "外部 writer 占用时三类纯查询读取已提交快照而不申请 writer");
                writer.Commit();
            }
            command.Transaction = null;
            TestCheck.That(repository.LoadCharacter(owner, 0).Inventory.AccountWallet.Get(CurrencyKind.Cera) == 999 &&
                repository.List(owner).All(row => row.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 999), "新请求读取外部已提交余额，不沿用上次共享对象");
            command.CommandText = "UPDATE character_inventory_items SET item_core='{}' WHERE character_id=$id AND item_uid=$uid";
            command.Parameters.AddWithValue("$id", second.Id);
            command.Parameters.AddWithValue("$uid", second.Inventory.Items.Single(item => item.Position.List == InventoryListType.Main).Uid);
            command.ExecuteNonQuery();
            TestCheck.That(repository.ListRoster(owner).Length == 2 && repository.LoadCharacter(owner, 0).Id == first.Id, "其他角色非穿戴资产异常不阻止名册与目标角色加载");
            Reject<InvalidDataException>(() => repository.LoadCharacter(owner, 1), "选中异常资产角色仍完整校验并拒绝");
            Reject<InvalidDataException>(() => repository.List(owner), "明确请求完整列表仍校验所有角色");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => repository.ListRoster(owner), "旧 generation 不能读名册");
            Reject<InvalidOperationException>(() => repository.LoadCharacter(owner, 0), "旧 generation 不能加载角色");
            TestCheck.That(repository.LoadCharacter(replacement, 0).Id == first.Id, "新 owner 可继续加载正常角色");
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
        Console.WriteLine($"PASS {TestCheck.Count - checks} 名册、按需角色与账号共享读取断言");
    }

    static void RosterAuraVisibility()
    {
        InventoryItem Aura(int uid, short slot, int template, int source = 0) =>
            new(uid, new(InventoryListType.Equipment, slot), new(template, ItemKind.Avatar, uid))
            {
                Avatar = new() { ClearAvatarId = source, Color1 = 0x1234, Color2 = 0x5678, Socket0 = new(0xFFEF, 3001) }
            };
        var point = new Location(1, 0, 10, 10);
        var bare = new Character(1, 0, "BareAura", 0, point, point);
        Character[] roles =
        [
            bare,
            bare with { Id = 2, Slot = 2, Name = "NormalAura", Inventory = new([Aura(11, 9, 1001)]) },
            bare with { Id = 3, Slot = 5, Name = "CloneAura", Inventory = new([Aura(12, 9, 1002, 1001)]) },
            bare with { Id = 4, Slot = 8, Name = "EmptyClone", Inventory = new([Aura(13, 9, 1002)]) },
            bare with { Id = 5, Slot = 10, Name = "SkinAura", Inventory = new([Aura(14, 9, 1001), Aura(15, 11, 1003)]) }
        ];
        byte[] body = CharacterPackets.Roster(roles.Select(CharacterRosterEntry.FromCharacter).ToArray());
        using var reader = new BinaryReader(new MemoryStream(body));
        That(reader.ReadBytes(15).Length == 15 && body[1] == roles.Length, "光环名册保持原头部与角色数");
        foreach (var role in roles)
        {
            That(reader.ReadUInt16() == role.Slot, "多角色光环名册保留持久槽位");
            That(CharacterName.Encoding.GetString(reader.ReadBytes(reader.ReadInt32())) == role.Name, "光环行后续角色名称不偏移");
            reader.ReadBytes(15); // 两个保留字节、职业/转职/等级及10B辅助字段。
            That(reader.ReadByte() == role.Inventory.Items.Length, "无光环不造装备，普通、克隆及幻化槽保留真实条目数");
            foreach (var item in role.Inventory.Items)
            {
                That(reader.ReadByte() == item.Position.Index && reader.ReadInt32() == item.Core.ItemId, "光环槽和模板不由使能位替代");
                That(reader.ReadInt32() == 4 && reader.ReadUInt16() == 0x1234 && reader.ReadUInt16() == 0x5678,
                    "名册保留光环的原生染色扩展");
                reader.ReadBytes(3);
                That(reader.ReadInt32() == item.Avatar!.ClearAvatarId, "普通及空克隆来源保持0，已有克隆保持来源编号");
                if (item.Position.Index == 9 && item.Avatar.ClearAvatarId > 0)
                {
                    That(reader.ReadInt32() == 30 && reader.ReadUInt16() == 0xFFEF && reader.ReadInt32() == 3001,
                        "克隆光环来源后保留完整孔位扩展");
                    reader.ReadBytes(24);
                }
                That(reader.ReadInt32() == 0 && reader.ReadByte() == 0, "光环条目附魔字段后准确进入行尾");
            }
            byte[] tail = reader.ReadBytes(48);
            That(tail.Length == 48 && tail[46] == 1 && tail[47] == 0, "每行末尾光环使能为原生u16值1，不扩宽或借用其他标志");
        }
        Bytes(reader.ReadBytes(10), [1, 0, 0, 0, 0, 0, 0, 0, 0, 0], "最后一行光环后名册公共尾部不变");
        That(reader.BaseStream.Position == body.Length, "多角色及可变长克隆光环名册完整消费");
    }

}
