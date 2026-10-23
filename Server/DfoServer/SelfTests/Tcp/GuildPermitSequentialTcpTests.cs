using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

/// <summary>
/// 顺序登录的线上契约：创建者与被邀请方先后上线。
///
/// peer A（账号 10000 的角色）发起 `CMD 0x2E5` 时目标**不在线**：回 `01`（不再回失败码 3），也不推
/// `NOTI 0x2AE[0x2D]`；A 断开后 peer B（目标账号）才上线入城，收到完整邀请
/// → 回 `CMD 0x2E6 [01]` → 服务端在**创建者离线**的情况下用离线账号租约提交建会：B 收 `CMD 0x2E6 [01]`，
/// 独立读库确认公会行（会长 = A 的角色）、成员行两行（会长 + B）、A 角色金币 −300000、B 不扣费。
///
/// 另外两条分支同样按「B 不在线时发起」走一遍：拒绝（`0x2E6 [00]`）不建会不扣钱；**关窗（`0x2E7`）
/// 不是取消**——实机流程就是「邀请完关掉客户端、换账号上线认可」，因此关窗之后 B 入城**仍然**收到弹窗、
/// 接受即建会，只有创建窗口草稿被清掉（关窗后同一角色直接发 `0x2E5` 因无草稿回 34）。
/// 取消能力保留在申请过期（600 秒）与将来的正式取消协议上。
/// </summary>
internal static class GuildPermitSequentialTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-permit-sequential"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var creatorOwner = owners.Acquire("10000", () => { }))
        using (var inviteeOwner = owners.Acquire("20001", () => { }))
        {
            // 建会花费来自 PVF（[guild create cost] = 300000）：创建者必须带够金币上线。
            var starter = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0));
            repository.Create(creatorOwner, new("SeqMaster", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = starter with { Gold = 1_000_000 }
            });
            repository.Create(inviteeOwner, new("SeqInvitee", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter
            });
        }
        await fixture.StartAsync();
        long creatorId = Convert.ToInt64(fixture.Query("SELECT id FROM characters WHERE name='SeqMaster'")[0][0]);
        long inviteeId = Convert.ToInt64(fixture.Query("SELECT id FROM characters WHERE name='SeqInvitee'")[0][0]);
        long inviteeGold = Convert.ToInt64(fixture.Query("SELECT gold FROM characters WHERE id=$p0", inviteeId)[0][0]);

        // ① 拒绝分支：A 发起邀请时目标不在线，A 下线后 B 才入城，B 收到弹窗后点拒绝。
        using (var creator = Open(fixture))
        {
            Enter(creator);
            Invite(creator, 2, "SeqInvitee");
            // 屏障包确保邀请应答之后**没有**别的包：目标不在线不再走「对方正在挑战地下城」分支。
            Barriered(creator);
        }
        await WaitReleasedAsync(fixture, "10000");
        using (var invitee = Open(fixture, "20001"))
        {
            Enter(invitee, pendingPermit: true);
            invitee.Expect(0, 0x02AD, [.. Text("顺序公会"), 2, .. Text("SeqMaster"), .. Text("顺序介绍")]);
            invitee.ReadWeeklyDungeon();
            invitee.Send(0x02E6, [0x00]);
            invitee.Expect(1, 0x02E6, [0x00, 0x22, 0x00]);
        }
        That(fixture.Query("SELECT count(*) FROM guilds") is [[0L]] &&
            fixture.Query("SELECT gold FROM characters WHERE id=$p0", creatorId) is [[1_000_000L]] &&
            fixture.Query("SELECT count(*) FROM guild_members") is [[0L]],
            "★ 拒绝分支不建会也不扣钱（没有公会行与成员行、创建者金币未变）");

        // ② 关窗分支（A 邀请后关掉创建窗口，CMD 0x2E7，空包体）：**关窗不是取消**。
        //    草稿被清掉（关窗后直接再发 0x2E5 因无草稿回 34），但登记表里的申请仍在：
        //    B 之后入城照旧收到补投的弹窗，接受即由离线账号租约建会——与实机操作顺序完全一致。
        using (var creator = Open(fixture))
        {
            Enter(creator);
            Invite(creator, 5, "SeqInvitee");
            creator.Send(0x02E7, []);                      // 客户端关窗：无处理器，服务端不回包
            creator.Send(0x02E5, Permit(5, "SeqInvitee")); // 草稿已被清掉 → 34（这条路径不动已登记的申请）
            creator.Expect(1, 0x02E5, [0x00, 0x22, 0x00]);
            Barriered(creator);                            // 0x2E7 本身没有应答包
        }
        await WaitReleasedAsync(fixture, "10000");
        using (var invitee = Open(fixture, "20001"))
        {
            Enter(invitee, pendingPermit: true);
            invitee.Expect(0, 0x02AD, [.. Text("顺序公会"), 5, .. Text("SeqMaster"), .. Text("顺序介绍")]);
            invitee.ReadWeeklyDungeon();
            invitee.Send(0x02E6, [0x01]);
            invitee.Expect(1, 0x02E6, [0x01]);
            GuildPermitTests.Entered(invitee.Expect(0, 58).Body, checked((ushort)inviteeId), "顺序公会", "SeqInvitee");
            var joinedActor = invitee.Expect(0, 2).Body;
            GuildPermitTests.Information(invitee.Expect(0, 70).Body, "顺序公会", "SeqMaster", 2, joinedActor[3]);
        }

        That(fixture.Query("SELECT name||':'||address||':'||master_character_id FROM guilds") is [[string guild]] &&
            guild == "顺序公会:顺序公会:" + creatorId,
            "★ 独立读库：关窗后接受认证仍建会成功（名称来自邀请时的草稿、地址回退为公会名，会长是创建者）");
        long guildId = Convert.ToInt64(fixture.Query("SELECT guild_id FROM guilds")[0][0]);
        That(fixture.Query("SELECT guild_id||':'||character_id||':'||grade FROM guild_members ORDER BY character_id") is
            [[string master], [string joiner]] &&
            master == guildId + ":" + creatorId + ":0" && joiner == guildId + ":" + inviteeId + ":0",
            "★ 会长与被邀请角色的成员行同事务写入（两行、职位都是 0）");
        That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", creatorId) is [[700_000L]],
            "★ 独立读库：离线扣费只扣一次（1,000,000 − 300,000）");
        That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", inviteeId) is [[long after]] && after == inviteeGold,
            "被邀请方不承担建会花费");
        // 申请取出即删：建会之后再次入城不再有弹窗（屏障包证明入城序列后没有多余通知）。
        using (var invitee = Open(fixture, "20001"))
        {
            Enter(invitee);
            Barriered(invitee);
        }

        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]],
            "顺序模式建会全过程数据库与外键完整");
        Console.WriteLine($"PASS {Count - before} 公会顺序认证（离线邀请、关窗不撤销、入城补投、离线建会）TCP 断言");
    }

    /// <summary>
    /// 选角并消费整段入城序列，读到入城最后一条 `NOTI 124 ENTER_GAMEWORLD_COMPLETE` 为止；
    /// 认证补投的 `NOTI 0x2AD` 在该序列之后，由调用方按需读取（没有申请时用 <see cref="Barriered"/> 断言没有多余包）。
    /// 与 `guild-permit` 组同一手法：不依赖共享夹具的期望表。
    /// 入城完成后固定跟720空正文与721五槽；有补投弹窗时0x2AD先到，调用方读弹窗后再调用
    /// <see cref="TcpPeer.ReadWeeklyDungeon"/>。
    /// </summary>
    static void Enter(TcpPeer peer, ushort slot = 0, bool pendingPermit = false)
    {
        peer.Send(8, [255, 255, 2]);                                 // 客户端选角前请求名册
        peer.Send(4, Build(writer => writer.Write(slot)));           // (1,4) SELECT_CHARACTER
        for (int guard = 0; guard < 256; guard++)
        {
            var packet = peer.Read();
            if (packet.Kind == 0 && packet.Opcode == 124)            // (0,124)：EnterAsync 的最后一条
            {
                if (!pendingPermit) peer.ReadWeeklyDungeon();
                return;
            }
        }
        throw new InvalidOperationException("入城序列没有在预期条数内结束");
    }

    /// <summary>真实流程的邀请：先名称查重（0x9C）与介绍校验（0x2E3），客户端随后自动发 0x2E5。</summary>
    static void Invite(TcpPeer peer, byte region, string name)
    {
        peer.Send(0x009C, Text("顺序公会"));
        peer.Expect(1, 0x009C, [0x01]);
        peer.Send(0x02E3, Text("顺序介绍"));
        peer.Expect(1, 0x02E3, [0x01]);
        peer.Send(0x02E5, Permit(region, name));
        peer.Expect(1, 0x02E5, [0x01]);
    }

    /// <summary>屏障包（CMD 1347 回 `01`）：读到的下一条必须是屏障应答，据此断言此前没有多余通知。</summary>
    static void Barriered(TcpPeer peer)
    {
        peer.SendBarrier();
        peer.Expect(1, 1347, [1]);
    }

    /// <summary>
    /// 等创建者的连接**真的收尾**：离线建会要取账号租约，而合同是 `Reject` 策略不抢线，租约在连接收尾时才释放。
    /// 依据是服务端自己写的 `authenticated` / `disconnected` 事件（`BufferedLogFile` 每秒落盘，因此轮询等待）；
    /// 超时即断言失败，不把竞态藏进「多等一会」。
    /// </summary>
    static async Task WaitReleasedAsync(TcpTestFixture fixture, string account)
    {
        var deadline = Stopwatch.StartNew();
        int connection = 0;
        while (connection == 0 && deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            connection = LastConnection(fixture, "authenticated", account);
            if (connection == 0)
                await Task.Delay(50);
        }
        That(connection != 0, "服务端记录了创建者的认证连接（等待它收尾：" + DescribeEvents(fixture) + "）");
        while (deadline.Elapsed < TimeSpan.FromSeconds(30) && !Disconnected(fixture, connection))
            await Task.Delay(50);
        That(Disconnected(fixture, connection), "★ 创建者连接已收尾：账号租约已释放（离线建会不抢线的前提）");
    }

    /// <summary>
    /// 读服务端事件日志（`server.jsonl`）。写入方把文件开在 `FileShare.Read`（只允许别人读它），
    /// 因此读**运行中**的日志必须自己以 `FileShare.ReadWrite` 打开，不能用只共享读的 `File.ReadLines`；
    /// 日志每秒落盘，半行按「还没写全」跳过，不影响等待。
    /// </summary>
    static List<JsonElement> Events(TcpTestFixture fixture)
    {
        var events = new List<JsonElement>();
        string path = Path.Combine(fixture.Options.LogDirectory, "server.jsonl");
        try
        {
            if (!File.Exists(path))
                return events;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    events.Add(document.RootElement.Clone());
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        return events;
    }

    /// <summary>等待失败时的自诊断：已经落盘的事件种类（写入方每秒落盘，进度可据此判断）。</summary>
    static string DescribeEvents(TcpTestFixture fixture)
    {
        var events = Events(fixture);
        return events.Count + " 条事件[" + string.Join(',', events.Select(item => item.TryGetProperty("Kind", out var kind) ? kind.GetString() : "?").Distinct().Take(10)) + "]";
    }

    static int LastConnection(TcpTestFixture fixture, string kind, string account)
    {
        int found = 0;
        foreach (var item in Events(fixture))
            if (item.TryGetProperty("Kind", out var name) && name.GetString() == kind &&
                item.TryGetProperty("Detail", out var detail) &&
                detail.TryGetProperty("Account", out var owner) && owner.GetString() == account &&
                detail.TryGetProperty("Connection", out var connection))
                found = connection.GetInt32();
        return found;
    }

    static bool Disconnected(TcpTestFixture fixture, int connection) => Events(fixture).Any(item =>
        item.TryGetProperty("Kind", out var name) && name.GetString() == "disconnected" &&
        item.TryGetProperty("Detail", out var detail) &&
        detail.TryGetProperty("Connection", out var value) && value.GetInt32() == connection);

    /// <summary>请求正文 = u32 字节长度 + CP936 文本（查重 / 介绍校验）。</summary>
    static byte[] Text(string text)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(text);
        byte[] body = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, bytes.Length);
        bytes.CopyTo(body, 4);
        return body;
    }

    /// <summary>请求正文 = u8 大区序号 + u32 名称长度 + CP936 角色名（实机样本 `02 05 00 00 00 "admin"`）。</summary>
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
