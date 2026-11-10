using static PvfLib.Tests.TestCheck;

namespace PvfLib.Tests;

internal static class PcRoomTimePointFileTests
{
    public static void Run()
    {
        var strings = new List<string>();
        var tokens = new List<PvfToken>();
        int TextId(string text)
        {
            int found = strings.IndexOf(text);
            if (found >= 0) return found;
            strings.Add(text);
            return strings.Count - 1;
        }
        void Field(string name, params object[] values)
        {
            tokens.Add(new(3, TextId("[" + name + "]")));
            foreach (object value in values)
                tokens.Add(value is int number ? new(0, number) : new(6, TextId((string)value)));
        }
        string Resolve(int index) => strings[index];
        Field("daily reward autoget", "false");
        Field("exit alarm", "true");
        Field("daily reward loop", 1);
        Field("period reward loop", 4);
        Field("daily reward items", 1, 490014060, 1, 1800000, "true", "false",
            1, 490014061, 1, 1800000, "true", "true",
            1, 490014063, 2, 1800000, "true", "true",
            1, 490014064, 1, 1800000, "true", "true");
        Field("/daily reward items");
        Field("period reward item", 1, 490014067, 3, 5, "true", 2, 490014068, 1, 5, "true",
            3, 490014069, 1, 5, "true", 4, 490014173, 1, 5, "false");
        Field("/period reward item");
        Field("ui file", "UI/Event/PCRoomTimePoint/PCRoomTimePointRewardItemInfoWindow_Chn.xui");
        var source = PcRoomTimePointFile.Parse(tokens, Resolve);
        That(!source.DailyRewardAutoGet && source.ExitAlarm && source.DailyRewardLoop == 1 && source.PeriodRewardLoop == 4,
            "累计在线原始开关与循环声明完整保留");
        That(source.DailyRewards.Length == 4 && source.DailyRewards[2].Count == 2 &&
            source.DailyRewards[0].DurationMilliseconds == 1800000 && !source.DailyRewards[0].SecondFlag,
            "每日六列保留原始毫秒、奖励数量和独立布尔字段，不预先累计");
        That(source.PeriodRewards.Length == 4 && source.PeriodRewards[3] is { StageIndex: 4, RequiredCount: 5, Flag: false } &&
            source.UiFile.EndsWith("RewardItemInfoWindow_Chn.xui", StringComparison.Ordinal),
            "累计五列及资源界面路径原值保留");
        var changed = tokens.Select(token => token.Type == 0 && token.Value == 1800000 ? new PvfToken(0, 900000) : token).ToArray();
        That(PcRoomTimePointFile.Parse(changed, Resolve).DailyRewards.All(row => row.DurationMilliseconds == 900000),
            "修改四档时长不改变资源解析");
        int[] replacements = [490000413, 5050528, 5050514, 5050518];
        var replaced = tokens.ToArray();
        int start = replaced.ToList().FindIndex(token => token.Type == 3 && Resolve(token.Value) == "[daily reward items]") + 1;
        for (int i = 0; i < replacements.Length; i++) replaced[start + i * 6 + 1] = new(0, replacements[i]);
        That(PcRoomTimePointFile.Parse(replaced, Resolve).DailyRewards.Select(row => row.ItemId).SequenceEqual(replacements),
            "替换所有每日奖励保留真实模板编号");
        var truncated = tokens.ToList();
        truncated.RemoveAt(truncated.FindIndex(token => token.Type == 3 && Resolve(token.Value) == "[/daily reward items]") - 1);
        Reject<InvalidDataException>(() => PcRoomTimePointFile.Parse(truncated, Resolve));
        var wrongBoolean = tokens.ToArray();
        wrongBoolean[1] = new(0, 1);
        Reject<InvalidDataException>(() => PcRoomTimePointFile.Parse(wrongBoolean, Resolve));
        Field("ui file", "UI/Event/Other.xui");
        Reject<InvalidDataException>(() => PcRoomTimePointFile.Parse(tokens, Resolve));
    }
}
