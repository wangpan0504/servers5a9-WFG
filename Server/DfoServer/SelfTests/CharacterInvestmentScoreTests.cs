using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Characters;
using DfoServer.Game.CollectBox;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CharacterInvestmentScoreTests
{
    public static void RunCore()
    {
        int before = Count;
        var templates = new Dictionary<int, ItemTemplate>();
        var sets = new Dictionary<int, EquipmentSetInvestmentMetadata>();
        var tals = new Dictionary<int, TalismanDefinition>();
        var runes = new Dictionary<int, RuneDefinition>();
        var boxes = new Dictionary<byte, CollectBoxEntry>();
        ItemTemplate Get(int id) => templates[id];
        InventoryItem Item(short slot, ItemKind kind = ItemKind.Equipment, int rarity = 4, int grade = 1)
        {
            int id = 1000 + slot;
            var t = new ItemTemplate(id, kind, (byte)(kind == ItemKind.Talisman ? 34 : slot), 95, 30, 1, 0,
                rarity, grade, "[free]", ushort.MaxValue, 0, "fixture");
            templates[id] = t;
            return new(id, new(InventoryListType.Equipment, slot), new(id, kind, kind is ItemKind.Avatar or ItemKind.Creature ? id : 1) { Durability = 30 })
            { Avatar = kind == ItemKind.Avatar ? new() : null, Creature = kind == ItemKind.Creature ? new() { Level = 50 } : null };
        }
        Character Character(params InventoryItem[] items) => new(1, 0, "V2Fixture", 7, default!, null)
            { Level = 95, Inventory = new([.. items]) };
        CharacterInvestmentResult Score(Character c, Func<int, byte, ItemKind, byte, bool>? enchant = null) =>
            CharacterInvestmentScore.Calculate(CharacterInvestmentScore.Capture(c, 1000), Get,
                enchant ?? ((id, index, kind, slot) => id == 9000 && index == 2 && kind == ItemKind.Equipment && slot == 13),
                raw => raw == 0x00020001, id => sets.GetValueOrDefault(id), id => tals.GetValueOrDefault(id),
                id => runes.GetValueOrDefault(id), id => boxes[id], (_, type) => type == 1);
        int Module(CharacterInvestmentResult r, string module) => r.Parts.Where(p => p.Module == module).Sum(p => p.Score);
        That(Score(Character()) is { Status: "Complete", Score: 0 }, "全空模块是完整零分");
        var equipped = EquipmentInvestmentScore.Slots.Select(slot => Item(slot)).ToArray();
        That(Score(Character(equipped)) is { Status: "Complete", Score: 24050 }, "v2底座与v1一致且不重复加算");
        var title = Item(13); var avatar = Item(10, ItemKind.Avatar, grade: 3); var pet = Item(26, ItemKind.Creature);
        var artifact = Item(27, ItemKind.CreatureEquipment); var medal = Item(32, ItemKind.GuildMedal);
        var extended = Character([.. equipped, title, avatar, pet, artifact, medal]);
        var full = Score(extended);
        That(full.Score == 24050 + 700 + 350 + 1800 + 300 + 600 && !full.Partial, "称号武器时装宠物宠物装备勋章真实独立模块");
        That(Score(extended with { Job = 12, GrowType = 4 }).Score == full.Score, "建设常量职业中性不借面板三攻");
        That(Score(extended with { Inventory = new([.. extended.Inventory.Items.Reverse()]) }).Score == full.Score,
            "输入顺序不影响建设分");
        var captured = CharacterInvestmentScore.Capture(extended, 1000);
        That(!ReferenceEquals(captured.Worn[0], extended.Inventory.Items[0]) && !ReferenceEquals(captured.Worn[0].Core, extended.Inventory.Items[0].Core),
            "owner内冻结实例记录而非锁外持有活引用");
        That(Score(Character(title, title)).Status == "Invalid", "重复UID/槽拒绝而非重复计分");
        That(Score(Character(title with { Uid = 0 })).Status == "Invalid", "未提交UID不评分");
        var bad = Score(Character(title with { Core = title.Core with { ItemId = 88888 } }, avatar));
        That(bad.Partial && bad.Score == 350, "单个目录缺失只影响该模块，保留其它分数");
        var expired = title with { Core = title.Core with { ExpireTime = 1000, ItemId = 99999 } };
        That(Score(Character(expired)) is { Score: 0, Partial: false }, "到期含等号且不查询已过期模板");
        var expiredAvatar = avatar with { Avatar = avatar.Avatar! with { ExpireTime = 1000 } };
        That(Score(Character(expiredAvatar)).Score == 0, "时装detail期限优先");
        var expiredPet = pet with { Creature = pet.Creature! with { ExpireTime = 1000 } };
        That(Score(Character(expiredPet)).Score == 0, "宠物detail期限优先");
        That(Score(Character(pet with { Creature = pet.Creature! with { Level = 255 } })) is { Score: 1800, ScoreCapped: true }, "宠物50级建设封顶保留原值");
        That(Score(Character(medal with { Core = medal.Core with { Attr = 31 } })) is { Score: 750, ScoreCapped: true }, "勋章强化15封顶");
        foreach (short slot in new short[] { 13, 24, 30 })
            That(Score(Character(Item(slot))).Score == 700, "额外Equipment宿主同一明确规则：" + slot);
        That(Score(Character(Item(11, ItemKind.Avatar), Item(33, ItemKind.Creature))).Score == 0, "纯外观替换不重计");
        for (int rarity = 0; rarity < 7; rarity++)
        {
            int[] expected = [300, 350, 425, 525, 700, 475, 625];
            That(Score(Character(Item(13, rarity: rarity))).Score == expected[rarity], "称号各稀有度明确表：" + rarity);
        }
        title = Item(13);
        That(Score(Character(Item(13, rarity: 99))) is { Score: 300, Partial: true }, "未知档位只给最低存在分且Partial");
        title = Item(13);
        templates[9000] = templates[1013] with { Id = 9000, Kind = ItemKind.Material, IsMonsterCard = true };
        var enchanted = title with { Core = title.Core with { EnchantCardId = 9000, EnchantUpgrade = 2, Extension97 = 0x00020001 } };
        var ench = Score(Character(enchanted));
        That(ench.Score == 850 && Module(ench, "enchant") == 100 && Module(ench, "random-title") == 50, "称号普通和随机附魔分别计而非互斥");
        That(Score(Character(enchanted), (_, _, _, _) => false) is { Partial: true, Score: 750 }, "卡片/索引/目标失败只缺普通附魔");
        That(Score(Character(title with { Core = title.Core with { Extension97 = 0x00090001 } })) is { Partial: true, Score: 700 }, "未知随机附魔条目Partial");
        templates[9100] = templates[1013] with { Id = 9100, Kind = ItemKind.Emblem, Rarity = 6, EmblemTargetSockets = 16 };
        var embl = title with { Core = title.Core with { EmblemSocketCount = 1, Emblem1 = 9100 } };
        That(Score(Character(embl)).Score == 800, "白金徽章家族6单独100，不借传说装备奖励");
        That(Score(Character(embl with { Core = embl.Core with { EmblemSocketCount = 0 } })).Partial, "未开孔藏徽章拒绝");
        templates[9100] = templates[9100] with { EmblemTargetSockets = 1 };
        That(Score(Character(embl)) is { Score: 700, Partial: true }, "徽章不兼容孔色不加分");
        templates[9100] = templates[9100] with { EmblemTargetSockets = 16 };
        var five = avatar with { Avatar = avatar.Avatar! with { Socket0 = new(16, 9100), Socket4 = new(16, 9100) } };
        That(Score(Character(five)).Score == 550, "时装第五孔计入，重复同模板不同已装孔合法");
        templates[90001] = templates[1032] with { Id = 90001, Kind = ItemKind.GuardianGem, Rarity = 4, GuardianGemEffects = ["strength"] };
        var gemmed = medal with { Core = medal.Core with { GuardianGemKeys = 2 } };
        That(Score(Character(gemmed)).Score == 730, "守护珠key+89999解码并计实际孔");
        var dupGem = Score(Character(gemmed with { Core = gemmed.Core with { GuardianGemKeys = 2UL | (2UL << 16) } }));
        That(dupGem is { Score: 730, Partial: true }, "守护珠冲突属性不重复加分");
        templates[90002] = templates[90001] with { Id = 90002, Rarity = 1 };
        var lowFirst = gemmed with { Core = gemmed.Core with { GuardianGemKeys = 3UL | (2UL << 16) } };
        var highFirst = gemmed with { Core = gemmed.Core with { GuardianGemKeys = 2UL | (3UL << 16) } };
        That(Score(Character(lowFirst)) is { Score: 730, Partial: true } && Score(Character(highFirst)).Score == 730,
            "守护珠冲突保留最高一份，不由孔序改变结果");
        var tal = Item(35, ItemKind.Talisman);
        tals[tal.Core.ItemId] = new(templates[tal.Core.ItemId], [0], 5);
        tal = tal with { Core = new TalismanSocketColors(1, 2, 3).Apply(tal.Core) };
        var runeTemplate = templates[tal.Core.ItemId] with { Id = 9200, Kind = ItemKind.Rune };
        runes[9200] = new(runeTemplate, [0], 9, RuneCamp.Terracotta, 1);
        var ritem = new InventoryItem(9200, new(InventoryListType.Talisman, 213), new(9200, ItemKind.Rune, 1));
        That(Score(Character(tal, ritem)).Score == 250, "第二护石共享34模板槽，绑定符文不要求相同技能");
        That(Score(Character(ritem)).Partial, "符文无父护石不计");
        That(Score(Character(tal, ritem with { Position = new(InventoryListType.Talisman, 214) })) is { Score: 200, Partial: true }, "符文阵营与孔色不符");
        for (int i = 0; i < 5; i++) sets[equipped[i].Core.ItemId] = new(7, [2, 3, 5]);
        var setScore = Score(Character(equipped));
        That(Module(setScore, "set") == 1000, "套装只取最高五件阈值，不叠加2/3/5");
        That(Module(Score(Character(equipped[..3])), "set") == 300, "三件阈值只一次且按不同宿主");
        boxes[1] = new(1, 1, 2, null, null, [new(0, 9300, 0), new(1, 9301, 0)], []);
        var withBox = Character() with { CollectBox = new(1, [new(1, 0, 9300), new(2, 0, 9400)], []) };
        That(Score(withBox).Score == 50, "只计当前选中箱有效格，不计其它箱库存");
        That(Score(withBox with { CollectBox = withBox.CollectBox with { Expirations = [new(1, 1000)] } }).Score == 0, "收集箱到期不补分");
        That(Score(withBox with { CollectBox = new(1, [new(1, 1, 9300)], []) }).Partial, "箱内物品与物理格不符Partial");
        var random = title with { Core = title.Core with { Random0 = new(1, 7, 8), RandomChange = new(1, 99, 99), RandomState = 1 } };
        That(Module(Score(Character(random)), "random-option") == 40, "只计已提交魔封选项，不计预览且不将字节当百分比");
        That(Score(Character(title with { Core = title.Core with { Chronicle0 = new(1, 0, 0, 0, 0) } })).Partial, "未明异界选项显式Partial");
        That(Module(Score(Character(random with { Core = random.Core with { Random1 = new(1, 0, 0) } })), "random-option") == 40 &&
            Score(Character(random with { Core = random.Core with { Random1 = new(1, 0, 0) } })).Partial, "同宿主重复魔封type不多拿分");
        That(Score(Character(avatar with { Avatar = avatar.Avatar! with { Socket0 = new(17, 9100) } })).Partial, "不接受未知组合孔色掩码");
        That(Score(Character(tal with { Core = tal.Core with { ExpireTime = 1000 } }, ritem)) is { Score: 0, Partial: false }, "过期护石连同附属符文已知排除");
        var inconsistent = captured with { Equipment = captured.Equipment with { Items = [] } };
        var rejected = CharacterInvestmentScore.Calculate(inconsistent, Get, (_, _, _, _) => true, _ => true,
            _ => null, _ => null, _ => null, id => boxes[id], (_, _) => true);
        That(rejected.Status == "Invalid", "不同基础装备投影不能在相同revision下拼接评分");
        var invalidBox = extended with { CollectBox = new(1, [new(1, 0, 1), new(1, 0, 2)], []) };
        That(Score(invalidBox).Partial && Score(invalidBox).Score == Score(extended).Score,
            "异常收集箱只降本模块，不把已知穿戴总计变为Invalid");
        That(Score(Character(pet with { Core = pet.Core with { EmblemSocketCount = 1, Emblem1 = 9100 } })) is { Partial: true, Score: 1800 },
            "不将非装备宿主异常两孔状态静默当作空徽章模块");
        That(DevelopmentRequest.Parse("character-score Hero", "owner") is { Action: DevelopmentAction.CharacterScore, Writes: false }, "新v2独立只读命令");
        using (var fixture = new MailboxPersistenceTests.Fixture())
        {
            var richTitle = enchanted with { Core = enchanted.Core with { EmblemSocketCount = 1, Emblem1 = 9100, Random0 = new(1, 7, 8) } };
            InventoryItem[] rich = [.. equipped, richTitle, five, pet, artifact, gemmed, tal, ritem, Item(24), Item(30)];
            var created = fixture.Repository.Create(fixture.SenderOwner,
                new("V2ScoreHero", fixture.Sender.Position, null) { Level = 95,
                    Inventory = new([.. rich.Select(i => i with { Uid = 0, Core = i.Core with { Value = i.Avatar is not null || i.Creature is not null ? 0 : i.Core.Value } })]) });
            created = fixture.Repository.CommitCollectBox(fixture.SenderOwner, new(created, withBox.CollectBox,
                new(created.Inventory, created.Inventory, "fixture_collectbox")));
            fixture.SenderOwner.Dispose();
            string beforeQuery = fixture.Snapshot();
            CharacterInvestmentResult Compute(CharacterInvestmentSnapshot snap) => CharacterInvestmentScore.Calculate(snap, Get,
                (id, index, kind, slot) => id == 9000 && index == 2 && kind == ItemKind.Equipment && slot == 13,
                raw => raw == 0x00020001, id => sets.GetValueOrDefault(id), id => tals.GetValueOrDefault(id),
                id => runes.GetValueOrDefault(id), id => boxes[id], (_, type) => type == 1);
            var request = DevelopmentRequest.Parse("character-score V2ScoreHero", "sender");
            string response = DevelopmentGm.QueryCharacterScore(fixture.Repository, fixture.Owners, null, request, Compute);
            using var json = JsonDocument.Parse(response[3..]);
            var root = json.RootElement;
            That(response.StartsWith("OK ", StringComparison.Ordinal) && root.GetProperty("Score").GetInt32() == 31120 &&
                root.GetProperty("Status").GetString() == "Complete" && root.GetProperty("Profile").GetString() == CharacterInvestmentScore.Profile &&
                root.GetProperty("CharacterId").GetUInt16() == created.Id && root.GetProperty("CharacterRevision").GetInt64() == created.Revision,
                "真实SQLite全模块角色经GM owner查询返回31120、完整状态、角色及revision；目录为synthetic");
            That(fixture.Snapshot() == beforeQuery, "全模块查询无资产或revision写入");
            bool denied = false;
            try { DevelopmentGm.QueryCharacterScore(fixture.Repository, fixture.Owners, null,
                DevelopmentRequest.Parse("character-score Receiver", "sender"), Compute); }
            catch (InvalidDataException) { denied = true; }
            That(denied && fixture.Snapshot() == beforeQuery, "跨账号目标仍由Target拒绝且不写库");
        }
        Console.WriteLine($"CharacterInvestmentScoreTests: {Count - before} passed");
    }
}
