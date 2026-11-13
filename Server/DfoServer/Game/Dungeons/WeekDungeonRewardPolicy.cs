namespace DfoServer.Game.Dungeons;

// 原生隐藏载体在宝箱407000500消失后自行DESTROY；奖励随真实宝箱死亡提交，避免终点清场吞掉载体掉落。
internal static class WeekDungeonRewardPolicy
{
    internal const uint DungeonId = 400000096;
    internal const int ChestId = 407000500;
    internal const int CarrierId = 407000499;
    internal const string Rule = "week-dungeon-hidden-chest-v1";
    const string MonsterRoot = "monster/newmonsters/gcontents/191128_weekdungeon/hidden/hiddenbox/";

    internal static bool Matches(DungeonEntryPlan entry, DungeonPlannedMonster monster)
    {
        if (entry.DungeonId != DungeonId || !entry.IsOrdinary || entry.IsPractice || entry.IsTutorial ||
            entry.IsClassExperience || entry.DisableItemDrops || entry.RaidEntry is not null ||
            monster.MonsterId is not (ChestId or CarrierId) || !monster.CanReportDeath || monster.IsFriendly ||
            monster.IsSpecialChild || monster.AiCharacter is not null || monster.HellWave is not null ||
            monster.SourceMap is not { Id: 400000486 or 400000492 or 400000498 } map || !entry.IsTerminalMap(map) ||
            !map.Path.StartsWith("map/gcontents/191128_weekdungeon/", StringComparison.OrdinalIgnoreCase))
            return false;
        string path = MonsterRoot + (monster.MonsterId == ChestId ? "hiddenbox.mob" : "hiddenbox_01.mob");
        return monster.Source?.Path.Equals(path, StringComparison.OrdinalIgnoreCase) == true &&
            monster.IsBoss == (monster.MonsterId == ChestId);
    }

    internal static DungeonDropGenerationPlan? Prepare(DungeonEntryPlan entry, DungeonPlannedMonster monster,
        Func<int, byte, DungeonDropGenerationPlan> prepare)
    {
        if (!Matches(entry, monster)) return null;
        // 不放宽死亡、清图或幂等门禁；载体先报、晚报或被清退都不能产生第二份奖励。
        if (monster.MonsterId == CarrierId)
            return new(Rule + ";carrier-retirement", []);
        var declared = prepare(CarrierId, 0);
        var trials = declared.Trials.Where(trial => trial.IndependentDrop).ToArray();
        if (trials.Length == 0)
            throw new NotSupportedException("异次元遗迹隐藏宝箱缺少奖励载体的独立掉落资源");
        return new(Rule + ";" + declared.ResourceSource,
            trials, declared.ReferenceQuality, declared.Amplification);
    }
}
