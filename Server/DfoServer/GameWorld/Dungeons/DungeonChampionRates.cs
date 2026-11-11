namespace DfoServer.GameWorld.Dungeons;

internal readonly record struct DungeonChampionRates(int Difficulty0, int Difficulty1, int Difficulty2, int Difficulty3)
{
    public bool IsEnabled => Difficulty0 > 0 || Difficulty1 > 0 || Difficulty2 > 0 || Difficulty3 > 0;
    public int Percent(byte difficulty) => difficulty switch
    {
        0 => Difficulty0, 1 => Difficulty1, 2 => Difficulty2, _ => Difficulty3
    };

    // [champion] 后四项是难度 0–3 的出现百分比；首项语义未闭合，不冒充词条ID或数量。
    // 普通卢克及持有正式团本准入的路线复用资源概率；其他特殊玩法仍保持隔离。
    public static DungeonChampionRates For(DungeonDefinition dungeon, bool raidAdmission = false)
    {
        if (dungeon.IsRaid && !dungeon.IsOrdinaryLuke && !raidAdmission || dungeon.IsTutorial || dungeon.IsClassExperience || dungeon.IsTournament ||
            dungeon.Id == 5000 || dungeon.AltarKind != 0 || dungeon.IsDespairTower || dungeon.RequiresGriefEntryContract ||
            dungeon.Path.StartsWith("dungeon/towers/", StringComparison.OrdinalIgnoreCase) ||
            dungeon.DungeonType?.Trim() is not (null or "" or "dungeon") || dungeon.RequiresCosmoEntryContract ||
            dungeon.IsInfernoDungeon || dungeon.IsCrackOfDimension ||
            dungeon.RawSections.Any(row => row.Tag == "special dungeon")) return default;
        var rows = dungeon.RawSections.Where(row => row.Tag == "champion").ToArray();
        if (rows is not [{ Tokens: { Count: 5 } tokens }] || tokens.Any(token => token.Type != 0) ||
            tokens.Skip(1).Any(token => token.Value is < 0 or > 100)) return default;
        return new(tokens[1].Value, tokens[2].Value, tokens[3].Value, tokens[4].Value);
    }
}
