using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class PcRoomQualificationTests
{
    public static void RunCore()
    {
        int checks = Count;
        var clock = new QualificationClock(DateTimeOffset.FromUnixTimeSeconds(1000));
        var character = new Character(1, 0, "PcRoomQualification", 0, new(1, 0, 0, 0), null);
        bool sourceQualified = true, pcRoom = true;
        var source = new InventoryCharacterPrecondition(character, 1100, clock, requirePosition: true,
            currentQualification: () => sourceQualified);
        var combined = source.WithQualification(character, 1200, () => pcRoom);
        That(combined.QualificationExpiresAt == 1100 && combined.Matches(character),
            "追加网吧资格保留原来更早的物品/业务截止");
        That(!combined.MatchesIdentity(character with { Position = new(1, 0, 1, 0) }) &&
            !combined.MatchesIdentity(character with { Job = 1 }),
            "追加资格不丢原位置、职业等角色前提");
        pcRoom = false;
        That(combined.IsExpired() && !combined.Matches(character),
            "规划后关闭网吧身份，即使时间未到也拒绝提交");
        pcRoom = true; sourceQualified = false;
        That(combined.IsExpired(), "原业务动态资格失效仍拒绝，不被新增网吧条件覆盖");
        sourceQualified = true;
        clock.Now = DateTimeOffset.FromUnixTimeSeconds(1100);
        That(combined.IsExpired(), "追加资格保留原时钟，到期等号拒绝");
        var paid = new InventoryCharacterPrecondition(character, clock: clock)
            .WithQualification(character, 1050, () => pcRoom);
        That(paid.QualificationExpiresAt == 1050 && !paid.IsExpired(1049) && paid.IsExpired(1050),
            "原来没有期限时，付费网吧截止成为真实提交期限");
        var free = new InventoryCharacterPrecondition(character, clock: clock)
            .WithQualification(character, null, () => pcRoom);
        That(!free.IsExpired(), "免费身份没有伪造账号购买期限");
        pcRoom = false;
        That(free.IsExpired(), "无到期时间的免费身份也在提交检查中复核开关");
        Reject<InvalidOperationException>(() => source.WithQualification(character with { Job = 1 }, 1200, () => true),
            "不能通过追加资格把原规划重新绑定到另一角色状态");
        That(!new InventoryCharacterPrecondition(character).IsExpired(),
            "未附加网吧资格的原有业务保持原提交条件");
        clock.Now = DateTimeOffset.FromUnixTimeMilliseconds(1100123);
        var temporary = new InventoryCharacterPrecondition(character, clock: clock,
            qualificationExpiresAtMilliseconds: 1100124).WithQualification(character, 1200, () => true);
        That(!temporary.IsExpired(), "追加网吧资格保留临时效果最后1毫秒，不提前取整");
        clock.Now = clock.Now.AddMilliseconds(1);
        That(temporary.IsExpired(), "追加网吧资格不能丢失主线临时效果的毫秒截止");
        Console.WriteLine($"PASS {Count - checks} pc-room qualification core checks");
    }

    public static void RunResources(ItemCatalog items)
    {
        int checks = Count;
        foreach (int id in new[] { 490007905, 490007908, 490007909, 490010101 })
            That(items.Get(id).RequiresPcRoom, "网吧礼盒和实际两种药剂保留资源声明的网吧环境门槛");
        Console.WriteLine($"PASS {Count - checks} pc-room qualification resource checks");
    }

    sealed class QualificationClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
