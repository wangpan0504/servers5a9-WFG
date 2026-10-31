using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

// 只解析显示编号，不创建物品；基础映射对应ARAD 02B61730，觉醒选择对应01E26BF0。
// 大多数场景不需要服务端解析并下发具体默认编号；消费者支持原生默认选择时，空槽发送-1即可。
// 仅在业务确需具体编号时使用本类，具体字段的哨兵值和客户端消费路径仍须分别核对。
internal sealed class CharacterDefaultAppearance
{
    public const string AwakeningPath = "character/chn_1stawaken_defaultavatarinfo.chr";
    readonly FrozenDictionary<(int Job, int Grow), ImmutableArray<int>> awakenings;

    // 列为显示槽0..12。新职业没有对应characteretc文件，不能借用其他职业或只靠旧表补默认。
    static readonly ImmutableArray<ImmutableArray<int>> Normal =
    [
        [-1, 39400, -1, 40600, 41000, 41800, -1, -1, 42200, -1, -1, -1, 27118],
        [-1, 43400, -1, 44600, 45000, 45800, -1, -1, 46200, -1, -1, -1, 29000],
        [104551334, 47400, -1, 48600, 49000, 49800, -1, -1, 50200, -1, -1, -1, 31000],
        [51265, 51400, -1, 52600, 53000, 53800, -1, -1, 54200, -1, -1, -1, 33679],
        [-1, 55400, 55820, 56600, 57000, 57800, -1, -1, 58200, -1, -1, -1, 35000],
        [1600000, 1610000, -1, 1640000, 1650000, 1670000, -1, -1, 1680000, -1, -1, -1, 31000],
        [1720000, 1730000, -1, 1760000, 1770000, 1790000, 1750000, -1, 1800000, -1, -1, -1, 37000],
        [-1, 29201, -1, 29202, 29203, 29204, -1, -1, 29205, -1, -1, -1, 29000],
        [-1, 2090000, -1, 2120000, 2130000, 2150000, -1, 2140000, 2160000, -1, -1, -1, 33679],
        [-1, 39400, -1, 40600, 41000, 41800, -1, -1, 42200, -1, -1, -1, 27118],
        [51265, 51400, -1, 52600, 53000, 53800, -1, -1, 54200, -1, -1, -1, 33679],
        [-1, 112560000, -1, 112500000, 112510000, 112540000, 112520000, -1, 112580001, -1, -1, -1, 27850],
        [-1, 113560000, -1, 113500000, 113510000, 113540000, 113520000, -1, 113580000, -1, -1, -1, 27250],
        [-1, 114560000, -1, 114500000, 114510000, 114540000, -1, -1, 114580091, -1, -1, -1, 114000155],
        [-1, 115560000, -1, 115500000, 115510000, 115540000, -1, -1, 115580000, -1, -1, -1, 35000],
        [-1, 116560000, 116570000, 116500000, 116510000, 116540000, -1, 116530000, 116580000, -1, -1, -1, 116000147]
    ];

    internal CharacterDefaultAppearance(IEnumerable<CharacterAwakeningEquipment> entries)
    {
        var rows = entries.ToArray();
        if (rows.Any(row => row.Job is < 0 or > 15 || row.GrowType is < 0 or > 15 ||
            row.Equipment.IsDefault || row.Equipment.Length != 13 || row.Equipment.Any(id => id < -1)))
            throw new InvalidDataException("觉醒默认装扮超出本版职业或槽位范围");
        awakenings = rows.ToFrozenDictionary(row => (row.Job, row.GrowType), row => row.Equipment);
    }

    public static CharacterDefaultAppearance Load(PvfArchive archive) => new(archive.Contains(AwakeningPath)
        ? CharacterEquipmentFile.ReadAwakeningEquipment(archive.ReadTokens(AwakeningPath)) : []);

    public int GetEquipment(byte job, byte growType, byte slot, bool awakeningAppearanceEnabled = true,
        bool? clientOption1Enabled = null)
    {
        if (job >= Normal.Length) return -1;
        int first = growType & 15;
        if (awakeningAppearanceEnabled && (growType >> 4 & 7) != 0 && first != 0 && slot <= 9 &&
            awakenings.TryGetValue((job, first), out var equipment) && equipment[slot] != -1)
            return equipment[slot];
        // 原生男鬼剑第五分支皮肤还受客户端选项1控制；未知时保留-1，让客户端自行决定。
        if (job == 0 && first == 5 && slot == 8)
            return clientOption1Enabled is null ? -1 : clientOption1Enabled.Value ? 101581022 : 42200;
        if (job == 12 && slot == 24) return 113370002;
        return slot < Normal[job].Length ? Normal[job][slot] : -1;
    }

    public ImmutableDictionary<byte, int> Resolve(byte job, byte growType, bool awakeningAppearanceEnabled = true,
        bool? clientOption1Enabled = null)
    {
        var result = ImmutableDictionary.CreateBuilder<byte, int>();
        for (byte slot = 0; slot < 36; slot++)
        {
            int item = GetEquipment(job, growType, slot, awakeningAppearanceEnabled, clientOption1Enabled);
            if (item != -1) result.Add(slot, item);
        }
        return result.ToImmutable();
    }
}
