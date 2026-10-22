using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class LegacyStatusConsumableTests
{
    static readonly InventorySlot Coin = new(InventoryListType.Main, 65), Book = new(InventoryListType.Main, 66), Reset = new(InventoryListType.Main, 67);

    public static void RunCore()
    {
        int checks = Count;
        foreach (var (id, path, type, kind, amount) in new[]
        {
            (7180, "event/newyear/coin_newyear.stk", "[waste]", LegacyStatusConsumableKind.ReviveCoins, 1),
            (7181, "event/newyear/coin_book.stk", "[waste]", LegacyStatusConsumableKind.ReviveCoins, 5)
        })
        {
            var template = new ItemTemplate(id, ItemKind.Consumable, 0, 1, 0, 1000, 0, 2, 1, "[trade]", ushort.MaxValue, 0, "fixture");
            var raw = new StackableFile(id, "stackable/" + path, new(type, [0]), 1000, null, null, 1, null, []);
            var file = new ExperienceConsumableFile(raw, null, null, [], [], []);
            var definition = LegacyStatusConsumableDefinition.Parse(file, template)!;
            That(definition is not null && definition.Kind == kind && definition.Amount == amount, "旧道具ID、类型与路径匹配 " + id);
            foreach (var bad in new[] { file with { Effect = new("[expUp]", [1]) },
                file with { Stackable = raw with { ScriptPath = "stackable/custom.stk" } },
                file with { DeclaredFields = ["action type"] }, file with { Diagnostics = ["duplicate"] } })
                That(LegacyStatusConsumableDefinition.Parse(bad, template) is null, "复合或错位资源不能伪装为原生旧道具");
            var source = new InventoryItem(9, Coin, new(id, ItemKind.Consumable, 1));
            Bytes(ExperienceConsumablePackets.Legacy(new(null!, source, definition!, null!)), TcpPeer.Build(w =>
                { w.Write((byte)1); w.Write((ushort)65); w.Write((byte)kind); w.Write((uint)amount); w.Write(0u); }),
                "当前CMD30使用12B原生种类回执而不是旧客户端包号");
        }
        That(!LegacyStatusConsumableDefinition.IsNativeItem(3) && !LegacyStatusConsumableDefinition.IsNativeItem(28) &&
            !LegacyStatusConsumableDefinition.IsNativeItem(42) &&
            !LegacyStatusConsumableDefinition.IsNativeItem(7518) && !LegacyStatusConsumableDefinition.IsNativeItem(7519),
            "不复活失效水、不再次消费钱包币、不把语义变化的同编号物品当旧升级券");
        Console.WriteLine($"PASS {Count - checks} 旧状态道具资源映射及原生字节断言");
    }

    public static void Run(TownCatalog towns, PvfArchive pvf, CharacterCatalog characters, ItemResources items, SkillLearningCatalog skills)
    {
        int checks = Count;
        var growth = SelfTestCatalogs.Growth(pvf, skills);
        var parameters = SelfTestCatalogs.QuestRewardParameters(pvf);
        var service = new LegacyStatusConsumableService(items.Items, items.GoldLimits);
        That(items.Items.LegacyStatusConsumable(28) is null &&
            items.Items.LegacyStatusConsumable(7180) is { Amount: 1 } && items.Items.LegacyStatusConsumable(7181) is { Amount: 5 } &&
            items.Items.LegacyStatusConsumable(3) is null && items.Items.LegacyStatusConsumable(42) is null,
            "真实PVF两种币券可消费，不将未证本版发送者的28或钱包42当作普通道具");
        That(!items.RecoveryConsumables.TryGet(7180, out _) && !items.RecoveryConsumables.TryGet(7181, out _),
            "原生币券只能沿CMD30到账，不能被普通44当成纯客户端效果吞掉");
        using (var f = Make())
        {
            var before = f.Read();
            That(f.Session.UseLegacyStatusConsumable(Coin, service).Outcome == ConsumableUseOutcome.Committed &&
                f.Session.UseLegacyStatusConsumable(Book, service).Outcome == ConsumableUseOutcome.Committed &&
                f.Read().Inventory.ReviveCoins == before.Inventory.ReviveCoins + 6,
                "两种币券一次各扣一件，真实角色钱包增加1和5");
            var saved = f.Read();
            That(f.Session.UseLegacyStatusConsumable(Reset, service).Outcome == ConsumableUseOutcome.Rejected && Same(saved, f.Read()),
                "28缺少当前客户端消费依据，拒绝伪造30请求且保留原技能、点数和源");
            f.Session.ReturnToRoster(); f.Session.Select(0); f.Session.MarkReady();
            That(Same(saved, f.Session.Selected!) && Same(saved, new CharacterRepository(f.OpenPath).LoadCharacter(f.Owner, 0)),
                "重选和新仓储恢复同一币余额及原双树状态");
        }
        {
            using var f = Make();
            var before = f.Read(); var plan = service.Prepare(before, Coin);
            f.Sql("CREATE TRIGGER legacy_abort AFTER UPDATE OF revive_coins ON characters BEGIN SELECT RAISE(ABORT,'legacy rollback'); END");
            Reject<SqliteException>(() => f.Repository.CommitLegacyStatusConsumable(f.Owner, plan, DateTimeOffset.UtcNow), "扣物后的真实SQL失败");
            That(Same(before, f.Read()) && Same(before, f.Session.Selected!), "SQL失败回滚源、余额、双树及revision");
            f.Sql("DROP TRIGGER legacy_abort");
            f.Repository.CommitLegacyStatusConsumable(f.Owner, plan, DateTimeOffset.UtcNow);
            Reject<InvalidOperationException>(() => f.Repository.CommitLegacyStatusConsumable(f.Owner, plan, DateTimeOffset.UtcNow), "旧revision不得重复执行");
        }
        using (var f = Make(lost: true))
        {
            var before = f.Read(); f.Lost!.LoseLegacyStatusResult = true;
            Reject<IOException>(() => f.Session.UseLegacyStatusConsumable(Coin, service), "真实币券提交后结果丢失");
            That(f.Read().Inventory.ReviveCoins == before.Inventory.ReviveCoins + 1 &&
                f.Session.UseLegacyStatusConsumable(Coin, service).Outcome == ConsumableUseOutcome.CloseConnection &&
                f.Read().Inventory.Find(Coin)!.Core.Count == 1, "提交不明不重新赠币或再扣源");
        }
        using (var f = Make(coins: int.MaxValue - 1))
        {
            var before = f.Read();
            That(f.Session.UseLegacyStatusConsumable(Book, service).Outcome == ConsumableUseOutcome.Rejected && Same(before, f.Read()),
                "币券不能完整到账时拒绝，不截断余额却吞掉券");
        }
        var clock = new ConsumableUseSessionTests.FakeClock();
        using (var f = Make(expires: checked((int)clock.GetUtcNow().ToUnixTimeSeconds() + 1)))
        {
            var before = f.Read();
            var pending = new LegacyStatusConsumableService(items.Items, items.GoldLimits, clock).Prepare(before, Coin);
            var requested = clock.GetUtcNow();
            clock.Advance(TimeSpan.FromSeconds(1));
            Reject<InventoryQualificationExpiredException>(() => f.Repository.CommitLegacyStatusConsumable(f.Owner, pending, requested),
                "持有排队前时间的币券计划仍须在实际写事务中复验源期限");
            That(Same(before, f.Read()), "源券在提交前到期不扣物、不赠币、不增加revision");
        }
        using (var f = Make())
        {
            var character = f.Read();
            foreach (var invalid in new[]
            {
                character with { Inventory = character.Inventory with { Items = character.Inventory.Items.Select(item => item.Position == Coin ?
                    item with { Core = item.Core with { EquipmentLockId = 1 } } : item).ToImmutableArray() } },
                character with { Inventory = character.Inventory with { Items = character.Inventory.Items.Select(item => item.Position == Coin ?
                    item with { Core = item.Core with { ExpireTime = 1 } } : item).ToImmutableArray() } }
            }) Reject<InvalidDataException>(() => service.Prepare(invalid, Coin), "锁定或到期券不可使用");
            Reject<InvalidDataException>(() => service.Prepare(character, Reset), "28不因保留旧端业务实现就允许直接消费");
        }
        Console.WriteLine($"PASS {Count - checks} 旧状态道具实际资源、拒绝边界与钱包原子事务断言");

        QuestOrdinaryCompletionTests.Fixture Make(bool lost = false, int coins = 3, int expires = 0) => new(towns, characters, skills, growth, parameters,
            items, QuestEligibilityTests.Quest(100), materialCount: 0, loseResult: lost, active: [], configure: candidate =>
            {
                var learning = SkillPointLedger.Synchronize(0, 0, 60, candidate.InitialLearning with
                    { Experience = growth.GetLevelStartExperience(60), BonusSp = 17, BonusTp = 5 }, skills);
                foreach (byte context in new byte[] { 0, 1 })
                {
                    var paid = SkillLearning.Plan(0, 0, 60, learning, new(context, 0, [new(5, false, 2)]), skills);
                    That(paid.Success, "旧币券消费前保留已学习的收费技能");
                    learning = paid.State!;
                }
                return candidate with { Level = 60, InitialLearning = learning, Inventory = candidate.Inventory with
                {
                    ReviveCoins = coins, Items = candidate.Inventory.Items.AddRange(new[]
                    {
                        new InventoryItem(0, Coin, new(7180, ItemKind.Consumable, 2) { ExpireTime = expires }),
                        new InventoryItem(0, Book, new(7181, ItemKind.Consumable, 2)),
                        new InventoryItem(0, Reset, new(28, ItemKind.Consumable, 1))
                    })
                } };
            });
    }

    static bool Same(Character left, Character right) => QuestCompletionRepositoryTests.Same(left, right);
}
