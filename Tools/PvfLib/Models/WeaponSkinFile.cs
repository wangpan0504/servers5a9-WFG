using System.Collections.Immutable;

namespace PvfLib;

public sealed record WeaponSkinMaterial(int ItemId, int Count);
public sealed record WeaponSkinRecipe(int Id, int ResultItemId, ImmutableArray<WeaponSkinMaterial> Materials);
public sealed record WeaponSkinItem(int ItemId, int Level, string Name, int EquipmentType, int Job);

public static class WeaponSkinFile
{
    public static ImmutableArray<WeaponSkinRecipe> ReadRecipes(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (document.Diagnostics.Length != 0 || document.Sections is not [{ Name: "make enchanted skin" } root] || root.Values.Length != 0)
            throw new InvalidDataException("武器幻化配方容器无效");
        var recipes = ImmutableArray.CreateBuilder<WeaponSkinRecipe>();
        foreach (var section in root.Children)
        {
            if (section.Name != "recipe" || section.Values is not [{ Type: 0, Value: > 0 } recipeId])
                throw new InvalidDataException("武器幻化配方编号无效");
            var results = section.Children.Where(child => child.Name == "enchanted result").ToArray();
            if (results is not [{ Values: [{ Type: 0, Value: > 0 } resultId], Children.Length: 0 }])
                throw new InvalidDataException("武器幻化产物声明无效");
            var materials = ImmutableArray.CreateBuilder<WeaponSkinMaterial>();
            foreach (var child in section.Children)
            {
                if (child.Name == "enchanted result") continue;
                if (child.Name != "material" || child.Children.Length != 0 ||
                    child.Values is not [{ Type: 0, Value: > 0 } item, { Type: 6 } mode, { Type: 0, Value: > 0 } count] ||
                    resolve(mode.Value) != "fixed")
                    throw new InvalidDataException("武器幻化材料不是固定物品和数量");
                materials.Add(new(item.Value, count.Value));
            }
            if (materials.Count == 0 || recipes.Any(recipe => recipe.Id == recipeId.Value))
                throw new InvalidDataException("武器幻化缺少材料或配方编号重复");
            recipes.Add(new(recipeId.Value, resultId.Value, materials.ToImmutable()));
        }
        if (recipes.Count == 0) throw new InvalidDataException("武器幻化配方为空");
        return recipes.ToImmutable();
    }

    public static ImmutableArray<WeaponSkinItem> ReadItems(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        if (tokens.Count == 0 || tokens.Count % 5 != 0)
            throw new InvalidDataException("武器幻化目录不是完整的五列记录");
        var rows = ImmutableArray.CreateBuilder<WeaponSkinItem>(tokens.Count / 5);
        var ids = new HashSet<int>();
        for (int i = 0; i < tokens.Count; i += 5)
        {
            var id = tokens[i];
            var level = tokens[i + 1];
            var name = tokens[i + 2];
            var type = tokens[i + 3];
            var job = tokens[i + 4];
            if (id is not { Type: 0, Value: > 0 } || level.Type != 0 || name.Type != 6 ||
                type.Type != 0 || job.Type != 0 || !ids.Add(id.Value))
                throw new InvalidDataException("武器幻化目录字段无效或编号重复");
            rows.Add(new(id.Value, level.Value, resolve(name.Value), type.Value, job.Value));
        }
        return rows.MoveToImmutable();
    }
}
