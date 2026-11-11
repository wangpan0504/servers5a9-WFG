using DfoServer.GameWorld.Dungeons;
using System.Collections.Immutable;

namespace DfoServer.Game.Settings;

internal enum SupportCompanionKind { Off, WangFugui, Guangbing, FemaleCrusader, Enchantress }
internal sealed record SupportCompanionConfiguration(int SelectionMask, string WangFuguiTier, string FemaleCrusaderTier, string EnchantressTier)
{
    public static SupportCompanionConfiguration Default { get; } = new(0, "entry", "entry", "entry");
    public string TierFor(SupportCompanionKind kind) => kind switch
    {
        SupportCompanionKind.WangFugui => WangFuguiTier,
        SupportCompanionKind.FemaleCrusader => FemaleCrusaderTier,
        SupportCompanionKind.Enchantress => EnchantressTier,
        _ => "entry"
    };
    public static bool ValidTier(string tier) => tier is "entry" or "graduate" or "huge";
}

// 当前服务实例的调试设置；支援兵名单与档次、账号无限疲劳分别持久化，其余设置重启恢复各自默认值。
internal sealed class GameDebugSettings
{
    public DfoServer.Game.Events.AdventurerBenefitService? AdventurerBenefits { get; init; }
    readonly object supportGate = new();
    readonly Action<SupportCompanionConfiguration>? persistSupport;
    readonly object fatigueGate = new();
    readonly Action<ImmutableHashSet<string>>? persistFatigue;
    ImmutableHashSet<string> fatigueAccounts;

    public GameDebugSettings(SupportCompanionConfiguration? initialSupport = null,
        Action<SupportCompanionConfiguration>? persistSupport = null,
        IEnumerable<string>? initialFatigueAccounts = null, Action<ImmutableHashSet<string>>? persistFatigue = null)
    {
        var initial = initialSupport ?? SupportCompanionConfiguration.Default;
        ValidateSupportConfiguration(initial);
        supportCompanion = initial;
        this.persistSupport = persistSupport;
        fatigueAccounts = (initialFatigueAccounts ?? []).ToImmutableHashSet(StringComparer.Ordinal);
        this.persistFatigue = persistFatigue;
    }

    internal static void ValidateSupportConfiguration(SupportCompanionConfiguration value)
    {
        if (!ValidSupportCompanions(value.SelectionMask) || !SupportCompanionConfiguration.ValidTier(value.WangFuguiTier) ||
            !SupportCompanionConfiguration.ValidTier(value.FemaleCrusaderTier) || !SupportCompanionConfiguration.ValidTier(value.EnchantressTier))
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    // 仅供读取旧安装时迁移；未知位、多辅助及损坏档次仍按旧合同拒绝。
    internal static void ValidateLegacySupportConfiguration(SupportCompanionConfiguration value)
    {
        int mask = value.SelectionMask, auxiliary = mask & 13;
        if (mask < 0 || (mask & ~15) != 0 || (auxiliary & (auxiliary - 1)) != 0 ||
            !SupportCompanionConfiguration.ValidTier(value.WangFuguiTier) ||
            !SupportCompanionConfiguration.ValidTier(value.FemaleCrusaderTier) ||
            !SupportCompanionConfiguration.ValidTier(value.EnchantressTier))
            throw new ArgumentOutOfRangeException(nameof(value));
    }

    // 两个开关均为true时忽略对应声明，不能互相代替或翻转成启用期限。
    public volatile bool IgnoreCashShopAbsoluteExpiration = true;
    public volatile bool IgnoreCashShopUsablePeriod = false;
    // 无记录即关闭；先持久化新快照，再发布给该账号的准入、扣点与显示读者。
    public bool InfiniteFatigueFor(string account) => Volatile.Read(ref fatigueAccounts).Contains(account);
    public void SetInfiniteFatigue(string account, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(account)) throw new ArgumentException("须指定当前账号", nameof(account));
        lock (fatigueGate)
        {
            var next = enabled ? fatigueAccounts.Add(account) : fatigueAccounts.Remove(account);
            if (ReferenceEquals(next, fatigueAccounts)) return;
            persistFatigue?.Invoke(next);
            Volatile.Write(ref fatigueAccounts, next);
        }
    }
    // 网吧身份独立于账号黑钻；字段默认关闭，线上实例由 ServerRuntime 启动时按数据目录 pc-room.json 置为开（缺文件即开），
    // GM 的 on/off 只改当前进程，重启不沿用上次的临时调试资格。
    public volatile bool PremiumPcRoom;
    // 进图伴侣默认关闭；线上实例先持久化完整配置再发布，重启保留选择。塔与祭坛等类型仍由资源标记排除。
    // 同一原子位集合保存四职业；辅助最多一名，可同时选择光兵，旧单选接口替换全部选择。
    private SupportCompanionConfiguration supportCompanion = SupportCompanionConfiguration.Default;
    public SupportCompanionCatalog SupportProfiles { get; init; } = SupportCompanionCatalog.Empty;
    public SupportCompanionConfiguration SupportConfiguration
    {
        get => Volatile.Read(ref supportCompanion);
        set
        {
            ValidateSupportConfiguration(value);
            lock (supportGate) CommitSupport(value);
        }
    }
    public int SupportCompanions
    {
        get => SupportConfiguration.SelectionMask;
        set
        {
            if (!ValidSupportCompanions(value)) throw new ArgumentOutOfRangeException(nameof(value));
            UpdateSelection(_ => value);
        }
    }
    public SupportCompanionKind SupportCompanion
    {
        get => LegacySupportCompanion(SupportCompanions);
        set
        {
            if (value is < SupportCompanionKind.Off or > SupportCompanionKind.Enchantress)
                throw new ArgumentOutOfRangeException(nameof(value));
            UpdateSelection(_ => SupportCompanionBit(value));
        }
    }
    public bool SummonWangFugui { get => HasCompanion(SupportCompanions, SupportCompanionKind.WangFugui); set => SetCompanion(SupportCompanionKind.WangFugui, value); }
    public bool SummonGuangbing { get => HasCompanion(SupportCompanions, SupportCompanionKind.Guangbing); set => SetCompanion(SupportCompanionKind.Guangbing, value); }
    public bool SummonFemaleCrusader { get => HasCompanion(SupportCompanions, SupportCompanionKind.FemaleCrusader); set => SetCompanion(SupportCompanionKind.FemaleCrusader, value); }
    public bool SummonEnchantress { get => HasCompanion(SupportCompanions, SupportCompanionKind.Enchantress); set => SetCompanion(SupportCompanionKind.Enchantress, value); }

