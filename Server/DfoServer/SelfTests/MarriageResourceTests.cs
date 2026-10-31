using DfoServer.GameWorld.Marriage;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MarriageResourceTests
{
    public static void RunCore()
    {
        int checks = Count;
        var source = new Source();
        var catalog = source.Parse();
        That(catalog.Quests.SequenceEqual([20737, 20738]) && catalog.MinLevel == 30 &&
            catalog.Ceremonies[0] is { Gold: 500000, AvatarType: 0, LettersPerPersonRaw: 10 }, "婚礼资格与四列婚礼配置独立保留");
        That(catalog.Timing.SelectionSeconds.SequenceEqual([300, 600]) && catalog.Timing.NotifyEndSeconds == 60 &&
            catalog.Timing.ForceMove[1] == new MarriageForceMove(685, 360, 0), "计时保持原秒值，双方移动坐标不混成一条");
        That(catalog.MoneyGiftMin == 10000 && catalog.MoneyGiftMax == 400000000 && catalog.MoneyGiftChargeRaw == 50 &&
            catalog.GuestGiftTiers[0].GiftsByWeddingType[0] is [{ ItemId: 101, Count: 2 }] &&
            catalog.GuestGiftTiers[1].GiftsByWeddingType[0] is [{ ItemId: 102, Count: 5 }], "礼金上下限与有序上限档位保持独立");
        That(catalog.UpgradeRingTickets is [{ ItemId: 0, Count: 0 }, { ItemId: 201, Count: 1 }] &&
            catalog.Rings[1].Buffs is [{ Name: "physical defense", Value: 10 }, { Name: "move speed", Value: 20 }] &&
            catalog.WeddingAvatars[(0, 0)][9] == -1 && catalog.WeddingAvatars[(0, 0)][10] == -1,
            "婚戒空券哨兵、逐级增益及装扮缺槽哨兵均不补造");
        That(catalog.LovePoints.ItemPoints[701] == 520 && catalog.AlimonyRaw.SequenceEqual([500000, 500000]),
            "爱心道具数值与两种离婚费用原值保留");
        That(catalog.Trees[1].Rewards is [{ ItemId: 601, Weight: 40000, Count: 2 }, { ItemId: 601, Weight: 1000, Count: 5 }] &&
            catalog.Trees[1].TotalRewardWeight == 41000 && catalog.Trees[1].Steps[0].Animation == "tree_01.ani",
            "同一树奖励物品的不同权重与数量逐行保留，不合并概率或假定分母");
        That(catalog.Furniture[301].Placements is [{ PositionId: 0, FirstRaw: -5, FourthRaw: 1 }, { PositionId: 1, SecondRaw: -10 }] &&
            catalog.Furniture[301].TailRaw == 7 && catalog.RoomPositions[0].Y == -255 && catalog.RoomBuffs[1].Threshold == 40,
            "家具负偏移、未知尾值和场景位置原值保留，仅验证允许位置引用");
        That(catalog.GrowthCapsule is { MaxOwnCount: 10, MaxTakenCount: 10, Gage: 2, MaxGage: 10, CapsuleId: 801 },
            "成长胶囊持有、领取、刻度和物品独立读取");
        source.AddField(MarriageCatalog.WeddingResourcePath, "future metadata", true, "opaque value", 77);
        var extended = source.Parse();
        That(extended.RawResources[MarriageCatalog.WeddingResourcePath].Single(row => row.Name == "future metadata").Values is
            [{ Type: 6 }, { Type: 0, Value: 77 }], "未解释声明保留原始token而不擅自授予效果");

        Invalid(f => f.Set(MarriageCatalog.WeddingResourcePath, "minimum level", 0, new(0, -1)), "负最低等级拒绝");
        Invalid(f => f.Set(MarriageCatalog.WeddingResourcePath, "minimum level", 0, new(2, 30)), "整数外观相似的浮点token拒绝");
        Invalid(f => f.AddField(MarriageCatalog.WeddingResourcePath, "minimum level", true, 30), "必填单值重复拒绝");
        Invalid(f => f.RemoveScalar(MarriageCatalog.CapsuleResourcePath, "capsule id"), "必填字段缺失拒绝，不生成默认胶囊编号");
        Invalid(f => f.Set(MarriageCatalog.WeddingResourcePath, "ceremony", 1, new(0, -1)), "负婚礼金币拒绝");
        Invalid(f => f.Set(MarriageCatalog.WeddingResourcePath, "guest gift", 1, new(0, 9)), "来宾礼物引用未知婚礼类型拒绝");
        Invalid(f => f.Set(MarriageCatalog.WeddingResourcePath, "guest gift", 0, new(0, 400000001)), "来宾礼物阈值倒序拒绝");
        Invalid(f => f.Set(MarriageCatalog.WeddingResourcePath, "wedding ring buff", 1, new(0, 3)), "婚戒增益条数与真实token不符拒绝");
        Invalid(f => f.Set(MarriageCatalog.AvatarResourcePath, "avatar list", 9, new(0, 0)), "装扮无效零编号拒绝，只有原始负一为空槽");
        Invalid(f => f.Set(MarriageCatalog.TreeResourcePath, "seed", 1, new(0, 9)), "种子引用未知树编号拒绝");
        Invalid(f => f.Set(MarriageCatalog.TreeResourcePath, "reward", 1, new(0, 0)), "零奖励权重拒绝");
        Invalid(f => f.Set(MarriageCatalog.RoomResourcePath, "coupleroom item", 1, new(0, int.MaxValue)), "家具数量溢出形状拒绝");
        Invalid(f => f.Set(MarriageCatalog.RoomResourcePath, "coupleroom item", 2, new(0, 9)), "家具未知位置拒绝");
        Invalid(f => f.Set(MarriageCatalog.RoomResourcePath, "coupleroom item", 7, new(0, 0)), "同件家具重复允许位置拒绝");
        Invalid(f => f.Set(MarriageCatalog.CapsuleResourcePath, "gage", 0, new(0, 11)), "单次刻度超过胶囊上限拒绝");
        Invalid(f => f.Files[MarriageCatalog.AvatarResourcePath].RemoveAt(f.Files[MarriageCatalog.AvatarResourcePath].Count - 1),
            "婚礼装扮外块未闭合拒绝");
        Console.WriteLine($"PASS {Count - checks} marriage resource parsing assertions");
    }

    public static void Run(PvfArchive pvf)
    {
        int checks = Count;
        var catalog = MarriageCatalog.Load(pvf);
        That(catalog.Quests.SequenceEqual([20737, 20738]) && catalog.MinLevel == 30, "真实婚礼任务与最低等级");
        That(catalog.Ceremonies.Count == 3 && catalog.Ceremonies[0] is { Gold: 500000, AvatarType: 0, LettersPerPersonRaw: 10 } &&
            catalog.Ceremonies[1] is { Gold: 2000000, AvatarType: 1, LettersPerPersonRaw: 20 } &&
            catalog.Ceremonies[2] is { Gold: 0, AvatarType: 2, LettersPerPersonRaw: 30 } &&
            catalog.ChargedWeddingTickets[2] == new MarriageItemCount(2683401, 1), "真实三档婚礼金币与付费券分别读取");
        That(catalog.Timing.SelectionSeconds.SequenceEqual([300, 600, 900]) && catalog.Timing.NotifyEndSeconds == 60 &&
            catalog.Timing.OnceHowManyDays == 1 && catalog.Timing.DaysAfterDivorce == 1, "真实礼堂计时与离婚日参数");
        That(catalog.GuestGiftTiers.Select(row => row.UpperBound).SequenceEqual([99999, 399999, 999999, 4999999, 19999999, 400000000]) &&
            catalog.GuestGiftTiers.All(row => row.GiftsByWeddingType.Count == 3) &&
            catalog.GuestGiftTiers[^1].GiftsByWeddingType[2].Contains(new(10002919, 1)), "真实六档来宾奖励及三种婚礼类型完整保留");
        That(catalog.WeddingGifts[0] == new MarriageItemCount(10002629, 1) && catalog.WeddingGifts[2] == new MarriageItemCount(10002631, 1) &&
            catalog.CeremonyGifts.SequenceEqual([new MarriageItemCount(10002627, 1), new MarriageItemCount(10002628, 1)]) &&
            catalog.InvitationGiftRaw.IsEmpty && catalog.Letters.Length == 8, "真实婚礼礼物、双方礼物、空邀请礼物和信件分别保留");
        That(catalog.Rings.Count == 6 && catalog.UpgradeRingTickets[0] == new MarriageItemCount(0, 0) &&
            catalog.UpgradeRingTickets[5] == new MarriageItemCount(10002615, 1) && catalog.Rings[5].Buffs.Length == 10 &&
            catalog.Rings[5].Buffs.Contains(new("cast speed", 20)), "真实六档戒指券及最终十项增益完整保留");
        That(catalog.WeddingAvatars.Count == 42 && catalog.WeddingAvatars.Keys.Select(key => key.Job).Distinct().Order()
            .SequenceEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15]) &&
            catalog.WeddingAvatars.Values.All(row => row.Length == 11) && catalog.WeddingAvatars[(0, 0)][9] == -1,
            "真实十四职业各三种婚礼装扮，不为未声明职业补表");
        That(catalog.LovePoints is { DailyRaw: 1, Max: 999 } && catalog.LovePoints.ItemPoints[490703877] == 520 &&
            catalog.AlimonyRaw.SequenceEqual([500000, 500000]), "真实爱心额度、特殊道具与离婚费用");
        That(catalog.Seeds.Count == 3 && catalog.Seeds[10002766] == 1 && catalog.Trees.Count == 3 &&
            catalog.Trees.Values.All(row => row.ExpireDays == 30 && row.DayWaterCount == 2 && row.Rewards.Length == 6 &&
                row.TotalRewardWeight == 100000 && row.Steps.Select(step => step.GrowPoint).SequenceEqual([10, 20, 30])),
            "真实三种夫妻树及各六行权重奖励保持原始顺序");
        That(catalog.RoomPositions.Count == 28 && catalog.Furniture.Count > 0 &&
            catalog.Furniture[10002802].Placements.Select(row => row.PositionId).SequenceEqual([25, 26, 27]) &&
            catalog.Furniture[10002711].Placements[0].SecondRaw == -100, "真实家具可摆位置和负偏移");
        That(catalog.RoomBuffs.Select(row => row.Threshold).SequenceEqual([10, 40, 100, 200, 500, 800, 1000]) &&
            catalog.RoomBuffs[^1].Buffs.Contains(new("separate attack", 20)), "真实七档房间增益及独立攻击保留");
        That(catalog.GrowthCapsule is { MaxOwnCount: 10, MaxTakenCount: 10, Gage: 2, MaxGage: 10, CapsuleId: 10002910 },
            "真实夫妻成长胶囊配置");
        Console.WriteLine($"PASS {Count - checks} marriage PVF resource assertions");
    }

    static void Invalid(Action<Source> mutate, string message)
    {
        var source = new Source();
        mutate(source);
        Reject<InvalidDataException>(() => source.Parse(), message);
    }

    internal sealed class Source
    {
        readonly List<string> strings = [];
        public Dictionary<string, List<PvfToken>> Files { get; } = new(StringComparer.Ordinal);

        public Source()
        {
            const string wedding = MarriageCatalog.WeddingResourcePath;
            AddField(wedding, "quest", true, 20737, 20738);
            AddField(wedding, "ceremony", true, 0, 500000, 0, 10);
            AddField(wedding, "wedding gift", true, 0, 100, 1);
            AddField(wedding, "minute selection", true, 300, 600);
            AddField(wedding, "noti end", true, 60);
            AddField(wedding, "force move", true, 670, 330, 0, 685, 360, 0);
            AddField(wedding, "minimum level", true, 30);
            AddField(wedding, "money gift limit", true, 10000, 400000000);
            AddField(wedding, "money gift charge", true, 50);
            AddField(wedding, "letter", true, 1078, 1192, 1078, 1193);
            AddField(wedding, "invitation gift", true);
            AddField(wedding, "guest gift", true, 99999, 0, 101, 2);
            AddField(wedding, "guest gift", true, 400000000, 0, 102, 5);
            AddField(wedding, "alimony", true, 500000, 500000);
            AddField(wedding, "once how many days", true, 1);
            AddField(wedding, "days after divorce", true, 1);
            AddField(wedding, "love point", true, 1);
            AddField(wedding, "max love point", true, 999);
            AddField(wedding, "charged wedding ticket", true, 0, 103, 1);
            AddField(wedding, "upgrade ring ticket", true, 0, 0, 201, 1);
            AddField(wedding, "love point item", true, 701, 520);
            AddField(wedding, "ceremony gift", true, 104, 1, 105, 1);
            AddField(wedding, "cerashop restrict item", true, 102317);
            AddField(wedding, "wedding ring nametag action", true, 0, "ring_01.act", 1, "ring_02.act");
            AddField(wedding, "wedding ring buff", true, 0, 1, "physical defense", 8, 1, 2, "physical defense", 10, "move speed", 20);

            const string avatar = MarriageCatalog.AvatarResourcePath;
            Tag(avatar, "[wedding avatar]");
            AddField(avatar, "job wedding", false, 0, 0);
            AddField(avatar, "avatar list", true, 11, 12, 13, 14, 15, 16, 17, 18, 19, -1, -1);
            Tag(avatar, "[/wedding avatar]");

            const string tree = MarriageCatalog.TreeResourcePath;
            AddField(tree, "seed", true, 401, 1);
            AddField(tree, "reward letter", true, 1078, 1192);
            Tag(tree, "[tree]");
            AddField(tree, "index", false, 1);
            AddField(tree, "name", false, "合成树");
            AddField(tree, "expire day", false, 30);
            AddField(tree, "day water count", false, 2);
            AddField(tree, "grow point", true, 501, 10);
            AddField(tree, "step", true, 10, "tree_01.ani", 20, "tree_02.ani");
            AddField(tree, "reward", true, 601, 40000, 2, 601, 1000, 5);
            Tag(tree, "[/tree]");

            const string room = MarriageCatalog.RoomResourcePath;
            AddField(room, "position list", true, 0, 273, -255, -250, 1, 560, -250, -200);
            Tag(room, "[coupleroom item list]");
            AddField(room, "coupleroom item", true, 301, 2, 0, -5, 10, 0, 1, 1, 5, -10, 0, 0, 7, "furniture.act");
            AddField(room, "coupleroom item", true, 302, 1, 0, 0, 0, 0, 0, 0, "floor.act");
            Tag(room, "[/coupleroom item list]");
            AddField(room, "couple room buff", true, 10, 1, "hp max", 100, 40, 2, "hp max", 100, "mp max", 100);

            const string capsule = MarriageCatalog.CapsuleResourcePath;
            AddField(capsule, "max own count", false, 10);
            AddField(capsule, "max taken count", false, 10);
            AddField(capsule, "gage", false, 2);
            AddField(capsule, "max gage", false, 10);
            AddField(capsule, "capsule id", false, 801);
        }

        public MarriageCatalog Parse() => MarriageCatalog.Parse(path => Files[path], id => strings[id]);
        public void Set(string path, string field, int offset, PvfToken token) => Files[path][Find(path, field) + 1 + offset] = token;
        public void RemoveScalar(string path, string field) => Files[path].RemoveRange(Find(path, field), 2);
        int Find(string path, string field) => Files[path].FindIndex(token => token.Type == 3 && strings[token.Value] == "[" + field + "]");
        public void AddField(string path, string name, bool block, params object[] values)
        {
            Tag(path, "[" + name + "]");
            foreach (var value in values)
                Files[path].Add(value is int number ? new(0, number) : String((string)value, 6));
            if (block) Tag(path, "[/" + name + "]");
        }
        void Tag(string path, string value)
        {
            if (!Files.TryGetValue(path, out var tokens)) Files.Add(path, tokens = []);
            tokens.Add(String(value, 3));
        }
        PvfToken String(string value, byte type)
        {
            strings.Add(value);
            return new(type, strings.Count - 1);
        }
    }
}
