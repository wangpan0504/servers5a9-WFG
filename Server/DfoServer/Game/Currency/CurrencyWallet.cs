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
    Cera, TokenCera, HappyTokenCera
}

// 账号余额随选中角色载入不可变快照；换角重读，不为每个角色复制持久余额。
internal sealed record AccountWallet(long Revision, ImmutableSortedDictionary<CurrencyKind, int> Balances)
{
    public static AccountWallet Empty { get; } = new(0, ImmutableSortedDictionary<CurrencyKind, int>.Empty);
    public int Get(CurrencyKind kind) => Balances.GetValueOrDefault(kind);
    public AccountWallet Set(CurrencyKind kind, int count)
    {
        if (!CurrencyWallet.IsAccount(kind) || count < 0)
            throw new InvalidDataException("无效账号货币余额");
        return this with { Balances = count == 0 ? Balances.Remove(kind) : Balances.SetItem(kind, count) };
    }
    public bool Same(AccountWallet other) => Revision == other.Revision && Balances.SequenceEqual(other.Balances);
    public void Validate()
    {
        if (Revision < 0 || Balances.Any(pair => !CurrencyWallet.IsAccount(pair.Key) || pair.Value <= 0))
            throw new InvalidDataException("无效账号钱包快照");
    }
}

internal static class CurrencyWallet
{
    public static bool IsAccount(CurrencyKind kind) => kind is >= CurrencyKind.BlackCube and <= CurrencyKind.HappyTokenCera;
    public static long Get(InventorySnapshot inventory, CurrencyKind kind) => kind switch
    {
        CurrencyKind.Gold => inventory.Gold,
        CurrencyKind.ReviveCoin => inventory.ReviveCoins,
        CurrencyKind.WinPoint => inventory.WinPoints,
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
}