    private void SetCompanion(SupportCompanionKind kind, bool enabled)
    {
        if (enabled) SupportCompanion = kind;
        else UpdateSelection(mask => mask & ~SupportCompanionBit(kind));
    }

    private void UpdateSelection(Func<int, int> select)
    {
        lock (supportGate)
        {
            var before = SupportConfiguration;
            var after = before with { SelectionMask = select(before.SelectionMask) };
            ValidateSupportConfiguration(after);
            CommitSupport(after);
        }
    }

    private void CommitSupport(SupportCompanionConfiguration value)
    {
        if (value == SupportConfiguration) return;
        persistSupport?.Invoke(value);
        Volatile.Write(ref supportCompanion, value);
    }

    public static int SupportCompanionBit(SupportCompanionKind kind) => kind == SupportCompanionKind.Off ? 0 : 1 << ((int)kind - 1);
    public static bool HasCompanion(int mask, SupportCompanionKind kind) => (mask & SupportCompanionBit(kind)) != 0;
    public static bool ValidSupportCompanions(int mask)
    {
        int auxiliary = mask & 13;
        return mask >= 0 && (mask & ~15) == 0 && (auxiliary & (auxiliary - 1)) == 0;
    }

    public static int ParseSupportCompanions(string value)
    {
        if (value == "off") return 0;
        int mask = 0;
        foreach (string token in value.Split(','))
        {
            int selection = ParseSupportCompanion(token);
            if (selection <= 0) return -1;
            int bit = SupportCompanionBit((SupportCompanionKind)selection);
            if ((mask & bit) != 0) return -1;
            mask |= bit;
        }
        return ValidSupportCompanions(mask) ? mask : -1;
    }

    public static SupportCompanionKind[] SupportCompanionKinds(int mask) =>
        new[] { SupportCompanionKind.WangFugui, SupportCompanionKind.FemaleCrusader, SupportCompanionKind.Enchantress, SupportCompanionKind.Guangbing }
            .Where(kind => HasCompanion(mask, kind)).ToArray();
    public static string[] SupportCompanionNames(int mask) => SupportCompanionKinds(mask).Select(SupportCompanionName).ToArray();
    public static SupportCompanionKind LegacySupportCompanion(int mask) => SupportCompanionKinds(mask).FirstOrDefault();

    public static int ParseSupportCompanion(string value) => value switch
    {
        "off" => 0, "wangfugui" => 1, "guangbing" => 2, "female-crusader" => 3, "enchantress" => 4, _ => -1
    };

    public static string SupportCompanionName(SupportCompanionKind value) => value switch
    {
        SupportCompanionKind.Off => "off",
        SupportCompanionKind.WangFugui => "wangfugui",
        SupportCompanionKind.Guangbing => "guangbing",
        SupportCompanionKind.FemaleCrusader => "female-crusader",
        SupportCompanionKind.Enchantress => "enchantress",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}
