using System.Collections.Immutable;
using DfoServer.GameWorld.Towns;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record EvildomWarDungeon(int Id, byte Mode, byte Kind, byte Difficulty, int MinimumLevel);
internal sealed record EvildomWarHiddenDungeon(byte Mode, int Id, int RawWeight);
// 保留配置五列；与1673字段的映射、抽样分母及困难表叠加方式尚未闭合。
internal sealed record EvildomWarRewardDefinition(int GroupRaw, int TypeRaw, int ItemRaw, int CountRaw, int WeightRaw);

internal sealed class EvildomWarCatalog
{
    public const string Path = "etc/dungeonetc/evildomwar.etc";
    public ImmutableArray<EvildomWarDungeon> Dungeons { get; }
    public ImmutableDictionary<byte, int> MemberLimits { get; }
    public Location WaitingArea { get; }
    public int LimitTime { get; }
    public int IncountDungeon { get; }
    public int DailyLimit { get; }
    public int WeeklyLimit { get; }
    public ushort NeedFatigue { get; }
    public ImmutableArray<EvildomWarHiddenDungeon> HiddenDungeons { get; }
    public ImmutableArray<EvildomWarRewardDefinition> Rewards { get; }
    public ImmutableArray<EvildomWarRewardDefinition> HardModeRewards { get; }
    public ImmutableArray<EvildomWarRewardDefinition> GuideModeRewards { get; }
    public ImmutableDictionary<byte, int> AntiEvilLimits { get; }
    public Location RecruitingArea { get; }
    public int MovieTime { get; }
    public int DisconnectWaitTime { get; }
    public int DisconnectRetryItem { get; }
    public int SwordBuffRate { get; }
    public int ShieldBuffRate { get; }

