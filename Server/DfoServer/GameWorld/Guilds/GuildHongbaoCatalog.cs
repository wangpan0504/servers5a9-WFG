using System.Collections.Frozen;
using PvfLib;

namespace DfoServer.GameWorld.Guilds;

internal sealed record GuildHongbaoDefinition(int ItemId, int CommodityId, int Cera, int Reserved);

internal sealed class GuildHongbaoCatalog
{
    public const string ResourcePath = "etc/chn_hongbao/chn_hongbao.etc";
    public FrozenDictionary<int, GuildHongbaoDefinition> ByCommodity { get; }
    public int MinimumMembers { get; }
    public int DailyCeraLimit { get; }
    public int DailyTakeLimit { get; }

    public GuildHongbaoCatalog(PvfArchive pvf) : this(pvf.ReadText(ResourcePath)) { }

    internal GuildHongbaoCatalog(string text)
    {
        int Scalar(string name)
        {
            int[] values = PvfScript.Integers(PvfScript.Section(text, name));
            return values.Length == 1 && values[0] > 0 ? values[0] :
                throw new InvalidDataException("公会红包购买限制缺失或无效: " + name);
        }
        MinimumMembers = Scalar("min guild member count");
        DailyCeraLimit = Scalar("max total cera per day");
        DailyTakeLimit = Scalar("max take count per day");
        int[] rows = PvfScript.Integers(PvfScript.Block(text, "hongbao"));
        if (MinimumMembers > ushort.MaxValue || rows.Length == 0 || rows.Length % 4 != 0)
            throw new InvalidDataException("公会红包商品声明无效");
        var definitions = new Dictionary<int, GuildHongbaoDefinition>();
        var items = new HashSet<int>();
        for (int at = 0; at < rows.Length; at += 4)
        {
            var row = new GuildHongbaoDefinition(rows[at], rows[at + 1], rows[at + 2], rows[at + 3]);
            // 第四值只保留原值；尚无证据将其解释为返点率或领取比例。
            if (row.ItemId <= 0 || row.CommodityId <= 0 || row.Cera <= 0 || row.Reserved < 0 ||
                !items.Add(row.ItemId) || !definitions.TryAdd(row.CommodityId, row))
                throw new InvalidDataException("公会红包商品重复或数值无效");
        }
        ByCommodity = definitions.ToFrozenDictionary();
    }
}
