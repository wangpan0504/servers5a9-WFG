using System.Collections.Immutable;

namespace PvfLib;

public sealed record PcRoomRewardRow(int Grade, int ItemId, int Count);
public sealed record PcRoomBuffRow(int Grade, int BuffId);
public sealed record PcRoomPremiumFile(ImmutableArray<PcRoomRewardRow> LoginRewards,
    int RentalItemId, ImmutableArray<PcRoomBuffRow> Buffs,
    ImmutableArray<int> Grade1Features, ImmutableArray<int> Grade2Features,
    ImmutableArray<(int Grade, int Percent)> AccelerationRates)
{
    public static PcRoomPremiumFile Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        PvfTokenSection Required(string name)
        {
            var rows = document.Sections.Where(row => row.Name == name).ToArray();
            return rows is [var row] ? row : throw new InvalidDataException("网吧声明缺失或重复：" + name);
        }
        static int[] Integers(PvfTokenSection section, int width)
        {
            if (!section.Children.IsEmpty || section.Values.Length % width != 0 || section.Values.Any(value => value.Type != 0))
                throw new InvalidDataException("网吧声明行宽或类型无效：" + section.Name);
            return section.Values.Select(value => value.Value).ToArray();
        }
        var login = Integers(Required("login reward info"), 3);
        var buffs = Integers(Required("various buff index"), 2);
        var rental = Integers(Required("legendary rental item"), 1);
        if (rental.Length != 1) throw new InvalidDataException("网吧租赁礼盒声明必须为单个编号");
        var rates = ImmutableArray.CreateBuilder<(int Grade, int Percent)>();
        // 本版用第二个同名空标签结束此表；保留所有非空行，不把它当作重复覆盖。
        foreach (var field in document.Sections.Where(row => row.Name == "accel fatigue bonus exp rate"))
        {
            var values = Integers(field, 2);
            for (int at = 0; at < values.Length; at += 2) rates.Add((values[at], values[at + 1]));
        }
        return new(Enumerable.Range(0, login.Length / 3).Select(at => new PcRoomRewardRow(login[at * 3], login[at * 3 + 1], login[at * 3 + 2])).ToImmutableArray(),
            rental[0], Enumerable.Range(0, buffs.Length / 2).Select(at => new PcRoomBuffRow(buffs[at * 2], buffs[at * 2 + 1])).ToImmutableArray(),
            Integers(Required("grade 1"), 1).ToImmutableArray(), Integers(Required("grade 2"), 1).ToImmutableArray(), rates.ToImmutable());
    }
}

public sealed record PcRoomRentalTier(int Index, int MinimumLevel);
public sealed record PcRoomRentalItem(int ItemId, int Upgrade, int Refinement, int Count);
public sealed record PcRoomRentalGroup(string JobTag, int Tier, ImmutableArray<PcRoomRentalItem> Items);
public sealed record PcRoomRentalFile(ImmutableArray<PcRoomRentalTier> Tiers, ImmutableArray<PcRoomRentalGroup> Groups)
{
    public static PcRoomRentalFile Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var sections = document.Sections.Where(row => row.Name == "section").ToArray();
        if (sections is not [{ Children.Length: 0 } section] || section.Values.IsEmpty ||
            section.Values.Length % 5 != 0 || section.Values.Any(value => value.Type != 0))
            throw new InvalidDataException("网吧武器租赁等级表不是完整五列");
        var tiers = ImmutableArray.CreateBuilder<PcRoomRentalTier>();
        for (int at = 0; at < section.Values.Length; at += 5)
            tiers.Add(new(section.Values[at].Value, section.Values[at + 1].Value));
        var groups = ImmutableArray.CreateBuilder<PcRoomRentalGroup>();
        foreach (var group in document.Sections.Where(row => row.Name == "group"))
        {
            if (group.Values is not [{ Type: 6 } job, { Type: 0 } tier] ||
                group.Children is not [{ Name: "package selection", Children.Length: 0 } selection] ||
                selection.Values.IsEmpty || selection.Values.Length % 4 != 0 || selection.Values.Any(value => value.Type != 0))
                throw new InvalidDataException("网吧武器租赁职业或四列候选无效");
            var items = ImmutableArray.CreateBuilder<PcRoomRentalItem>();
            for (int at = 0; at < selection.Values.Length; at += 4)
                items.Add(new(selection.Values[at].Value, selection.Values[at + 1].Value,
                    selection.Values[at + 2].Value, selection.Values[at + 3].Value));
            groups.Add(new(resolve(job.Value), tier.Value, items.ToImmutable()));
        }
        return new(tiers.ToImmutable(), groups.ToImmutable());
    }
}
