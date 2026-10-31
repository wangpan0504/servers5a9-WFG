using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class RecipeCraftTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        RecipeResourceTests.Run(resources.Archive);
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "recipe-craft"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var items = resources.ItemResources;
        Character ordinary, upgrade;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var candidate = CharacterCreation.Prepare("RecipeTcp", resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            candidate = candidate with { Level = 14 };
            ordinary = repository.Create(owner, candidate);
            var grant = InventoryRewardGrantService.TryGrantBatch(ordinary,
                [new(10087066, 2), new(3037, 72), new(3138, 32), new(3053, 8), new(3161, 36), new(3166, 4), new(3030, 8)],
                items.Items, items.GoldLimits, items.EpicPieces);
            That(grant.Accepted, "制作TCP准备真实PVF材料");
            ordinary = repository.CommitInventory(owner, ordinary, new(ordinary.Inventory, grant.Plan!.After with
                { Gold = 10000, ItemMakingSkills = ItemMakingSkillState.Empty.Set(2, 19) }, "recipe_setup"));
            candidate = candidate with { Name = "RecipeKeep", Level = 90 };
            upgrade = repository.Create(owner, candidate);
            grant = InventoryRewardGrantService.TryGrantBatch(upgrade,
                [new(10304473, 1), new(100390005, 1), new(10159623, 40), new(10158964, 5), new(3262, 100)],
                items.Items, items.GoldLimits, items.EpicPieces);
            That(grant.Accepted, "升级配方TCP准备原件及完整材料");
            upgrade = repository.CommitInventory(owner, upgrade, new(upgrade.Inventory, grant.Plan!.After with { Items = grant.Plan.After.Items.Select(item =>
                item.Core.ItemId == 100390005 ? item with { Core = item.Core with
                    { Attr = 7, GenuineUpgrade = 3, EnchantCardId = 100000201, Random0 = new(1, 2, 3), RandomState = 1 } } : item).ToImmutableArray() }, "recipe_keep_setup"));
        }
        var blueprint = ordinary.Inventory.Items.Single(item => item.Core.ItemId == 10087066);
        var original = upgrade.Inventory.Items.Single(item => item.Core.ItemId == 100390005);
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select(ordinary.Slot);
            That(U16(peer.ItemMakingSkills, 8) == 19, "登录351恢复本人皮甲熟练度绝对值");
            string before = Snapshot();
            Send(peer, 10087066, 1, 1);
            peer.Expect(1, 25, [0, 179, 0]);
            Send(peer, uint.MaxValue, 0, 1);
            peer.Expect(1, 25, [0, 179, 0]);
            peer.Barrier();
            That(before == Snapshot(), "非法模板直取和越界槽位均回复失败，不扣料或污染进度");
            Send(peer, (uint)blueprint.Position.Index, 0, 1);
            var first = Decode(peer.Expect(1, 25).Body);
            That(first.Success == 1 && first.Products is [{ Id: 100060157, Count: 999999998, Upgrade: >= 4 and <= 12 }] &&
                first.Consumed.GetValueOrDefault(blueprint.Position.Index) == 1 && first.Consumed.GetValueOrDefault((short)358) == 36,
                "纯材料设计图以原生消耗行同步源与无色晶块，完整装备行携带熟练度强化");
            peer.Expect(0, 352, [0, 2, 1, 0]);
            peer.Barrier();
            using (var reader = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
            {
                var current = repository.LoadCharacter(reader, ordinary.Slot);
                That(current.Inventory.ItemMakingSkills.Points[2] == 20 && current.Inventory.Gold == 10000 &&
                    current.Inventory.Find(blueprint.Position)!.Core.Count == 1 && current.Inventory.Items.Any(item => item.Core.ItemId == 100060157 && item.Uid > 0),
                    "一次提交保存新UID、源数量和实际熟练度，不把售价扣成费用");
            }
            Send(peer, (uint)blueprint.Position.Index, 0, 1);
            Decode(peer.Expect(1, 25).Body);
            peer.Expect(0, 352, [22, 2, 0, 0]);
            peer.Barrier();
            Send(peer, (uint)blueprint.Position.Index, 0, 1);
            peer.Expect(1, 25, [0, 179, 0]);
            peer.Barrier();

            for (int attempt = 0; attempt < 2; attempt++)
            {
                Send(peer, 5058, 1, 3);
                var npc = Decode(peer.Expect(1, 25).Body);
                That(npc.Consumed.GetValueOrDefault((short)0) == 600 && npc.Consumed.Values.Sum() == 603 &&
                    npc.Products is [{ Id: 3135, Count: 3 }], "罗杰相同CMD25按3份扣材料及600金币，堆叠行只填本次增量");
                peer.Barrier();
            }
            before = Snapshot();
            Send(peer, 5058, 1, 3);
            peer.Expect(1, 25, [0, 19, 0]);
            peer.Barrier();
            That(Snapshot() == before && fixture.Query("SELECT leather FROM item_making_skill_info WHERE charac_no=$p0", ordinary.Id) is [[20L]],
                "NPC整批缺料不半扣，也不向没有熟练声明的配方虚增经验");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(upgrade.Slot);
            var source = upgrade.Inventory.Items.Single(item => item.Core.ItemId == 10304473);
            Send(peer, (uint)source.Position.Index, 0, 1);
            var kept = Decode(peer.Expect(1, 25).Body);
            That(kept.Products is [{ Id: 100390011, Upgrade: 7, Enchant: 100000201 }] &&
                kept.Consumed.GetValueOrDefault(original.Position.Index) == 1, "原件升级回复完整属性行，先消费旧模板再装载新模板");
            peer.Barrier();
            using var reader = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var current = repository.LoadCharacter(reader, upgrade.Slot).Inventory.Items.Single(item => item.Core.ItemId == 100390011);
            That(current.Uid == original.Uid && current.Position == original.Position &&
                current.Core == original.Core with { ItemId = 100390011 }, "黑暗祭礼仍保留完整原UID、槽位、锻造、附魔和随机属性");
        }

        // 补回同一角色的一份设计图与材料；在熟练度更新处注入晚期失败，不另起一套制作实现。
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            ordinary = repository.LoadCharacter(owner, ordinary.Slot);
            var refill = InventoryRewardGrantService.TryGrantBatch(ordinary,
                [new(10087066, 1), new(3037, 36), new(3138, 16), new(3053, 4), new(3161, 18), new(3166, 2)],
                items.Items, items.GoldLimits, items.EpicPieces);
            ordinary = repository.CommitInventory(owner, ordinary, refill.Plan!);
        }
        fixture.Sql("CREATE TRIGGER recipe_abort AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'recipe late rollback'); END");
        using (var peer = Open(fixture))
        {
            peer.Select(ordinary.Slot);
            string before = Snapshot();
            Send(peer, (uint)ordinary.Inventory.Items.Single(item => item.Core.ItemId == 10087066).Position.Index, 0, 1);
            peer.Closed();
            That(before == Snapshot(), "晚期SQL失败不发送成功ACK，恢复材料、源、产物、UID序列和熟练度");
        }
        fixture.Sql("DROP TRIGGER recipe_abort");
        await fixture.StopAsync();
        await fixture.StartAsync("recipe-restart");
        using (var peer = Open(fixture))
        {
            peer.Select(ordinary.Slot);
            That(U16(peer.ItemMakingSkills, 8) == 20, "重启登录恢复熟练度，不续增或回退");
            using var reader = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var current = repository.LoadCharacter(reader, ordinary.Slot);
            That(current.Inventory.Items.Where(item => item.Core.ItemId == 100060157).Count() == 2 &&
                current.Inventory.Items.Where(item => item.Core.ItemId == 3135).Sum(item => item.Core.Count) == 6 && current.Inventory.Gold == 8800,
                "两次普通制作及NPC合堆在服务重启后完整恢复");
            peer.Barrier();
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "设计图及制作熟练度数据库完整");
        await RunProbabilityAndCustomAsync(fixture, repository, resources);
        await RunDisjointRecipesAsync(fixture, repository, resources);

        string Snapshot() => fixture.Snapshot() + JsonSerializer.Serialize(fixture.Query("SELECT * FROM item_making_skill_info ORDER BY charac_no")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM account_currency_balances ORDER BY account,currency_kind")) +
            JsonSerializer.Serialize(fixture.Query("SELECT * FROM sqlite_sequence ORDER BY name"));
    }

    static async Task RunProbabilityAndCustomAsync(TcpTestFixture fixture, CharacterRepository repository, TcpTestResources resources)
    {
        var items = resources.ItemResources;
        Character actor;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            actor = repository.Create(owner, CharacterCreation.Prepare("RecipeChance", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get) with { Level = 95 });
            var grant = InventoryRewardGrantService.TryGrantBatch(actor, [new(10095704,1),new(109040072,1),new(10000204,15),
                new(10000327,75),new(3167,250),new(3037,2800)], items.Items, items.GoldLimits, items.EpicPieces);
            That(grant.Accepted, "概率升级真实原件及材料准备");
            actor = repository.CommitInventory(owner, actor, new(actor.Inventory, grant.Plan!.After with
                { Items = grant.Plan.After.Items.Select(item => item.Core.ItemId == 109040072 ? item with { Core = item.Core with
                    { Attr=7, EnchantCardId=100000201, EmblemSocketCount=1, Emblem1=42 } } : item).ToImmutableArray() }, "chance_setup"));
        }
        var original = actor.Inventory.Items.Single(item => item.Core.ItemId == 109040072);
        var book = actor.Inventory.Items.Single(item => item.Core.ItemId == 10095704);
        using (var peer = Open(fixture))
        {
            peer.Select(actor.Slot);
            Send(peer,(uint)book.Position.Index,0,1);
            var reply = Decode(peer.Expect(1,25).Body);
            That(reply.Success is 1 or 2 && reply.Products.Any(product => product.Id == (reply.Success == 1 ? 109040073 : 109040072)),
                "原生制作结果标记与真实成功/失败产物一致");
            peer.Barrier();
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            actor = repository.LoadCharacter(owner,actor.Slot);
            var output = actor.Inventory.Items.Single(item => item.Core.ItemId is 109040072 or 109040073);
            var expected = reply.Success == 2 ? original.Core : original.Core with { ItemId=109040073, EmblemSocketCount=0,
                Emblem1=0,Emblem2=0,SealFlag=1,Durability=Math.Min(original.Core.Durability,items.Items.Get(109040073).Durability) };
            That(output.Uid == original.Uid && output.Core == expected && !actor.Inventory.Items.Any(item => item.Core.ItemId == 10095704) &&
                (reply.Success == 1 || actor.Inventory.Items.Any(item => item.Core.ItemId == 10150748)),
                "实际概率分支保留UID，失败完整返件及额外返料，成功按封装规则生成");
        }
        if (items.ItemRecipes.Get(10090321) is null) return;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            actor = repository.Create(owner, CharacterCreation.Prepare("ReforgeTcp", resources.Characters.Get(0),
                resources.Towns.Room,resources.Towns.InitialReturn,resources.Learning.Get) with { Level=95 });
            var grant=InventoryRewardGrantService.TryGrantBatch(actor,[new(10090321,1),new(10090322,1),new(10090323,1),new(10090324,1),new(102030279,1)],
                items.Items,items.GoldLimits,items.EpicPieces);
            That(grant.Accepted,"四张本服重铸图真实准备");
            actor=repository.CommitInventory(owner,actor,grant.Plan!);
        }
        using (var peer = Open(fixture))
        {
            peer.Select(actor.Slot);
            foreach (int id in Enumerable.Range(10090321,4))
            {
                var source=actor.Inventory.Items.Single(item=>item.Core.ItemId==id);
                var before=actor.Inventory.Items.Single(item=>item.Core.ItemId==102030279);
                Send(peer,(uint)source.Position.Index,0,1);
                var reply=Decode(peer.Expect(1,25).Body);
                peer.Barrier();
                using var owner=new AccountOwners().Acquire(fixture.Options.DevelopmentAccount,()=>{});
                actor=repository.LoadCharacter(owner,actor.Slot);
                var after=actor.Inventory.Items.Single(item=>item.Core.ItemId==102030279);
                That(reply.Success==1 && after.Uid==before.Uid && after.Core==before.Core with { Attr=after.Core.Attr } &&
                    items.ItemRecipes.Get(id)!.UpgradeTransitions[before.Core.Upgrade][after.Core.Upgrade]>0,
                    "四张图使用对应概率表，原位原UID重铸并完整保留其他字段");
            }
        }
    }

    static async Task RunDisjointRecipesAsync(TcpTestFixture fixture, CharacterRepository repository, TcpTestResources resources)
    {
        var items = resources.ItemResources;
        var actors = new List<(Character Actor, int Recipe, InventoryItem Source)>();
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        foreach (int id in Enumerable.Range(10311664, 6))
        {
            var definition = items.ItemRecipes.Get(id);
            That(definition is { SetsDisjointInstanceType: true, Outputs.Length: 1 } &&
                items.Items.Get(definition.Outputs[0].ItemId).Kind == ItemKind.Equipment,
                "六个实际设计图均保存根级实例声明和唯一装备产物 " + id);
            var actor = repository.Create(owner, CharacterCreation.Prepare("Restricted" + (id - 10311664), resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get) with { Level = 95 });
            var rewards = new[] { new InventoryReward(id, 1) }.Concat(definition!.Inputs.Where(row => row.ItemId != 0)
                .Select(row => new InventoryReward(row.ItemId, row.Count))).ToArray();
            var grant = InventoryRewardGrantService.TryGrantBatch(actor, rewards, items.Items, items.GoldLimits, items.EpicPieces);
            That(grant.Accepted, "实际声明的源与完整成本可准备，不以商店need material代替制作费 " + id);
            actor = repository.CommitInventory(owner, actor, new(actor.Inventory, grant.Plan!.After with
                { Gold = checked((uint)definition.Inputs.Where(row => row.ItemId == 0).Sum(row => row.Count)) }, "restricted_recipe_setup"));
            actors.Add((actor, id, actor.Inventory.Items.Single(item => item.Core.ItemId == id)));
        }
        foreach (var entry in actors)
        {
            if (entry.Recipe == 10311664)
            {
                using var peer = Open(fixture);
                peer.Select(entry.Actor.Slot); peer.Barrier();
                string before = Snapshot();
                fixture.Sql("CREATE TRIGGER fail_restricted_recipe BEFORE INSERT ON inventory_audit_log " +
                    "WHEN NEW.action_name='recipe_craft' BEGIN SELECT RAISE(ABORT,'restricted recipe rollback'); END");
                Send(peer, (uint)entry.Source.Position.Index, 0, 1); peer.Closed();
                That(Snapshot() == before, "制作晚SQL失败同时回滚源、材料、实例限制、UID、revision及审计");
                fixture.Sql("DROP TRIGGER fail_restricted_recipe");
            }
            using var client = Open(fixture);
            client.Select(entry.Actor.Slot); client.Barrier();
            string unchanged = Snapshot();
            Send(client, (uint)entry.Source.Position.Index, 0, 1);
            if (entry.Recipe == 10311665)
            {
                client.Expect(1, 25, [0, 19, 0]); client.Barrier();
                That(Snapshot() == unchanged && items.ItemRecipes.Get(entry.Recipe) is { MaintainAbility: "normal" } recipe &&
                    items.Items.Get(recipe.Inputs[0].ItemId).Kind == ItemKind.Material,
                    "10311665首项材料却声明保留装备属性，原未闭合资格继续拒绝且完整保留，不猜测删除声明");
                continue;
            }
            var reply = Decode(client.Expect(1, 25).Body); client.Barrier();
            var current = Read(entry.Actor.Slot);
            var output = current.Inventory.Items.Single(item => item.Core.ItemId == items.ItemRecipes.Get(entry.Recipe)!.Outputs[0].ItemId);
            var expected = InventoryRewardGrantService.Create(items.Items.Get(output.Core.ItemId)) with { DisjointForbidden = true };
            That(reply.Success == 1 && reply.Products is [{ }] && reply.Products[0].Id == output.Core.ItemId && output.Uid > 0 &&
                output.Uid != entry.Source.Uid && output.Core == expected &&
                !current.Inventory.Items.Any(item => item.Core.ItemId == entry.Recipe) && current.Inventory.Gold == 0,
                "实际CMD25完整落库新装备及分解限制，只扣真实源和成本，毁装限制不凭空新增 " + entry.Recipe);
            string committed = Snapshot();
            client.Send(26, Build(writer =>
                { writer.Write(output.Position.Index); writer.Write((byte)0); writer.Write((short)-1); writer.Write(0); }));
            client.Expect(1, 26, [0, 19, 0]); client.Expect(0, 14); client.Barrier();
            That(Snapshot() == committed, "实际制作装备经CMD26拒绝分解，且不消费产物或额外费用 " + entry.Recipe);
        }
        var inventories = actors.ToDictionary(entry => entry.Actor.Slot, entry => JsonSerializer.Serialize(Read(entry.Actor.Slot).Inventory));
        await fixture.StopAsync();
        await fixture.StartAsync("restricted-recipes-restart");
        foreach (var entry in actors)
        {
            using var peer = Open(fixture);
            peer.Select(entry.Actor.Slot); peer.Barrier();
            That(JsonSerializer.Serialize(Read(entry.Actor.Slot).Inventory) == inventories[entry.Actor.Slot],
                "服务重启和重登恢复完整实例/UID及未闭合图的原材料 " + entry.Recipe);
        }
        Character Read(ushort slot)
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repository.LoadCharacter(owner, slot);
        }
        string Snapshot() => fixture.Snapshot() + fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT * FROM character_inventory_slots ORDER BY character_id,list_type,slot_index") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") + fixture.Snapshot("SELECT * FROM sqlite_sequence ORDER BY name");
    }

    static void Send(TcpPeer peer, uint source, byte mode, ushort count) => peer.Send(25,
        Build(writer => { writer.Write(source); writer.Write(mode); writer.Write(count); writer.Write((byte)0); writer.Write((byte)1); }));

    sealed record Product(int Id, uint Count, byte Upgrade, uint Enchant);
    sealed record Reply(Dictionary<short, int> Consumed, byte Success, Product[] Products);
    static Reply Decode(byte[] body)
    {
        That(body.Length >= 4 && body[0] == 1, "制作成功ACK有原生最小字段");
        int at = 2;
        var consumed = new Dictionary<short, int>();
        for (int i = 0; i < body[1]; i++, at += 7)
        {
            That(at + 7 <= body.Length && body[at] == 0 && U32(body, at + 3) > 0 &&
                consumed.TryAdd((short)U16(body, at + 1), checked((int)U32(body, at + 3))), "消耗行固定7B、Main类型、正数量且无重复槽");
        }
        That(at + 2 <= body.Length && body[at] is 1 or 2, "消耗列表之后是成功/失败结果标记");
        byte flag = body[at++], count = body[at++];
        That(body.Length == at + count * 124, "产物数是u8，每行action+123B，没有未核实的尾部u16");
        var products = new List<Product>();
        for (int i = 0; i < count; i++, at += 124)
        {
            That(body[at] == 0 && U16(body, at + 1) <= 365 && U32(body, at + 3) > 0, "产物普通Main分支及实际槽位/编号");
            products.Add(new((int)U32(body, at + 3), U32(body, at + 7), body[at + 11], U32(body, at + 15)));
        }
        return new(consumed, flag, products.ToArray());
    }
}
