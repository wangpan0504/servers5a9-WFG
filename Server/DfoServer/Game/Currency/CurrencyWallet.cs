using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Currency;

// 数值用于持久化，后续种类只在末尾追加；它们不是协议槽位编号。
internal enum CurrencyKind
{
    Gold, ReviveCoin, WinPoint,
    BlackCube, WhiteCube, RedCube, BlueCube, ClearCube, GoldCube,
    CommonSoul, UncommonSoul, RareSoul, UniqueSoul,
    // 13/14原本已分别保存这两个物品的余额；更正名称，不能交换存量资产或客户端槽位。
    LegendarySoul = 13, EpicSoul = 14,
    Cera, TokenCera, HappyTokenCera,
    // 租赁幸运星是账号级虚拟余额，不映射到任何普通背包槽位。
    RentalPoint = 18,
    Mileage = 19
}

// 账号余额随选中角色载入不可变快照；换角重读，不为每个角色复制持久余额。
internal sealed record AccountWallet(long Revision, ImmutableSortedDictionary<CurrencyKind, int> Balances, int RentalPoints = 0,
    int SeriaLuck = 0, ImmutableSortedDictionary<int, long>? Contracts = null)
{
    public static AccountWallet Empty { get; } = new(0, ImmutableSortedDictionary<CurrencyKind, int>.Empty);
    public ushort CharacterSlotCapacity { get; init; } = 32;
    public int Mileage { get; init; }
    public MileageShopState MileageShop { get; init; } = MileageShopState.Empty;
    public ImmutableSortedDictionary<int, AccountMaterialBalance> Materials { get; init; } = ImmutableSortedDictionary<int, AccountMaterialBalance>.Empty;
    public AccountMaterialBalance Material(int itemId) => Materials.GetValueOrDefault(itemId) ?? AccountMaterialBalance.Empty;
    public AccountWallet SetMaterial(int itemId, AccountMaterialBalance value)
    {
        if (itemId <= 0) throw new InvalidDataException("账号材料编号无效");
        value.Validate();
        return this with { Materials = value.Balance == 0 && value.RaidWeek == 0 ? Materials.Remove(itemId) : Materials.SetItem(itemId, value) };
    }
    public bool SameMaterials(AccountWallet other) => Materials.Count == other.Materials.Count &&
        Materials.All(pair => other.Materials.TryGetValue(pair.Key, out var value) && pair.Value.Same(value));
    public ImmutableSortedDictionary<int, long> PremiumContracts => Contracts ?? ImmutableSortedDictionary<int, long>.Empty;
    public int Get(CurrencyKind kind) => kind switch
    {
        CurrencyKind.RentalPoint => RentalPoints,
        CurrencyKind.Mileage => Mileage,
        _ => Balances.GetValueOrDefault(kind)
    };
    public AccountWallet Set(CurrencyKind kind, int count)
    {
        if (!CurrencyWallet.IsAccount(kind) || count < 0)
            throw new InvalidDataException("无效账号货币余额");
        if (kind == CurrencyKind.RentalPoint)
            return this with { RentalPoints = count };
        if (kind == CurrencyKind.Mileage)
            return this with { Mileage = count };
        return this with { Balances = count == 0 ? Balances.Remove(kind) : Balances.SetItem(kind, count) };
    }
    public AccountWallet ActivateContract(int type, long durationSeconds, DateTimeOffset now, int count = 1)
    {
        if (type <= 0 || durationSeconds <= 0 || count <= 0)
            throw new InvalidDataException("无效契约期限");
        long current = PremiumContracts.GetValueOrDefault(type);
        long start = Math.Max(current, now.ToUnixTimeSeconds());
        long expiry = checked(start + checked(durationSeconds * count));
        return this with { Contracts = PremiumContracts.SetItem(type, expiry) };
    }
    public long ContractExpiry(int type) => PremiumContracts.GetValueOrDefault(type);
    public AccountWallet SetContractExpiry(int type, long expires)
    {
        if (type is <= 0 or >= 132 || expires < 0)
            throw new InvalidDataException("契约类型或绝对到期时间无效");
        return this with { Contracts = expires == 0 ? PremiumContracts.Remove(type) : PremiumContracts.SetItem(type, expires) };
    }
    public AccountWallet SetSeriaLuck(int value)
    {
        if (value is < 0 or > 8)
            throw new InvalidDataException("无效赛丽亚幸运值");
        return this with { SeriaLuck = value };
    }
    public bool Same(AccountWallet other) => Revision == other.Revision && CharacterSlotCapacity == other.CharacterSlotCapacity && RentalPoints == other.RentalPoints && Mileage == other.Mileage && MileageShop.Same(other.MileageShop) && SeriaLuck == other.SeriaLuck &&
        Balances.SequenceEqual(other.Balances) && PremiumContracts.SequenceEqual(other.PremiumContracts) && SameMaterials(other);
    public void Validate()
    {
        MileageShop.Validate();
        foreach (var (id, value) in Materials)
        {
            if (id <= 0) throw new InvalidDataException("账号材料编号无效");
            value.Validate();
        }
        if (CharacterSlotCapacity is < 32 or > 62 || Revision < 0 || RentalPoints < 0 || Mileage < 0 || SeriaLuck is < 0 or > 8 || Balances.Any(pair => !CurrencyWallet.IsAccount(pair.Key) || pair.Key is CurrencyKind.RentalPoint or CurrencyKind.Mileage || pair.Value <= 0) ||
            PremiumContracts.Any(pair => pair.Key <= 0 || pair.Value <= 0))
            throw new InvalidDataException("无效账号钱包快照");
    }
}

