using System.Collections.Immutable;

namespace DfoServer.GameWorld.Items;

// 只解释本活动原三源及三套，运行中的死亡处理不再读取资源。
internal sealed class SpringTreasureAvatarCatalog
{
    public ImmutableDictionary<int, ItemTemplate> Members { get; }
    public ImmutableDictionary<int, int> SetCounts { get; }

    internal SpringTreasureAvatarCatalog(IEnumerable<(ItemTemplate Template, int SetId)> rows)
    {
        var entries = rows.ToArray();
        if (entries.Length == 0 || entries.Any(row => row.Template.Kind != ItemKind.Avatar || row.Template.EquipmentSlot > 7 ||
            row.SetId is not (30179 or 30180 or 30181)) || entries.Select(row => row.Template.Id).Distinct().Count() != entries.Length)
            throw new InvalidDataException("春节宝藏装扮目录存在重复或非八槽三套成员");
        Members = entries.ToImmutableDictionary(row => row.Template.Id, row => row.Template);
        SetCounts = entries.GroupBy(row => row.SetId).ToImmutableDictionary(group => group.Key, group => group.Count());
    }

    public static SpringTreasureAvatarCatalog Load(AvatarPackageCatalog packages, Func<int, ItemTemplate> template,
        Func<int, EquipmentSetInvestmentMetadata?> set)
    {
        var ids = new HashSet<int>();
        foreach (int source in new[] { 490705387, 490705425, 490705439 })
        {
            var package = packages.Get(source);
            if (!package.Available || package.Categories.Any(category => category.PendingRule is not null))
                throw new InvalidDataException("春节宝藏原装扮源尚不支持完整解释");
            foreach (var row in package.Categories.SelectMany(category => category.Rows))
            {
                if (row.Count != 1) throw new InvalidDataException("春节宝藏原装扮源数量改变");
                ids.Add(row.ItemId);
            }
        }
        var members = new List<(ItemTemplate, int)>();
        foreach (int id in ids.Order())
        {
            var item = template(id);
            if (item.Kind != ItemKind.Avatar) throw new InvalidDataException("春节装扮源出现非装扮模板");
            if (item.EquipmentSlot > 7) continue;
            var membership = set(id);
            if (membership?.SetId is 30179 or 30180 or 30181) members.Add((item, membership.SetId));
        }
        var result = new SpringTreasureAvatarCatalog(members);
        if (result.Members.Count != 576 || result.SetCounts.GetValueOrDefault(30179) != 112 ||
            result.SetCounts.GetValueOrDefault(30180) != 448 || result.SetCounts.GetValueOrDefault(30181) != 16)
            throw new InvalidDataException("春节宝藏三套原成员总数不符");
        return result;
    }
}
