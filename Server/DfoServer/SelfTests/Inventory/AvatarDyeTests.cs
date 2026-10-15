using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class AvatarDyeTests
{
    public static void RunCore()
    {
        int checks = Count;
        var request = AvatarDyeRequests.Read([74, 0, 0, 0]);
        That(request is { SourceSlot: 74, AvatarSlot: 0 } && request.Source == new InventorySlot(InventoryListType.Main, 74) &&
            request.Target == new InventorySlot(InventoryListType.Avatar, 0), "实际CMD498按两个i16解释Main74和Avatar0");
        foreach (byte[] invalid in new byte[][] { [], [74, 0, 0], [74, 0, 0, 0, 0] })
            Reject<InvalidDataException>(() => AvatarDyeRequests.Read(invalid), "漂染不接受截断或附加正文");
        That(Convert.ToHexString(AvatarDyePackets.Success(0, new() { Color1 = 1 })) == "0100000400000001000000" &&
            AvatarDyePackets.Failure().SequenceEqual(new byte[] { 0, 0, 0 }), "498成功是槽位加RAW4，失败是公共u16通用错误");

        const string colorMap = "[avatar color map]\n0 0 255 0 ` `\n1 252 159 172 `color`\n[/avatar color map]";
        var files = new Dictionary<string, string>
        {
            ["stackable/dye.stk"] = Dye("1 1000"), ["stackable/wash.stk"] = Dye("0 1000"),
            ["stackable/unknown.stk"] = Dye("99 1000"), ["stackable/random.stk"] = Dye("1 1000").Replace("[dye type]\n1", "[dye type]\n2"),
            ["stackable/multi.stk"] = Dye("1 500 0 500"), ["stackable/bad.stk"] = Dye("1suffix 1000"),
            ["equipment/yes.equ"] = "[enable dye]\n1 0", ["equipment/no.equ"] = "[enable dye]\n0 1"
        };
        var stackable = new Dictionary<int, string> { [100] = "dye.stk", [101] = "wash.stk", [102] = "unknown.stk",
            [103] = "random.stk", [104] = "multi.stk", [105] = "bad.stk" };
        var equipment = new Dictionary<int, string> { [200] = "yes.equ", [201] = "no.equ" };
        var dyes = new AvatarDyeCatalog(path => files[path], equipment, stackable, colorMap);
        That(dyes.Get(100) is { Color: 1 } && dyes.Get(101) is { Color: 0 } && dyes.CanDye(200) && !dyes.CanDye(201),
            "固定染色与洗色0来自当前颜色表，enable dye只按第一部位启用");
        foreach (int id in new[] { 102, 103, 104, 105, 999 })
            That(dyes.Get(id) is null, "未知颜色、类型、多行和畸形声明不冒充固定染色 " + id);
        That(new AvatarDyeCatalog(path => files[path], equipment, stackable, null).Get(100) is null,
            "缺少可选颜色表时只拒绝漂染，不捏造颜色范围");
        Reject<InvalidDataException>(() => new AvatarDyeCatalog(path => files[path], equipment, stackable,
            colorMap.Replace("1 252 159 172", "0 252 159 172")), "重复颜色索引拒绝");

        var items = new ItemCatalog(null!, equipment, stackable, 0);
        var sourceTemplate = new ItemTemplate(100, ItemKind.Consumable, 0, 1, 0, 1000, 0, 1, 1, "[account]", ushort.MaxValue, 0, "dye");
        var targetTemplate = new ItemTemplate(200, ItemKind.Avatar, 1, 30, 0, 1, 0, 1, 2, "[trade]", 8, 0, "hair");
        items.PrefillTemplates(new Dictionary<int, ItemTemplate> { [100] = sourceTemplate, [101] = sourceTemplate with { Id = 101 },
            [200] = targetTemplate, [201] = targetTemplate with { Id = 201 } });
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var source = new InventoryItem(11, request.Source, new(100, ItemKind.Consumable, 2) { SortLockFlag = 1, ExpireTime = int.MaxValue });
        var target = new InventoryItem(12, request.Target, new(200, ItemKind.Avatar, 12) { Durability = 4, Attr = 3, SortLockFlag = 1 })
        { Avatar = new() { ExpireTime = int.MaxValue - 10, Socket0 = AvatarSocket.Empty(1), Socket1 = AvatarSocket.Empty(2) } };
        var character = new Character(1, 0, "Dye", 0, new(1, 0, 10, 10), null)
            { Job = 3, Level = 16, Inventory = new([source, target], Gold: 1234) };
        var service = new InventoryAvatarDyeService(items, dyes);
        var result = service.Apply(character, request, out var reason, now);
        That(result.Accepted && reason is null && result.Plan!.After.Find(request.Source) == source with { Core = source.Core with { Value = 1 } } &&
            result.Plan.After.Find(request.Target) == target with { Avatar = target.Avatar with { Color1 = 1 } } &&
            result.Plan.After.Gold == 1234 && character.Inventory.Find(request.Target) == target,
            "纯计划只扣一瓶及改颜色，保留UID、核心属性、排序锁、孔位、期限、钱包，Lv16可染尚不能穿戴的Lv30时装");
        result.Plan!.After.Validate();
        That(result.Plan.CharacterPrecondition!.Matches(character, now.ToUnixTimeSeconds()) &&
            !result.Plan.CharacterPrecondition.Matches(character, target.ExpireTime) &&
            !result.Plan.CharacterPrecondition.Matches(character with { Revision = 1 }, now.ToUnixTimeSeconds()),
            "提交前复验较早的实例期限及角色revision");
        var colored = character with { Inventory = result.Plan.After };
        That(service.Apply(colored, request, out _, now).Accepted, "有效重复使用同色仍消费一瓶，不伪造请求去重");
        var wash = colored with { Inventory = colored.Inventory with { Items = [source with { Core = source.Core with { ItemId = 101 } },
            target with { Avatar = target.Avatar with { Color1 = 1, Color2 = 2 } }] } };
        That(service.Apply(wash, request, out _, now).Plan!.After.Find(request.Target)!.Avatar is { Color1: 0, Color2: 0 },
            "洗色恢复两项颜色为0，不重置核心和孔位");
        foreach (var invalid in new[]
        {
            character with { Inventory = new([source]) },
            character with { Inventory = new([target]) },
            Role(source with { Core = source.Core with { EquipmentLockId = 1 } }, target),
            Role(source, target with { Core = target.Core with { EquipmentLockId = 1 } }),
            Role(source with { Core = source.Core with { ExpireTime = (int)now.ToUnixTimeSeconds() } }, target),
            Role(source, target with { Avatar = target.Avatar with { ExpireTime = (int)now.ToUnixTimeSeconds() } }),
            Role(source, target with { Avatar = null }),
            Role(source, target with { Position = new(InventoryListType.Equipment, 1) }),
            Role(source, target with { Core = target.Core with { ItemId = 201 } }),
            character with { Level = 0 },
            Role(source with { Core = source.Core with { ItemId = 101 } }, target)
        })
            That(service.Apply(invalid, request, out var rejected, now) is { Accepted: false, Plan: null } && rejected is not null,
                "缺失、锁定、过期、缺详情、穿戴中、不允许染色、源资格不足或未染色洗色均不扣源");
        Console.WriteLine($"PASS {Count - checks} 漂染解析、资源、完整实例及纯计划断言");
        Character Role(InventoryItem from, InventoryItem to) => character with { Inventory = character.Inventory with { Items = [from, to] } };
        static string Dye(string body) => "[stackable type]\n`[dye]` 0\n[dye type]\n1\n[dye info]\n" + body + "\n[/dye info]";
    }

    public static void RunResources(ItemCatalog items, AvatarDyeCatalog dyes)
    {
        That(dyes.Get(490704677) is { Color: 1 } && dyes.CanDye(406560053) &&
            items.Get(490704677).Kind == ItemKind.Consumable && items.Get(406560053).Kind == ItemKind.Avatar,
            "现场STK为珊瑚1，幻想诗篇卷发实际声明允许第一部位染色");
    }
}
