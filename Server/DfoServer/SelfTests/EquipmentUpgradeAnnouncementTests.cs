using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Channels;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class EquipmentUpgradeAnnouncementTests
{
    static readonly InventorySlot Target = new(InventoryListType.Main, 3);
    static readonly ItemCore Weapon = new(1, ItemKind.Equipment, 1) { Attr = 12, GenuineUpgrade = 6 };

    public static async Task RunCoreAsync()
    {
        int before = Count;
        using var world = new World();
        var source = world.Add("notice-source", "Announcer", 2, 10);
        var same = world.Add("notice-same", "SameGroup", 2, 11);
        var other = world.Add("notice-other", "OtherGroup", 3, 10);
        var loading = world.Add("notice-loading", "Loading", 2, 12, ready: false);
        var unselected = world.Add("notice-roster", "Roster", 2, 13, select: false);
        Rules(source.Session.Selected!);
        using var stop = new CancellationTokenSource();
        using var output = new MemoryStream();
        await using var sender = new GameConnectionSender(source.Owner, output, stop, 0, (_, _, _, _, _) => { });
        var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), source.Owner, source.Session, sender);
        using var log = new ServerLog(Path.Combine(world.Root, "Logs"));
        var projection = new EquipmentUpgradeAnnouncementProjection(world.Online, world.Items, 12, 12, log);
        var committed = source.Session.MutateInventory(character =>
        {
            var item = character.Inventory.Find(Target)!;
            var after = character.Inventory with { Items = character.Inventory.Items.Replace(item, item with
                { Core = item.Core with { Attr = 13 } }) };
            return new(true, 0, new(character.Inventory, after, "equipment_amplification_npc"));
        });
        var receipt = new EnhancementReceipt(EnhancementMode.Reinforce, Target, 4, 0, 12, 13, true) { UsesLevelProbability = true };
        That(committed.Receipt?.Character.Inventory.Find(Target)?.Core.Upgrade == 13, "公告来源是已经落库的真实装备");
        projection.Enhancement(context, committed, receipt);
        That(source.Pending.Count == 1 && same.Pending.Count == 1 && other.Pending.Count == 0 &&
            loading.Pending.Count == 0 && unselected.Pending.Count == 0, "仅同server组的已就绪角色收到公告，允许跨频道，不跨组或发给选角/加载连接");
        var queued = same.Pending.Single();
        That(queued.Opcode == 502 && queued.Current() && queued.Body.AsSpan().SequenceEqual(source.Pending.Single().Body),
            "正文只构造一次并共用既有受守卫发送入口，入队不等于已写出");
        That(Text(queued.Body) == "玩家[Announcer]强化 +13 Test Weapon成功", "跨频道正文携带真实名字，不依赖接收端同屏玩家缓存");
        source.Session.MutateInventory(character => new(true, 0, new(character.Inventory,
            character.Inventory with { Gold = character.Inventory.Gold + 1 }, "ordinary_change")));
        same.Session.MutateInventory(character => new(true, 0, new(character.Inventory,
            character.Inventory with { Gold = character.Inventory.Gold + 1 }, "ordinary_change")));
        That(queued.Current(), "公告是已提交操作的记录，双方普通资产revision变化不抹掉合法公告");
        same.Session.ReturnToRoster();
        That(!queued.Current(), "出队前返回选角阻止旧角色公告");
        same.Session.Select(0); same.Session.MarkReady();
        That(!queued.Current(), "同角色重选的新epoch不能复活旧公告");
        projection.Enhancement(context, committed, receipt);
        var afterReselect = same.Pending.Last();
        That(afterReselect.Current(), "后续公告使用接收者当前选角epoch");
        using (world.Owners.Acquire(same.Owner.Account, () => { }))
            That(!afterReselect.Current(), "账号接管阻止原连接排队公告");
        int count = source.Pending.Count;
        string databaseSnapshot = JsonSerializer.Serialize(new CharacterRepository(world.Database).List(source.Owner).Single());
        world.Online.RegisterConnection(1, source.Session, (_, _, _) => throw new InvalidOperationException("不得降级到无守卫投递"),
            guardedProbe: (_, _, _, _) => false);
        projection.Enhancement(context, committed, receipt);
        That(source.Pending.Count == count && JsonSerializer.Serialize(new CharacterRepository(world.Database).List(source.Owner).Single()) == databaseSnapshot,
            "发送队列拒绝不重试、不重抽、不撤销或重复提交装备");
        source.Session.ReturnToRoster(); source.Session.Select(0); source.Session.MarkReady();
        count = same.Pending.Count;
        projection.Enhancement(context, committed, receipt);
        That(same.Pending.Count == count && !source.Pending[0].Current(), "原发送者选角epoch变化后不发布旧回执或写出旧公告");
        using (world.Owners.Acquire(source.Owner.Account, () => { }))
            projection.Enhancement(context, committed, receipt);
        Console.WriteLine($"EquipmentUpgradeAnnouncementTests: {Count - before} passed");
    }

    static void Rules(Character character)
    {
        InventoryResult Result(byte old, byte value, bool destroyed = false, ItemKind kind = ItemKind.Equipment)
        {
            var item = new InventoryItem(123, Target, Weapon with { Attr = old, Kind = kind });
            var before = new InventorySnapshot([item]);
            var after = before with { Items = destroyed ? [] : [item with { Core = item.Core with { Attr = value } }] };
            return new(true, 0, new(before, after, "test"))
                { Receipt = new(character with { Inventory = after }, 1, 1) };
        }
        foreach (var mode in new[] { EnhancementMode.Reinforce, EnhancementMode.Amplify })
        {
            EquipmentUpgradeAnnouncement? Notice(byte old, byte next, bool success, bool ticket = false, bool destroyed = false) =>
                EquipmentUpgradeAnnouncement.Enhancement(Result(old, next, destroyed),
                    new(mode, Target, 4, 0, old, next, success) { UsesLevelProbability = !ticket }, 12);
            That(Notice(10, 11, true) is null && Notice(11, 12, true) is { Level: 12, AttemptedLevel: 12 },
                "强化与增幅按各自表中的公告门槛，成功以结果等级判断");
            That(Notice(11, 0, false, destroyed: true) is null &&
                Notice(12, 0, false, destroyed: true) is { Level: 12, AttemptedLevel: 13, Succeeded: false, Item.ItemId: 1 },
                "高等级失败先于归零或碎装判断，原装备仍供失败文案使用");
            That(Notice(12, 10, false) is not null && Notice(12, 12, false, ticket: true) is null,
                "保护后的普通失败仍公告，失败等级券不公告");
            That(Notice(15, 12, true, ticket: true) is null && Notice(12, 13, true, ticket: true) is null,
                "直接定级或追加一级的券即使成功达到门槛也不公告，不按成功率或等级差判断");
        }
        var result = Result(11, 12);
        var receipt = new EnhancementReceipt(EnhancementMode.Reinforce, Target, 4, 0, 11, 12, true) { UsesLevelProbability = true };
        That(EquipmentUpgradeAnnouncement.Enhancement(result with { Receipt = null }, receipt, 12) is null &&
            EquipmentUpgradeAnnouncement.Enhancement(result with { Accepted = false }, receipt, 12) is null &&
            EquipmentUpgradeAnnouncement.Enhancement(result, receipt, null) is null &&
            EquipmentUpgradeAnnouncement.Enhancement(Result(11, 12, kind: ItemKind.GuildMedal), receipt, 12) is null,
            "未提交、被拒绝、未配置公告及独立勋章强化不误发装备公告");
        foreach (var mode in new[] { EnhancementMode.Reinforce, EnhancementMode.Amplify })
            foreach (int chance in new[] { 100, 30 })
                foreach (bool additional in new[] { false, true })
                    foreach (int roll in new[] { 0, 9999 })
                    {
                        const int coupon = 990201;
                        var source = new InventoryItem(91, new(InventoryListType.Main, 65), new(coupon, ItemKind.Consumable, 1));
                        var gear = new InventoryItem(92, Target, Weapon with { AmplifyType = mode == EnhancementMode.Amplify ? (byte)2 : (byte)0,
                            AmplifyValue = mode == EnhancementMode.Amplify ? (ushort)21 : (ushort)0 });
                        var role = character with { Inventory = new([source, gear]) };
                        string label = mode == EnhancementMode.Amplify ? "equipment amplify reinforcement ticket" : "equipment reinforcement ticket";
                        var definition = EnhancementCatalog.Parse(coupon,
                            $"[{label}]\n{(additional ? 1 : 13)} {chance} `{(additional ? "additional" : "fixed")}` -1\n")!;
                        var couponTemplate = new ItemTemplate(coupon, ItemKind.Consumable, 0, 1, 0, 1000, 1, 2, 1,
                            "[free]", ushort.MaxValue, 0, "Coupon");
                        var gearTemplate = new ItemTemplate(1, ItemKind.Equipment, 12, 1, 30, 1, 1, 2, 1,
                            "[free]", ushort.MaxValue, 0, "Weapon");
                        var planned = InventoryEnhancementService.Plan(role,
                            new(mode, Target, 1, 65, -1, ReadOnlyMemory<byte>.Empty, -1, -1), 0,
                            definition, couponTemplate, gearTemplate, () => roll, out var applied);
                        That(planned.Accepted && applied is { UsesLevelProbability: false },
                            "真实券使用入口不标记普通逐级概率：100%和30%、固定和增量、成功和失败全部排除");
                        var committed = planned with { Receipt = new(role with { Inventory = planned.Plan!.After }, 1, 1) };
                        That(EquipmentUpgradeAnnouncement.Enhancement(committed, applied!, 12) is null,
                            "券已真实消费并到达公告门槛也不播报，不靠概率或结果等级猜测升级路径");
                    }
        foreach (var mode in new[] { EnhancementMode.RandomReinforce, EnhancementMode.LuckyAmplify })
            That(EquipmentUpgradeAnnouncement.Enhancement(Result(12, 13),
                new(mode, Target, 4, 0, 12, 13, true), 12) is null,
                "随机等级和幸运独立奖池不混入普通逐级概率公告");
        var target = new InventoryItem(123, Target, Weapon);
        var refineResult = new InventoryResult(true, 0, new(new([target]), new([target]), "test"))
            { Receipt = new(character with { Inventory = new([target]) }, 1, 1) };
        var refine = new RefinementReceipt(4, 0, 5, true, 6, Target) { Target = target };
        That(EquipmentUpgradeAnnouncement.Refinement(refineResult, refine) is { Level: 6 } &&
            EquipmentUpgradeAnnouncement.Refinement(refineResult, refine with { NewLevel = 5 }) is null,
            "普通锻造原生门槛是结果大于5，不是大于等于5");
        That((refine with { Succeeded = false, UsedTicket = false }).Announce &&
            !(refine with { Succeeded = false, UsedTicket = true }).Announce &&
            !(refine with { Succeeded = true, UsedTicket = true }).Announce &&
            (refine with { Succeeded = false, UsedTicket = true, Portable = true }).Announce,
            "普通及便携炉高等级失败保级公告，直接等级券无论成功失败均不公告");
        foreach (var kind in Enum.GetValues<EquipmentUpgradeKind>())
        {
            var notice = new EquipmentUpgradeAnnouncement(kind, Weapon, kind == EquipmentUpgradeKind.Refinement ? (byte)6 : (byte)12, false);
            byte[] body = EquipmentUpgradeAnnouncementPackets.Body("Announcer", "Test Weapon", notice);
            string expected = kind switch
            {
                EquipmentUpgradeKind.Reinforcement => "玩家[Announcer]强化 +13 Test Weapon失败",
                EquipmentUpgradeKind.Amplification => "玩家[Announcer]增幅 +13 Test Weapon失败",
                _ => "玩家[Announcer]锻造 +12 Test Weapon 第7阶段失败"
            };
            byte[] bytes = CharacterName.Encoding.GetBytes(expected);
            That(body[0] == 2 && BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1)) == bytes.Length &&
                body.AsSpan(5).SequenceEqual(bytes), "独立编码核对NOTI502/type2、CP936字节长度和三种原生文案");
        }
        var longBody = EquipmentUpgradeAnnouncementPackets.Body("Announcer", new string('武', 2000), new(EquipmentUpgradeKind.Reinforcement, Weapon, 12, true));
        That(longBody.Length <= 1028 && Text(longBody).Length > 0, "超长资源名称按CP936字符边界限长，不让提交后的展示错误踢线");
    }

    static string Text(byte[] body) => CharacterName.Encoding.GetString(body, 5, BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1)));

    sealed record Pending(ushort Opcode, byte[] Body, Func<bool> Current);
    sealed record Recipient(AccountOwners.Lease Owner, GameSession Session, List<Pending> Pending);
    sealed class World : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "equipment-notices-" + Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Root, "game.db");
        public AccountOwners Owners { get; } = new();
        public OnlineSessionDirectory Online { get; } = new();
        public ItemCatalog Items { get; } = new(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        readonly TownCatalog towns;
        readonly List<Recipient> recipients = [];
        public World()
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("notice.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            towns = new(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) }, point, point,
                new(0, [], []), [], new Dictionary<uint, uint[]>());
            Items.PrefillTemplates(new Dictionary<int, ItemTemplate>
                { [1] = new(1, ItemKind.Equipment, 12, 1, 30, 1, 0, 4, 0, "[free]", ushort.MaxValue, 0, "Test Weapon") });
        }
        public Recipient Add(string account, string name, byte server, byte channel, bool ready = true, bool select = true)
        {
            var owner = Owners.Acquire(account, () => { });
            var repository = new CharacterRepository(Database);
            repository.Create(owner, new(name, towns.Room, towns.InitialReturn) { Inventory = new([new(0, Target, Weapon)]) });
            var session = new GameSession(repository, towns, owner, entry: new(new(server, channel), 22, "[normal]"));
            if (select) { session.Select(0); if (ready) session.MarkReady(); }
            var recipient = new Recipient(owner, session, []);
            recipients.Add(recipient);
            Online.Register(owner, session);
            Online.RegisterConnection(recipients.Count, session, (_, _, _) => throw new InvalidOperationException("无守卫投递"),
                guardedProbe: (opcode, body, _, current) => { recipient.Pending.Add(new(opcode, body, current)); return true; });
            return recipient;
        }
        public void Dispose()
        {
            foreach (var recipient in recipients) { recipient.Session.End(); recipient.Owner.Dispose(); }
            TestFiles.DeleteDirectory(Root, true);
        }
    }
}
