using static DfoServer.Testing.TestCheck;
using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

/// <summary>
/// 建会认证链路（阶段 2 最后一段）的 Handler 级自测：真实库 + 真实会话 + 真实发送队列，
/// 不需要 PVF 也不需要 socket。覆盖 `0x2E5` 的失败码 3 / 5 / 23 / 34、**顺序模式**（目标不在线仍回 01 并把
/// 申请留在登记表里）、拒绝分支不建会不扣钱、**关窗分支（`0x2E7` 只清草稿、不取消申请）**、接受时
/// **在创建者自己的会话里提交**并把金币差额与 `NOTI 0x2AE`/`0x47`/`0x3A` 发到**创建者连接自己的上下文**，
/// 以及创建者离线时的离线租约提交与失败码 103。
/// 线上字节契约、关窗后入城仍补投、逐包顺序由定向 TCP 组 `guild-permit` 与 `guild-permit-sequential` 复核。
/// </summary>
internal static class GuildPermitHandlerTests
{
    static readonly Location Room = new(38, 1, 450, 234, 0);

    public static void Run()
    {
        int before = Count;
        Flow().GetAwaiter().GetResult();
        Console.WriteLine($"PASS {Count - before} 建会认证 Handler 失败码、跨连接投递与提交断言");
    }

    sealed record Frame(byte Kind, ushort Opcode, byte[] Body, string Action);

    /// <summary>一条测试连接：真实 Session + 真实发送队列（写进 MemoryStream），发出的包按序记录。</summary>
    sealed class Peer : IAsyncDisposable
    {
        readonly CancellationTokenSource lifetime = new();
        readonly MemoryStream stream = new();
        readonly GameConnectionSender sender;
        readonly List<Frame> sent = [];
        public GameSession Session { get; }
        public GameConnectionContext Context { get; }
        public IReadOnlyList<Frame> Sent => sent;

        public Peer(AccountOwners.Lease owner, GameSession session, int id)
        {
            Session = session;
            sender = new GameConnectionSender(owner, stream, lifetime, 0,
                (kind, opcode, _, body, action) => sent.Add(new(kind, opcode, body.ToArray(), action)));
            Context = new GameConnectionContext(new(id, IPAddress.Loopback, stream, lifetime), owner, session, sender);
        }

        public void Clear() => sent.Clear();

        /// <summary>
        /// 发送队列按序：await 一个屏障包即可确保本连接上更早的 <c>Post</c>（邀请通知）已经写出，
        /// 随后把它从记录里剔除，让断言仍只看契约包。
        /// </summary>
        public async Task DrainAsync()
        {
            await Context.Sender.Send(1, byte.MaxValue, [], "test_drain");
            sent.RemoveAll(frame => frame.Action == "test_drain");
        }

        public async ValueTask DisposeAsync()
        {
            await sender.DisposeAsync();
            lifetime.Dispose();
            stream.Dispose();
        }
    }

