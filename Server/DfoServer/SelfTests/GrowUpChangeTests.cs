using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GrowUpChangeTests
{
    internal const uint StartGold = 1_000_000;

    public static void RunCore()
    {
        int checks = Count;
        byte[] Body(byte target, byte confirm, byte fill = 0xCC)
        {
            var body = Enumerable.Repeat(fill, 15).ToArray();
            body[13] = target; body[14] = confirm;
            return body;
        }
        That(GrowUpChangeRequests.Read(Body(2, 0)) == new GrowUpChangeRequest(2, false) &&
            GrowUpChangeRequests.Read(Body(4, 1, 0x00)) == new GrowUpChangeRequest(4, true),
            "785按offset13目标、offset14确认读取，前13B未初始化字节不参与语义");
        var tailed = Body(3, 1).Concat(BitConverter.GetBytes(ClientBodyChecksum.Compute(Body(3, 1)))).ToArray();
        That(GrowUpChangeRequests.Read(tailed) == new GrowUpChangeRequest(3, true, true), "完全匹配的4B检查尾可接受");
        tailed[^1] ^= 1;
        Reject<InvalidDataException>(() => GrowUpChangeRequests.Read(tailed), "检查尾不匹配时不截断借用");
        foreach (int length in new[] { 0, 2, 14, 16, 18, 20 })
            Reject<InvalidDataException>(() => GrowUpChangeRequests.Read(new byte[length]), "785不吞缺字段或额外尾部");
        Reject<InvalidDataException>(() => GrowUpChangeRequests.Read(Body(2, 2)), "确认位只允许0/1");
        Bytes(GrowUpChangePackets.Committed(1), [1, 0, 0, 0, 0, 1], "ACK785成功为01+result0+已变更次数");
        Bytes(GrowUpChangePackets.Quote(3), [1, 133, 0, 0, 0, 3], "ACK785询价result133仍带当前次数");
        Bytes(GrowUpChangePackets.Failure(10), [0, 10, 0], "ACK785失败保持公共3B");
        Bytes(GrowUpChangePackets.ManageInfo(2, 30), [2, 30, 0, 0, 0], "NOTI1153为count:sbyte+level:i32");
        Reject<ArgumentOutOfRangeException>(() => GrowUpChangePackets.Committed(128), "次数超过sbyte不回绕");
        var script = new Script();
        script.Tag("[grow up change lv]"); script.Int(15); script.Int(49); script.Tag("[/grow up change lv]");
        script.Tag("[grow up change gold]"); foreach (int fee in new[] { 0, 10, 20 }) script.Int(fee); script.Tag("[/grow up change gold]");
        var parsed = GrowUpChangeCatalog.Parse(script.Tokens, script.Resolve);
        That(parsed.MinimumLevel == 15 && parsed.MaximumLevel == 49 && parsed.Fee(0) == 0 && parsed.Fee(2) == 20 && parsed.Fee(9) == 20,
            "金币表按次数索引，超出表长取末项");
        var reversed = new Script();
        reversed.Tag("[grow up change lv]"); reversed.Int(49); reversed.Int(15); reversed.Tag("[/grow up change lv]");
        reversed.Tag("[grow up change gold]"); reversed.Int(0); reversed.Tag("[/grow up change gold]");
        Reject<InvalidDataException>(() => GrowUpChangeCatalog.Parse(reversed.Tokens, reversed.Resolve), "等级区间必须递增");
        var empty = new Script();
        empty.Tag("[grow up change lv]"); empty.Int(15); empty.Int(49); empty.Tag("[/grow up change lv]");
        Reject<InvalidDataException>(() => GrowUpChangeCatalog.Parse(empty.Tokens, empty.Resolve), "缺金币表不默认免费");
        Console.WriteLine($"PASS {Count - checks} 技能窗职业变更785/1153协议与GrowUp脚本断言");
    }

    internal static NewCharacter CharacterFixture(TcpTestResources resources, byte grow = 0x01, byte level = 30, string name = "GrowHero")
    {
        var fixture = ClassChangeTests.CharacterFixture(resources, grow, level);
        return fixture with { Name = name, Inventory = fixture.Inventory with { Gold = StartGold } };
    }

    public static void RunResources(TcpTestResources resources)
    {
        int checks = Count;
        var now = DateTimeOffset.UtcNow;
        var catalog = GrowUpChangeCatalog.Load(resources.Archive);
        That(catalog.MinimumLevel == 15 && catalog.MaximumLevel == 49 &&
            catalog.Fees.SequenceEqual([0, 100113, 133484, 177979, 237305, 316406, 421875, 562500, 750000, 1000000]) &&
            catalog.Fee(0) == 0 && catalog.Fee(1) == 100113 && catalog.Fee(9) == 1000000 && catalog.Fee(40) == 1000000,
            "当前PVF的GrowUp.etc：15–49级，一次免费后按次数收费，超表长取末项");
        var service = new GrowUpChangeService(catalog,
            new ClassChangeService(resources.ItemResources.Items, resources.Characters, resources.Learning, resources.Quests));
        var path = Path.Combine(Path.GetTempPath(), "growup-change-" + Guid.NewGuid().ToString("N"), "game.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var repository = new CharacterRepository(path, resources.Quests);
            new AuthenticationService(new AuthenticationRepository(path), 100000).Create("growup", NativeLoginWire.Digest);
            using var owner = new AccountOwners().Acquire("growup", () => { });
            var fixture = CharacterFixture(resources);
            repository.Create(owner, fixture);
            var before = repository.LoadCharacter(owner, 0);
            That(repository.LoadGrowUpChangeCount(owner, before.Id) == 0 && before.Inventory.Gold == StartGold, "新角色从0次开始");
            foreach (var (character, count, target, error) in new (Character, int, byte, ushort)[]
            {
                (before, 0, 0, 1), (before, 0, 6, 1), (before, 0, 1, 3), (before with { Level = 14 }, 0, 2, 23),
                (before with { Level = 50 }, 0, 2, 23), (before with { GrowType = 0 }, 0, 2, 19), (before with { GrowType = 0x11 }, 0, 2, 23),
                (before with { Job = 9 }, 0, 2, 235), (before, GrowUpChangeService.MaximumCount, 2, 23),
                (before with { Inventory = before.Inventory with { Gold = 100112 } }, 1, 2, 10)
            })
                That(service.Prepare(character, count, target, now) is { Plan: null } rejected && rejected.Error == error,
                    $"非法目标/原分支/等级/未转职/觉醒/特殊职业/次数上限/金币不足均拒绝: {error}");
            That(service.Prepare(before with { Level = 15 }, 0, 2, now).Plan is not null &&
                service.Prepare(before with { Level = 49 }, 0, 2, now).Plan is not null, "15与49级边界可规划");
            var career = resources.Quests.All.First(quest => quest.Definition?.JobChange == 1);
            var active = before with { ActiveMainQuests = [new(0, checked((ushort)career.Id), 0) { AcceptanceId = Guid.NewGuid() }] };
            That(service.Prepare(active, 0, 2, now).Error == 22, "活动转职任务不被菜单变更覆盖");
            var free = service.Prepare(before, 0, 2, now).Plan!;
            That(free.Fee == 0 && !free.Inventory.Changed && free.GrowType == 0x02, "0次免费，不产生库存差额");
            var first = repository.CommitGrowUpChange(owner, free);
            That(first.Count == 1 && first.Character.GrowType == 0x02 && first.Character.Revision == before.Revision + 1 &&
                first.Character.Inventory.Gold == StartGold && repository.LoadGrowUpChangeCount(owner, before.Id) == 1,
                "免费变更提交分支、次数和revision，不扣金币");
            VerifyFree(first.Character, resources.Learning);
            Reject<InvalidOperationException>(() => repository.CommitGrowUpChange(owner, free), "旧计划不能重复提交或再推进次数");
            var paid = service.Prepare(first.Character, 1, 3, now).Plan!;
            That(paid.Fee == 100113 && paid.Inventory.After.Gold == StartGold - 100113, "第二次按表扣100113金币");
            Sql("CREATE TRIGGER growup_late BEFORE INSERT ON character_growup_change BEGIN SELECT RAISE(ABORT,'growup rollback'); END;" +
                "CREATE TRIGGER growup_late_update BEFORE UPDATE ON character_growup_change BEGIN SELECT RAISE(ABORT,'growup rollback'); END");
            string snapshot = JsonSerializer.Serialize(repository.LoadCharacter(owner, 0));
            Reject<SqliteException>(() => repository.CommitGrowUpChange(owner, paid), "次数末段失败回滚");
            That(JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == snapshot && repository.LoadGrowUpChangeCount(owner, before.Id) == 1,
                "失败不扣金币、不改职业、两树、任务或次数");
            Sql("DROP TRIGGER growup_late; DROP TRIGGER growup_late_update");
            var second = repository.CommitGrowUpChange(owner, paid);
            That(second.Count == 2 && second.Character.GrowType == 0x03 && second.Character.Inventory.Gold == StartGold - 100113,
                "收费变更同事务扣金币、改分支、次数+1");
            var reloaded = new CharacterRepository(path, resources.Quests);
            That(reloaded.LoadGrowUpChangeCount(owner, before.Id) == 2 &&
                JsonSerializer.Serialize(reloaded.LoadCharacter(owner, 0)) == JsonSerializer.Serialize(second.Character),
                "重建仓储后保留次数、分支、两树与金币");
            var session = new GameSession(repository, resources.Towns, owner, questCatalog: resources.Quests);
            session.Select(0); session.MarkReady();
            string selected = JsonSerializer.Serialize(reloaded.LoadCharacter(owner, 0));
            var quote = session.ChangeGrowUp(service, new(4, false), now);
            That(quote is { Error: 0, Quoted: true, Count: 2 } && quote.Plan!.Fee == 133484 && session.CanPublishGrowUpChange(quote) &&
                JsonSerializer.Serialize(reloaded.LoadCharacter(owner, 0)) == selected && reloaded.LoadGrowUpChangeCount(owner, before.Id) == 2, "收费首包只报价，不扣费或改职业");
            That(session.ChangeGrowUp(service, new(5, true), now).Error == 23, "确认目标与报价不同则拒绝");
            That(session.ChangeGrowUp(service, new(4, true), now).Error == 23, "拒绝后报价作废，不能直接确认");
            That(session.ChangeGrowUp(service, new(4, false), now).Quoted, "重新询价");
            var third = session.ChangeGrowUp(service, new(4, true), now);
            That(third is { Error: 0, Quoted: false, Count: 3 } && third.Character!.GrowType == 0x04 &&
                third.Character.Inventory.Gold == StartGold - 100113 - 133484 && session.CanPublishGrowUpChange(third),
                "按报价确认后提交第三次变更");
            That(session.ChangeGrowUp(service, new(5, true), now).Error == 23, "已用报价不能再次确认");
            That(session.ChangeGrowUp(service, new(5, false), now).Quoted, "再次询价");
            session.ReturnToRoster();
            That(!session.CanPublishGrowUpChange(third) && session.ChangeGrowUp(service, new(5, true), now).Error == 30,
                "返回选角后旧报价和旧发布回执失效");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(Path.GetDirectoryName(path)!, true); }
        Console.WriteLine($"PASS {Count - checks} 技能窗职业变更资格、费用、报价及真实事务断言");

        void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True");
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }
    }

    static void VerifyFree(Character character, SkillLearningCatalog catalog)
    {
        var free = SkillPointLedger.FreeBaseline(character.Job, character.GrowType, character.Level, catalog,
            catalog.ActiveExpertContractLevels(character.Learning.ExpertContractExpiresAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        That(character.Learning.Trees.All(tree => tree.Sp == catalog.TotalSp(character.Level) + 137 && tree.Tp == catalog.TotalTp(character.Level) + 11 &&
            tree.Skills.Count == free.Count && tree.Skills.All(skill => free.GetValueOrDefault(skill.Id) == skill.Level && skill.Command.Length == 0)),
            "变更后两树只保留目标免费基线，已用SP/TP完整返还，额外来源保留");
    }

    sealed class Script
    {
        readonly List<string> strings = [];
        readonly List<PvfToken> tokens = [];
        public void Tag(string name) { strings.Add(name); tokens.Add(new(3, strings.Count - 1)); }
        public void Int(int value) => tokens.Add(new(0, value));
        public PvfToken[] Tokens => tokens.ToArray();
        public string Resolve(int index) => strings[index];
    }
}
