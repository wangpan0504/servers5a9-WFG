using DfoServer.GameWorld.Characters;
using PvfLib;

namespace DfoServer;

internal static class SkillCatalogTests
{
    public static void Run(PvfArchive pvf)
    {
        var catalog = SkillCatalog.Load(pvf);
        Check(catalog.GetLevelSpTotal(1) == 0, "Lv1 不赠送技能点");
        Check(catalog.GetLevelSpTotal(2) == 30 && catalog.GetLevelSpTotal(3) == 60,
            "正常等级表的 SP 为累计值");
        Check(catalog.GetLevelSpTotal(10) == 270 && catalog.GetLevelSpTotal(11) == 330 &&
            catalog.GetLevelSpTotal(95) == 13320, "SP 跨分段边界累计");
        Check(catalog.TpTable[0] == new SkillPointTableEntry(50, 1) &&
            catalog.TpTable[^1] == new SkillPointTableEntry(100, 1), "TP 保留资源等级与原值");
        Reject<ArgumentOutOfRangeException>(() => catalog.GetLevelSpTotal(0));
        Reject<ArgumentOutOfRangeException>(() => catalog.GetLevelSpTotal(201));

        PvfToken[] tokens = pvf.ReadTokens("etc/sptable.etc");
        int firstValue = Array.FindIndex(tokens, token => token.Type == 0);
        PvfToken[] badType = (PvfToken[])tokens.Clone();
        badType[firstValue] = new PvfToken(4, 1);
        Reject<InvalidDataException>(() => SkillCatalog.Parse(badType, pvf.ResolveString));
        PvfToken[] badOrder = (PvfToken[])tokens.Clone();
        badOrder[firstValue + 2] = new PvfToken(0, 1);
        Reject<InvalidDataException>(() => SkillCatalog.Parse(badOrder, pvf.ResolveString));
        PvfToken[] negative = (PvfToken[])tokens.Clone();
        negative[firstValue + 1] = new PvfToken(0, -1);
        Reject<InvalidDataException>(() => SkillCatalog.Parse(negative, pvf.ResolveString));
        Reject<InvalidDataException>(() => SkillCatalog.Parse(tokens[..^1], pvf.ResolveString));
        Reject<InvalidDataException>(() => SkillCatalog.Parse([.. tokens, .. tokens], pvf.ResolveString));
        string[] tags = ["[sp table]", "[/sp table]", "[tp table]", "[/tp table]"];
        var custom = SkillCatalog.Parse(
            [new(3, 0), new(0, 1), new(0, 5), new(0, 2), new(0, 7), new(3, 1),
             new(3, 2), new(0, 3), new(0, 2), new(3, 3)], id => tags[id]);
        Check(custom.GetLevelSpTotal(1) == 5 && custom.GetLevelSpTotal(2) == 12 &&
            custom.TpTable.Single() == new SkillPointTableEntry(3, 2), "可解析的自定义点数和表长度无需匹配固定资源");
        var limits = new GameWorld.Items.GoldLimitCatalog("[gold limit from level]\n0 10 1 20 2 30\n[/gold limit from level]");
        Check(limits.CarryLimit(1) == 20 && limits.CarryLimit(99) == 30, "金币上限沿实际资源长度读取末级值");
        Console.WriteLine("PASS 技能点表的累计、边界及非法资源拒绝");
    }

    private static void Check(bool valid, string name)
    {
        if (!valid)
            throw new InvalidOperationException("FAIL: " + name);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException("FAIL: 未拒绝 " + typeof(T).Name);
    }
}
