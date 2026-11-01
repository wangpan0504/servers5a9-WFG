using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class SkillTreeExpansionTests
{
    public static void RunCore()
    {
        int checks = Count;
        var point = new Location(38, 1, 450, 234);
        var towns = new TownCatalog(new(), point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
        var learning = new CharacterLearningState(0, Array.AsReadOnly(new CharacterSkillTree[]
        {
            new(0, 205, 0, Array.AsReadOnly(new LearnedSkill[] { new(0, 5, 1) { Command = "0102" } })),
            new(1, 13000, 46, Array.AsReadOnly(new LearnedSkill[] { new(7, 5, 2) { Command = "0304" } }))
        }));
        That(learning.ActiveContext == 0 && learning.TreeWireValue == 255, "未开通仍使用第一树，不丢第二树历史记录");
        foreach (var body in new byte[][] { [], [0, 0], [0, 0, 0, 0], [0, 0, 0, 0, 0, 0] })
            Reject<InvalidDataException>(() => SkillRequests.ParseTreeChange(body), "切树拒绝未知长度");
        That(SkillRequests.ParseTreeChange([0]) == 0 && SkillRequests.ParseTreeChange([1, 2, 3, 4, 5]) == 1,
            "切树仅取当前方案，检查尾不能充当方案");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-skill-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("skill-tree", () => { });
            var original = repository.Create(owner, new("TreeOwner", point, point)
            {
                InitialLearning = learning, Skills = [new(5, 1)]
            });
            original = repository.CommitInventory(owner, original, new(original.Inventory,
                CurrencyWallet.Set(original.Inventory, CurrencyKind.Cera, 100), "fixture_currency"));
            var session = new GameSession(repository, towns, owner);
            session.Select(original.Slot);
            session.MarkReady();
            That(!session.ChangeSkillTree(0).Success && session.Selected!.Revision == original.Revision, "未开通不能伪造切换或递增版本");
            var plan = new InventoryPlan(original.Inventory, CurrencyWallet.Set(original.Inventory, CurrencyKind.Cera, 90),
                "cash_shop_buy", new(original)) { UnlockSkillTree = true };
            string before = Snapshot();
            Execute("CREATE TRIGGER skill_tree_failure BEFORE INSERT ON character_skill_points WHEN NEW.context=1 BEGIN SELECT RAISE(ABORT,'skill tree rollback'); END");
            Reject<SqliteException>(() => repository.CommitInventory(owner, original, plan), "第二树写入失败传播");
            Execute("DROP TRIGGER skill_tree_failure");
            That(Snapshot() == before, "开通失败回滚扣款、权益、两树、审计和版本");
            var unlocked = repository.CommitInventory(owner, original, plan);
            That(unlocked.Learning.SelectedTree == 0 && unlocked.Revision == original.Revision + 1 &&
                unlocked.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 90 && Trees(unlocked.Learning) == Trees(learning),
                "商城同事务扣款并开通，保留两树SP、TP、槽位与指令");
            Reject<InvalidOperationException>(() => repository.CommitInventory(owner, original, plan), "旧库存计划不能重复扣费开通");
            Reject<InvalidDataException>(() => repository.CommitLearning(owner, unlocked, unlocked.Learning with { SelectedTree = -1 }), "学习事务不能撤销权益");
            session.End();
            session.Select(original.Slot);
            session.MarkReady();
            var switched = session.ChangeSkillTree(0);
            var selected = switched.Receipt!.Character;
            That(switched.Success && selected.Learning.SelectedTree == 1 && selected.Skills.SequenceEqual(new CharacterSkill[] { new(5, 2) }) &&
                selected.Revision == unlocked.Revision + 1 && Trees(selected.Learning) == Trees(learning),
                "切到第二树仅改当前页及技能镜像，全部加点和绑定独立保留");
            That(SkillPackets.TreeChanged(selected.Learning).SequenceEqual(new byte[] { 1, 1 }), "ACK260包含实际提交后的当前页");
            That(!session.ChangeSkillTree(0).Success && !session.ChangeSkillTree(255).Success && ReferenceEquals(session.Selected, selected),
                "重放旧当前页或非法页不触发第二次切换");
            var reopened = new CharacterRepository(path).LoadCharacter(owner, original.Slot);
            That(JsonSerializer.Serialize(reopened) == JsonSerializer.Serialize(selected), "重开库保留第二树及其技能镜像");
            var stalePlan = new InventoryPlan(selected.Inventory, selected.Inventory, "cash_shop_buy", new(selected)) { UnlockSkillTree = true };
            Reject<InvalidOperationException>(() => repository.CommitInventory(owner, selected, stalePlan), "已开通不能再消费权益计划");
            session.End();
            That(!session.CanPublishLearning(switched.Receipt) && !session.ChangeSkillTree(1).Success, "离开选角代次后旧回执不能发布或写入");
            string Snapshot() => JsonSerializer.Serialize(repository.LoadCharacter(owner, original.Slot));
            void Execute(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
            }
        }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - checks} 技能方案权益、事务与切换断言");
    }

    static string Trees(CharacterLearningState state) => JsonSerializer.Serialize(state.Trees);
}
