using System.Collections.Immutable;

namespace PvfLib;

public sealed record TitleBookRow(int Index, int Kind, int QuestId, ImmutableArray<int> Items);
public sealed record TitleBookCategory(string Name, int Capacity, ImmutableArray<TitleBookRow> Rows);
public sealed record TitleBookSection(string Category, int Index, string LockMessage, int Emotion,
    int PostalItem, int PostalCount, string Sender, string Message);
public readonly record struct TitleBookAdjustment(int QuestId, int ItemId, int AchievementId);
public sealed record TitleBookFile(ImmutableArray<TitleBookCategory> Categories, ImmutableArray<TitleBookSection> Sections,
    ImmutableArray<TitleBookAdjustment> Adjustments)
{
    public static TitleBookFile Read(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve, new Dictionary<string, string>
        {
            ["title collection info"] = "title collection info", ["section"] = "section", ["reward type"] = "reward type",
            ["default reward"] = "default reward", ["adjust list"] = "adjust list", ["adjust jelba list"] = "adjust jelba list"
        });
        var categories = ImmutableArray.CreateBuilder<TitleBookCategory>();
        var sections = ImmutableArray.CreateBuilder<TitleBookSection>();
        var adjustments = ImmutableArray.CreateBuilder<TitleBookAdjustment>();
        foreach (var section in document.Sections)
        {
            var values = section.Values;
            if (section.Name == "title collection info")
            {
                int at = 0;
                string name = String();
                int capacity = Integer();
                Require(capacity is > 0 and <= 256 && section.Children.Length == 0);
                var rows = ImmutableArray.CreateBuilder<TitleBookRow>();
                while (at < values.Length)
                {
                    int index = Integer(), kind = Integer();
                    Require(index >= 0 && index < capacity && kind is -1 or 0 or 1 or 2);
                    if (kind == -1) { rows.Add(new(index, kind, -1, [])); continue; }
                    int quest = Integer(), count = Integer();
                    Require(quest is -1 or > 0 && count is > 0 and <= 256 && count <= values.Length - at);
                    var items = ImmutableArray.CreateBuilder<int>(count);
                    for (int i = 0; i < count; i++) { int item = Integer(); Require(item is -1 or > 0); items.Add(item); }
                    rows.Add(new(index, kind, quest, items.MoveToImmutable()));
                }
                Require(rows.Count == capacity && rows.Select(row => row.Index).Distinct().Count() == capacity);
                categories.Add(new(name, capacity, rows.ToImmutable()));
                int Integer() { Require(at < values.Length && values[at].Type == 0); return values[at++].Value; }
                string String() { Require(at < values.Length && values[at].Type == 6); return resolve(values[at++].Value); }
            }
            else if (section.Name == "section")
            {
                Require(values is [{ Type: 6 }]);
                string category = resolve(values[0].Value), locked = "";
                int index = -1;
                foreach (var field in section.Children)
                {
                    if (field.Name == "section name") { index++; locked = ""; }
                    else if (field.Name == "lock message")
                    {
                        Require(field.Values is [{ Type: 6 }]); locked = resolve(field.Values[0].Value);
                    }
                    else if (field.Name == "reward type")
                    {
                        Require(index >= 0);
                        int emotion = 0, item = 0, count = 0; string sender = "", message = "";
                        foreach (var reward in field.Children)
                        {
                            if (reward.Name == "give emotion")
                            {
                                Require(reward.Values is [{ Type: 0, Value: > 0 }]); emotion = reward.Values[0].Value;
                            }
                            else if (reward.Name == "send postal")
                            {
                                Require(reward.Values.Length is 4 or 5 && reward.Values[0] is { Type: 0, Value: > 0 } &&
                                    reward.Values[1] is { Type: 0, Value: > 0 } && reward.Values.Skip(2).All(token => token.Type == 6));
                                item = reward.Values[0].Value; count = reward.Values[1].Value;
                                sender = resolve(reward.Values[2].Value); message = resolve(reward.Values[3].Value);
                            }
                        }
                        sections.Add(new(category, index, locked, emotion, item, count, sender, message));
                    }
                }
            }
            else if (section.Name is "adjust list" or "adjust jelba list")
            {
                Require(values.Length % 3 == 0 && values.All(token => token.Type == 0 && token.Value > 0));
                for (int at = 0; at < values.Length; at += 3)
                    adjustments.Add(new(values[at].Value, values[at + 1].Value, values[at + 2].Value));
            }
            else if (section.Name == "default reward")
                Require(section.Values.Length == 0 && section.Children.Length == 0);
        }
        Require(categories.Select(category => category.Name).Distinct().Count() == categories.Count &&
            sections.Select(section => (section.Category, section.Index)).Distinct().Count() == sections.Count);
        return new(categories.ToImmutable(), sections.ToImmutable(), adjustments.ToImmutable());
    }

    static void Require(bool valid)
    {
        if (!valid) throw new InvalidDataException("称号簿脚本的字段、边界或重复声明无效");
    }
}
