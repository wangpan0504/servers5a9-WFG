using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EquipmentInvestmentScoreTests
{
    internal static void RunCore()
    {
        int before = Count;
        var full = EquipmentInvestmentScore.Slots.Select(slot => Item(slot)).ToImmutableArray();
        var character = Character(full);
        InvestmentScoreResult Score(ImmutableArray<InventoryItem> items, Func<int, ItemTemplate>? get = null) =>
            EquipmentInvestmentScore.Calculate(EquipmentInvestmentScore.Capture(Character(items), 1000), get ?? Template);
        That(Score([]) is { Status: "Complete", Score: 0, Slots.Length: 12 }, "空穿戴是完整零分并返回12槽");
        var baseScore = Score(full);
        That(baseScore is { Score: 24050, Partial: false, ScoreCapped: false } && baseScore.Profile == EquipmentInvestmentScore.Profile,
            "95史诗12件+0建设分24050，版本独立于旧0.1");
        var plus10 = full.Select(item => item with { Core = item.Core with { Attr = 10 } }).ToImmutableArray();
        That(Score(plus10).Score == 26650, "全12件+10普通强化26650");
        var amplified = plus10.Select(item => item with { Core = item.Core with { AmplifyType = 1, AmplifyValue = 30 } }).ToImmutableArray();
        That(Score(amplified).Score == 27950, "全12件+10有效增幅27950，不重复算强化");
        var weapon = plus10[0];
        var forge8 = plus10.SetItem(0, weapon with { Core = weapon.Core with { GenuineUpgrade = 8 } });
        var reinforce12 = plus10.SetItem(0, weapon with { Core = weapon.Core with { Attr = 12 } });
        That(Score(forge8).Score == 26770 && Score(reinforce12).Score == 26770 &&
            Score(forge8.SetItem(0, forge8[0] with { Core = forge8[0].Core with { Attr = 12 } })).Score == 26770,
            "锻8等价建设12级，武器强化与锻造取max不叠加");
        int[] rarityBonus = [0, 100, 250, 450, 800, 350, 650];
        for (int rarity = 0; rarity <= 6; rarity++)
        {
            int selected = rarity;
            That(Score([Item(12)], id => Template(id) with { Rarity = selected }).Score == 2 * (1050 + rarityBonus[rarity]),
                "PVF稀有度编码独立映射：" + rarity);
        }
        That(Score([Item(12)], id => Template(id) with { Rarity = 77 }) is { Status: "Partial", Score: 2100 },
            "未知稀有度只缺稀有度奖金，不伪称完整也不丢已知等级");
        That(Score([Item(12, 10)], _ => throw new InvalidDataException("missing")) is { Partial: true, Score: 400 },
            "模板缺失仍保留实例已证建设分，基础分为0并标Partial");
        var mismatched = Score([Item(12), Item(14)], id => id == 1012 ? Template(id) with { EquipmentSlot = 13 } : Template(id));
        That(mismatched is { Partial: true, Score: 1850 } && mismatched.Slots[0].Notes.Contains("template_slot_or_kind_mismatch"),
            "错误模板槽只排除该槽，不污染其他装备");
        That(Score([Item(12)], id => Template(id) with { Kind = ItemKind.Material }).Slots[0].Score == 0,
            "模板种类不符不当装备计分");
        var capped = Score([Item(12, 31) with { Core = Item(12, 31).Core with { GenuineUpgrade = 127 } }],
            id => Template(id) with { MinimumLevel = 255 });
        That(capped is { ScoreCapped: true, Partial: false } && capped.Slots[0] is { MinimumLevel: 255, Upgrade: 31, Refinement: 127 } &&
            capped.Score == Score([Item(12, 15)], id => Template(id) with { MinimumLevel = 95 }).Score,
            "95/15/8封顶只影响计分，保留原值和警示，不改存档");
        foreach (byte type in new byte[] { 1, 2, 3, 4 })
            That(Score([Item(12, 10) with { Core = Item(12, 10).Core with { AmplifyType = type, AmplifyValue = 1 } }]).Score == 4300,
                "四种有效增幅等价建设奖金");
        foreach (byte type in new byte[] { 0x81, 0x82, 0x83, 0x84 })
            That(Score([Item(12, 10) with { Core = Item(12, 10).Core with { AmplifyType = type, AmplifyValue = 30 } }]) is
                { Score: 4100, Partial: false }, "未净化红字不计有效增幅奖金");
        foreach (var pair in new[] { ((byte)128, (ushort)30), ((byte)0, (ushort)30), ((byte)1, (ushort)0), ((byte)5, (ushort)1) })
            That(Score([Item(12, 10) with { Core = Item(12, 10).Core with { AmplifyType = pair.Item1, AmplifyValue = pair.Item2 } }]) is
                { Score: 4100, Partial: true }, "非法/未知增幅不补有效红字奖金");
        That(Score([Item(14) with { Core = Item(14).Core with { GenuineUpgrade = 8 } }]) is { Score: 1850, Partial: true },
            "非武器锻造不加分且显式标记");
        That(Score([Item(12) with { Core = Item(12).Core with { ExpireTime = 1000 } }]) is { Score: 0, Partial: false },
            "到期等号排除、不读模板、不伪造未知");
        That(Score([Item(12) with { Core = Item(12).Core with { ExpireTime = 1001 } }]).Score == 3700,
            "未来到期装备在捕获时仍计分");
        var broken = Item(12) with { Core = Item(12).Core with { Durability = 0 } };
        That(Score([broken]).Score == 3700 && Score([broken]).Slots[0].Notes.Contains("broken_investment_retained"),
            "建设分不是当前伤害，破损保留建设并说明");
        var excluded = full.Add(Item(13)).Add(Item(24)).Add(Item(30)).Add(Item(12) with { Uid = 99, Position = new(InventoryListType.Main, 9) });
        That(Score(excluded).Score == baseScore.Score, "称号副武器名牌和背包装备不计分");
        var cosmetic = full.Select(item => item with { Core = item.Core with { Value = 999999998, EnchantCardId = 111,
            EnchantUpgrade = 5, Emblem1 = 99, Rune = 88, EmancipateLevel = 9, Attr = 0xE0 } }).ToImmutableArray();
        That(Score(cosmetic).Score == baseScore.Score, "品级种子附魔徽章及未知高位等明确不计，不冒充DPS");
        var snapshot = EquipmentInvestmentScore.Capture(character, 1000);
        var changed = character with { Job = 4, Level = 1, Inventory = character.Inventory with { Items = plus10 }, Revision = 9 };
        That(EquipmentInvestmentScore.Calculate(snapshot, Template).Score == 24050 &&
            EquipmentInvestmentScore.Calculate(EquipmentInvestmentScore.Capture(changed, 1000), Template).Score == 26650 && snapshot.CharacterRevision == 4,
            "捕获值脱离后续角色快照；revision为捕获版本而非当前承诺");
        That(EquipmentInvestmentScore.Calculate(EquipmentInvestmentScore.Capture(character with { Job = 15, GrowType = 255 }, 1000), Template).Score == baseScore.Score,
            "相同装备跨职业同建设分，不额外使用客户端属性");
        foreach (var invalid in new[] { full.Add(full[0]), full.SetItem(1, full[1] with { Uid = full[0].Uid }),
            full.SetItem(0, full[0] with { Uid = 0 }), default(ImmutableArray<InventoryItem>) })
            That(Score(invalid) is { Status: "Invalid", Score: null }, "坏集合/重复槽/UID拒绝，不冒充0分");
        That(EquipmentInvestmentScore.Calculate(snapshot with { Items = snapshot.Items.Add(snapshot.Items[0]) }, Template).Score is null,
            "纯计算入口同样拒绝重复槽，不能绕过Capture");
        var top = full.Select(item => item with { Core = item.Core with { Attr = 15, AmplifyType = 4, AmplifyValue = 30 } }).ToImmutableArray();
        That(Score(top).Score == 31525, "全+15有效增幅31525独立固定向量");
        That(Score(full.Reverse().ToImmutableArray()).Score == baseScore.Score &&
            Score(full.Reverse().ToImmutableArray()).Slots.Select(row => (row.Slot, row.Score)).SequenceEqual(baseScore.Slots.Select(row => (row.Slot, row.Score))),
            "相同快照输入倒序不影响逐槽输出");
        bool expiryQueried = false;
        var expiry = Score([Item(12) with { Core = Item(12).Core with { ExpireTime = 1000 } }], id =>
            { expiryQueried = true; return Template(id); });
        That(!expiryQueried && expiry.Score == 0, "已过期槽不触碰资源目录");
        bool monotonic = true, caps = true;
        for (int u = 0; u <= 31; u++)
        {
            int previous = 0;
            for (int f = 0; f <= 127; f++)
            {
                var row = Score([Item(12, (byte)u) with { Core = Item(12, (byte)u).Core with { GenuineUpgrade = (byte)f } }]);
                monotonic &= row.Score >= previous;
                previous = row.Score!.Value;
                var bounded = Score([Item(12, (byte)Math.Min(u, 15)) with
                    { Core = Item(12, (byte)Math.Min(u, 15)).Core with { GenuineUpgrade = (byte)Math.Min(f, 8) } }]);
                caps &= row.Score == bounded.Score && row.ScoreCapped == (u > 15 || f > 8);
            }
        }
        That(monotonic && caps, "强化0..31和锻造0..127组合保持单调及封顶，不暗改原值");
        GmQuery();
        Console.WriteLine($"EquipmentInvestmentScoreTests: {Count - before} passed");
    }

    static InventoryItem Item(short slot, byte upgrade = 0) => new(slot, new(InventoryListType.Equipment, slot),
        new(1000 + slot, ItemKind.Equipment, 1) { Attr = upgrade, Durability = 30 });
    static Character Character(ImmutableArray<InventoryItem> items) => new(1, 0, "ScoreFixture", 4, default!, null)
        { Inventory = new(items) };
    static ItemTemplate Template(int id) => new(id, ItemKind.Equipment, (byte)(id - 1000), 95, 30, 1, 1, 4, 1, "[free]", ushort.MaxValue, 0, "fixture");

    static void GmQuery()
    {
        That(DevelopmentRequest.Parse("equipment-score Sender", "sender") is { Action: DevelopmentAction.EquipmentScore, Writes: false },
            "新查询命令有独立action且无写入标记");
        foreach (string invalid in new[] { "equipment-score", "equipment-score Sender extra" })
        {
            bool rejected = false;
            try { DevelopmentRequest.Parse(invalid, "sender"); }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, "查询参数严格校验");
        }
        using var fixture = new MailboxPersistenceTests.Fixture();
        var equipped = EquipmentInvestmentScore.Slots.Select(slot => Item(slot) with { Uid = 0 }).ToImmutableArray();
        var scored = fixture.Repository.Create(fixture.SenderOwner,
            new("ScoreHero", fixture.Sender.Position, null) { Level = 95, Inventory = new(equipped) });
        fixture.SenderOwner.Dispose();
        var catalog = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        catalog.PrefillTemplates(scored.Inventory.Items.Select(item => KeyValuePair.Create(item.Core.ItemId, Template(item.Core.ItemId))));
        string before = fixture.Snapshot();
        string query = DevelopmentGm.Answer("equipment-score ScoreHero", "sender", fixture.Repository, fixture.Owners, _ => null, items: catalog);
        That(query.StartsWith("OK ", StringComparison.Ordinal), "真实GM路由返回服务器快照建设分JSON");
        using var json = JsonDocument.Parse(query[3..]);
        var expected = EquipmentInvestmentScore.Calculate(EquipmentInvestmentScore.Capture(scored,
            json.RootElement.GetProperty("ObservedAt").GetInt64()), catalog.Get);
        That(json.RootElement.GetProperty("Profile").GetString() == EquipmentInvestmentScore.Profile &&
            json.RootElement.GetProperty("CharacterId").GetUInt16() == scored.Id &&
            json.RootElement.GetProperty("CharacterRevision").GetInt64() == scored.Revision &&
            json.RootElement.GetProperty("Status").GetString() == expected.Status &&
            json.RootElement.GetProperty("Score").GetInt32() == expected.Score && expected.Score == 24050 && fixture.Snapshot() == before,
            "查询不更新revision、不改数据库，返回明确角色与版本");
        That(DevelopmentGm.Answer("equipment-score Receiver", "sender", fixture.Repository, fixture.Owners, _ => null, items: catalog)
            .StartsWith("ERR ", StringComparison.Ordinal), "保留既有GM账号目标归属边界");
    }
}
