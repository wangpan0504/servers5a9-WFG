using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record AccountMaterialDefinition(int HoldingLimit, int WeeklyAccountLimit = 0, int WeeklyCharacterLimit = 0,
    bool CommonInventory = false);

internal sealed partial class ItemCatalog
{
    readonly Lazy<ImmutableDictionary<int, AccountMaterialDefinition>> accountMaterialLimits;
    public IReadOnlyDictionary<int, AccountMaterialDefinition> RaidAccountMaterialLimits => accountMaterialLimits.Value;

    AccountMaterialDefinition? ReadAccountMaterial(int id, bool equipment, ItemKind kind, PvfDocument text, int stackLimit)
    {
        if (equipment || !text.Has("stack in account inventory") && !text.Has("stack in account common item inventory")) return null;
        var raw = StackableDefinition(id) ?? throw new InvalidDataException("账号材料缺少堆叠物品声明");
        bool common = text.Has("stack in account common item inventory");
        // 共通声明按标记存在启用，不要求正文为空；list45与普通账号材料list44共用账号余额事务。
        if (!common && (raw.UnsupportedFields.Contains("stack in account inventory") || raw.DeclaredStackInAccountInventory is not (0 or 1)))
            throw new InvalidDataException("账号材料容器声明无效：" + id);
        if (!common && raw.DeclaredStackInAccountInventory == 0) return null;
        if (kind != ItemKind.Material || text.Section("attach type").Trim('`', ' ', '\r', '\n') != "[account]")
            throw new InvalidDataException("账号材料类型或归属声明无效：" + id);
        // 星河两种材料沿已核实的45实体链保留完整核心，不转为账号余额。
        if (common && id is (490013716 or 490013717)) return null;
        var limits = accountMaterialLimits.Value.GetValueOrDefault(id);
        return new(stackLimit, limits?.WeeklyAccountLimit ?? 0, limits?.WeeklyCharacterLimit ?? 0, common);
    }

    static ImmutableDictionary<int, AccountMaterialDefinition> ReadAccountMaterialLimits(PvfArchive archive)
    {
        // 核心自测用空档案构造目录。没有 PVF 时额度文件不存在，与 Contains 为假相同。
        if (archive is null) return ImmutableDictionary<int, AccountMaterialDefinition>.Empty;
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
