using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class ExpertJobCompoundTutorialTcpTests
{
    public static async Task RunAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "expert-job-compound-tutorial"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var scripts = resources.ItemResources.ExpertJobs;
        var items = resources.ItemResources.Items;
        var cases = new List<(byte Type, ushort Quest, ushort Id, int Recipe, int Product, int ProductCount)>();
        foreach (byte type in new byte[] { 2, 4 })
        {
            int level = scripts.Level(type, 10000);
            var recipe = scripts.UnlockedRecipes(type, level).Select(id => scripts.TryRecipe(type, id, out var row) ? row : null)
                .First(row => row is not null && row.Materials.Length > 0 && row.Materials.All(material => material.ItemId > 0) &&
                    scripts.Outcome(type, row, level).SuccessPercent == 100)!;
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var character = repository.Create(owner, new("Tutorial" + type, resources.Towns.Room, resources.Towns.InitialReturn)
            { Level = 50, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = new([], Gold: 1000000) });
            var supplies = InventoryRewardGrantService.TryGrantBatch(character,
                recipe.Materials.Select(row => new InventoryReward(row.ItemId, checked(row.Count * 5))).ToArray(), items,
                resources.ItemResources.GoldLimits, resources.ItemResources.EpicPieces);
            That(supplies.Accepted && supplies.Plan is not null, "炼金/控偶教程准备真实配方完整材料");
            repository.CommitInventory(owner, character, supplies.Plan!);
            ushort quest = type == 2 ? (ushort)11015 : (ushort)11018;
            fixture.Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness,learned_recipes) SELECT id,account,{type},10000,'[{recipe.RecipeId}]' FROM characters WHERE id={character.Id}");
            fixture.Sql($"INSERT INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id) VALUES({character.Id},0,{quest},1,'{Guid.NewGuid():N}')");
            cases.Add((type, quest, character.Id, recipe.RecipeId, recipe.ProductId, recipe.ProductCount));
        }
        await fixture.StartAsync();
        for (byte slot = 0; slot < cases.Count; slot++)
        {
            var row = cases[slot];
            using (var peer = Open(fixture))
            {
                peer.Select(slot, expertJobInfo: Info(row.Type)); peer.Barrier();
                string before = Snapshot();
                Craft(peer, 0); peer.Expect(1, 238, [0, 19, 0]); peer.Barrier();
                That(Snapshot() == before, "未开店未知配方拒绝且不消耗材料或推进教程");
                Report(peer, row.Quest); peer.Expect(1, 33, [0, 19, 0]); peer.Barrier();
                That(Snapshot() == before, "没有真实制作时客户端完成报告不能推进教程");
                Craft(peer, row.Recipe, 100); peer.Expect(1, 238, [0, 19, 0]); peer.Barrier();
                That(Snapshot() == before && Condition(row.Id) == 1, "未开店材料不足不消耗资产或提前完成个人制作教程");
                fixture.Sql("CREATE TRIGGER fail_compound_tutorial BEFORE UPDATE OF condition_state ON character_active_main_quests BEGIN SELECT RAISE(ABORT,'tutorial transaction failure'); END");
                Craft(peer, row.Recipe); peer.Closed();
                That(Snapshot() == before, "教程写入失败时材料、产物、钱包、经验、revision和任务整体回滚");
                fixture.Sql("DROP TRIGGER fail_compound_tutorial");
            }
            using (var peer = Open(fixture))
            {
                peer.Select(slot, expertJobInfo: Info(row.Type)); peer.Barrier();
                long revision = Revision(row.Id);
                Craft(peer, row.Recipe); Success(peer, row.Type, row.Product, row.ProductCount, tutorial: true);
                That(Condition(row.Id) == 0 && Revision(row.Id) == revision + 1,
                    "未开店个人制作的真实产物、教学条件和经验同一次revision提交且完整ACK不会被过期回执吞掉");
                string reported = Snapshot();
                Report(peer, row.Quest);
                peer.Expect(1, 33, Build(writer => { writer.Write((byte)1); writer.Write(row.Quest); writer.Write(0u); }));
                peer.Barrier();
                That(Snapshot() == reported, "制作ACK后的原生33读回已提交条件，不重复扣料、发产物或写任务");
                revision = Revision(row.Id);
                Craft(peer, row.Recipe); Success(peer, row.Type, row.Product, row.ProductCount, tutorial: false);
                That(Condition(row.Id) == 0 && Revision(row.Id) == revision + 1, "教程已归零仍正常制作但不重复写教程状态");
                string before = Snapshot();
                Craft(peer, row.Recipe, 100); peer.Expect(1, 238, [0, 19, 0]); peer.Barrier();
                That(Snapshot() == before, "材料不足不能冒充教学成功或额外修改已完成条件");
                OpenStore(peer, row.Type);
                before = Snapshot();
                Craft(peer, 0); peer.Expect(1, 238, [0, 19, 0]); peer.Barrier();
                That(Snapshot() == before, "已开店未知配方仍拒绝，不因个人制作修复放宽配方校验");
                revision = Revision(row.Id);
                Craft(peer, row.Recipe); Success(peer, row.Type, row.Product, row.ProductCount, tutorial: false);
                That(Condition(row.Id) == 0 && Revision(row.Id) == revision + 1, "已有开店制作路径保持，已归零教程不重复推进");
            }
            using (var peer = Open(fixture))
            {
                peer.Select(slot, expertJobInfo: Info(row.Type)); peer.Barrier();
                That(Condition(row.Id) == 0, "炼金/控偶教学真实制作状态重连保存");
            }
        }
        await fixture.StopAsync();

        long Condition(ushort id) => (long)fixture.Query($"SELECT condition_state FROM character_active_main_quests WHERE character_id={id}")[0][0]!;
        long Revision(ushort id) => (long)fixture.Query($"SELECT revision FROM characters WHERE id={id}")[0][0]!;
        string Snapshot() => JsonSerializer.Serialize(new[] { fixture.Query("SELECT * FROM character_inventory_items ORDER BY item_uid"),
            fixture.Query("SELECT * FROM character_expert_job ORDER BY character_id"),
            fixture.Query("SELECT * FROM character_active_main_quests ORDER BY character_id,slot"),
            fixture.Query("SELECT * FROM characters ORDER BY id"),
            fixture.Query("SELECT * FROM account_wallets ORDER BY account"),
            fixture.Query("SELECT * FROM account_currency_balances ORDER BY account,currency_kind") });
        byte[] Info(byte type) => Build(writer =>
        {
            var recipes = scripts.LearnedRecipes(type, scripts.Level(type, 10000), [cases.Single(row => row.Type == type).Recipe]);
            writer.Write((byte)0); writer.Write(type); writer.Write(checked((byte)recipes.Length));
            foreach (int recipe in recipes) writer.Write(recipe);
        });
        void Success(TcpPeer peer, byte type, int product, int count, bool tutorial)
        {
            peer.Expect(0, 14);
            if (tutorial) peer.Expect(0, 574);
            peer.Expect(0, 2); peer.Expect(0, 205, Info(type));
            peer.Expect(1, 238, Build(writer =>
            { writer.Write((byte)1); writer.Write((byte)1); writer.Write(product); writer.Write(count); writer.Write(1u); writer.Write(0u); writer.Write((byte)0); }));
            peer.Barrier();
        }
    }
    static void Craft(TcpPeer peer, int recipe, ushort count = 1) => peer.Send(238, Build(writer =>
    { writer.Write(recipe); writer.Write(count); writer.Write(ushort.MaxValue); }));
    static void Report(TcpPeer peer, ushort quest) => peer.Send(33, Build(writer =>
    { writer.Write((ushort)33); writer.Write(quest); writer.Write((byte)0); writer.Write((byte)0); }));
    static void OpenStore(TcpPeer peer, byte type)
    {
        peer.Send(598, Build(writer =>
        { writer.Write((byte)0); writer.Write(1); writer.Write(type); writer.Write(0u); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); }));
        peer.Expect(1, 598, [1]); peer.Barrier();
    }
}
