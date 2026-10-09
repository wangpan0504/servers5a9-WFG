using System.Collections.Immutable;

namespace DfoServer.GameWorld.Quests;

// 与S4A21/external的资源行同序：monster 7项、enemy 8项、clear 6项。
// EnemyType: 0通关、1怪物、2 AI角色、3被动物件。难度是精确匹配，-1为任意。
internal sealed record QuestDropDefinition(int TemplateId, int EnemyType, int DungeonId, int Difficulty,
    int ItemId, int Count, int Rate, int MaximumHeld)
{
    public bool Matches(uint dungeon, byte difficulty, int template, int enemyType, bool cleared) =>
        (EnemyType == 0 ? cleared : EnemyType == enemyType && TemplateId == template) &&
        (DungeonId == -1 || DungeonId == dungeon) && (Difficulty == -1 || Difficulty == difficulty);

    public static ImmutableArray<QuestDropDefinition> Read(ImmutableArray<QuestField> fields, ImmutableArray<string>.Builder errors)
    {
        var result = ImmutableArray.CreateBuilder<QuestDropDefinition>();
        foreach (var field in fields.Where(field => field.Name is "monster reward item" or "enemy reward item" or "clear reward item"))
        {
            int width = field.Name == "monster reward item" ? 7 : field.Name == "enemy reward item" ? 8 : 6;
            if (field.Children.Length != 0 || field.Values.Length % width != 0 || field.Values.Any(value => value.Type != 0))
            { errors.Add("任务掉落行不完整: " + field.Name); continue; }
            for (int at = 0; at < field.Values.Length; at += width)
            {
                int Value(int index) => field.Values[at + index].Value;
                int offset = width - 6;
                var row = new QuestDropDefinition(width == 6 ? 0 : Value(0), width == 6 ? 0 : width == 7 ? 1 : Value(1),
                    Value(offset),Value(offset + 1),Value(offset + 2),Value(offset + 3),Value(offset + 4),Value(offset + 5));
                if (row.EnemyType is < 0 or > 3 || row.EnemyType != 0 && row.TemplateId <= 0 || row.DungeonId < -1 ||
                    row.Difficulty is < -1 or > 3 || row.ItemId <= 0 || row.Count <= 0 || row.Rate is < 0 or > 100 || row.MaximumHeld < -1)
                    errors.Add("任务掉落参数无效: " + field.Name);
                else result.Add(row);
            }
        }
        return result.ToImmutable();
    }
}
