using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Quests;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EmotionSelectionTests
{
    public static void RunCore()
    {
        var character = new Character(1, 0, "Emotion", 0, new(38, 1, 450, 234), null);
        var categories = TitleCatalog.Categories.Select(name => new TitleBookCategory(name, 1,
            [new(0, 0, 6501, [26596])])).ToImmutableArray();
        var titles = new TitleCatalog(new TitleBookFile(categories,
            [new("general", 0, "", 10, 0, 0, "", ""), new("event", 0, "", 23, 0, 0, "", "")], []), new QuestCatalog([]));
        var catalog = new CharacterEmotionCatalog([new(0, 0, true), new(1, 1, true), new(10, 3, true),
            new(23, 3, false), new(300, 1, false), new(1000, 5, true, [20983, 20984], true), new(1001, 300, true, Special: true)]);
        That(EmotionSelectionRules.Error(catalog, titles, character, 0) == 0 &&
            EmotionSelectionRules.Error(catalog, titles, character, 1) == 0, "基础心情无需伪造章节资格");
        That(EmotionSelectionRules.Error(catalog, titles, character, 10) == 3 &&
            EmotionSelectionRules.Error(catalog, titles, character, 65535) == 1, "存在但未解锁与未知编号区分失败提示");
        var unlocked = character with { TitleSections = ImmutableHashSet.Create(0, 1024) };
        That(EmotionSelectionRules.Error(catalog, titles, unlocked, 10) == 0 &&
            EmotionSelectionRules.Error(catalog, titles, unlocked, 23) == 0, "章节资格开放普通及隐藏活动心情");
        That(EmotionSelectionRules.Error(catalog, titles, unlocked, 1000) == 3 &&
            EmotionSelectionRules.Error(catalog, titles, unlocked, 300) == 3 &&
            EmotionSelectionRules.Error(catalog, titles, unlocked, 1001) == 3, "章节不代替特殊任务、隐藏基础或专用关系资格");
        var partial = character with { CompletedQuests = [20983] };
        That(EmotionSelectionRules.Error(catalog, titles, partial, 1000) == 3, "任务心情不能只完成一项就获得");
        var learned = partial with { AccountCompletedQuests = [20984] };
        That(EmotionSelectionRules.Error(catalog, titles, learned, 1000) == 0, "角色与账号已完成任务共同满足真实心情来源");
        learned = partial with { TitleProgress = partial.TitleProgress.Add(20984, new(default)) };
        That(EmotionSelectionRules.Error(catalog, titles, learned, 1000) == 0, "称号任务完成资格也能授予对应特殊心情");

        Dictionary<int, string> strings = new() { [1] = "[EMOTION INFO]", [2] = "[/EMOTION INFO]",
            [3] = "[SPECIAL EMOTION INFO]", [4] = "[/SPECIAL EMOTION INFO]", [5] = "", [6] = "name", [7] = "-name", [8] = "$END" };
        PvfToken[] plain = [new(0, 0), new(0, 0), new(0, 1), new(6, 5), new(6, 5), new(0, 0), new(0, 0), new(0, 1), new(6, 6), new(6, 7), new(6, 8)];
        PvfToken[] special = [new(0, 1000), new(0, 5), new(0, 2), new(0, 20983), new(0, 20984),
            new(6, 5), new(6, 5), new(0, 0), new(0, 0), new(6, 6), new(6, 7), new(6, 8)];
        PvfToken[] relation = [new(0, 1001), new(0, 300), new(0, 0), new(6, 5), new(6, 5), new(0, 0), new(0, 0), new(6, 6), new(6, 7), new(6, 8)];
        var definitions = EmotionExpressionFile.Read([new(3, 1), .. plain, new(3, 2), new(3, 3), .. special, .. relation, new(3, 4)], id => strings[id]);
        That(definitions.Length == 3 && definitions[1].Special && definitions[1].RequiredQuests.SequenceEqual([20983, 20984]) &&
            definitions[2].Kind == 300, "特殊心情独立解析两项布尔、任务列表和300附属声明");
        Reject<InvalidDataException>(() => EmotionExpressionFile.Read([new(3, 1), .. plain, new(3, 2), new(3, 3), .. special[..^1], new(3, 4)], id => strings[id]),
            "特殊目录缺少END拒绝，不把后续心情串行吞掉");
    }

    public static void RunResources(PvfArchive pvf, TitleCatalog titles)
    {
        var definitions = EmotionExpressionFile.Read(pvf.ReadTokens(CharacterEmotionCatalog.ScriptPath), pvf.ResolveString);
        var catalog = new CharacterEmotionCatalog(definitions);
        var character = new Character(1, 0, "Emotion", 0, new(38, 1, 450, 234), null);
        foreach (var definition in definitions)
        {
            ushort id = checked((ushort)definition.Id);
            That(catalog.Find(id) is not null, "资源每个普通和特殊心情均保留注册: " + id);
            if (id != 0)
                That(pvf.Contains(definition.EmbossedAnimation) && pvf.Contains(definition.NormalAnimation), "心情两条原生ANI资源存在: " + id);
            if (definition.Kind == 3)
            {
                var sections = titles.Sections.Where(pair => pair.Value.Emotion == id).ToArray();
                That(sections.Length != 0 && EmotionSelectionRules.Error(catalog, titles, character, id) == 3,
                    "称号心情有资源来源且默认保持锁定: " + id);
                foreach (var section in sections)
                    That(EmotionSelectionRules.Error(catalog, titles, character with { TitleSections = ImmutableHashSet.Create(section.Key) }, id) == 0,
                        "每个真实章节资格独立解锁对应心情: " + id + "/" + section.Key);
            }
            if (definition.Kind == 5)
                That(EmotionSelectionRules.Error(catalog, titles, character with { CompletedQuests = [.. definition.RequiredQuests] }, id) == 0,
                    "每个真实特殊任务列表完整完成后可用: " + id);
        }
    }
}
