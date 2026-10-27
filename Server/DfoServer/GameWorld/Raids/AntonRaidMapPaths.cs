using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Raids;

// KB-20261027T112356-3f4d3db595：仅采纳原始MAP列表中已核对当前PVF的72项，不导入整表。
// 来源：Gloria-GK-406/DNF-JP-86-Server，提交1cff7218ddff1570dcd48eb07b779283ee7c6542，
// Document/abyss-research-20261007/evidence/upstream-pvf-map-lst.txt，第2行。
// 原表SHA256：f1522dfa14b5618cb4b85f3195d81cf8e62c3d3e3495123f23d14d3be0a53020。
// 路径与DGN布局分别保留原始证据；不根据文件名坐标修图，也不补背景或机制。
internal static class AntonRaidMapPaths
{
    internal static ImmutableDictionary<int, string> Paths { get; } = new Dictionary<int, string>
    {
        [17181] = "map/221_hatchery1/17181(0,0)boss.map",
        [17182] = "map/221_hatchery1/17182(1,0)named.map",
        [17183] = "map/221_hatchery1/17183(3,0)start.map",
        [17184] = "map/222_hatchery2/17184(0,0)boss.map",
        [17185] = "map/222_hatchery2/17185(0,1)named.map",
        [17186] = "map/222_hatchery2/17186(0,2)start.map",
        [17187] = "map/223_hatchery3/17187(0,0)start.map",
        [17188] = "map/223_hatchery3/17188(1,0)named.map",
        [17189] = "map/223_hatchery3/17189(2,0)boss.map",
        [17190] = "map/224_hatchery4/17190(0,0)start.map",
        [17191] = "map/224_hatchery4/17191(0,1)named.map",
        [17192] = "map/224_hatchery4/17192(0,2)boss.map",
        [17193] = "map/220_antonheart/17193(0,0)start.map",
        [17194] = "map/220_antonheart/17194(1,0)boss.map",
        [17400] = "map/221_hatchery1/17400(2,0)normal.map",
        [17401] = "map/222_hatchery2/17401(0,2)normal.map",
        [17402] = "map/223_hatchery3/17402(1,0)normal.map",
        [17403] = "map/224_hatchery4/17403(0,1)normal.map",
        [19100] = "map/218_energycut/19100(0,0)n.map",
        [19101] = "map/218_energycut/19101(1,0)n.map",
        [19102] = "map/218_energycut/19102(2,0)n.map",
        [19103] = "map/218_energycut/19103(3,0)n.map",
        [19104] = "map/218_energycut/19104(0,1)n.map",
        [19105] = "map/218_energycut/19105(1,1)n.map",
        [19106] = "map/218_energycut/19106(2,1)n.map",
        [19107] = "map/218_energycut/19107(3,1)n.map",
        [19109] = "map/218_energycut/19109(1,2)n.map",
        [19110] = "map/218_energycut/19110(2,2)n.map",
        [19112] = "map/218_energycut/19112(0,3)n.map",
        [19113] = "map/218_energycut/19113(1,3)n.map",
        [19114] = "map/218_energycut/19114(2,3)n.map",
        [19115] = "map/218_energycut/19115(3,3)n.map",
        [19116] = "map/218_energycut/19116(0,4)n.map",
        [19117] = "map/218_energycut/19117(1,4)n.map",
        [19118] = "map/218_energycut/19118(2,4)n.map",
        [19119] = "map/218_energycut/19119(3,4)n.map",
        [19120] = "map/218_energycut/19120(0,2)b.map",
        [19123] = "map/218_energycut/19123(3,2)s.map",
        [19200] = "map/219_blackvolcano/19200(0,0)b.map",
        [19201] = "map/219_blackvolcano/19201(1,0)n.map",
        [19202] = "map/219_blackvolcano/19202(2,0)n.map",
        [19203] = "map/219_blackvolcano/19203(3,0)n.map",
        [19204] = "map/219_blackvolcano/19204(4,0)n.map",
        [19205] = "map/219_blackvolcano/19205(5,0)n.map",
        [32200] = "map/210_close_blacksmoke/(0,0)32200_start.map",
        [32201] = "map/210_close_blacksmoke/(2,0)32201_normal.map",
        [32202] = "map/210_close_blacksmoke/(1,0)32202_normal.map",
        [32204] = "map/210_close_blacksmoke/(4,0)32204_boss.map",
        [32205] = "map/211_close_blacksmoke2/(0,0)32205_start.map",
        [32206] = "map/211_close_blacksmoke2/(2,0)32206_normal.map",
        [32207] = "map/211_close_blacksmoke2/(1,0)32207_normal.map",
        [32208] = "map/211_close_blacksmoke2/(3,0)32208_boss.map",
        [32220] = "map/212_strongleg_high/(0,0)32220_start.map",
        [32221] = "map/212_strongleg_high/(1,0)32221_normal.map",
        [32222] = "map/212_strongleg_high/(2,0)32222_boss.map",
        [32223] = "map/212_strongleg_high/(3,0)32223_start.map",
        [32230] = "map/213_strongleg_lower/(0,0)32230_normal.map",
        [32232] = "map/213_strongleg_lower/(2,0)32232_normal.map",
        [32233] = "map/213_strongleg_lower/(0,1)32233_normal.map",
        [32235] = "map/213_strongleg_lower/(0,2)32235_start.map",
        [32236] = "map/213_strongleg_lower/(2,2)32236_boss.map",
        [32240] = "map/214_strongleg_high2/(0,0)32240_start.map",
        [32241] = "map/214_strongleg_high2/(1,0)32241_normal.map",
        [32242] = "map/214_strongleg_high2/(2,0)32242_boss.map",
        [32243] = "map/214_strongleg_high2/(3,0)32243_start.map",
        [32250] = "map/215_strongleg_lower2/(0,0)32250_normal.map",
        [32252] = "map/215_strongleg_lower2/(2,0)32252_normal.map",
        [32253] = "map/215_strongleg_lower2/(0,1)32253_normal.map",
        [32255] = "map/215_strongleg_lower2/(0,2)32255_start.map",
        [32256] = "map/215_strongleg_lower2/(2,2)32256_boss.map",
        [32260] = "map/216_fleet_fire/(0,1)32260_start.map",
        [32261] = "map/216_fleet_fire/(0,0)32261_boss.map",
    }.ToImmutableDictionary();

    // 应用装配显式选用；主目录已有相同登记优先，冲突拒绝，不修改共享普通目录。
    internal static Func<int, string?> Bind(Func<int, string?> main)
    {
        foreach (var (id, path) in Paths)
            if (main(id) is { } existing && PvfArchive.NormalizePath(existing) != path)
                throw new InvalidDataException($"安徒恩MAP登记与主目录冲突：{id}");
        return id => main(id) ?? Paths.GetValueOrDefault(id);
    }
}
