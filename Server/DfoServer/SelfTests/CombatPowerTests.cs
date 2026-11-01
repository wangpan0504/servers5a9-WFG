using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 官方风格战斗力（cp-official/v1）：乘区公式、段位、CP_SCORE/2 合同与真实PVF词条普查。
internal static class CombatPowerTests
{
    const string Nonce = "0123456789abcdef0123456789abcdef";

    static CharacterStats Level(short strength10, short intelligence10) =>
        new(0, 0, strength10, 0, intelligence10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public static void RunCore()
    {
        int before = Count;
        var templates = new Dictionary<int, ItemTemplate>
        {
            [1] = new(1, ItemKind.Equipment, 12, 95, 30, 1, 0, 4, 0, "[free]", ushort.MaxValue, 0, "weapon"),
            [2] = new(2, ItemKind.Equipment, 13, 95, 30, 1, 0, 2, 0, "[free]", ushort.MaxValue, 0, "title"),
        };
        var effects = new Dictionary<int, ImmutableArray<CombatEffectRow>>
        {
            [1] = [new("separate attack", "+", "", 1000), new("physical attack", "+", "", 100),
                new("unique increase damage", "%", "", 20), new("unique increase damage", "%", "", 30),
                new("add absolute damage buff", "%", "all", 20), new("add absolute damage buff", "%", "fire", 10),
                new("fire attack", "+", "", 100), new("physical critical hit", "+", "", 50),
                new("unique increase critical damage", "%", "", 50), new("add increase critical damage", "%", "", 10),
                new("skill attack bonus rate", "%", "", 10), new("all attack bonus rate", "%", "", 5)],
            [2] = [new("add increase damage", "%", "", 8), new("unique increase damage", "%", "", 15)],
        };
        using (var f = new CharacterScoreNoticeTests.Fixture())
        {
            var capture = f.Session.CaptureCharacterScoreNotice("notice", "NoticeHero");
            var snapshot = capture.Snapshot;
            CombatPowerResult Run(Func<int, ImmutableArray<CombatEffectRow>> read, CharacterStats? level) =>
                CombatPowerScore.Calculate(snapshot, level, id => templates[id], read,
                    (_, _) => throw new InvalidDataException("无附魔"), _ => null, _ => []);
            var r = Run(id => effects[id], Level(1000, 0));
            // base = (1000+1)×(1+200/250)×(1.05+0.0045×100)×(1+0.5×53%)÷1.5 = 2279.277
            // 白字 20+10×1.5=35%；黄字 max(30,15)+8=38%；爆伤 50+10=60%；装备 (1+(2.5+1+3.1(+31强化)+5)%)×1.1−1=22.76%
            That(r.BaseScore == 2279 && r.WhiteBp == 3500 && r.YellowBp == 3800 && r.CritDamageBp == 6000 && r.EquipmentBp == 2276,
                "乘区：白字累加、黄字/爆伤取最高另加追加、装备含稀有度/所攻/技攻：" + r);
            That(r.Total == 8340 && r.Grade == 1 && r.GradeName == "开拓" && !r.Partial && r.MainStat == 200 && r.Attack == 1000 &&
                r.CritRate == 53 && r.Element == 100, "总战力=基础×四乘区，段位与中间量");
            That(r.CharacterId == snapshot.Equipment.CharacterId && r.CharacterRevision == snapshot.Equipment.CharacterRevision &&
                r.ObservedAt == snapshot.Equipment.ObservedAt, "战力结果绑定快照身份");
            var missing = Run(id => id == 2 ? throw new InvalidDataException("坏脚本") : effects[id], null);
            That(missing.Partial && missing.Notes.Any(n => n.Contains("/unavailable", StringComparison.Ordinal)) &&
                missing.Notes.Contains("level_stats_unavailable") && missing.YellowBp == 3000, "坏脚本/缺等级属性标记partial，不伪作完整");
            byte[]? sent = null;
            CharacterScoreProjection.Send(f.Session, capture, f.Calculate, _ => null,
                (_, body, _, _) => { sent = body; return true; }, Nonce, _ => r);
            int title = BinaryPrimitives.ReadInt32LittleEndian(sent!);
            That(Encoding.ASCII.GetString(sent!, 4, title) == "CP_SCORE/2", "版本2使用独立标题");
            string text = Encoding.ASCII.GetString(sent!, 8 + title, BinaryPrimitives.ReadInt32LittleEndian(sent.AsSpan(4 + title)));
            var fields = text.Split('|');
            That(fields.Length == 15 && fields[0] == "2" && fields[4] == "cp-official" && fields[5] == "v1" && fields[6] == "complete" &&
                fields[7] == "8340" && fields[8] == "2279" && fields[9] == "3500" && fields[13] == "1", "真实投影输出15段战力正文");
            bool rejected = false;
            try
            {
                CharacterScoreProjection.Send(f.Session, capture, f.Calculate, _ => null, (_, _, _, _) => true, Nonce,
                    _ => r with { CharacterRevision = r.CharacterRevision + 1 });
            }
            catch (InvalidDataException) { rejected = true; }
            That(rejected, "战力身份不符拒绝发送");
        }
        That(CombatPowerScore.GradeOf(0) == 0 && CombatPowerScore.GradeOf(3499) == 0 && CombatPowerScore.GradeOf(85000) == 6 &&
            CombatPowerScore.GradeOf(499999) == 6 && CombatPowerScore.GradeOf(500000) == 7 && CombatPowerScore.GradeOf(1199999) == 8 &&
            CombatPowerScore.GradeOf(1200000) == 9 && CombatPowerScore.GradeNames.Length == 10, "段位门槛边界");
        string Body(byte[] body)
        {
            int t = BinaryPrimitives.ReadInt32LittleEndian(body);
            return Encoding.ASCII.GetString(body, 8 + t, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4 + t)));
        }
        That(Body(MachineCharacterScorePackets.BuildCombat(Nonce, 42, 7, "partial", 72697, 8764, 6900, 4000, 15405, 3800, 6, 10000)) ==
            "2|0123456789abcdef0123456789abcdef|42|7|cp-official|v1|partial|72697|8764|6900|4000|15405|3800|6|10000", "双端黄金正文v2");
        string max = Body(MachineCharacterScorePackets.BuildCombat(Nonce, ushort.MaxValue, long.MaxValue, "complete", int.MaxValue,
            int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, 9, 10000));
        That(max.Split('|').Length == 15 && max.Length <= 600, "v2最大字段在600字节内");
        void Reject(Action action) { bool ok = false; try { action(); } catch (InvalidDataException) { ok = true; } That(ok, "v2无效字段拒绝"); }
        Reject(() => MachineCharacterScorePackets.BuildCombat("bad", 1, 0, "complete", 0, 0, 0, 0, 0, 0, 0, 1));
        Reject(() => MachineCharacterScorePackets.BuildCombat(Nonce, 1, 0, "unrated", 0, 0, 0, 0, 0, 0, 0, 1));
        Reject(() => MachineCharacterScorePackets.BuildCombat(Nonce, 1, 0, "complete", -1, 0, 0, 0, 0, 0, 0, 1));
        Reject(() => MachineCharacterScorePackets.BuildCombat(Nonce, 1, 0, "complete", 0, 0, 0, 0, 0, 0, 10, 1));
        Reject(() => MachineCharacterScorePackets.BuildCombat(Nonce, 1, 0, "complete", 0, 0, 0, 0, 0, 0, 0, 0));
        That(SelfCharacterScoreProjection.MachineVersion(new(3, 0, 0, 0, "#cp-score/2 " + Nonce)) == 2 &&
            SelfCharacterScoreProjection.MachineNonce(new(3, 0, 0, 0, "#cp-score/2 " + Nonce)) == Nonce &&
            SelfCharacterScoreProjection.MachineVersion(new(3, 0, 0, 0, "#cp-score/1 " + Nonce)) == 1, "v2命令同nonce合同");
        That(!SelfCharacterScoreProjection.MachineNamespace(new(3, 0, 0, 0, "#cp-score/2x")) &&
            SelfCharacterScoreProjection.MachineNamespace(new(3, 0, 0, 0, "#cp-score/2 bad")) &&
            SelfCharacterScoreProjection.MachineNonce(new(3, 0, 0, 0, "#cp-score/2 bad")) is null, "v2坏语法拒绝，相似消息不劫持");
        Console.WriteLine($"CombatPowerTests: {Count - before} passed");
    }

    // 真实PVF普查：全部装备脚本可读率与主要伤害词条覆盖，证明标签映射对当前资源有效。
    public static void RunResource(ItemResources resources)
    {
        int before = Count;
        var metadata = resources.CharacterInvestmentMetadata;
        var watch = Stopwatch.StartNew();
        int total = 0, failed = 0, setFailed = 0, sets = 0;
        var stats = new Dictionary<string, int>(StringComparer.Ordinal);
        var setIds = new HashSet<int>();
        foreach (int id in metadata.CombatEquipmentIds.OrderBy(i => i))
        {
            total++;
            try
            {
                foreach (var row in metadata.ReadCombatEffects(id).DistinctBy(r => r.Stat))
                    stats[row.Stat] = stats.GetValueOrDefault(row.Stat) + 1;
                if (metadata.ReadCombatSetId(id) is { } set) setIds.Add(set);
            }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FileNotFoundException or FormatException)
            { failed++; }
        }
        foreach (int set in setIds)
        {
            try { if (metadata.ReadCombatSetTiers(set).Length > 0) sets++; }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FileNotFoundException or FormatException)
            { setFailed++; }
        }
        Console.WriteLine($"CombatPowerTests.Resource: equipment={total} failed={failed} sets={setIds.Count} tiered={sets} setFailed={setFailed} " +
            string.Join(' ', stats.OrderBy(p => p.Key).Select(p => $"[{p.Key}]={p.Value}")) + $" elapsed={watch.ElapsedMilliseconds}ms");
        That(total > 1000 && failed * 50 < total, "真实装备脚本可读率>98%");
        foreach (string stat in new[] { "unique increase damage", "unique increase critical damage", "add absolute damage buff",
            "equipment physical attack", "physical attack", "add increase damage" })
            That(stats.GetValueOrDefault(stat) > 0, "真实PVF存在词条：" + stat);
        That(sets > 0 && setFailed * 10 <= setIds.Count, "真实套装阶梯可读");
        Console.WriteLine($"CombatPowerTests.Resource: {Count - before} passed");
    }
}
