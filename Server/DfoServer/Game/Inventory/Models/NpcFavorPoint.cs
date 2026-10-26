using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace DfoServer.Game.Inventory.Models;

internal sealed record NpcFavorPoint(int NpcId, int Points)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int GiftDay { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public byte GiftCount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint Colors { get; init; }

    public byte EffectiveGifts(int day) => GiftDay >= day ? GiftCount : (byte)0;
    public byte Color(int index) => index is >= 0 and < 4 ? (byte)(Colors >> (index * 8)) :
        throw new ArgumentOutOfRangeException(nameof(index));
    public NpcFavorPoint WithColor(int index, byte value) => index is >= 0 and < 4 ?
        this with { Colors = (Colors & ~(255u << (index * 8))) | ((uint)value << (index * 8)) } :
        throw new ArgumentOutOfRangeException(nameof(index));

    public static void Validate(ImmutableArray<NpcFavorPoint> rows)
    {
        if (rows.IsDefault || rows.Length > byte.MaxValue || rows.Any(row => row is null || row.NpcId <= 0 || row.Points <= 0 ||
                row.GiftDay < 0 || row.GiftDay > DateOnly.MaxValue.DayNumber || row.GiftCount != 0 && row.GiftDay == 0) ||
            rows.Select(row => row.NpcId).Distinct().Count() != rows.Length)
            throw new InvalidDataException("角色NPC好感度快照无效");
    }
}
