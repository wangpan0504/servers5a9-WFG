using System.Text;
using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Projection;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class DimensionEnergyInvestmentTests
{
    public static void RunCore()
    {
        int before = Count, reads = 0;
        var strings = new List<string>();
        string Resolve(int id) => strings[id];
        PvfToken Text(string value, byte type = 6) { strings.Add(value); return new(type, strings.Count - 1); }
        PvfToken Tag(string value) => Text("[" + value + "]", 3);
        PvfToken N(int value) => new(0, value);
        // 与四份定向导出资源同形；这里只复用结构，不把合成保存实例称作真实消费历史。
        PvfTokenDocument Ticket(int step = 3, int max = 90, int action = 5, int rarity = 5, int min = 70,
            int limit = -1, bool duplicate = false, bool emptyProbability = false, bool ignore = true) => new([
            Tag("stackable type"), Text("[etc]"), N(1), Tag("emancipate ticket"), N(action),
            Tag("equipment level emancipate"), Tag("probability"),
            .. (emptyProbability ? Array.Empty<PvfToken>() : new[] { N(75), N(100000), N(85), N(100000) }), Tag("/probability"),
            Tag("equipment upgrade level"), N(step), Tag("equipment condition"), Tag("rarity"), N(rarity),
            Tag("minimum level"), N(min), Tag("maximum level"), N(max),
            .. (duplicate ? new[] { Tag("maximum level"), N(max) } : Array.Empty<PvfToken>()),
            Tag("/equipment condition"), Tag("ignore index"),
            .. (ignore ? new[] { N(450114), N(440343) } : Array.Empty<PvfToken>()),
            Tag("/ignore index"), Tag("/equipment level emancipate"),
            Tag("emancipate grade max"), N(limit), Tag("emancipate amplify max"), N(-1),
            Tag("emancipate genuineGrade max"), N(-1)], Resolve);
        var ids = new[] { 10094062, 10094063, 10304573, 490000095 };
        var docs = ids.ToDictionary(id => "stackable/" + id + ".stk", id => Ticket(id is 10094063 or 10304573 ? 5 : 3, id == 490000095 ? 86 : 90));
        CharacterInvestmentMetadataCatalog Catalog() => new(path => { reads++; return docs.TryGetValue(path, out var doc) ? doc : throw new FileNotFoundException(path); },
            Resolve, new Dictionary<int, string>(), ids.ToDictionary(id => id, id => id + ".stk"));
        var template = new ItemTemplate(100, ItemKind.Equipment, 12, 85, 30, 1, 0, 5, 999, "[free]", ushort.MaxValue, 0, "fixture");
        var catalog = Catalog();
        That(catalog.ReadDimensionEnergy(template, 0).Supported && reads == 0, "零档不读取目录且品级999不误当所需等级");
        That(catalog.ReadDimensionEnergy(template, 5) is { Supported: true, EffectiveLevel: 90 } supported && supported.SourceIds.SequenceEqual(ids.Take(3)), "有效等级90并只记录匹配券，不宣称消费来源");
        That(catalog.ReadDimensionEnergy(template, 1).SourceIds.SequenceEqual(ids), "四份真实形状均可提供局部正向支持");
        That(!catalog.ReadDimensionEnergy(template, 6).Supported && !catalog.ReadDimensionEnergy(template, 255).Supported, "超90与byte255不封顶偷加分");
        That(!catalog.ReadDimensionEnergy(template with { Rarity = 4 }, 1).Supported, "稀有度条件不是卷轴自身rarity");
        foreach (int id in new[] { 450114, 440343 }) That(!catalog.ReadDimensionEnergy(template with { Id = id }, 1).Supported, "忽略ID不给建设奖");
        That(!catalog.ReadDimensionEnergy(template with { MinimumLevel = 69 }, 1).Supported && !catalog.ReadDimensionEnergy(template with { MinimumLevel = 90 }, 1).Supported, "原始最低等级与有效最高等级边界");
        That(catalog.ReadDimensionEnergy(template with { MinimumLevel = 70 }, 4).Supported && catalog.ReadDimensionEnergy(template with { MinimumLevel = 70 }, 20).Supported, "本服支持包络不是3/5历史可达图且不cap5");
        That(!catalog.ReadDimensionEnergy(template with { Kind = ItemKind.Avatar }, 1).Supported, "非装备宿主不认可");
        That(catalog.ReadDimensionEnergy(template with { MinimumLevel = 88 }, 2).Supported &&
            !catalog.ReadDimensionEnergy(template with { MinimumLevel = 88 }, 3).Supported, "最大等级截尾后的2档受支持，不要求恰好3或5档");
        bool boundaryMatrix = true;
        for (int d = 1; d <= byte.MaxValue; d++)
            boundaryMatrix &= catalog.ReadDimensionEnergy(template, (byte)d).Supported == (d <= 5);
        That(boundaryMatrix, "85级全部非零byte档位支持边界一致");
        var original = docs.ToDictionary(p => p.Key, p => p.Value);
        docs.Clear(); docs["stackable/10094062.stk"] = Ticket();
        That(Catalog().ReadDimensionEnergy(template, 5) is { Supported: true } one && one.SourceIds.SequenceEqual(new[] { 10094062 }), "其他券缺失不污染已闭合的局部正证");
        foreach (var bad in new[] { Ticket(action: 61), Ticket(step: 0), Ticket(step: 256), Ticket(max: 70), Ticket(min: 0), Ticket(rarity: 7), Ticket(limit: 10), Ticket(duplicate: true), Ticket(emptyProbability: true), Ticket(ignore: false) })
        {
            docs["stackable/10094062.stk"] = bad;
            That(Catalog().ReadDimensionEnergy(template, 1) is { Supported: false, Reason: "dimension_energy_metadata_unavailable" }, "坏条件局部未知而非成功或全局异常");
        }
        docs.Clear(); foreach (var pair in original) docs.Add(pair.Key, pair.Value); catalog = Catalog();
        var weapon = new InventoryItem(1, new(InventoryListType.Equipment, 12), new(100, ItemKind.Equipment, 1) { Durability = 30, EmancipateLevel = 5 });
        Character Character(InventoryItem item) => new(1, 0, "次元测试", 7, default!, null) { Level = 95, Inventory = new([item]) };
        CharacterInvestmentResult Calculate(CharacterInvestmentSnapshot snapshot, int version = 22) => CharacterInvestmentScore.Calculate(snapshot,
            id => id == 100 ? template : throw new KeyNotFoundException(), (_, _, _, _) => false, _ => false, _ => null,
            _ => null, _ => null, _ => throw new KeyNotFoundException(), (_, _) => false,
            version >= 21 ? _ => throw new InvalidOperationException("空气息不得读目录") : null,
            version >= 22 ? catalog.ReadDimensionEnergy : null);
        CharacterInvestmentResult Score(byte d, int version = 22) => Calculate(CharacterInvestmentScore.Capture(Character(weapon with { Core = weapon.Core with { EmancipateLevel = d } }), 1000), version);
        That(Score(5) is { Score: 2700, Partial: false, Profile: CharacterInvestmentScore.DimensionEnergyProfile }, "85级异界武器底座2600加5档100且不重复改变底座等级");
        That(Score(0) is { Score: 2600, Partial: false } && Score(3).Score == 2660, "零档完整与20每档规则");
        That(Score(255) is { Score: 2600, Partial: true }, "不支持状态保留基础分局部Partial");
        That(Score(5, 20) is { Score: 2600, Partial: true, Profile: CharacterInvestmentScore.Profile } && Score(5, 21) is { Score: 2600, Partial: true, Profile: CharacterInvestmentScore.ChronicleProfile }, "旧v2和v2.1仍可复算原未解释规则");
        That(Calculate(CharacterInvestmentScore.Capture(Character(weapon with { Core = weapon.Core with { ExpireTime = 1000 } }), 1000)).Score == 0, "到期宿主次元建设奖同时排除");
        That(Score(5).Parts.All(p => p.RuleVersion == (p.Module == "equipment" ? EquipmentInvestmentScore.Profile : CharacterInvestmentScore.DimensionEnergyProfile)), "分项版本不静默沿用v2.1");
        using (var fixture = new MailboxPersistenceTests.Fixture())
        {
            var created = fixture.Repository.Create(fixture.SenderOwner, new("DimScoreHero", fixture.Sender.Position, null) { Level = 95, Inventory = new([weapon with { Uid = 0 }]) });
            fixture.SenderOwner.Dispose(); string beforeQuery = fixture.Snapshot();
            string response = DevelopmentGm.QueryCharacterScore(fixture.Repository, fixture.Owners, null,
                DevelopmentRequest.Parse("character-score DimScoreHero", "sender"), snapshot => Calculate(snapshot));
            using var json = JsonDocument.Parse(response[3..]); var root = json.RootElement;
            That(root.GetProperty("Score").GetInt32() == 2700 && root.GetProperty("Profile").GetString() == CharacterInvestmentScore.DimensionEnergyProfile && root.GetProperty("Status").GetString() == "Complete" && root.GetProperty("CharacterId").GetUInt16() == created.Id, "真实SQLite保存byte经GM冻结快照只读计分");
            That(fixture.Snapshot() == beforeQuery, "计分不改变数据库或装备真实属性");
        }
        using (var fixture = new CharacterScoreNoticeTests.Fixture())
        {
            var capture = fixture.Session.CaptureCharacterScoreNotice("notice", "NoticeHero");
            byte[]? body = null;
            var result = CharacterScoreProjection.Send(fixture.Session, capture,
                snapshot => fixture.Calculate(snapshot) with { Profile = CharacterInvestmentScore.DimensionEnergyProfile }, _ => null,
                (_, bytes, _, _) => { body = bytes; return true; }, "0123456789abcdef0123456789abcdef");
            That(result.Status == "queued" && Encoding.ASCII.GetString(body!).Contains("|v2.2|", StringComparison.Ordinal), "机器回包版本同步v2.2且不宣称已经显示");
            body = null;
            CharacterScoreProjection.Send(fixture.Session, capture,
                snapshot => fixture.Calculate(snapshot) with { Profile = CharacterInvestmentScore.DimensionEnergyProfile }, _ => null,
                (_, bytes, _, _) => { body = bytes; return true; });
            That(Encoding.UTF8.GetString(body!).Contains(CharacterInvestmentScore.DimensionEnergyProfile, StringComparison.Ordinal), "摘要展示完整v2.2版本");
        }
        Console.WriteLine($"DimensionEnergyInvestmentTests: {Count - before} passed");
    }
}
