using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class AvatarAbilityChangeTests
{
    public static void RunCore()
    {
        var captured = AvatarAbilityChangeRequests.Read(Convert.FromHexString("7400649828002A006DA6000000"));
        That(captured == new AvatarAbilityChangeRequest(116, 2660452, 42, 42605, 0), "#801稀有装扮实际13字节请求逐字段还原");
        That(AvatarAbilityChangeRequests.Read(Convert.FromHexString("7300E29928002400E2B5EE1700")) ==
            new AvatarAbilityChangeRequest(115, 2660834, 36, 401520098, 0), "#801高级装扮实际请求保留完整双物品编号");
        foreach (byte[] invalid in new[] { new byte[12], new byte[14], Convert.FromHexString("FFFF649828002A006DA6000000"),
            Convert.FromHexString("740064982800FFFF6DA6000000"), new byte[13] })
            Reject<InvalidDataException>(() => AvatarAbilityChangeRequests.Read(invalid), "459拒绝截断、附加字段、负槽或无效编号");
        Bytes(AvatarAbilityChangePackets.Success(42, 3), [1, 42, 0, 3], "459成功只携带目标装扮槽与属性字节");
        Bytes(AvatarAbilityChangePackets.Failure(), [0, 0, 0], "459失败保留公共通用失败头");
        // 使用原始token验证字符串/整数类型、重复声明和白名单闭合，避免文本展示丢失信息。
        static string Resolve(int key) => key switch
        {
            1 => "[stackable type]", 2 => "[avatar select ability change]", 3 => "[etc]", 4 => "[all]",
            5 => "[swordman]", 6 => "[usable avatar index]", 7 => "[/usable avatar index]",
            8 => "[use only with period avatar]", 9 => "[avatar select ability except]", _ => throw new KeyNotFoundException()
        };
        static AvatarAbilityChangeDefinition? Script(PvfToken[] arguments, params PvfToken[] tail) =>
            AvatarAbilityChangeDefinition.Parse(new PvfTokenDocument([new(3, 1), new(6, 3), new(0, 0),
                new(3, 2), .. arguments, .. tail], Resolve), Resolve);
        PvfToken[] rareArguments = [new(6, 4), new(6, 4), new(0, 3), new(0, 0)];
        var rare = Script(rareArguments)!;
        var ordinary = Script([new(6, 4), new(6, 4), new(0, 2), new(0, 1)])!;
        var targetTemplate = new ItemTemplate(200, ItemKind.Avatar, 2, 1, 0, 1, 0, 2, 3, "[trade]", ushort.MaxValue, 0, "稀有装扮");
        That(rare.Allows(targetTemplate) && !ordinary.Allows(targetTemplate) &&
            ordinary.Allows(targetTemplate with { Grade = 1 }) && ordinary.Allows(targetTemplate with { Grade = 2 }),
            "资格按装扮grade3/2/1判定，不能把稀有装扮的rarity2误当高级");
        var whitelist = Script(rareArguments, new(3, 6), new(0, 200), new(0, 201), new(3, 7))!;
        That(whitelist.Allows(targetTemplate) && !whitelist.Allows(targetTemplate with { Id = 202 }),
            "专用调整器的真实目标白名单仍生效，不能只按相同grade放行");
        foreach (PvfToken[] arguments in new PvfToken[][] { rareArguments[..^1], [.. rareArguments, new(0, 1)],
            [new(6, 4), new(6, 4), new(0, 0), new(0, 0)], [new(6, 4), new(6, 4), new(0, 3), new(0, 1)],
            [new(6, 5), new(6, 4), new(0, 3), new(0, 0)], [new(6, 4), new(6, 4), new(6, 3), new(0, 0)] })
            That(Script(arguments) is null, "未确认模式、错误token类型或畸形资格不猜测放行");
        foreach (PvfToken[] tail in new PvfToken[][] { [new(3, 2), .. rareArguments], [new(3, 9)],
            [new(3, 6), new(0, 200)], [new(3, 6), new(0, 200), new(0, 200), new(3, 7)],
            [new(3, 8), new(0, 1)], [new(3, 8), new(3, 8)] })
            That(Script(rareArguments, tail) is null, "重复声明、未知例外、未闭合白名单和畸形限期标志拒绝");
        That(Script(rareArguments, new PvfToken(3, 8)) is { OnlyFinite: true }, "空限期标志按PVF原始声明识别");

        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var toolTemplate = new ItemTemplate(100, ItemKind.Consumable, 0, 1, 0, 100, 0, 2, 1, "[trade]", ushort.MaxValue, 0, "调整器")
            { AvatarAbilityChange = rare };
        items.PrefillTemplates([new(100, toolTemplate), new(200, targetTemplate)]);
        var avatars = new AvatarPackageCatalog(_ => "[avatar select ability]\n0 `[HP MAX]` `+` 10 1 `[MP MAX]` `+` 10\n[/avatar select ability]",
            new Dictionary<int, string> { [200] = "avatar.equ" }, new Dictionary<int, string>());
        var clock = new ConsumableUseSessionTests.FakeClock();
        var service = new InventoryAvatarAbilityChangeService(items, avatars, clock);
        int expires = checked((int)clock.GetUtcNow().ToUnixTimeSeconds() + 10);
        var source = new InventoryItem(1, new(InventoryListType.Main, 65), new(100, ItemKind.Consumable, 2) { SortLockFlag = 1 });
        var target = new InventoryItem(2, new(InventoryListType.Avatar, 42), new(200, ItemKind.Avatar, 2)
        {
            Attr = 7, Durability = 1, SortLockFlag = 1, EnchantCardId = 5001, Extension119 = 3
        }) { Avatar = new() { Color1 = 7, ClearAvatarId = 300, Socket0 = AvatarSocket.Empty(2), ExpireTime = expires } };
        var role = new Character(1, 0, "AvatarOpt", 0, new(1, 0, 10, 10), null) { Level = 95, Inventory = new([source, target], Gold: 123) };
        var request = new AvatarAbilityChangeRequest(65, 100, 42, 200, 0);
        var result = service.Apply(role, request, out var rejection);
        That(result.Accepted && rejection is null && result.Plan!.After.Find(target.Position) == target with
            { Core = target.Core with { Durability = 0 } } && result.Plan.After.Find(source.Position) == source with
            { Core = source.Core with { Value = 1 } } && result.Plan.After.Gold == 123 && role.Inventory.Find(target.Position) == target,
            "459只消费一件真实源并改写装扮属性，完整UID、核心、详情、期限与输入快照保留");
        That(result.Plan!.CharacterPrecondition is { } guard && guard.Matches(role, expires - 1) && !guard.Matches(role, expires),
            "装扮期限在规划后到期时拒绝旧计划");
        var permanentTarget = target with { Avatar = target.Avatar! with { ExpireTime = 0 } };
        var finiteSource = source with { Core = source.Core with { ExpireTime = expires } };
        var sourceRole = role with { Inventory = role.Inventory with { Items = [finiteSource, permanentTarget] } };
        That(service.Apply(sourceRole, request, out _).Plan!.CharacterPrecondition!.QualificationExpiresAt == expires,
            "永久装扮也保留有限调整器的截止");
        var finiteRule = rare with { OnlyFinite = true };
        items.PrefillTemplates([new(100, toolTemplate with { AvatarAbilityChange = finiteRule })]);
        That(service.Apply(role, request, out _).Accepted &&
            !service.Apply(role with { Inventory = role.Inventory with { Items = [source, permanentTarget] } }, request, out _).Accepted,
            "仅限有期限装扮的调整器不接受永久目标");
        items.PrefillTemplates([new(100, toolTemplate)]);
        var identical = service.Apply(role, request with { Ability = 1 }, out _);
        That(identical.Accepted && !identical.Plan!.ChangedSlots.Contains(target.Position) &&
            identical.Plan.CharacterPrecondition!.QualificationExpiresAt == expires,
            "相同属性仍按原生请求消费一件，未产生目标差量也复验其期限");
        foreach (var invalid in new[] { request with { Ability = 255 }, request with { SourceId = 101 }, request with { AvatarId = 201 },
            request with { AvatarSlot = 105 }, request with { SourceSlot = 0 } })
            That(service.Apply(role, invalid, out _) is { Accepted: false, Plan: null }, "非法属性、过期回显及错误槽不扣源");
        foreach (var invalid in new[] { source with { Core = source.Core with { EquipmentLockId = 1 } },
            source with { Core = source.Core with { ExpireTime = expires - 10 } }, source with { Uid = 0 } })
            That(!service.Apply(role with { Inventory = role.Inventory with { Items = [invalid, target] } }, request, out _).Accepted,
                "源锁定、精确到期和临时UID拒绝");
        foreach (var invalid in new[] { target with { Core = target.Core with { EquipmentLockId = 1 } },
            target with { Avatar = target.Avatar! with { ExpireTime = expires - 10 } }, target with { Avatar = null } })
            That(!service.Apply(role with { Inventory = role.Inventory with { Items = [source, invalid] } }, request, out _).Accepted,
                "装扮锁定、精确到期及缺详情拒绝");
        Console.WriteLine("PASS 装扮属性转换真实请求、资格、完整实例与提交截止断言");
    }
}
