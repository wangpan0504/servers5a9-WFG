using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Quests;

internal readonly record struct TitleCounters(ushort First, ushort Second, ushort Third)
{
    public bool Complete => First == 0 && Second == 0 && Third == 0;
    public TitleCounters Subtract(TitleCounters delta) => new((ushort)Math.Max(0, First - delta.First),
        (ushort)Math.Max(0, Second - delta.Second), (ushort)Math.Max(0, Third - delta.Third));
}

internal sealed record TitleQuest(StoryQuest Quest, TitleCounters Initial);
internal sealed record TitleSlotDefinition(int Kind, int QuestId, ImmutableArray<int> Items);
internal sealed record TitleSectionDefinition(string Category, int Index, string LockMessage, int Emotion,
    int PostalItem, int PostalCount, string Sender, string Message);
internal readonly record struct TitleAdjustmentDefinition(int QuestId, int ItemId, int AchievementId);

internal sealed class TitleCatalog
{
    public static readonly string[] Categories = ["general", "specific", "pvp", "despair", "event"];
    public FrozenDictionary<int, TitleQuest> Quests { get; }
    public FrozenDictionary<(int Category, int Index), TitleSlotDefinition> Slots { get; }
    public FrozenDictionary<int, TitleSectionDefinition> Sections { get; }
    public ImmutableArray<TitleAdjustmentDefinition> Adjustments { get; }
    public FrozenDictionary<int, ImmutableArray<int>> QuestCategories { get; }
    public FrozenDictionary<int, ImmutableArray<int>> SectionQuests { get; }

    public TitleCatalog(PvfArchive pvf, QuestCatalog quests) : this(TitleBookFile.Read(pvf.ReadTokens("etc/titlebook.etc"), pvf.ResolveString), quests) { }
    internal TitleCatalog(TitleBookFile book, QuestCatalog quests)
    {
        if (book.Categories.Length != Categories.Length || Categories.Any(name => !book.Categories.Any(category => category.Name == name)))
            throw new InvalidDataException("称号簿五种分类不完整");
        Slots = book.Categories.SelectMany(category => category.Rows.Select(row =>
            new KeyValuePair<(int, int), TitleSlotDefinition>((Array.IndexOf(Categories, category.Name), row.Index), new(row.Kind, row.QuestId, row.Items)))).ToFrozenDictionary();
        var sections = new List<TitleSectionDefinition>();
        foreach (var category in book.Categories)
        {
            // 普通章节声明省略整组空槽；活动的关闭栏和空白栏仍占原位置。
            var groups = category.Rows.Where(row => row.Kind != -1).Select(row => row.Index / 10).Distinct().Order().ToArray();
            foreach (var section in book.Sections.Where(section => section.Category == category.Name))
            {
                if (section.Index < 0 || category.Name != "event" && section.Index >= groups.Length)
                    throw new InvalidDataException("称号章节没有对应的槽位组");
                int index = category.Name == "event" ? section.Index : groups[section.Index];
                sections.Add(new(section.Category, index, section.LockMessage, section.Emotion,
                    section.PostalItem, section.PostalCount, section.Sender, section.Message));
            }
        }
        Sections = sections
            .ToFrozenDictionary(section => SectionKey(Array.IndexOf(Categories, section.Category), section.Index));
        Adjustments = book.Adjustments.Select(row => new TitleAdjustmentDefinition(row.QuestId, row.ItemId, row.AchievementId)).ToImmutableArray();
        QuestCategories = Slots.Where(pair => pair.Value.Kind != -1 && pair.Value.QuestId > 0).GroupBy(pair => pair.Value.QuestId)
            .ToFrozenDictionary(group => group.Key, group => group.Select(pair => pair.Key.Category).Distinct().ToImmutableArray());
        SectionQuests = Slots.Where(pair => pair.Value.Kind != -1 && pair.Value.QuestId > 0)
            .GroupBy(pair => SectionKey(pair.Key.Category, pair.Key.Index / 10))
            .ToFrozenDictionary(group => group.Key, group => group.Select(pair => pair.Value.QuestId).Distinct().ToImmutableArray());
        var definitions = new Dictionary<int, TitleQuest>();
        foreach (var quest in quests.All.Where(quest => quest.Grade == "[title]"))
        {
            var values = quest.Definition?.Values("check count") ?? [];
            if (values.Length > 3 || values.Any(value => value.Type != 0 || value.Value is < 0 or > ushort.MaxValue)) continue;
            ushort Count(int at) => (ushort)(at < values.Length ? values[at].Value : 0);
            definitions.Add(quest.Id, new(quest, values.Length == 0 ? new(1, 0, 0) : new(Count(0), Count(1), Count(2))));
        }
        Quests = definitions.ToFrozenDictionary();
    }
    public static int SectionKey(int category, int index) => checked(category * 256 + index);
    public bool Fits(int category, int index, int itemId) => Slots.TryGetValue((category, index), out var row) &&
        row.Kind != -1 && (row.QuestId == -1 || row.Items.Contains(itemId)) &&
        (!Sections.TryGetValue(SectionKey(category, index / 10), out var section) || section.LockMessage.Length == 0);
    public (int Category, int Index)? RewardSlot(int questId, int itemId, int? category = null) => Slots.Where(pair =>
        (category is null || pair.Key.Category == category) && pair.Value.QuestId == questId && pair.Value.Items.Contains(itemId))
        .Select(pair => ((int Category, int Index)?)pair.Key).FirstOrDefault();
}
