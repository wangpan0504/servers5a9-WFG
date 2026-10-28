using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class EvildomWarFileTests
{
    public static void Run()
    {
        (string Name, int[] Values)[] rows =
        [
            ("dungeon list", [0, 0, 5420]), ("party member count", [0, 4]),
            ("waiting area", [67, 2, 468, 208]), ("limit time", [3000]),
            ("incount dungeon index", [5428]), ("limit daily enter count", [1]),
            ("limit weekly enter count", [2]), ("need fatigue", [30]),
            ("hidden dungeon", [0, 5421, 1000, 1, 5427, 1000, 2, 5441, 1000]),
            ("recruiting area", [67, 1, 891, 291]),
            ("reward list", [3, 0, -1, 0, 9372, 3, 1, 10310788, 1, 500]),
            ("hard mode reward list", [0, 0, -1, 0, 1000, 0, 1, 10310783, 1, 6300]),
            ("guide mode reward list", [3, 0, -1, 0, 9622, 3, 1, 10310788, 1, 300]),
            ("limit anti evil", [0, 5000, 1, 5150, 2, 4000]), ("movie time", [74]),
            ("disconnect wait time", [300]), ("disconnect retry item", [10310974]),
            ("sword dungeon buff rate", [15]), ("shield dungeon buff rate", [15])
        ];
        var names = new List<string>();
        PvfToken Tag(string value) { names.Add(value); return new(3, names.Count - 1); }
        PvfToken[] Tokens(IEnumerable<(string Name, int[] Values)> source) => source.SelectMany(row =>
            new[] { Tag("[" + row.Name + "]") }.Concat(row.Values.Select(value => new PvfToken(0, value)))
                .Append(Tag("[/" + row.Name + "]"))).ToArray();
        EvildomWarFile Parse(IEnumerable<(string Name, int[] Values)> source) => EvildomWarFile.Parse(Tokens(source), id => names[id]);
        var parsed = Parse(rows);
        That(parsed.HiddenDungeons.SequenceEqual(rows[8].Values) && parsed.RecruitingArea.SequenceEqual(rows[9].Values) &&
            parsed.AntiEvilLimits.SequenceEqual(rows[13].Values), "魔界三模式隐藏图、招聘位置和抗魔原行保持顺序");
        That(parsed.Rewards.SequenceEqual(rows[10].Values) && parsed.HardModeRewards.SequenceEqual(rows[11].Values) &&
            parsed.GuideModeRewards.SequenceEqual(rows[12].Values),
            "三张奖励表独立保留负一、零数量和非满额权重，不归一化、不合并或补齐");
        That(parsed.MovieTime == 74 && parsed.DisconnectWaitTime == 300 && parsed.DisconnectRetryItem == 10310974 &&
            parsed.SwordBuffRate == 15 && parsed.ShieldBuffRate == 15, "魔界影片、断线及剑盾原标量不丢失");
        foreach (string name in new[] { "hidden dungeon", "reward list", "hard mode reward list", "guide mode reward list", "limit anti evil" })
            Reject<InvalidDataException>(() => Parse(rows.Select(row => row.Name == name ? (row.Name, row.Values[..^1]) : row)));
        Reject<InvalidDataException>(() => Parse(rows.Where(row => row.Name != "disconnect retry item")));
        Reject<InvalidDataException>(() => Parse(rows.Append(rows[10])));
        Reject<InvalidDataException>(() => Parse(rows.Select(row => row.Name == "movie time" ? (row.Name, new[] { 74, 75 }) : row)));
        var tokens = Tokens(rows);
        int rewardAt = Array.FindIndex(tokens, token => token.Type == 3 && names[token.Value] == "[reward list]") + 1;
        var nonInteger = (PvfToken[])tokens.Clone(); nonInteger[rewardAt] = new(4, 0);
        Reject<InvalidDataException>(() => EvildomWarFile.Parse(nonInteger, id => names[id]));
        var nested = tokens[..rewardAt].Concat(new[] { Tag("[nested]"), new PvfToken(0, 1) }).Concat(tokens[rewardAt..]).ToArray();
        Reject<InvalidDataException>(() => EvildomWarFile.Parse(nested, id => names[id]));
        Reject<InvalidDataException>(() => EvildomWarFile.Parse(tokens.Where(token => token.Type != 3 ||
            names[token.Value] != "[/hidden dungeon]").ToArray(), id => names[id]));
    }
}
