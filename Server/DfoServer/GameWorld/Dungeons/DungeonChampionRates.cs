namespace DfoServer.GameWorld.Dungeons;

internal static class DungeonChampionRates
{
    // 断头谷 [champion] 3 25 40 60 60：首项是未恢复的能力档，后四项是难度 0–3 的精英出现百分比。
    // 只对 7322 生效。这张图没有 [named monster]，6060 的稀有怪靠这里把普通怪升成生成类型 1。
    public static int[] For(DungeonDefinition dungeon)
    {
        if (dungeon.Id != 7322) return [];
        var rows = dungeon.RawSections.Where(row => row.Tag == "champion").ToArray();
        if (rows is not [{ Tokens: { Count: 5 } tokens }] || tokens.Any(token => token.Type != 0)) return [];
        var rates = new int[4];
        for (int index = 0; index < 4; index++)
        {
            int rate = tokens[index + 1].Value;
            if (rate is < 0 or > 100) return [];
            rates[index] = rate;
        }
        return rates;
    }
}