internal static class CurrencyWallet
{
    public static bool IsAccount(CurrencyKind kind) => kind is >= CurrencyKind.BlackCube and <= CurrencyKind.HappyTokenCera or CurrencyKind.RentalPoint or CurrencyKind.Mileage;
    public static long Get(InventorySnapshot inventory, CurrencyKind kind) => kind switch
    {
        CurrencyKind.Gold => inventory.Gold,
        CurrencyKind.ReviveCoin => inventory.ReviveCoins,
        CurrencyKind.WinPoint => inventory.WinPoints,
        CurrencyKind.RentalPoint => inventory.AccountWallet.RentalPoints,
        _ when IsAccount(kind) => inventory.AccountWallet.Get(kind),
        _ => throw new InvalidDataException("未知货币")
    };
    public static InventorySnapshot Set(InventorySnapshot inventory, CurrencyKind kind, long count)
    {
        if (count < 0 || count > (kind == CurrencyKind.Gold ? uint.MaxValue : int.MaxValue))
            throw new InvalidDataException("余额超出货币存储范围");
        return kind switch
        {
            CurrencyKind.Gold => inventory with { Gold = checked((uint)count) },
            CurrencyKind.ReviveCoin => inventory with { ReviveCoins = checked((int)count) },
            CurrencyKind.WinPoint => inventory with { WinPoints = checked((int)count) },
            CurrencyKind.RentalPoint => inventory with { AccountWallet = inventory.AccountWallet.Set(kind, checked((int)count)) },
            _ when IsAccount(kind) => inventory with { AccountWallet = inventory.AccountWallet.Set(kind, checked((int)count)) },
            _ => throw new InvalidDataException("未知货币")
        };
    }

    public static CurrencyKind? RewardKind(int itemId) => itemId switch
    {
        0 => CurrencyKind.Gold, 1 or 42 => CurrencyKind.ReviveCoin, 2 => CurrencyKind.WinPoint,
        3033 => CurrencyKind.BlackCube, 3034 => CurrencyKind.WhiteCube, 3035 => CurrencyKind.RedCube,
        3036 => CurrencyKind.BlueCube, 3037 => CurrencyKind.ClearCube, 3262 => CurrencyKind.GoldCube,
        10100115 => CurrencyKind.CommonSoul, 10100116 => CurrencyKind.UncommonSoul,
        10099773 => CurrencyKind.RareSoul, 10099774 => CurrencyKind.UniqueSoul,
        10099775 => CurrencyKind.LegendarySoul, 10158124 => CurrencyKind.EpicSoul,
        2681762 => CurrencyKind.TokenCera, 2681917 => CurrencyKind.HappyTokenCera,
        _ => null
    };

    public static CurrencyKind? TokenKind(int itemId) => RewardKind(itemId) is { } kind &&
        kind is CurrencyKind.TokenCera or CurrencyKind.HappyTokenCera ? kind : null;
}
