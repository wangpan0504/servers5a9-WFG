using System.Buffers.Binary;
using System.Text;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Chat;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Skills;
using DfoServer.Persistence;
using DfoServer.Testing.Inventory;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

/// <summary>
/// 聊天 Phase 1 的线上字节回归（真 socket，不需要真实客户端）：用预置角色登录后发 <c>CMD 0x0011</c>，
/// 直接断言服务端写出的帧与"投给谁"。
/// <list type="bullet">
/// <item>房间隔离：选角落在赛丽亚房间（PVF 房间按账号隔离），普通聊天只投本人。</item>
/// <item>普通聊天：同场景的在线角色各收一帧 <c>NOTI 0x0C</c>，正文
/// <c>[03][层 id][00][u32 CP936 字节数][CP936 文本]</c>；另一频道的角色不收。</item>
/// <item>公会聊天：只投同公会且会话快照一致的角色；成员表在选角后追加的账号不投（锁内复核）。</item>
/// <item>悄悄话：只投目标角色（下行显示 type 映射为 3）；目标不存在时不发任何帧，只记 <c>chat_whisper_offline</c>。</item>
/// <item>物品链接 <c>CMD 0x01A8</c>：实测 179 字节正文发 <c>NOTI 0x172 MESSAGE_HYPER_LINK</c>，
/// 正文 = <c>[type][layer][v=0][u32 文本字节数][19B 链接文本][cnt=1][148B 记录][u32 1][20]</c>，记录与尾串原样携带、
/// 发送者也收到回显；记录为空时退化 <c>0x0C</c> 纯文本；非法长度不广播、不断线。</item>
/// <item>超长文本：入站按 200 字节截断，下行正文 = 8 字节头 + 200 字节文本，不断线。</item>
/// <item>每连接节流：同一连接 1 秒内第 6 条被丢弃并记 <c>chat_throttled</c>。</item>
/// <item>两种喇叭：槽位道具不符/为空拒绝且不扣；扣成功后库存 -1，且每个在线会话各收一帧
/// <c>NOTI 0x0076</c> 喇叭页签行（<c>[type][B=0][C=0][D=0][E=0][str1 发言人][str2 正文][str3 占位]</c>）；
/// 服务器喇叭发 <c>0x0E</c>（D&amp;F 官方标签）并投全服，频道喇叭发 <c>0x0B</c>（Ch. 频道标签）只投同频道；
/// 喇叭不再弹旧的 <c>NOTI 0x06AD</c> 公告框。</item>
/// </list>
/// 期望值来自实测收包样本、反汇编结论与仓库既有背包查询，不调用服务端 Parser/Builder 反算。
/// </summary>
internal static class ChatTcpTests
{
    const ushort CommandSendMessage = 0x0011;
    const ushort CommandItemHyperlink = 0x01A8;
    const ushort NoticeMessage = 0x0C;
    const ushort NoticeHyperLink = 0x172;
    const ushort NoticeMegaphone = 0x76;
    const ushort NoticeServerNotice = 0x06AD;
    const ushort NoticeUpdateItemList = 0x0E;

    static readonly Encoding Text = CreateTextEncoding();