    static async Task Flow()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-guild-permit-handler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var owners = new AccountOwners();
            using var creatorOwner = owners.Acquire("guild-permit-creator", () => { });
            using var inviteeOwner = owners.Acquire("guild-permit-invitee", () => { });
            using var otherOwner = owners.Acquire("guild-permit-other", () => { });
            using var offlineOwner = owners.Acquire("guild-permit-offline", () => { });
            // 顺序模式的被邀请方账号：⑫/⑬ 两段离线建会都用它的角色（同账号两个角色，各自一条测试连接）。
            using var seqInviteeOwner = owners.Acquire("guild-permit-seq-invitee", () => { });
            var repository = new CharacterRepository(path);
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), Room, Room, new TownDock(0, [], []),
                [], new Dictionary<uint, uint[]>());
            var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
            var directory = new GuildDirectory(repository, GuildParameterTests.Synthetic());
            var permits = new GuildPermitRegistry();
            var online = new OnlineSessionDirectory();
            var stats = new CharacterStatComputer(new CharacterCatalog(new Dictionary<byte, CharacterProfile>()));
            var guild = new GuildProjection(new InventoryProjection(stats, new TownProjection(towns, stats, null, pieces), pieces));

            var creator = repository.Create(creatorOwner, new("GuildMaster", Room, null)
            {
                Level = 60, Inventory = InventorySnapshot.Empty with { Gold = 1_000_000 }
            });
            var invitee = repository.Create(inviteeOwner, new("GuildInvitee", Room, null) { Level = 60 });
            var sameAccount = repository.Create(creatorOwner, new("GuildSecond", Room, null) { Level = 60 });
            var inGuild = repository.Create(otherOwner, new("GuildOther", Room, null) { Level = 60 });
            var offline = repository.Create(offlineOwner, new("GuildOffline", Room, null) { Level = 60 });
            That(sameAccount.Id != creator.Id && offline.Id != creator.Id, "夹具角色互不相同");
            SeedGuild(path, inGuild.Id);

            await using var creatorPeer = new Peer(creatorOwner, Ready(repository, towns, creatorOwner, creator.Slot), 1);
            await using var inviteePeer = new Peer(inviteeOwner, Ready(repository, towns, inviteeOwner, invitee.Slot), 2);
            // 生产接线：与 GameProtocolHandler 里对每个连接做的两行完全一致（通知与结果都走目标连接自己的上下文）。
            inviteePeer.Session.GuildPermitNoticeArrived = notice => guild.Notice(inviteePeer.Context, notice);
            creatorPeer.Session.GuildPermitResolved = outcome => guild.ResolvedAsync(creatorPeer.Context, outcome);
            online.Register(creatorOwner, creatorPeer.Session);
            online.Register(inviteeOwner, inviteePeer.Session);

            using var log = new ServerLog(Path.Combine(root, "Logs"));
            var handler = new GuildHandler(directory, permits, online, owners, log, guild);
            var commands = new GameCommandRegistry();
            handler.Register(commands);
            commands.Seal();
            Task Dispatch(Peer peer, ushort opcode, byte[] body) => commands.DispatchAsync(peer.Context, new Packet(1, opcode, body));

            // ① 创建窗口的名称与介绍校验成功后，文本进入建会草稿（0x2E5 本身不带公会名/地址/介绍）。
            await Dispatch(creatorPeer, (ushort)CmdPacketType.CHECK_GUILD_NAME_DOUBLE, Text("认证公会"));
            Only(creatorPeer, 1, 0x009C, [0x01], "名称查重可用，文本进入建会草稿");
            creatorPeer.Clear();
            await Dispatch(creatorPeer, (ushort)CmdPacketType.CHECK_GUILD_CREATE_PROMOTE_MSG, Text("介绍文本"));
            Only(creatorPeer, 1, 0x02E3, [0x01], "介绍校验可用");

            // ② 失败码 3：角色不存在。
            creatorPeer.Clear();
            await Dispatch(creatorPeer, 0x02E5, Permit(2, "NoSuchRole"));
            Only(creatorPeer, 1, 0x02E5, [0x00, 0x03, 0x00], "★ 目标不存在回失败码 3（「玩家不在线或者不存在。」）");
            That(permits.Count == 0, "失败的邀请不留下申请");

            // ③ 失败码 5：目标与创建者同账号。
            creatorPeer.Clear();
            await Dispatch(creatorPeer, 0x02E5, Permit(2, "GuildSecond"));
            Only(creatorPeer, 1, 0x02E5, [0x00, 0x05, 0x00], "★ 目标与创建者同账号回失败码 5");
            That(permits.Count == 0, "同账号邀请不登记申请");

            // ④ 失败码 23：目标已在公会。
            creatorPeer.Clear();
            await Dispatch(creatorPeer, 0x02E5, Permit(2, "GuildOther"));
            Only(creatorPeer, 1, 0x02E5, [0x00, 0x17, 0x00], "★ 目标已在公会回失败码 23（「无法加入该公会。」）");

            // ⑤ 顺序模式：角色存在但不在线**不再是失败** —— 回 01，申请留在登记表里等它入城时补投。
            creatorPeer.Clear();
            await Dispatch(creatorPeer, 0x02E5, Permit(2, "GuildOffline"));
            Only(creatorPeer, 1, 0x02E5, [0x01], "★ 目标不在线也回 01（客户端按钮本地转「等待认证」）");
            That(permits.Count == 1 && permits.Find(offline.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds()) is not null,
                "★ 目标不在线时申请留在登记表里，等它入城补投（60 秒内不会过期）");

            // ⑥ 没有建会草稿（名称查重没做过）：回 34，不拿空名字去建会。
            permits.ClearDraft(creator.Id);
            creatorPeer.Clear();
            await Dispatch(creatorPeer, 0x02E5, Permit(2, "GuildInvitee"));
            Only(creatorPeer, 1, 0x02E5, [0x00, 0x22, 0x00], "★ 没有可用名称草稿时回 34（「无法创建公会。」）");

            // 拒绝分支：完整邀请到达后，拒绝不建会也不扣钱。
            await Dispatch(creatorPeer, (ushort)CmdPacketType.CHECK_GUILD_NAME_DOUBLE, Text("认证公会"));
            await Dispatch(creatorPeer, (ushort)CmdPacketType.CHECK_GUILD_CREATE_PROMOTE_MSG, Text("介绍文本"));
            creatorPeer.Clear();
            await Dispatch(creatorPeer, 0x02E5, Permit(2, "GuildInvitee"));
            await inviteePeer.DrainAsync();
            Only(inviteePeer, 0, 0x02AD, [.. Text("认证公会"), 2, .. Text("GuildMaster"), .. Text("介绍文本")],
                "邀请包含完整公会名、大区2、创建者与介绍，不能把缺省文案当固定内容");
            Only(creatorPeer, 1, 0x02E5, [0x01], "邀请确实投递后才回 01");
            That(permits.Count == 1, "申请已登记");

            inviteePeer.Clear();
            creatorPeer.Clear();
            await Dispatch(inviteePeer, 0x02E6, [0x00]);
            Only(inviteePeer, 1, 0x02E6, [0x00, 0x22, 0x00], "拒绝时被邀请方只收到静默码 34，不弹「已经完成认可创建公会。」");
            Only(creatorPeer, 0, 0x02AE, [0x00], "★ 创建者收到 NOTI 0x2AE[0]（「对方拒绝认可。」）");
            That(repository.GuildOf(creator.Id) is null && Gold(repository, creatorOwner, creator.Id) == 1_000_000,
                "★ 拒绝分支不建会也不扣钱");

            // ⑧ 关窗分支（CMD 0x2E7，空包体）：**关窗不是取消**——只清创建窗口草稿，申请留在登记表里等被邀请方上线。
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            creatorPeer.Clear();
            inviteePeer.Clear();
            await Dispatch(creatorPeer, 0x02E5, Permit(2, "GuildInvitee"));
            await inviteePeer.DrainAsync();
            Only(inviteePeer, 0, 0x02AD, [.. Text("认证公会"), 2, .. Text("GuildMaster"), .. Text("介绍文本")],
                "关窗前投递完整邀请");
            That(permits.Count == 1 && permits.DraftCount == 1, "关窗前申请与创建窗口草稿都在");

            creatorPeer.Clear();
            inviteePeer.Clear();
            await Dispatch(creatorPeer, 0x02E7, []);
            await inviteePeer.DrainAsync();
            That(creatorPeer.Sent.Count == 0, "0x2E7 没有客户端处理器，服务端保持静默");
            That(inviteePeer.Sent.Count == 0, "★ 关窗不再推 NOTI 0x2AF：被邀请方不会据此关窗并自动回 0x2E6[00]");
            That(permits.Count == 1 && permits.Find(invitee.Id, now) is not null,
                "★ 关窗后待认证申请仍在登记表里（600 秒内有效，等被邀请方上线补投）");
            That(permits.DraftCount == 0 && permits.Draft(creator.Id, now) is null, "★ 关窗只清创建窗口草稿");

            // ⑧-b 草稿确实被清掉：同一角色直接发 0x2E5 因无草稿回 34，重新做一次 0x9C 查重后才恢复。
            creatorPeer.Clear();
            await Dispatch(creatorPeer, 0x02E5, Permit(2, "GuildInvitee"));
            Only(creatorPeer, 1, 0x02E5, [0x00, 0x22, 0x00], "★ 关窗后没有可用草稿：0x2E5 回 34（「无法创建公会。」）");
            That(permits.Count == 1 && permits.Find(invitee.Id, now) is not null, "★ 这条 34 既不消耗也不覆盖已登记的申请");
            creatorPeer.Clear();
            await Dispatch(creatorPeer, (ushort)CmdPacketType.CHECK_GUILD_NAME_DOUBLE, Text("认证公会"));
            Only(creatorPeer, 1, 0x009C, [0x01], "重新做一次名称查重（0x9C）通过");
            That(permits.DraftCount == 1 && permits.Draft(creator.Id, now)?.Name == "认证公会", "★ 草稿在重新查重之后恢复");

            // ⑨ 创建者账号在线但当前不是发起角色：不建会、不动钱，被邀请方按「找不到指定玩家」答复。
            permits.Register(new GuildPermit(2, "认证公会", "认证公会", "", sameAccount.Id, "guild-permit-creator",
                invitee.Id, "guild-permit-invitee", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60));
            inviteePeer.Clear();
            await Dispatch(inviteePeer, 0x02E6, [0x01]);
            Only(inviteePeer, 1, 0x02E6, [0x00, 0x03, 0x00], "★ 创建者已换角时回失败码 3，不提交建会");
            That(repository.GuildOf(creator.Id) is null && Gold(repository, creatorOwner, creator.Id) == 1_000_000,
                "创建者身份不符时不动任何人的钱");

            // ⑩ 接受分支：提交发生在创建者自己的会话，发布发生在创建者自己的连接。
            await Dispatch(creatorPeer, (ushort)CmdPacketType.CHECK_GUILD_NAME_DOUBLE, Text("认证公会"));
            creatorPeer.Clear();
            inviteePeer.Clear();
            await Dispatch(creatorPeer, 0x02E5, Permit(2, "GuildInvitee"));
            await inviteePeer.DrainAsync();
            Only(inviteePeer, 0, 0x02AD, [.. Text("认证公会"), 2, .. Text("GuildMaster"), .. Text("")],
                "重新建草稿后投递新邀请，不沿用已清除的介绍");
            Only(creatorPeer, 1, 0x02E5, [0x01], "第二次邀请同样回 01");
            inviteePeer.Clear();
            creatorPeer.Clear();
            await Dispatch(inviteePeer, 0x02E6, [0x01]);
            That(inviteePeer.Sent.Select(frame => frame.Opcode).SequenceEqual(new ushort[] { 742, 58, 2, 127, 70 }) &&
                At(inviteePeer.Sent, 0, 1, 742, [1]), "认可方也收到入会、Actor与公会资料，不是只写成员表");
            That(creatorPeer.Sent.Select(frame => frame.Opcode).SequenceEqual(new ushort[] { 14, 686, 71, 58, 2, 127, 70 }),
                "创建者按序收到金币、认可结果、建会、入会及资料投影");
            That(At(creatorPeer.Sent, 5, 0, 127, [1]) && At(inviteePeer.Sent, 3, 0, 127, [4]),
                "自然建会双方在70之前安装本人会长1与普通成员4职位，不能让客户端保留缺省6");
            That(At(creatorPeer.Sent, 0, 0, (ushort)NotiPacketType.UPDATE_ITEM_LIST) &&
                creatorPeer.Sent[0].Action == "inventory_committed_delta" && DeltaGold(creatorPeer.Sent[0].Body) == 700_000,
                "★ 金币差额（1,000,000 − 300,000）由创建者连接自己的背包投影发布");
            That(At(creatorPeer.Sent, 1, 0, 0x02AE, [0x01]), "0x2AE[01]：创建者按钮变「认证完成」");
            That(At(creatorPeer.Sent, 2, 0, 0x0047) && creatorPeer.Sent[2].Body.AsSpan(4).SequenceEqual(Text("认证公会")),
                "建会通知包含公会名，不触发无公会名兜底");
            That(At(creatorPeer.Sent, 3, 0, 0x003A) &&
                creatorPeer.Sent[3].Body.AsSpan(8).SequenceEqual((byte[])[.. Text("认证公会"), .. Text("GuildMaster")]),
                "入会通知包含公会名与角色名");
            That(permits.Count == 0, "答复后申请取出即删，重复答复不会二次建会");

            long guildId = BinaryPrimitives.ReadUInt32LittleEndian(creatorPeer.Sent[2].Body);
            That(BinaryPrimitives.ReadUInt32LittleEndian(creatorPeer.Sent[3].Body) == guildId &&
                BinaryPrimitives.ReadUInt32LittleEndian(inviteePeer.Sent[1].Body) == guildId &&
                creatorPeer.Session.Selected?.Guild?.Id == guildId && inviteePeer.Session.Selected?.Guild?.Id == guildId,
                "双方客户端与服务端角色快照使用实际公会ID，而不是角色ID");
            That(repository.LoadCharacter(inviteeOwner, invitee.Slot).Guild?.Name == "认证公会",
                "重新加载角色仍能读取已提交公会，不依赖在线邀请缓存");
            var oldMembership = inviteePeer.Session.CaptureGuildMembership(invitee.Id)!;
            inviteePeer.Session.ReturnToRoster();
            inviteePeer.Session.Select(invitee.Slot);
            inviteePeer.Session.MarkReady();
            That(!inviteePeer.Session.CanPublishGuildMembership(oldMembership) && inviteePeer.Session.Selected?.Guild?.Id == guildId,
                "重新选角恢复真实公会，原选角的入会回执失效");
            That(guildId > 0 && repository.GuildOf(creator.Id) == guildId, "公会归属与 NOTI 0x47 里的公会 id 一致");
            That(Gold(repository, creatorOwner, creator.Id) == 700_000, "★ 独立读库确认金币只扣了一次");
            That(repository.GuildOf(invitee.Id) == guildId, "★ 认可认证的被邀请角色与会长同一事务入会（成员行职位 0）");

            creatorPeer.Clear(); inviteePeer.Clear();
            permits.Register(new(2, "OtherGuild", "other-guild", "", creator.Id, creatorOwner.Account,
                invitee.Id, inviteeOwner.Account, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60));
            await Dispatch(inviteePeer, 0x02E6, [0x01]);
            Only(inviteePeer, 1, 0x02E6, [0x00, 0x27, 0x00], "已入会认可答复使用本版错误表39，不沿用邀请错误码23");
            That(Gold(repository, creatorOwner, creator.Id) == 700_000 && permits.Count == 0,
                "已入会的认可被拒绝，不重复扣费或保留旧申请");

            // ⑪ 创建者已在公会后再次邀请：回 34（目标自己已在会时先回 23，因此这里换一个未入会的角色当目标）。
            creatorPeer.Clear();
            await Dispatch(creatorPeer, 0x02E5, Permit(2, "GuildOffline"));
            Only(creatorPeer, 1, 0x02E5, [0x00, 0x22, 0x00], "★ 已在公会的角色不能再发起建会认证");
            That(permits.Count == 0, "失败的第二次邀请不留下申请");

            // ⑪-b 目标已在公会（⑩ 认可认证后入会的那个角色）：回 23，同样不登记申请。
            creatorPeer.Clear();
            await Dispatch(creatorPeer, 0x02E5, Permit(2, "GuildInvitee"));
            Only(creatorPeer, 1, 0x02E5, [0x00, 0x17, 0x00], "★ 入会后的角色不能再被邀请（回 23「无法加入该公会。」）");
            That(permits.Count == 0, "目标已在公会时不登记申请");

            // ⑫ 顺序模式的离线建会：创建者发起邀请后**离线**，被邀请方之后才入城认可。
            //    账号租约在这里只用于造角色，`using` 一退出账号就没有 owner（等价于创建者已下线）。
            ushort awayCreatorId;
            using (var awayOwner = owners.Acquire("guild-permit-away", () => { }))
                awayCreatorId = repository.Create(awayOwner, new("GuildAway", Room, null)
                {
                    Level = 60, Inventory = InventorySnapshot.Empty with { Gold = 1_000_000 }
                }).Id;
            var seqInvitee = repository.Create(seqInviteeOwner, new("SeqInvitee2", Room, null) { Level = 60 });
            await using var seqInviteePeer = new Peer(seqInviteeOwner, Ready(repository, towns, seqInviteeOwner, seqInvitee.Slot), 3);
            online.Register(seqInviteeOwner, seqInviteePeer.Session);
            permits.Register(Pending(awayCreatorId, "guild-permit-away", seqInvitee.Id, "离线认证公会"));
            await Dispatch(seqInviteePeer, 0x02E6, [0x01]);
            That(seqInviteePeer.Sent.Select(frame => frame.Opcode).SequenceEqual(new ushort[] { 742, 58, 2, 127, 70 }) &&
                At(seqInviteePeer.Sent, 0, 1, 742, [1]) && At(seqInviteePeer.Sent, 3, 0, 127, [4]),
                "创建者离线时也向认可方下发完整入会状态，127 在 70 之前安装普通成员职位4");
            That(Scalar(path, "SELECT gold FROM characters WHERE id=" + awayCreatorId) is 700_000L &&
                Scalar(path, "SELECT revision FROM characters WHERE id=" + awayCreatorId) is 1L,
                "★ 离线建会扣了创建者的 300000 并推进 revision（下次上线选角读到最新快照）");
            That(Scalar(path, "SELECT gold FROM characters WHERE id=" + seqInvitee.Id) is 0L, "被邀请方不承担建会花费");
            long seqGuildId = Convert.ToInt64(Scalar(path, "SELECT guild_id FROM guild_members WHERE character_id=" + seqInvitee.Id));
            string awayMember = Convert.ToString(Scalar(path,
                "SELECT guild_id||':'||character_id||':'||grade FROM guild_members WHERE character_id=" + awayCreatorId)) ?? "无成员行";
            string seqMember = Convert.ToString(Scalar(path,
                "SELECT guild_id||':'||character_id||':'||grade FROM guild_members WHERE character_id=" + seqInvitee.Id)) ?? "无成员行";
            string guildRow = Convert.ToString(Scalar(path,
                "SELECT master_character_id||':'||name FROM guilds WHERE guild_id=" + seqGuildId)) ?? "无公会行";
            That(Scalar(path, "SELECT count(*) FROM guilds") is 3L &&
                awayMember == seqGuildId + ":" + awayCreatorId + ":0" && seqMember == seqGuildId + ":" + seqInvitee.Id + ":0" &&
                guildRow == awayCreatorId + ":离线认证公会",
                "★ 独立读库：离线建会写入公会行（会长=创建者）与被邀请角色的成员行，两者同一公会" +
                "（实为 公会=" + guildRow + "，创建者成员行=" + awayMember + "，被邀请成员行=" + seqMember + "）");
            That(Scalar(path, "SELECT grade FROM guild_members WHERE character_id=" + seqInvitee.Id) is 0L &&
                Scalar(path, "SELECT count(*) FROM guild_members WHERE character_id=" + seqInvitee.Id) is 1L,
                "被邀请角色入会职位固定 0，且只有一个成员行");

            // ⑬ 离线提交失败（创建者金币不足）：不建会、不扣钱，被邀请方收到码表内语义最近的 103 而不是静默。
            ushort poorAwayId;
            using (var poorOwner = owners.Acquire("guild-permit-poor", () => { }))
                poorAwayId = repository.Create(poorOwner, new("PoorAway", Room, null) { Level = 60 }).Id;
            var poorInvitee = repository.Create(seqInviteeOwner, new("PoorInvitee", Room, null) { Level = 60 });
            await using var poorInviteePeer = new Peer(seqInviteeOwner, Ready(repository, towns, seqInviteeOwner, poorInvitee.Slot), 4);
            online.Register(seqInviteeOwner, poorInviteePeer.Session);
            permits.Register(Pending(poorAwayId, "guild-permit-poor", poorInvitee.Id, "穷公会"));
            await Dispatch(poorInviteePeer, 0x02E6, [0x01]);
            Only(poorInviteePeer, 1, 0x02E6, [0x00, 0x67, 0x00],
                "★ 离线提交失败回 103（「公会邀请的等待时间已达到上限」），不弹成功文案");
            That(Scalar(path, "SELECT count(*) FROM guilds") is 3L && Scalar(path, "SELECT gold FROM characters WHERE id=" + poorAwayId) is 0L &&
                Scalar(path, "SELECT count(*) FROM guild_members WHERE character_id=" + poorInvitee.Id) is 0L,
                "★ 失败的离线建会不建会、不扣钱、不写成员行");
            That(permits.Count == 0, "答复后申请取出即删（离线路径同样不会二次提交）");
        }
        finally
        {
            foreach (string file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file))
                    TestFiles.DeleteFile(file);
            TestFiles.DeleteDirectory(root, recursive: true);
        }
    }

    static GameSession Ready(CharacterRepository repository, TownCatalog towns, AccountOwners.Lease owner, ushort slot)
    {
        var session = new GameSession(repository, towns, owner);
        session.Select(slot);
        session.MarkReady();
        return session;
    }

    /// <summary>给第三方角色预置一行公会与成员行（TCP 组用 <c>fixture.Sql</c> 的同一手法）。</summary>
    static void SeedGuild(string path, ushort characterId)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO guilds(name,address,master_character_id,created_at_unix) VALUES('别的公会','other-guild',$master,1700000000);" +
            "INSERT INTO guild_members(guild_id,character_id,grade,contribution,member_memo,joined_at_unix,last_login_unix)" +
            " VALUES((SELECT guild_id FROM guilds WHERE name='别的公会'),$id,0,0,'',1700000000,0)";
        command.Parameters.AddWithValue("$master", characterId);
        command.Parameters.AddWithValue("$id", characterId);
        command.ExecuteNonQuery();
    }

    static uint Gold(CharacterRepository repository, AccountOwners.Lease owner, ushort characterId) =>
        repository.List(owner).Single(character => character.Id == characterId).Inventory.Gold;

    /// <summary>离线建会的独立读库断言：只读一个标量，不借用服务端读取路径（创建者账号没有 owner）。</summary>
    static object? Scalar(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>一条待认证申请：有效期与 Handler 的 600 秒同口径（顺序模式：创建者与被邀请方不同时在线）。</summary>
    static GuildPermit Pending(ushort initiator, string initiatorAccount, ushort invitee, string name) =>
        new(2, name, name, "", initiator, initiatorAccount, invitee, "guild-permit-seq-invitee",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600);

    /// <summary>UPDATE_ITEM_LIST 差量正文：u16 计数 + 每条 123 字节（u16 槽位、u32 物品 id、u32 值…）；金币是主背包槽 0。</summary>
    static uint DeltaGold(byte[] body)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1));
        for (int index = 0; index < count; index++)
        {
            int offset = 3 + 123 * index;
            if (BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset)) == 0)
                return BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 6));
        }
        throw new InvalidOperationException("金币差额里没有主背包槽 0");
    }

    static bool At(IReadOnlyList<Frame> sent, int index, byte kind, ushort opcode) =>
        sent.Count > index && sent[index].Kind == kind && sent[index].Opcode == opcode;

    static bool At(IReadOnlyList<Frame> sent, int index, byte kind, ushort opcode, byte[] body) =>
        At(sent, index, kind, opcode) && sent[index].Body.SequenceEqual(body);

    static void Only(Peer peer, byte kind, ushort opcode, byte[] body, string message) =>
        That(peer.Sent.Count == 1 && At(peer.Sent, 0, kind, opcode, body), message + "（实发 " + Describe(peer.Sent) + "）");

    static string Describe(IReadOnlyList<Frame> sent) => sent.Count == 0 ? "无" : string.Join(", ",
        sent.Select(frame => $"({frame.Kind},{frame.Opcode:X4})[{Convert.ToHexString(frame.Body)}]"));

    /// <summary>正文 = u32 字节长度 + CP936 文本（与查重 / 介绍校验请求同一口径）。</summary>
    static byte[] Text(string text)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(text);
        byte[] body = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, bytes.Length);
        bytes.CopyTo(body, 4);
        return body;
    }

    /// <summary>正文 = u8 大区序号 + u32 名称长度 + CP936 角色名（实机样本 `02 05 00 00 00 "admin"` 的口径）。</summary>
    static byte[] Permit(byte region, string name)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(name);
        byte[] body = new byte[5 + bytes.Length];
        body[0] = region;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), bytes.Length);
        bytes.CopyTo(body, 5);
        return body;
    }
}
