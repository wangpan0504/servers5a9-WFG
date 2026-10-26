using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class AvatarSocketTests
{
    public static void RunCore()
    {
        int checks = Count;
        byte[] body = [0, 0, 100, 0, 0, 0, 74, 0];
        var request = AvatarSocketRequests.Read(body);
        That(request == new AvatarSocketRequest(0, 100, 74), "C206按8字节读取时装槽、模板、材料槽");
        foreach (byte[] bad in new byte[][] { [], body[..^1], [.. body, 0], [255, 255, 100, 0, 0, 0, 74, 0],
            [0, 0, 0, 0, 0, 0, 74, 0], [0, 0, 100, 0, 0, 0, 255, 255] })
            Reject<InvalidDataException>(() => AvatarSocketRequests.Read(bad), "C206拒绝截断、附加及非法实例字段");
        That(AvatarSocketPackets.Success(260, 300).SequenceEqual(new byte[] { 1, 4, 1, 44, 1 }) &&
            AvatarSocketPackets.Failure().SequenceEqual(new byte[] { 0, 4, 0 }), "ACK206成功仅两个u16，失败含公共u16错误码");

        var sourceTemplate = Template(7421, ItemKind.Consumable, 3);
        var template = Template(100, ItemKind.Avatar, 3) with { Rarity = 0, EquipmentSlot = 3 };
        var raw = new RecoveryConsumableFile(7421, "opener.stk",
            new(7421, "opener.stk", new("[waste]", [0]), 10, null, null, 1, null, [])
            { HasActionType = true, ActionDeclaration = new("[add avatar socket]", [2]) },
            null, null, null, null, ["grade", "action type"], []);
        var opener = AvatarSocketOpenerDefinition.Parse(raw, sourceTemplate)!;
        That(opener is { Mode: 2 } && opener.Allows(template) && !opener.Allows(template with { Grade = 2 }) &&
            !opener.Allows(template with { EquipmentSlot = 8 }) && (opener with { Mode = 3 }).Allows(template with { EquipmentSlot = 8 }),
            "grade相同而非rarity相同；参数2排除皮肤，参数3允许皮肤及其他同grade装扮");
        That((opener with { Mode = 0 }).Allows(template) && !(opener with { Mode = 1 }).Allows(template) &&
            !(opener with { Mode = 0 }).Allows(template with { CloneAvatar = true }) &&
            (opener with { Mode = 1 }).Allows(template with { CloneAvatar = true }), "参数0/1分别限制普通及克隆装扮");
        var limited = AvatarSocketOpenerDefinition.Parse(raw with { DeclaredFields = ["add socket limited avatar"] }, sourceTemplate, [100])!;
        That(limited.Allows(template) && !limited.Allows(template with { Id = 101 }), "白名单按模板限制，不能只检查grade");
        foreach (var bad in new[]
        {
            raw with { Diagnostics = ["duplicate"] }, raw with { DeclaredFields = ["total usable count"] },
            raw with { DeclaredFields = ["add socket limited avatar"] },
            raw with { Stackable = raw.Stackable with { ActionDeclaration = new("[add avatar socket]", [4]) } },
            raw with { Stackable = raw.Stackable with { ActionDeclaration = new("[add avatar socket]", [2, 3]) } },
            raw with { Stackable = raw.Stackable with { ActionDeclaration = new("[upgrade inventory]", []) } },
            raw with { Stackable = raw.Stackable with { HasUseActionPacket = true, DeclaredUseActionPacket = 1 } }
        })
            That(AvatarSocketOpenerDefinition.Parse(bad, sourceTemplate) is null, "畸形、未知参数、无效白名单及其他动作拒绝");

        var avatar = AvatarPackageCatalog.ParseAvatar("[avatar type select]\n0 0 0 0 0 0 0\n0 0 0 0 0 0 3 `[S socket]` `[C socket]` `[C socket]`\n[/avatar type select]");
        var source = new InventoryItem(11, request.Material, new(7421, ItemKind.Consumable, 2) { SortLockFlag = 1, ExpireTime = 1200 });
        var target = new InventoryItem(12, request.Target, new(100, ItemKind.Avatar, 12) { Attr = 7, Durability = 3, SortLockFlag = 1 })
        { Avatar = new() { ExpireTime = 1100, ClearAvatarId = 1234, Color1 = 3, Color2 = 4 } };
        var character = new Character(1, 0, "Socket", 0, new(1, 0, 10, 10), null)
            { Level = 95, Inventory = new([source, target], Gold: 1234) };
        var result = Plan(character);
        That(result.Accepted && result.Plan!.After.Find(request.Material) == source with { Core = source.Core with { Value = 1 } } &&
            result.Plan.After.Find(request.Target) == target with { Avatar = target.Avatar with
            { Socket0 = AvatarSocket.Empty(16), Socket1 = AvatarSocket.Empty(4), Socket2 = AvatarSocket.Empty(4) } } &&
            result.Plan.After.Gold == 1234 && character.Inventory.Find(request.Target) == target,
            "按完整资源开三孔且只扣一个源，保留完整UID、核心、染色、克隆外观、期限和金币，不修改原快照");
        That(result.Plan!.CharacterPrecondition!.Matches(character, 1099) &&
            !result.Plan.CharacterPrecondition.Matches(character, 1100) &&
            !result.Plan.CharacterPrecondition.Matches(character with { Revision = 1 }, 1000), "提交复验最早期限及revision");
        foreach (var invalid in new[]
        {
            Role(source with { Uid = 0 }, target), Role(source, target with { Uid = 0 }),
            Role(source with { Core = source.Core with { EquipmentLockId = 1 } }, target),
            Role(source, target with { Core = target.Core with { EquipmentLockId = 1 } }),
            Role(source with { Core = source.Core with { ExpireTime = 1000 } }, target),
            Role(source, target with { Avatar = target.Avatar with { ExpireTime = 1000 } }),
            Role(source, target with { Avatar = null }), Role(source, target with { Position = new(InventoryListType.Equipment, 3) }),
            Role(source, target with { Avatar = target.Avatar with { Socket4 = AvatarSocket.Empty(1) } }),
            Role(source, target with { Avatar = target.Avatar with { Socket0 = new(0, 200) } }),
            character with { Inventory = result.Plan.After }, character with { Level = 0 }
        })
            That(Plan(invalid) is { Accepted: false, Plan: null }, "锁定、期限、无详情、穿戴、已有孔或徽章及重复请求不消费源");
        That(!InventoryAvatarSocketService.Plan(character, request with { TargetItemId = 101 }, opener, template, avatar, out _, 1000).Accepted &&
            !InventoryAvatarSocketService.Plan(character, request, null, template, avatar, out _, 1000).Accepted &&
            !InventoryAvatarSocketService.Plan(character, request, opener, template, avatar with { Types = [] }, out _, 1000).Accepted &&
            !InventoryAvatarSocketService.Plan(character, request, opener, template, avatar with
            { Types = [new(0, default), new(0, new(1, 1, 0, 0, 0)), new(0, new(2, 2, 0, 0, 0))] }, out _, 1000).Accepted,
            "错误模板、非开孔源、无孔声明及冲突孔型均拒绝");
        Console.WriteLine($"PASS {Count - checks} 时装开孔解析、资格及完整实例断言");

        Character Role(InventoryItem from, InventoryItem to) => character with { Inventory = character.Inventory with { Items = [from, to] } };
        InventoryResult Plan(Character role) => InventoryAvatarSocketService.Plan(role, request, opener, template, avatar, out _, 1000);
    }

    public static void RunResources(ItemCatalog items, AvatarPackageCatalog avatars)
    {
        That(items.AvatarSocketOpener(7421) is { Item.Grade: 3, Mode: 2 } rare && rare.Allows(items.Get(101550535)) &&
            items.AvatarSocketOpener(10301316) is { Item.Grade: 1, Mode: 3 } skin && skin.Allows(items.Get(415580044)) &&
            avatars.GetAvatar(101550535).Types.Any(type => type.Sockets == new AvatarSocketTypes(1, 1, 0, 0, 0)) &&
            avatars.GetAvatar(415580044).Types.Any(type => type.Sockets == new AvatarSocketTypes(239, 239, 0, 0, 0)),
            "真实7421、10301316分别匹配稀有帽子双红孔及高级皮肤双多色孔");
        foreach (int id in new[] { 10014841, 10093555 })
        {
            var limited = items.AvatarSocketOpener(id);
            That(limited is { AllowedTargets.Count: > 0 } && !limited.Allows(items.Get(101550535)), "真实限定开孔器保留模板白名单 " + id);
        }
    }

    static ItemTemplate Template(int id, ItemKind kind, int grade) => new(id, kind, 0, 1, 0,
        kind == ItemKind.Avatar ? 1 : 10, 0, 2, grade, "[free]", ushort.MaxValue, 0, "socket");
}
