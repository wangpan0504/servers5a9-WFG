using static DfoServer.Testing.TestCheck;
using DfoServer.Infrastructure;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing.Inventory;

internal static class DisjointTests
{
    public static void Run(TownCatalog towns, CharacterCatalog characters, ItemResources itemResources)
    {
        int checks = Count;
        void Reject(Action action, string name)
        {
            bool failed = false;
            try { action(); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or SqliteException) { failed = true; }
            That(failed, name);
        }
        var catalog = itemResources.Items;
        var config = itemResources.Disjoint;
        var service = new InventoryDisjointService(catalog, itemResources.Disjoint, itemResources.GoldLimits, itemResources.EpicPieces, new FixedRandom(false));
        That(config.CubeItemId == 3037 && config.CubeBase == 150 && config.CubeMultipliers[6] == 1.53, "matched ordinary cube table");
        That(config.Additional[0] is null && config.Additional[1]!.Items.SequenceEqual(new[] { 3033, 3034, 3035, 3036 }) &&
            config.Additional[2]!.Items.Single() == 3166 && config.Additional[3]!.Items.Single() == 3167 &&
            config.Additional[4] is null && config.Additional[5]!.Items.Single() == 3229 && config.Additional[6]!.Items.Single() == 3332,
            "counted additional rows retain empty rarity rows");
        That(config.Souls[4]!.ItemId == 10158124 && config.Souls[5] is null && config.Souls[6]!.ItemId == 10099775 &&
            config.Souls[6]!.LevelDivisor == 16, "seven soul rows do not shift legendary into empty chronicle row");
        That(config.ExcludedSouls.Contains(102040538) && config.SpecialResults.Contains(100300212) && config.SpecialResults.Contains(450004),
            "resource exclusions and unimplemented special outputs retained");
        That(new[] { 2600468, 2600469, 2600470, 2600548, 10308018 }.Select(id => catalog.Get(id).PortableDisjointLevel)
            .SequenceEqual(new[] { 30, 50, 70, 85, 95 }), "all five portable tool levels include level 95");
        That((int)CurrencyKind.LegendarySoul == 13 && (int)CurrencyKind.EpicSoul == 14 &&
            CurrencyWallet.RewardKind(10099775) == CurrencyKind.LegendarySoul && CurrencyWallet.RewardKind(10158124) == CurrencyKind.EpicSoul,
            "soul names match PVF without moving persisted numeric balances");
        var synthetic = catalog.Get(10400) with { Rarity = 1, MinimumLevel = 95, DisjointValue = 10000 };
        var calculated = DisjointResultCalculator.Calculate(config, synthetic, new FixedRandom(false));
        That(calculated.SequenceEqual(new InventoryReward[] { new(3037, 90), new(3036, 10), new(10100116, 3) }),
            "normal result uses full source value, rarity multiplier, level divisor and wallet soul");
        calculated = DisjointResultCalculator.Calculate(config, synthetic, new FixedRandom(true));
        That(calculated.SequenceEqual(new InventoryReward[] { new(3037, 90), new(3033, 317), new(10100116, 5) }),
            "great result and fractional rounding use one planned random outcome");
        calculated = DisjointResultCalculator.Calculate(config, synthetic with { Id = 102040538 }, new FixedRandom(false));
        That(calculated.All(reward => reward.ItemId != 10100116), "excluded template does not grant soul");
        for (int rarity = 0; rarity < 7; rarity++)
        {
            calculated = DisjointResultCalculator.Calculate(config, synthetic with { Rarity = rarity }, new FixedRandom(false));
            var noSoul = DisjointResultCalculator.Calculate(config, synthetic with { Rarity = rarity }, new FixedRandom(false), grantSouls: false);
            That(noSoul.SequenceEqual(calculated.Where(row => row.ItemId != config.Souls[rarity]?.ItemId)),
                "毁装实例仅禁止灵魂，晶体和独立产物不丢失 " + rarity);
            var expected = config.Souls[rarity];
            That(expected is null ? calculated.All(reward => CurrencyWallet.RewardKind(reward.ItemId) is not (>= CurrencyKind.CommonSoul and <= CurrencyKind.EpicSoul)) :
                calculated.Any(reward => reward.ItemId == expected.ItemId && reward.Count > 0), "soul mapping for rarity " + rarity);
        }
        var request = DisjointRequests.Read([3, 0, 0, 255, 255, 0x78, 0x56, 0x34, 0x12]);
        That(request == new DisjointRequest(Bag(3), -1, 0x12345678), "nine-byte request preserves context independently of source/tool");
        foreach (int size in new[] { 0, 5, 8, 10 })
            Reject(() => DisjointRequests.Read(new byte[size]), "truncated/extended request rejected");
        var coat = InventoryRewardGrantService.Create(catalog.Get(10400)) with { SortLockFlag = 1, SealFlag = 1 };
        var tool = InventoryRewardGrantService.Create(catalog.Get(2600468), 2);
        var current = new Character(1, 0, "Disjoint", 0, towns.Room, towns.InitialReturn)
        {
            Inventory = new([new(1, Bag(3), coat), new(2, Bag(4), tool)])
        };
        var result = service.Disjoint(current, request with { ToolSlot = 4 }, out var receipt);
        That(result.Accepted && result.Plan!.After.Find(Bag(3)) is null && result.Plan.After.Find(Bag(4))!.Core.Count == 1,
            "quickbar source and portable tool work with sort lock and seal");
        That(result.Plan!.After.AccountWallet.Get(CurrencyKind.ClearCube) == 1 && result.Plan.After.AccountWallet.Get(CurrencyKind.CommonSoul) == 1,
            "ordinary outputs credit account wallet without physical cube/soul items");
        That(result.Plan.After.Find(Bag(4))!.Uid == 2 && (result.Plan.After.Find(Bag(4))!.Core with { Value = 2 }) == tool,
            "portable consumption preserves remaining UID and all attributes");
        var ack = DisjointPackets.Ack(result, receipt);
        That(ack.Length == 27 && ack[0] == 1 && BinaryPrimitives.ReadInt16LittleEndian(ack.AsSpan(1)) == 3 && ack[3] == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(4)) == ushort.MaxValue && ack[6] == 2 &&
            BinaryPrimitives.ReadInt16LittleEndian(ack.AsSpan(7)) == 358 && BinaryPrimitives.ReadInt32LittleEndian(ack.AsSpan(9)) == 3037 &&
            BinaryPrimitives.ReadInt32LittleEndian(ack.AsSpan(13)) == 1, "ACK includes extra owner word before byte count and ten-byte incremental rows");
        That(DisjointPackets.Ack(InventoryResult.Reject(19), null).SequenceEqual(new byte[] { 0, 19, 0 }), "failure uses common ushort error");
        That(!service.Disjoint(current with { Inventory = result.Plan.After }, request, out _).Accepted, "repeated source slot cannot yield twice");
        foreach (var invalid in new[] { request with { Position = Bag(9) }, request with { ToolSlot = -2 }, request with { ToolSlot = 3 },
            request with { ToolSlot = 65 }, request with { Position = new(InventoryListType.Equipment, 3) } })
            That(!service.Disjoint(current, invalid, out _).Accepted, "invalid source/tool never consumes assets");
        foreach (var bad in new[] { coat with { EquipmentLockId = 1 }, coat with { ExpireTime = 1 }, coat with { AmplifyType = 0x80 },
            coat with { DisjointForbidden = true } })
            That(!service.Disjoint(current with { Inventory = new([new(1, Bag(3), bad)]) }, request, out _).Accepted, "locked/expired/unidentified source retained");
        var tooHigh = current with { Inventory = new([new(1, Bag(3), InventoryRewardGrantService.Create(catalog.Get(10117))), new(2, Bag(4), tool)]) };
        That(!service.Disjoint(tooHigh, request with { ToolSlot = 4 }, out _).Accepted && service.Disjoint(tooHigh, request, out _).Accepted,
            "portable level cap does not become a blanket NPC level limit");
        // 保留工单六个原始样本，避免用任意一件史诗代替90/95级套装的资源合同。
        foreach (var (id, level, pieceId) in new[]
        {
            (100060651, 95, 10305633), (100160525, 90, 10300751), (100110584, 90, 10300741),
            (100260565, 90, 10300771), (100210549, 90, 10300761), (100060598, 90, 10300731)
        })
        {
            var template = catalog.Get(id);
            That(template.Kind == ItemKind.Equipment && template.Rarity == 4 && template.MinimumLevel == level &&
                template.Grade == level + 6 && template.AttachType.Trim('`', ' ', '\r', '\n') == "[trade]" && !template.DisjointForbidden &&
                template.Expiration == new ItemExpiration() && config.PieceResults[id] == pieceId &&
                itemResources.EpicPieces.ByPiece[pieceId].EquipmentId == id && !config.EventResults.Contains(id) &&
                !config.IndependentResults.ContainsKey(id), "工单装备仅追加对应碎片，无禁止标记或活动分支：" + id);
            var grant = InventoryRewardGrantService.TryGrantBatch(current with { Inventory = InventorySnapshot.Empty },
                [new(id, 1)], catalog, itemResources.GoldLimits, itemResources.EpicPieces);
            var granted = grant.Plan!.After.Items.Single().Core;
            var dropped = DungeonDropCreation.Create(template, 1, _ => 123, DateTimeOffset.UtcNow);
            That(grant.Accepted && granted == InventoryRewardGrantService.Create(template) &&
                dropped.Value == 123 && (dropped with { Value = granted.Value }) == granted,
                "GM直接入包与普通购买共用创建策略，普通掉落仅改变品级种子：" + id);
            foreach (var core in new[] { granted, dropped, granted with
            {
                Attr = 12, Durability = 0, SealFlag = 1, TradeRestriction = 2,
                GenuineUpgrade = 3, AmplifyType = 1, AmplifyValue = 25
            } })
            {
                var source = current with { Inventory = new([new(1, Bag(3), core)]) };
                var decomposed = service.Disjoint(source, request, out var reportedReceipt);
                That(decomposed.Accepted && decomposed.Plan!.After.Find(Bag(3)) is null &&
                    reportedReceipt!.Rewards.Select(row => row.ItemId).SequenceEqual(new[] { 3037, 10158124, pieceId }) &&
                    reportedReceipt.Rewards.Single(row => row.ItemId == pieceId) == new DisjointReward(-1, pieceId, 110) &&
                    decomposed.Plan.After.EpicPieces.Get(itemResources.EpicPieces.ByPiece[pieceId].Index) == 110 &&
                    decomposed.Plan.After.Items.All(item => item.Core.ItemId != pieceId),
                    "原始装备及品级/耐久/强化/净化增幅/封装/交易计数变化均可NPC分解并完整入图鉴：" + id);
            }
            var prohibited = current with { Inventory = new([new(1, Bag(3), granted with { AmplifyType = 0x80 })]) };
            That(!service.Disjoint(prohibited, request, out _).Accepted,
                "原始史诗仍拒绝未净化红字，不因碎片支持绕过资格：" + id);
        }
        var forbidden = catalog.Get(100050791);
        var forbiddenResult = service.Disjoint(current with
        {
            Inventory = new([new(1, Bag(3), InventoryRewardGrantService.Create(forbidden))])
        }, request, out var forbiddenReceipt);
        That(forbidden.DisjointForbidden && !forbiddenResult.Accepted && forbiddenResult.Plan is null && forbiddenReceipt is null,
            "真实PVF禁止分解的限期装备仍整笔拒绝");
        var fullWallet = current with { Inventory = current.Inventory with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.ClearCube, int.MaxValue) } };
        That(!service.Disjoint(fullWallet, request with { ToolSlot = 4 }, out _).Accepted && fullWallet.Inventory.Find(Bag(3))!.Core == coat,
            "wallet overflow rejects whole plan rather than clipping reward");
        var material = InventoryRewardGrantService.Create(catalog.Get(3167), int.MaxValue);
        var fullItems = Enumerable.Range(121, 56).Select(index => new InventoryItem(index, Bag((short)index), material)).ToImmutableArray();
        var full = current with { Inventory = new(fullItems.Add(new(1, Bag(3), coat)).Add(new(2, Bag(4), tool))) };
        That(service.Disjoint(full, request, out _).Accepted, "wallet-only rewards require no free material slot");
        var artifact = InventoryRewardGrantService.Create(catalog.Get(10025));
        full = full with { Inventory = full.Inventory with { Items = full.Inventory.Items.Replace(full.Inventory.Find(Bag(3))!, new(1, Bag(3), artifact)) } };
        That(!service.Disjoint(full, request with { ToolSlot = 4 }, out _).Accepted, "full physical stacks reject without deleting source/tool");
        var first = full.Inventory.Find(Bag(121))!;
        full = full with { Inventory = full.Inventory with { Items = full.Inventory.Items.Replace(first, first with { Core = material with { Value = int.MaxValue - 4, SortLockFlag = 1 } }) } };
        result = service.Disjoint(full, request, out receipt);
        That(result.Accepted && result.Plan!.After.Find(Bag(121))!.Core.Count == int.MaxValue && receipt!.Rewards.Single(reward => reward.ItemId == 3167).Count == 4,
            "full page accepts exact existing-stack capacity and ACK gives only increment");
        That(result.Plan!.After.Find(Bag(121))!.Core.SortLockFlag == 1, "grant into sorting-locked stack preserves lock");
        var overflow = current with { Inventory = new([new(1, Bag(9), artifact), new(2, Bag(3), material with { Value = int.MaxValue - 2 }), new(3, Bag(4), tool)]) };
        string overflowBefore = JsonSerializer.Serialize(overflow);
        result = service.Disjoint(overflow, request with { Position = Bag(9), ToolSlot = 4 }, out receipt);
        That(!result.Accepted && result.Error == 4 && result.Plan is null && receipt is null && JsonSerializer.Serialize(overflow) == overflowBefore,
            "reward stack overflow rejects without spilling into an empty slot or consuming source, tool or wallet");

        string root = Path.Combine(Path.GetTempPath(), "s5a9-disjoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("disjoint", () => { });
            current = repo.Create(owner, new("DisjointOne", towns.Room, towns.InitialReturn)
            {
                Inventory = new([new(0, Bag(3), coat), new(0, Bag(4), tool), new(0, Bag(9), InventoryRewardGrantService.Create(catalog.Get(10114))),
                    new(0, Bag(10), InventoryRewardGrantService.Create(catalog.Get(400050000)))])
            });
            var session = new GameSession(repo, towns, owner);
            session.Select(0);
            session.MarkReady();
            current = session.Selected!;
            string before = Snapshot();
            Query("CREATE TRIGGER fail_disjoint BEFORE INSERT ON currency_audit_log BEGIN SELECT RAISE(ABORT,'disjoint rollback'); END");
            Reject(() => session.MutateInventory(character => service.Disjoint(character, request with { ToolSlot = 4 }, out _)), "SQL failure rejects before success");
            That(Snapshot() == before && session.Selected == current, "failure rolls back source, tool, wallet, revisions, audits and online state");
            Query("DROP TRIGGER fail_disjoint");
            result = service.Disjoint(current, request with { ToolSlot = 4 }, out _);
            current = InventoryService.Commit(repo, owner, current, result);
            Reject(() => repo.CommitInventory(owner, session.Selected!, result.Plan!), "old revision cannot commit disjoint again");
            That(!service.Disjoint(current, request, out _).Accepted, "saved deletion prevents duplicate grant");
            current = InventoryService.Commit(repo, owner, current, service.Disjoint(current, request with { Position = Bag(9) }, out _));
            current = InventoryService.Commit(repo, owner, current, service.Disjoint(current, request with { Position = Bag(10) }, out _));
            That(current.Inventory.AccountWallet.Get(CurrencyKind.EpicSoul) == 1 && current.Inventory.AccountWallet.Get(CurrencyKind.LegendarySoul) == 1,
                "real epic and legendary equipment produce different named currencies");
            current = InventoryService.Commit(repo, owner, current, InventoryRewardGrantService.TryGrantBatch(current, [new(10158124, 6), new(10099775, 8)], catalog, itemResources.GoldLimits, itemResources.EpicPieces));
            That(Query("SELECT balance FROM account_currency_balances WHERE currency_kind=13") is 9L &&
                Query("SELECT balance FROM account_currency_balances WHERE currency_kind=14") is 7L, "numeric soul rows preserve their item identity");
            That(JsonSerializer.Serialize(new CharacterRepository(path).List(owner).Single()) == JsonSerializer.Serialize(current),
                "restart restores exact disjoint result, tool remainder and wallet ownership");
            using var replacement = owners.Acquire(owner.Account, () => { });
            Reject(() => session.MutateInventory(character => service.Disjoint(character, request, out _)), "old generation cannot mutate inventory");
            That(Query("PRAGMA integrity_check") is "ok", "disjoint database integrity");

            object? Query(string sql)
            {
                using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False");
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            string Snapshot() => JsonSerializer.Serialize(repo.List(owner)) + Query("SELECT count(*) FROM inventory_audit_log") + ":" + Query("SELECT count(*) FROM currency_audit_log");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - checks} equipment disjoint assertions");
    }

    static InventorySlot Bag(short slot) => new(InventoryListType.Main, slot);
    sealed class FixedRandom(bool first) : Random
    {
        public override int Next(int maxValue) => first ? 0 : maxValue - 1;
    }
}