    internal EvildomWarCatalog(EvildomWarFile file, Func<int, DungeonDefinition> get)
    {
        if (file.Dungeons.IsEmpty || file.Dungeons.Length % 3 != 0 || file.PartyMembers.IsEmpty ||
            file.PartyMembers.Length % 2 != 0 || file.WaitingArea is not [>= 0 and <= 255, >= 0 and <= 255, >= 0 and <= 65535, >= 0 and <= 65535] ||
            file.LimitTime <= 0 || file.IncountDungeon <= 0 || file.DailyLimit <= 0 || file.WeeklyLimit <= 0 || file.NeedFatigue is < 0 or > 65535)
            throw new InvalidDataException("魔界大战准入资源无效");
        var members = ImmutableDictionary.CreateBuilder<byte, int>();
        for (int i = 0; i < file.PartyMembers.Length; i += 2)
            if (file.PartyMembers[i] is < 0 or > 2 || file.PartyMembers[i + 1] is < 1 or > 4 ||
                !members.TryAdd((byte)file.PartyMembers[i], file.PartyMembers[i + 1]))
                throw new InvalidDataException("魔界大战队伍模式或人数无效");
        var dungeons = ImmutableArray.CreateBuilder<EvildomWarDungeon>();
        for (int i = 0; i < file.Dungeons.Length; i += 3)
        {
            int mode = file.Dungeons[i], kind = file.Dungeons[i + 1], id = file.Dungeons[i + 2];
            var definition = get(id);
            if (mode is < 0 or > 2 || kind is < 0 or > 1 || !members.ContainsKey((byte)mode) ||
                !definition.RawSections.Any(row => row.Tag == "evildom war dungeon") ||
                !definition.TryGetDesignatedDifficulty(out byte difficulty) || definition.MinimumLevel is not > 0 ||
                dungeons.Any(row => row.Id == id)) throw new InvalidDataException("魔界大战副本声明不匹配");
            dungeons.Add(new(id, (byte)mode, (byte)kind, difficulty, definition.MinimumLevel.Value));
        }
        if (members.Count != 3 || members.Keys.Any(mode => dungeons.Count(row => row.Mode == mode && row.Kind == 0) != 4 ||
            dungeons.Count(row => row.Mode == mode && row.Kind == 1) != 1))
            throw new InvalidDataException("匹配客户端的魔界大战每模式必须有四张普通图和一张王座");
        Dungeons = dungeons.ToImmutable();
        MemberLimits = members.ToImmutable();
        WaitingArea = new((byte)file.WaitingArea[0], (byte)file.WaitingArea[1], (ushort)file.WaitingArea[2], (ushort)file.WaitingArea[3]);
        LimitTime = file.LimitTime;
        IncountDungeon = file.IncountDungeon;
        DailyLimit = file.DailyLimit;
        WeeklyLimit = file.WeeklyLimit;
        NeedFatigue = (ushort)file.NeedFatigue;
        if (file.RecruitingArea is not [>= 0 and <= 255, >= 0 and <= 255, >= 0 and <= 65535, >= 0 and <= 65535] ||
            file.MovieTime < 0 || file.DisconnectWaitTime <= 0 || file.DisconnectRetryItem <= 0 ||
            file.SwordBuffRate < 0 || file.ShieldBuffRate < 0 || file.HiddenDungeons.IsEmpty ||
            file.HiddenDungeons.Length % 3 != 0 || file.AntiEvilLimits.IsEmpty || file.AntiEvilLimits.Length % 2 != 0)
            throw new InvalidDataException("魔界大战附加配置无效");
        var hidden = ImmutableArray.CreateBuilder<EvildomWarHiddenDungeon>();
        for (int i = 0; i < file.HiddenDungeons.Length; i += 3)
        {
            int mode = file.HiddenDungeons[i], id = file.HiddenDungeons[i + 1], weight = file.HiddenDungeons[i + 2];
            if (mode is < 0 or > 2 || !members.ContainsKey((byte)mode) || weight < 0 ||
                hidden.Any(row => row.Mode == mode || row.Id == id) || Dungeons.Any(row => row.Id == id) ||
                !get(id).RawSections.Any(row => row.Tag == "evildom war dungeon"))
                throw new InvalidDataException("魔界大战隐藏图配置不匹配");
            hidden.Add(new((byte)mode, id, weight));
        }
        var antiEvil = ImmutableDictionary.CreateBuilder<byte, int>();
        for (int i = 0; i < file.AntiEvilLimits.Length; i += 2)
            if (file.AntiEvilLimits[i] is < 0 or > 2 || file.AntiEvilLimits[i + 1] < 0 ||
                !antiEvil.TryAdd((byte)file.AntiEvilLimits[i], file.AntiEvilLimits[i + 1]))
                throw new InvalidDataException("魔界大战抗魔模式或阈值无效");
        if (hidden.Count != members.Count || antiEvil.Count != members.Count)
            throw new InvalidDataException("魔界大战附加配置缺少模式");
        HiddenDungeons = hidden.ToImmutable();
        AntiEvilLimits = antiEvil.ToImmutable();
        Rewards = RewardRows(file.Rewards);
        HardModeRewards = RewardRows(file.HardModeRewards);
        GuideModeRewards = RewardRows(file.GuideModeRewards);
        RecruitingArea = new((byte)file.RecruitingArea[0], (byte)file.RecruitingArea[1],
            (ushort)file.RecruitingArea[2], (ushort)file.RecruitingArea[3]);
        MovieTime = file.MovieTime;
        DisconnectWaitTime = file.DisconnectWaitTime;
        DisconnectRetryItem = file.DisconnectRetryItem;
        SwordBuffRate = file.SwordBuffRate;
        ShieldBuffRate = file.ShieldBuffRate;
    }

    static ImmutableArray<EvildomWarRewardDefinition> RewardRows(ImmutableArray<int> values)
    {
        if (values.IsEmpty || values.Length % 5 != 0) throw new InvalidDataException("魔界大战奖励行宽度无效");
        var result = ImmutableArray.CreateBuilder<EvildomWarRewardDefinition>();
        for (int i = 0; i < values.Length; i += 5)
            result.Add(new(values[i], values[i + 1], values[i + 2], values[i + 3], values[i + 4]));
        return result.ToImmutable();
    }

    public static EvildomWarCatalog Load(PvfArchive pvf, DungeonCatalog dungeons) =>
        new(EvildomWarFile.Parse(pvf.ReadTokens(Path), pvf.ResolveString), dungeons.Get);
}