    static Encoding CreateTextEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        var clock = new DailyBoosterTestClock(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "chat"), resources: resources, clock: clock);
        var repository = new CharacterRepository(fixture.Database);
        var growth = SelfTestCatalogs.Growth(resources.Archive, resources.Learning);
        var owners = new AccountOwners();
        var profiles = new Dictionary<string, Character>();
        foreach (var (account, name) in new[] { ("10000", "ChatA"), ("20001", "ChatB"), ("20002", "ChatC"), ("20003", "ChatD") })
        {
            using var owner = owners.Acquire(account, () => { });
            var candidate = CharacterCreation.Prepare(name, resources.Characters.Get(0), resources.Towns.Room,
                resources.Towns.InitialReturn, resources.Learning.Get);
            var learning = SkillPointLedger.Synchronize(0, 0, 20, candidate.InitialLearning with
            {
                Experience = growth.GetLevelStartExperience(20)
            }, resources.Learning);
            var inventory = candidate.Inventory;
            if (name == "ChatA")
                // 服务器喇叭 36 放在主背包槽 67（2 个，便于核对"扣 1 个"），频道喇叭 37 放在槽 68。
                inventory = inventory with
                {
                    Items = inventory.Items
                        .Add(new(0, new(InventoryListType.Main, 67), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(36), 2)))
                        .Add(new(0, new(InventoryListType.Main, 68), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(37), 1)))
                };
            profiles[name] = repository.Create(owner, candidate with { Level = 20, InitialLearning = learning, Inventory = inventory });
        }

        await fixture.StartAsync();

        // 公会成员表：A、B 在选角前入会；C 的成员行在它选角之后才写入，用来验证"投递前锁内复核会话快照"。
        fixture.Sql("INSERT INTO guilds(name,address,master_character_id,tier,level,experience,public_funds,guild_crystal,notice,memo,created_at_unix) " +
            "VALUES('聊天公会','chat-guild',$p0,0,1,0,0,0,'','',1700000000)", (int)profiles["ChatA"].Id);
        long guildId = Convert.ToInt64(fixture.Query("SELECT guild_id FROM guilds WHERE name='聊天公会'")[0][0]);
        foreach (string member in new[] { "ChatA", "ChatB" })
            fixture.Sql("INSERT INTO guild_members(guild_id,character_id,grade,contribution,member_memo,joined_at_unix,last_login_unix) " +
                "VALUES($p0,$p1,0,0,'',1700000000,0)", guildId, (int)profiles[member].Id);

        var entries = ChannelTcpTests.Endpoints(fixture);
        using var alpha = Open(fixture, "10000");
        using var beta = Open(fixture, "20001");
        using var gamma = Open(fixture, "20002");
        using var delta = Open(fixture, "20003", gamePort: entries[11], channelNumber: 11);
        // A/B 有公会成员行，选角时比共享 Select 的固定包序表多出公会投影；本组只依赖 Ready+Town，
        // 因此按 124 哨兵消费入城序列（与建会认证组同一做法），不复制也不修改共享期望表。
        // 同频道有新成员加入时，已在线角色会收到同屏进入帧；先让新成员自己收尾（其 Join 完成后），
        // 再收已在线成员的帧，避免并发入队顺序不确定。
        Enter(alpha);
        Settle(alpha);
        Enter(beta);
        Settle(beta);
        Settle(alpha);
        Enter(gamma);
        Settle(gamma);
        Settle(alpha);
        Settle(beta);
        Enter(delta);
        Settle(delta);
        int entry = Count;

        // 先用真实有序请求确认上一场处理完，再推进短/长窗口；不依赖主机调度速度。
        // 六条突发消息之间不推进单调时钟，保证真正测试同一秒的限流规则。
        void NextChatWindow()
        {
            alpha.Barrier();
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        // ① 房间隔离：选角落在赛丽亚房间（PVF IsRoom ⇒ 场景键含账号），普通聊天只投本人一帧。
        NextChatWindow();
        alpha.Send(CommandSendMessage, SendBody(3, 4, 0, "房间"));
        Same(alpha.Expect(0, NoticeMessage).Body, MessageBody(3, 4, "房间"), "房间内普通聊天 0x0C 正文");
        beta.NoFrame(300, "房间按账号隔离：别的账号收不到普通聊天", (0, NoticeMessage));
        gamma.NoFrame(300, "房间按账号隔离：第三个账号也收不到", (0, NoticeMessage));
        delta.NoFrame(300, "另一频道的房间更收不到", (0, NoticeMessage));

        // ② 离开房间进入同一公开城镇地图（38/0 街道），A、B、C 之后同场景；D 留在自己的房间。
        Leave(alpha);
        Leave(beta);
        Settle(alpha);
        Leave(gamma);
        Settle(alpha);
        Settle(beta);

        // ③ 普通聊天（type 3）：A、B、C 同场景各收一帧 [03][请求层 id][00][字节数][文本]；D（另一频道）不收。
        NextChatWindow();
        alpha.Send(CommandSendMessage, SendBody(3, 4, 0, "普通聊天"));
        Same(alpha.Expect(0, NoticeMessage).Body, MessageBody(3, 4, "普通聊天"), "普通聊天下行 0x0C 正文");
        Same(beta.Expect(0, NoticeMessage).Body, MessageBody(3, 4, "普通聊天"), "同场景接收者收到同样的 0x0C 正文");
        Same(gamma.Expect(0, NoticeMessage).Body, MessageBody(3, 4, "普通聊天"), "同场景第三个角色也收到 0x0C 正文");
        delta.NoFrame(400, "普通聊天不投不同频道", (0, NoticeMessage));

        // ③b 物品链接（CMD 0x01A8）：实测 179 字节正文（kind=3、19 字节链接文本、cnt=1、148 字节记录）。
        // 文本与记录配对时发 0x172（记录原样携带），发送者自己也收到同帧回显。
        byte[] linkText = Convert.FromHexString("7F7F5BB2D0C6C6B5C4C5A3C6A4B6CCBFE35D7F");
        byte[] linkRecord = LinkRecord();
        NextChatWindow();
        alpha.Send(CommandItemHyperlink, HyperlinkBody(3, linkText));
        byte[] linkFrame = HyperLinkBody(3, 4, "ChatA", linkText, linkRecord);
        Same(alpha.Expect(0, NoticeHyperLink).Body, linkFrame, "物品链接发送者自己收到 0x172 回显（含 148B 记录）");
        Same(beta.Expect(0, NoticeHyperLink).Body, linkFrame, "同场景接收者收到同一条 0x172 链接帧");
        Same(gamma.Expect(0, NoticeHyperLink).Body, linkFrame, "同场景第三个角色也收到 0x172 链接帧");
        delta.NoFrame(400, "物品链接不投其他频道", (0, NoticeHyperLink), (0, NoticeMessage));

        // 记录为空（cnt=0）：退化回 0x0C 纯文本，链接文本原样、不带记录段。
        NextChatWindow();
        alpha.Send(CommandItemHyperlink, HyperlinkBody(3, linkText, withRecord: false));
        byte[] plainLink = LinkMessageBody(3, 4, linkText);
        Same(alpha.Expect(0, NoticeMessage).Body, plainLink, "记录为空时发送者收到 0x0C 纯文本回显");
        Same(beta.Expect(0, NoticeMessage).Body, plainLink, "记录为空时同场景接收者也收到 0x0C 纯文本");
        Same(gamma.Expect(0, NoticeMessage).Body, plainLink, "记录为空时第三个接收者收到 0x0C 纯文本");
        delta.NoFrame(400, "退化纯文本也不投其他频道", (0, NoticeHyperLink), (0, NoticeMessage));

        // 非法 0x01A8（文本长度 0）只记日志丢弃，不断线也不广播；后面的 ④ 继续用同一连接即证明未断。
        byte[] badLink = new byte[12];
        badLink[0] = 3;
        NextChatWindow();
        alpha.Send(CommandItemHyperlink, badLink);
        alpha.NoFrame(400, "非法长度的物品链接不广播", (0, NoticeHyperLink), (0, NoticeMessage));

        // ④ 公会聊天（type 6）：A、B 同公会都收；C 的成员行是选角后追加的（会话快照无公会）必须不收；D 不在会。
        // 选角之后才写入 C 的成员行：成员表已变，但 C 会话的 Selected.Guild 仍为空，投递前锁内复核必须拒绝。
        fixture.Sql("INSERT INTO guild_members(guild_id,character_id,grade,contribution,member_memo,joined_at_unix,last_login_unix) " +
            "VALUES($p0,$p1,0,0,'',1700000000,0)", guildId, (int)profiles["ChatC"].Id);
        NextChatWindow();
        alpha.Send(CommandSendMessage, SendBody(6, 3, 0, "公会聊天"));
        Same(alpha.Expect(0, NoticeMessage).Body, MessageBody(6, 3, "公会聊天"), "公会聊天下行 0x0C 正文");
        Same(beta.Expect(0, NoticeMessage).Body, MessageBody(6, 3, "公会聊天"), "同公会在线成员收到 0x0C");
        gamma.NoFrame(400, "公会成员表在选角后追加时不投旧会话（锁内复核 Selected.Guild）", (0, NoticeMessage));
        delta.NoFrame(400, "公会聊天不投非成员", (0, NoticeMessage));

        // ⑤ 悄悄话（请求 type 0x2E）：arg 是目标角色 id，只投目标；发送者本人与其他角色都不收。
        // 下行显示 type 实测映射为 3（「普通」页签只渲染 3/6/0x37/0x38），不再是请求里的 0x2E。
        NextChatWindow();
        alpha.Send(CommandSendMessage, SendBody(0x2E, 5, profiles["ChatB"].Id, "悄悄话"));
        Same(beta.Expect(0, NoticeMessage).Body, MessageBody(3, 5, "悄悄话"), "悄悄话只投目标角色的 0x0C 正文（显示 type=3）");
        alpha.NoFrame(400, "悄悄话不回显给发送者", (0, NoticeMessage));
        gamma.NoFrame(400, "悄悄话不投旁人", (0, NoticeMessage));
        delta.NoFrame(400, "悄悄话不投其他频道", (0, NoticeMessage));

        // 目标角色不存在：不发任何帧（只记 chat_whisper_offline），不自造失败包。
        NextChatWindow();
        alpha.Send(CommandSendMessage, SendBody(0x2E, 5, 65000, "离线"));
        alpha.NoFrame(400, "悄悄话目标不存在时不发帧", (0, NoticeMessage));

        // ⑥ 节流：窗口清空后同一连接 1 秒内发 6 条，第 6 条被丢弃（其余 5 条各投 A、B、C）。
        NextChatWindow();
        for (int index = 0; index < 6; index++)
            alpha.Send(CommandSendMessage, SendBody(3, 7, 0, "节流" + index));
        for (int index = 0; index < ChatRateLimiter.ShortWindowLimit; index++)
        {
            byte[] expected = MessageBody(3, 7, "节流" + index);
            Same(alpha.Expect(0, NoticeMessage).Body, expected, "节流窗口内发送者按序收到前五条");
            Same(beta.Expect(0, NoticeMessage).Body, expected, "节流窗口内接收者按序收到前五条");
            Same(gamma.Expect(0, NoticeMessage).Body, expected, "节流窗口内第三个接收者按序收到前五条");
        }
        alpha.Barrier(); // 第六条已经由真实请求队列处理；误发聊天会被严格屏障拒绝。
        alpha.NoFrame(500, "第 6 条被节流，不再下行", (0, NoticeMessage));
        beta.NoFrame(500, "第 6 条被节流，接收者也不收", (0, NoticeMessage));
        gamma.NoFrame(500, "第 6 条被节流，第三个接收者也不收", (0, NoticeMessage));

        // ⑦ 超长文本：入站先按 200 字节截断（日志可查），下行正文 = 8 字节头 + 200 字节文本，连接不断。
        NextChatWindow();
        alpha.Send(CommandSendMessage, SendBody(3, 6, 0, new string('x', 250)));
        byte[] longChat = alpha.Expect(0, NoticeMessage).Body;
        That(longChat.Length == 8 + 200 && BinaryPrimitives.ReadUInt32LittleEndian(longChat.AsSpan(4)) == 200 &&
            Text.GetString(longChat, 8, 200) == new string('x', 200),
            "超长聊天下行正文 = 8 字节头 + 200 字节文本（服务端 200 字节上限）");
        beta.Expect(0, NoticeMessage);
        gamma.Expect(0, NoticeMessage);

        // ⑧ 服务器喇叭（type 11）：槽位 68 放的是频道喇叭 37 ⇒ 物品不符拒绝，不扣也不广播。
        int channelMegaphoneBefore = CountSlot(68);
        NextChatWindow();
        alpha.Send(CommandSendMessage, SendBody(11, 0, 68, "错槽"));
        alpha.NoFrame(400, "服务器喇叭槽位物品不匹配时拒绝且不广播", (0, NoticeMegaphone));
        That(CountSlot(68) == channelMegaphoneBefore && channelMegaphoneBefore == 1, "槽位不匹配不扣道具");

        // ⑨ 频道喇叭（type 13）：槽 68 扣 1 个，同频道的 A、B、C 各收一帧 0x76 喇叭行
        // （type 0x0B = Ch. 频道标签，str1=ChatA、str2=频道喇叭）；另一频道的 D 不收。
        NextChatWindow();
        alpha.Send(CommandSendMessage, SendBody(13, 0, 68, "频道喇叭"));
        byte[] channelMegaphone = MegaphoneBody(0x0B, "ChatA", "频道喇叭");
        Same(alpha.Expect(0, NoticeMegaphone).Body, channelMegaphone, "频道喇叭 0x76 正文");
        alpha.Expect(0, NoticeUpdateItemList); // 本人背包差量：槽 68 数量减 1
        Same(beta.Expect(0, NoticeMegaphone).Body, channelMegaphone, "同频道接收者收到 0x76 喇叭行");
        Same(gamma.Expect(0, NoticeMegaphone).Body, channelMegaphone, "同频道接收者（第三个）收到 0x76");
        delta.NoFrame(400, "频道喇叭不投其他频道", (0, NoticeMegaphone));
        That(CountSlot(68) == 0, "频道喇叭扣减后库存为 0");

        // ⑩ 服务器喇叭（type 11）：槽 67 扣 1 个（2→1），全服 4 个在线会话各收一帧 0x76 喇叭行
        // （type 0x0E = D&F 官方标签，客户端会忽略 str1），不弹 0x06AD 公告框。
        NextChatWindow();
        alpha.Send(CommandSendMessage, SendBody(11, 0, 67, "服务器喇叭"));
        byte[] serverMegaphone = MegaphoneBody(0x0E, "ChatA", "服务器喇叭");
        Same(alpha.Expect(0, NoticeMegaphone).Body, serverMegaphone, "服务器喇叭 0x76 正文");
        alpha.Expect(0, NoticeUpdateItemList);
        Same(beta.Expect(0, NoticeMegaphone).Body, serverMegaphone, "服务器喇叭投同频道在线");
        Same(gamma.Expect(0, NoticeMegaphone).Body, serverMegaphone, "服务器喇叭投同频道第三个在线");
        Same(delta.Expect(0, NoticeMegaphone).Body, serverMegaphone, "服务器喇叭投其他频道在线");
        That(CountSlot(67) == 1, "服务器喇叭扣减后库存 2→1");
        delta.NoFrame(300, "喇叭不再发 0x06AD 公告弹框", (0, NoticeServerNotice));
        alpha.Barrier();
        beta.Barrier();
        gamma.Barrier();
        delta.Barrier();

        await fixture.StopAsync();
        var events = fixture.Events();
        int delivered(string kind, int? recipients = null) => events.Count(row => row.GetProperty("Kind").GetString() == "chat_delivered" &&
            row.GetProperty("Detail").GetProperty("Kind").GetString() == kind &&
            (recipients is null || row.GetProperty("Detail").GetProperty("Recipients").GetInt32() == recipients));
        That(delivered("Normal", 1) == 1 && delivered("Normal", 3) == 7 && delivered("Guild", 2) == 1 && delivered("Whisper", 1) == 1,
            "投递结果按类别记录：房间内普通=1、公开城镇普通=3、公会=2、悄悄话=1");
        // 物品链接：成功一次记 item_hyperlink（Opcode=0x172、Kind=Normal、TextBytes=19、Records=1、
        // MarkerPairs=1、Degraded=false、Recipients=3）；记录为空的退化一次记 Opcode=0x0C、Degraded=true；
        // 非法长度一次记 item_hyperlink_rejected。都不写完整文本。
        That(events.Count(row => row.GetProperty("Kind").GetString() == "item_hyperlink" &&
                row.GetProperty("Detail").GetProperty("Kind").GetString() == "Normal" &&
                row.GetProperty("Detail").GetProperty("Opcode").GetInt32() == 0x172 &&
                row.GetProperty("Detail").GetProperty("TextBytes").GetInt32() == 19 &&
                row.GetProperty("Detail").GetProperty("Records").GetInt32() == 1 &&
                row.GetProperty("Detail").GetProperty("MarkerPairs").GetInt32() == 1 &&
                !row.GetProperty("Detail").GetProperty("Degraded").GetBoolean() &&
                row.GetProperty("Detail").GetProperty("Recipients").GetInt32() == 3) == 1,
            "物品链接记 item_hyperlink：Opcode=0x172、Kind=Normal、Records=1、MarkerPairs=1、Degraded=false、Recipients=3");
        That(events.Count(row => row.GetProperty("Kind").GetString() == "item_hyperlink" &&
                row.GetProperty("Detail").GetProperty("Opcode").GetInt32() == 0x0C &&
                row.GetProperty("Detail").GetProperty("MarkerPairs").GetInt32() == 1 &&
                row.GetProperty("Detail").GetProperty("Records").GetInt32() == 0 &&
                row.GetProperty("Detail").GetProperty("Degraded").GetBoolean() &&
                row.GetProperty("Detail").GetProperty("Recipients").GetInt32() == 3) == 1,
            "记录为空时记 item_hyperlink：Opcode=0x0C、MarkerPairs=1、Records=0、Degraded=true、Recipients=3");
        That(events.Count(row => row.GetProperty("Kind").GetString() == "item_hyperlink_rejected") == 1,
            "非法长度的物品链接记 item_hyperlink_rejected");
        That(delivered("ChannelMegaphone", 3) == 1,
            "频道喇叭投递数 = 同频道在线 3（A/B/C），不含另一频道的 D");
        That(delivered("ServerMegaphone", 4) == 1, "服务器喇叭投递数 = 全服在线 4");
        // 喇叭专项日志：记种类/类型字节/两段编码长度，不写正文。
        That(events.Count(row => row.GetProperty("Kind").GetString() == "chat_megaphone" &&
                row.GetProperty("Detail").GetProperty("Variant").GetString() == "server" &&
                row.GetProperty("Detail").GetProperty("Type").GetByte() == 0x0E &&
                row.GetProperty("Detail").GetProperty("SpeakerBytes").GetInt32() == 5 &&
                row.GetProperty("Detail").GetProperty("TextBytes").GetInt32() == 10 &&
                row.GetProperty("Detail").GetProperty("Recipients").GetInt32() == 4) == 1,
            "服务器喇叭记 chat_megaphone：Variant=server、Type=0x0E、SpeakerBytes=5、TextBytes=10、Recipients=4");
        That(events.Count(row => row.GetProperty("Kind").GetString() == "chat_megaphone" &&
                row.GetProperty("Detail").GetProperty("Variant").GetString() == "channel" &&
                row.GetProperty("Detail").GetProperty("Type").GetByte() == 0x0B &&
                row.GetProperty("Detail").GetProperty("SpeakerBytes").GetInt32() == 5 &&
                row.GetProperty("Detail").GetProperty("TextBytes").GetInt32() == 8 &&
                row.GetProperty("Detail").GetProperty("Recipients").GetInt32() == 3) == 1,
            "频道喇叭记 chat_megaphone：Variant=channel、Type=0x0B、TextBytes=8、Recipients=3");
        That(events.All(row => row.GetProperty("Kind").GetString() != "chat_megaphone" ||
                !row.GetProperty("Detail").GetRawText().Contains("服务器喇叭", StringComparison.Ordinal)),
            "chat_megaphone 日志不写正文（只记长度）");
        That(events.Any(row => row.GetProperty("Kind").GetString() == "chat_send" &&
                row.GetProperty("Detail").GetProperty("Type").GetByte() == 3 &&
                row.GetProperty("Detail").GetProperty("TextLength").GetInt32() == 200 &&
                row.GetProperty("Detail").GetProperty("Truncated").GetBoolean()),
            "超长普通聊天按 200 字节截断并记 chat_send(Truncated=true)");
        That(events.Count(row => row.GetProperty("Kind").GetString() == "chat_throttled") == 1,
            "同一连接 1 秒内第 6 条记一次 chat_throttled");
        That(events.Count(row => row.GetProperty("Kind").GetString() == "chat_whisper_offline") == 1,
            "悄悄话目标不存在记 chat_whisper_offline");
        That(events.Count(row => row.GetProperty("Kind").GetString() == "chat_megaphone_rejected") == 1,
            "服务器喇叭槽位物品不符记 chat_megaphone_rejected");
        var normalDeliveries = events.Where(row => row.GetProperty("Kind").GetString() == "chat_delivered" &&
            row.GetProperty("Detail").GetProperty("Kind").GetString() == "Normal").ToArray();
        That(normalDeliveries.Where(row => row.GetProperty("Detail").GetProperty("Recipients").GetInt32() > 1)
                .All(row => row.GetProperty("Detail").GetProperty("LayerFallback").GetBoolean()) &&
            normalDeliveries.Where(row => row.GetProperty("Detail").GetProperty("Recipients").GetInt32() == 1)
                .All(row => !row.GetProperty("Detail").GetProperty("LayerFallback").GetBoolean()),
            "普通聊天投给他人时记录层 id 兜底标记 LayerFallback=true，只投本人时标记为 false");

        Console.WriteLine($"PASS {Count - entry} 聊天线上帧/投递/扣减断言（本组共 {Count - before} 条，其余为入城契约）");

        int CountSlot(short slot) => fixture.Query("SELECT item_core FROM character_inventory_items i JOIN character_inventory_slots s USING(item_uid) " +
            "WHERE s.list_type=0 AND s.slot_index=$p0 AND s.character_id=$p1", slot, profiles["ChatA"].Id) is [var row]
            ? System.Text.Json.JsonSerializer.Deserialize<ItemCore>((string)row[0])!.Count : 0;
    }

    /// <summary>
    /// 选角并消费入城序列到 <c>NOTI 124</c>（ENTER_GAMEWORLD_COMPLETE）为止。
    /// 公会成员选角时会多出公会投影包，共享 <c>TcpPeer.Select</c> 的固定包序表不包含它们；
    /// 本组只依赖 Ready+Town，因此与建会认证组一样按哨兵收尾，不复制也不削弱共享期望表。
    /// </summary>
    static void Enter(TcpPeer peer, ushort slot = 0)
    {
        peer.Send(8, [255, 255, 2]);
        peer.Send(4, Build(writer => writer.Write(slot)));
        for (int guard = 0; guard < 256; guard++)
        {
            var packet = peer.Read();
            if (packet.Kind == 0 && packet.Opcode == 124)
            {
                // 入城完成后固定跟720空正文与721五槽；本组不校验内容，只消费以免污染后续应答。
                peer.ReadWeeklyDungeon();
                return;
            }
        }
        throw new InvalidOperationException("入城序列没有在预期条数内结束");
    }

    /// <summary>
    /// 收尾一个连接：同频道成员入场/离开会在屏障之前插入同屏投影帧（2/3/22/23/24 与虚弱 4），
    /// 公会成员还会补发公会被动属性 487；读到本次屏障 ACK 为止，其它通知仍失败。
    /// </summary>
    static void Settle(TcpPeer peer)
    {
        peer.SendBarrier();
        for (int guard = 0; guard < 64; guard++)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == 1347)
                return;
            // 公会成员的守护石1276按真实账本核验，不当作可丢弃的噪声。
            if (packet is { Kind: 0, Opcode: 487 })
            {
                peer.CheckGuildPassiveBuffs(packet);
                continue;
            }
            That(packet.Kind == 0 && packet.Opcode is 2 or 3 or 4 or 22 or 23 or 24,
                $"入城收尾只允许同屏/状态帧，实际 ({packet.Kind},{packet.Opcode})");
        }
        That(false, "入城收尾未在预期帧数内完成");
    }

    /// <summary>离开私人房间进入公开城镇（原生 CMD 1426），再用 <see cref="Settle"/> 收尾。</summary>
    static void Leave(TcpPeer peer)
    {
        peer.Send(1426);
        Settle(peer);
    }

    /// <summary>
    /// CMD 0x0011 正文 = <c>[u8 type][u16 层 id][u32 arg][u32 textLen][CP936 文本]</c>，固定 11 字节头。
    /// 测试侧自己按 CP936 编码，不调用服务端 Builder。
    /// </summary>
    static byte[] SendBody(byte type, ushort layer, uint arg, string text)
    {
        byte[] bytes = Text.GetBytes(text);
        byte[] body = new byte[11 + bytes.Length];
        body[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), layer);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(3), arg);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), checked((uint)bytes.Length));
        bytes.CopyTo(body, 11);
        return body;
    }

    /// <summary>
    /// <c>CMD 0x01A8</c> 正文（实测 179 字节）：<c>[kind][u16 未定][u32 未定][u32 文本字节数][文本][u8 cnt][cnt × 148]</c>。
    /// 测试侧按实测样本自行编码，不调用服务端 Parser；<paramref name="withRecord"/> 为 false 时 <c>cnt=0</c>，
    /// 用于验证退化到 <c>0x0C</c> 的路径。
    /// </summary>
    static byte[] HyperlinkBody(byte kind, byte[] text, bool withRecord = true)
    {
        int count = withRecord ? 1 : 0;
        byte[] body = new byte[11 + text.Length + 1 + count * 148];
        body[0] = kind;
        body[1] = 3; // 实测头部第 1 字节；1..6 语义未定，测试只复刻样本
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), checked((uint)text.Length));
        text.CopyTo(body, 11);
        body[11 + text.Length] = checked((byte)count);
        if (withRecord)
            LinkRecord().CopyTo(body, 12 + text.Length);
        return body;
    }

    /// <summary>实测 148 字节物品记录：前 15 字节非零（<c>+0x04=0x3070</c>=12400），其余 0。</summary>
    static byte[] LinkRecord()
    {
        byte[] record = new byte[148];
        Convert.FromHexString("FFFFFFFF70300000FEC99A3B00001B").CopyTo(record, 0);
        return record;
    }

    /// <summary>
    /// <c>NOTI 0x172</c> 正文 =
    /// <c>[u8 type][u16 layer][u8 v=0][u32 文本字节数][文本][u8 cnt][cnt × 148][u32 尾串字节数][尾串]</c>；
    /// 尾串是发言人名字（CP936 字节）。测试侧按文档布局自行编码，不调用服务端 Builder。
    /// </summary>
    static byte[] HyperLinkBody(byte type, ushort layer, string speaker, byte[] text, params byte[][] records)
    {
        byte[] tail = Text.GetBytes(speaker);
        byte[] body = new byte[8 + text.Length + 1 + records.Length * 148 + 4 + tail.Length];
        body[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), layer);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), checked((uint)text.Length));
        text.CopyTo(body, 8);
        body[8 + text.Length] = checked((byte)records.Length);
        for (int index = 0; index < records.Length; index++)
            records[index].CopyTo(body, 9 + text.Length + index * 148);
        int tailOffset = 9 + text.Length + records.Length * 148;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(tailOffset), checked((uint)tail.Length));
        tail.CopyTo(body, tailOffset + 4);
        return body;
    }

    /// <summary>
    /// <c>NOTI 0x000C</c> 正文 = <c>[u8 显示 type][u16 层 id][u8 v=0][u32 CP936 字节数][CP936 文本]</c>；
    /// 物品链接的文本段是原始字节，测试侧不重新编码。
    /// </summary>
    static byte[] LinkMessageBody(byte type, ushort layer, byte[] text)
    {
        byte[] body = new byte[8 + text.Length];
        body[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), layer);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), checked((uint)text.Length));
        text.CopyTo(body, 8);
        return body;
    }

    /// <summary>
    /// 测试侧按文档布局自行编码，不调用服务端 Builder。
    /// </summary>
    static byte[] MessageBody(byte type, ushort layer, string text)
    {
        byte[] bytes = Text.GetBytes(text);
        byte[] body = new byte[8 + bytes.Length];
        body[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(1), layer);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (uint)bytes.Length);
        bytes.CopyTo(body, 8);
        return body;
    }

    /// <summary>
    /// <c>NOTI 0x0076</c> 喇叭正文 = <c>[u8 type][u8 B=0][u8 C=0][u8 D=0][u16 E=0]</c> +
    /// 三段 <c>[u32 CP936 字节数][CP936 字节]</c>（发言人 / 正文 / 占位）。
    /// 测试侧按文档布局自行编码，不调用服务端 Builder；<c>str3</c> 用生产占位 <c>X</c>。
    /// </summary>
    static byte[] MegaphoneBody(byte type, string speaker, string text)
    {
        byte[] speakerBytes = Text.GetBytes(speaker);
        byte[] textBytes = Text.GetBytes(text);
        byte[] tagBytes = Text.GetBytes("X");
        byte[] body = new byte[6 + 12 + speakerBytes.Length + textBytes.Length + tagBytes.Length];
        body[0] = type;
        int offset = WriteMegaphoneString(body, 6, speakerBytes);
        offset = WriteMegaphoneString(body, offset, textBytes);
        WriteMegaphoneString(body, offset, tagBytes);
        return body;
    }

    static int WriteMegaphoneString(byte[] body, int offset, byte[] bytes)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(offset), (uint)bytes.Length);
        bytes.CopyTo(body, offset + 4);
        return offset + 4 + bytes.Length;
    }

    /// <summary>逐字节断言，失败信息同时给出实际与期望的十六进制，便于一眼看出差异。</summary>
    static void Same(byte[] actual, byte[] expected, string what) =>
        That(actual.AsSpan().SequenceEqual(expected),
            $"{what}: 实际 {Convert.ToHexString(actual)} ({actual.Length}B), 期望 {Convert.ToHexString(expected)} ({expected.Length}B)");
}
