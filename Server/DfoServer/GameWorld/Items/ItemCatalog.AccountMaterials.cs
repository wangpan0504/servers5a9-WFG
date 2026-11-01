using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record AccountMaterialDefinition(int HoldingLimit, int WeeklyAccountLimit = 0, int WeeklyCharacterLimit = 0);

internal sealed partial class ItemCatalog
{
    readonly Lazy<ImmutableDictionary<int, AccountMaterialDefinition>> accountMaterialLimits;
    public IReadOnlyDictionary<int, AccountMaterialDefinition> RaidAccountMaterialLimits => accountMaterialLimits.Value;

    AccountMaterialDefinition? ReadAccountMaterial(int id, bool equipment, ItemKind kind, PvfDocument text, int stackLimit)
    {
        if (equipment || !text.Has("stack in account inventory")) return null;
        var raw = StackableDefinition(id) ?? throw new InvalidDataException("账号材料缺少堆叠物品声明");
        if (raw.UnsupportedFields.Contains("stack in account inventory") || raw.DeclaredStackInAccountInventory is not (0 or 1))
            throw new InvalidDataException("账号材料容器声明无效：" + id);
        if (raw.DeclaredStackInAccountInventory == 0) return null;
        if (kind != ItemKind.Material || text.Section("attach type").Trim('`', ' ', '\r', '\n') != "[account]")
            throw new InvalidDataException("账号材料类型或归属声明无效：" + id);
        var limits = accountMaterialLimits.Value.GetValueOrDefault(id);
        return new(stackLimit, limits?.WeeklyAccountLimit ?? 0, limits?.WeeklyCharacterLimit ?? 0);
    }

    static ImmutableDictionary<int, AccountMaterialDefinition> ReadAccountMaterialLimits(PvfArchive archive)
    {
        var rows = ImmutableDictionary.CreateBuilder<int, AccountMaterialDefinition>();
        foreach (string path in new[] { "etc/raid/raidrewardlimit.etc", "etc/raid/chn_raidrewardlimit.etc" })
        {
            if (!archive.Contains(path)) continue;
            var document = new PvfTokenDocument(archive.ReadTokens(path), archive.ResolveString);
            foreach (string name in new[] { "top raid common coin limit", "top raid common coin limit character" })
            {
                var sections = document.Sections.Where(section => section.Name == name).ToArray();
                if (sections.Length == 0) continue;
                if (!document.Diagnostics.IsEmpty || sections is not [{ Values: [{ Type: 0, Value: > 0 } item, { Type: 0, Value: > 0 } limit], Children.Length: 0 }])
                    throw new InvalidDataException("攻坚账号材料额度声明无效：" + path + "/" + name);
                var before = rows.GetValueOrDefault(item.Value) ?? new AccountMaterialDefinition(int.MaxValue);
                rows[item.Value] = name == "top raid common coin limit"
                    ? before with { WeeklyAccountLimit = limit.Value }
                    : before with { WeeklyCharacterLimit = limit.Value };
            }
        }
        return rows.ToImmutable();
    }
}
