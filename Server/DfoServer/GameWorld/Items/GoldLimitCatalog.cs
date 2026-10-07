using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed class GoldLimitCatalog
{
    readonly int[] limits;
    public GoldLimitCatalog(PvfArchive pvf) : this(pvf.ReadText("etc/goldlimitbylevel.etc")) { }

    internal GoldLimitCatalog(string text)
    {
        int[] values = PvfScript.Integers(PvfScript.Block(text, "gold limit from level"));
        if (values.Length == 0 || values.Length % 2 != 0 || Enumerable.Range(0, values.Length / 2).Any(i => values[i * 2] != i || values[i * 2 + 1] < 0))
            throw new InvalidDataException("PVF 金币上限表不完整");
        limits = Enumerable.Range(0, values.Length / 2).Select(i => values[i * 2 + 1]).ToArray();
    }
    public int CarryLimit(int level) => limits[Math.Clamp(level, 0, limits.Length - 1)];
}
