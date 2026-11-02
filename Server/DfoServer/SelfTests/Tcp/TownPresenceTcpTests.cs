using DfoServer.Game.Accounts;
using DfoServer.Game.Quests;
using DfoServer.Persistence;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class TownPresenceTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "presence"), gm: true, resources: resources);
        await fixture.StartAsync();
        That((await fixture.CliAsync(null, "--gm", "pc-room", "on")) == "OK {\"premiumPcRoom\":true}",
            "新建会话前全局开启独立网吧身份");
        using (var alpha = Open(fixture, "alpha"))
        using (var beta = Open(fixture, "beta"))
        {
            alpha.Create("PresenceA");
            beta.Create("PresenceB", job: 1);
            var ratingRules = new DfoServer.GameWorld.Pvp.PvpRatingCatalog(resources.Archive);
            var initialRating = new DfoServer.Game.Pvp.PvpRatingCalculator(ratingRules).Initial();
            var terraRating = initialRating with { Experience = ratingRules.Experience[20] - 1, HighestGrade = 20, Initialized = true, PowerTitle = 9 };
            fixture.Sql("INSERT INTO character_pvp_ratings(account,character_id,revision,state_json) VALUES($p0,$p1,1,$p2)",
                "alpha", alpha.Id, System.Text.Json.JsonSerializer.Serialize(terraRating));
            using (var seedOwner = new AccountOwners().Acquire("alpha", () => { }))
                new CharacterRepository(fixture.Database).CommitPvpArenaResult(seedOwner,
                    new(Guid.NewGuid(), Guid.NewGuid(), 296, DfoServer.Game.Pvp.PvpArenaMode.Individual,
                        [new("alpha", alpha.Id, 0, 0, DfoServer.Game.Pvp.PvpArenaOutcome.Win),
                         new("beta", beta.Id, 1, 1, DfoServer.Game.Pvp.PvpArenaOutcome.Lose)], false, 0));
            var alphaActor = alpha.Select(premiumPcRoom: true)[2].Body;
            var betaActor = beta.Select(premiumPcRoom: true)[2].Body;
            AssertBlackDiamondActor(alphaActor, active: false, pcRoom: true);
            AssertBlackDiamondActor(betaActor, active: false, pcRoom: true);
            PvpGrade(alphaActor, 20, 9);
            PvpGrade(betaActor, 0);
            That(fixture.Query("SELECT count(*) FROM character_pvp_rating_claims WHERE receipt_json IS NOT NULL") is [[2L]],
                "首次Actor之前恢复双方真实来源，19升20直接投影最新值，0不变不额外发Actor");
            alpha.Barrier();
            beta.Barrier();
            string beforePcRoom = fixture.Snapshot() + fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account");
            That((await fixture.CliAsync(null, "--gm", "pc-room", "off")) == "OK {\"premiumPcRoom\":false}",
                "全局关闭网吧身份而不是按账号撤销黑钻");
            alpha.CheckPcRoomRefresh(false, blackDiamond: false);
            beta.CheckPcRoomRefresh(false, blackDiamond: false);
            That(fixture.Snapshot() + fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") == beforePcRoom,
                "同一全局开关作用于两个账号但不写任何角色资产或会员期限");
            alpha.Send(1426);
            alpha.SendBarrier();
            alpha.Batch((0, 23), (0, 24), (1, 1347));
            beta.Send(1426);
            beta.SendBarrier();
            var bEntry = beta.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
            alpha.SendBarrier();
            var aEntry = alpha.Batch((0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
            That(aEntry[0].Body[0] == 0 && U16(aEntry[0].Body, 121) == beta.Id && bEntry[2].Body[0] == 0 && U16(bEntry[2].Body, 121) == alpha.Id, "mutual public actor snapshots");
            PvpGrade(aEntry[0].Body, 0);
            PvpGrade(bEntry[2].Body, 20, 9);
            Bytes(aEntry[2].Body, Build(w => { w.Write(beta.Id); w.Write((byte)38); w.Write((byte)0);
                w.Write((ushort)622); w.Write((ushort)196); w.Write((byte)0); w.Write((byte)3); }),
                "远端23携带同一地图与显示位，创建实体而不切换接收者地图");
            Bytes(aEntry[3].Body, Position(beta.Id, 622, 196), "peer entrance position");
            string beforeInteraction = fixture.Snapshot() + fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account");
            alpha.Send(10, Build(w => { w.Write(beta.Id); w.Write((byte)15); w.Write(0u); w.Write((ushort)0); w.Write(0u); }));
            alpha.Expect(0, 7, Build(w => { w.Write(beta.Id); w.Write((byte)15); w.Write(0u); w.Write(ushort.MaxValue); }));
            alpha.Barrier();
            beta.Barrier();
            alpha.Send(8, Build(w => { w.Write(beta.Id); w.Write((byte)3); }));
            var publicActor = alpha.Expect(0, 2).Body;
            var publicInfo = alpha.Expect(0, 2).Body;
            That(publicActor[0] == 0 && U16(publicActor, 121) == beta.Id &&
                publicInfo[0] == 3 && U16(publicInfo, 1) == 1 && U16(publicInfo, 14) == beta.Id,
                "查看资料先安装目标身份再发subtype3，不把目标覆盖到本人subtype1");
            That(U32(publicInfo, 24) == 89 && publicInfo.AsSpan(28, 89).SequenceEqual(beta.PlayerBody.AsSpan(36, 89)) &&
                !publicInfo.AsSpan(28, 89).SequenceEqual(alpha.PlayerBody.AsSpan(36, 89)),
                "不同职业的双方查询使用目标属性，不泄漏或误用请求者资产");
            alpha.Barrier();
            beta.Barrier();
            alpha.Send(10, Build(w => { w.Write(ushort.MaxValue); w.Write((byte)15); w.Write(new byte[10]); }));
            alpha.Send(8, [255, 255, 3]);
            alpha.Send(10, Build(w => { w.Write(beta.Id); w.Write((byte)2); w.Write(new byte[8]); }));
            alpha.Barrier();
            That(fixture.Snapshot() + fixture.Snapshot("SELECT * FROM account_wallets ORDER BY account") == beforeInteraction,
                "菜单、公开查询、离线目标与其他未实现peer模式均不修改双方资产或假回成功");
            beta.Send(12, Convert.FromHexString("000304000000000500000000000007"));
            var party = beta.Batch((0, 2), (0, 153), (0, 11), (0, 9), (0, 11), (0, 153), (0, 26))[3].Body;
            ushort partyId = U16(party, 4);
            beta.Barrier();
            Bytes(alpha.Expect(0, 9).Body, party, "同屏观察者收到新建队伍的相同设置与名册");
            alpha.Send(10, Build(w => { w.Write(beta.Id); w.Write((byte)15); w.Write(new byte[10]); }));
            alpha.Expect(0, 7, Build(w => { w.Write(beta.Id); w.Write((byte)15); w.Write(0u); w.Write(partyId); }));
            beta.Send(13); beta.Expect(1, 13, [1]); beta.Expect(0, 9); beta.Barrier();
            alpha.Expect(0, 9);
            alpha.Send(12, Convert.FromHexString("000304000000000500000000000007"));
            var alphaParty = alpha.Batch((0, 2), (0, 153), (0, 11), (0, 9), (0, 11), (0, 153), (0, 26));
            PvpGrade(alphaParty[0].Body, 20, 9);
            alpha.Barrier(); beta.Expect(0, 9); beta.Barrier();
            alpha.Send(13); alpha.Expect(1, 13, [1]); alpha.Expect(0, 9); alpha.Barrier();
            beta.Expect(0, 9); beta.Barrier();
            alpha.Send(10, Build(w => { w.Write(beta.Id); w.Write((byte)15); w.Write(new byte[10]); }));
            alpha.Expect(0, 7, Build(w => { w.Write(beta.Id); w.Write((byte)15); w.Write(0u); w.Write(ushort.MaxValue); }));
            alpha.Barrier();
            string otherBefore = fixture.Snapshot("SELECT * FROM account_wallets WHERE account='beta'") +
                fixture.Snapshot("SELECT * FROM characters WHERE account='beta'");
            foreach (bool active in new[] { true, false })
            {
                string reply = await fixture.CliAsync(null, "--gm", "account", "alpha", "black-diamond", "PresenceA", active ? "30" : "0");
                That(reply.StartsWith("OK ", StringComparison.Ordinal), "同屏场景按账号开通或撤销黑钻");
                alpha.CheckBlackDiamondRefresh(active);
                beta.SendBarrier();
                var refreshed = beta.Expect(0, 2).Body;
                That(U16(refreshed, 121) == alpha.Id, "黑钻刷新只替换原同屏成员，不更换成员身份或场景");
                AssertBlackDiamondActor(refreshed, active);
                PvpGrade(refreshed, 20, 9);
                beta.Expect(1, 1347, [1]);
                beta.Barrier();
                That(fixture.Snapshot("SELECT * FROM account_wallets WHERE account='beta'") +
                    fixture.Snapshot("SELECT * FROM characters WHERE account='beta'") == otherBefore,
                    "同屏观察不把源账号黑钻或PC房资格写入接收账号");
            }
            alpha.Send(35, Move(633, 196));
            alpha.Barrier();
            beta.SendBarrier();
            beta.Expect(0, 22, Position(alpha.Id, 633, 196, direction: 5));
            beta.Expect(1, 1347, [1]);
            foreach (byte direction in new byte[] { 1, 3, 5 })
            {
                alpha.Send(35, Build(w =>
                {
                    w.Write((ushort)633); w.Write((ushort)196); w.Write(direction); w.Write((ushort)513);
                }));
                alpha.Barrier();
                beta.SendBarrier();
                beta.Expect(0, 22, Position(alpha.Id, 633, 196, direction, 513));
                beta.Expect(1, 1347, [1]);
            }
            string beforeSpeed = fixture.Snapshot();
            foreach (ushort speed in new ushort[] { 0, ushort.MaxValue })
            {
                // 坐标未变且方向0必须沿用5，速度仍按完整u16原样转发。
                alpha.Send(35, Build(w =>
                {
                    w.Write((ushort)633); w.Write((ushort)196); w.Write((byte)0); w.Write(speed);
                }));
                alpha.Barrier();
                beta.SendBarrier();
                beta.Expect(0, 22, Position(alpha.Id, 633, 196, 5, speed));
                beta.Expect(1, 1347, [1]);
                That(fixture.Snapshot() == beforeSpeed, "仅改变速度不保存位置、不增加revision或污染副本准入快照");
            }
            string selectionBefore = fixture.Snapshot();
            alpha.Send(15, new byte[4]);
            var select = alpha.Batch((0, 6), (1, 15), (0, 3), (0, 1536), (0, 27));
            Bytes(select[2].Body, [1, (byte)alpha.Id, (byte)(alpha.Id >> 8), 1], "选图本人从Town状态0变为State1");
            beta.SendBarrier();
            beta.Expect(0, 6, Build(w => w.Write(alpha.Id)));
            beta.Expect(1, 1347, [1]);
            alpha.Send(132);
            alpha.SendBarrier();
            alpha.Batch((1, 132), (0, 3), (0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
            beta.SendBarrier();
            beta.Batch((0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
            That(fixture.Snapshot() == selectionBefore, "选图离场和回城重建双向同屏，不改变两账号资产");
            byte[] motion = Move(634, 196, motion: true);
            motion[4] = 9;
            motion[5] = 1;
            motion[6] = 2;
            alpha.Send(631, motion);
            alpha.Barrier();
            beta.SendBarrier();
            beta.Expect(0, 22, Position(alpha.Id, 634, 196, direction: 5, speed: 513));
            beta.Expect(1, 1347, [1]);
            alpha.Send(35, Move(633, 196));
            alpha.Barrier();
            beta.SendBarrier();
            beta.Expect(0, 22, Position(alpha.Id, 633, 196, direction: 5));
            beta.Expect(1, 1347, [1]);
            alpha.Send(35, Move(633, 196));
            alpha.Send(35, Move(0, 0));
            alpha.Barrier();
            beta.Barrier();
            alpha.Send(36, Area(38, 1, 450, 234, 38, 0));
            alpha.SendBarrier();
            alpha.Batch((0, 23), (0, 24), (0, 1379), (1, 1347));
            beta.SendBarrier();
            beta.Expect(0, 23, Build(w => { w.Write(alpha.Id); w.Write(new byte[] { 38, 1, 194, 1, 234, 0, 5, 3 }); }));
            beta.Expect(1, 1347, [1]);
            alpha.Send(1426);
            alpha.SendBarrier();
            alpha.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
            beta.SendBarrier();
            beta.Batch((0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
            using (var replacement = Open(fixture, "alpha"))
            {
                alpha.Closed();
                beta.SendBarrier();
                beta.Expect(0, 6, Build(w => w.Write(alpha.Id)));
                beta.Expect(1, 1347, [1]);
                PvpGrade(replacement.Select()[2].Body, 20, 9);
                // 旧连接 finally 已结束后，GM 必须仍找到新会话并原地更新其 Selected。
                string gm = await fixture.CliAsync(null, "--gm", "account", "alpha", "level", "PresenceA", "40");
                That(gm.StartsWith("OK ", StringComparison.Ordinal), "GM uses online replacement session");
                replacement.Send(35, Move(461, 234));
                var dailyNotice=replacement.Expect(0,574).Body;
                using(var readOwner=new AccountOwners().Acquire("alpha",()=>{}))
                {
                    var role=new CharacterRepository(fixture.Database).LoadCharacter(readOwner,0);
                    That(role.ActiveMainQuests is [{QuestId:13031,ConditionState:1636}],"接管后GM升40级只为新会话分配当前地区每日");
                    Bytes(dailyNotice,QuestPackets.ActiveList(role.ActiveMainQuests),"新会话每日通知精确匹配持久身份");
                    replacement.Expect(0,356,TownPackets.CompletedQuests(role));
                    replacement.Expect(0,21,QuestPackets.AvailableList(role.Level,QuestEligibility.Candidates(role,resources.Quests)));
                    // 即使历史为空也恢复世界地图显示标记，严格消费21后的完整356。
                    replacement.Expect(0,356,TownPackets.CompletedQuests(role));
                }
                // 接管后GM升级改变表情资格，严格消费本次刷新末尾的完整411，再等待屏障。
                if (fixture.ExpectedChatEmoticons(replacement.Id) is { } expectedChatEmoticons &&
                    !expectedChatEmoticons.SequenceEqual(replacement.ChatEmoticonInfo))
                    replacement.ReadChatEmoticons(replacement.Read());
                replacement.Barrier();
                if (fixture.ExpectedChatEmoticons(replacement.Id) is { } expectedEmoticons)
                    Bytes(replacement.ChatEmoticonInfo, expectedEmoticons, "接管后GM升级刷新真实表情资格与账号指令");
                That(fixture.Snapshot("SELECT level,x FROM characters WHERE account='alpha'") == "[[40,461]]", "movement retains GM update after takeover");
                replacement.Send(1426);
                replacement.SendBarrier();
                replacement.Batch((0, 23), (0, 24), (0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
                beta.SendBarrier();
                beta.Batch((0, 2), (0, 3), (0, 23), (0, 22), (1, 1347));
                replacement.Send(7);
                replacement.SendBarrier();
                replacement.Batch((0, 6), (1, 7), (0, 2));
                replacement.Send(8, [255, 255, 2]);
                beta.SendBarrier();
                beta.Expect(0, 6, Build(w => w.Write(alpha.Id)));
                beta.Expect(1, 1347, [1]);
            }

            beta.Send(3, [0]);
            beta.Expect(1, 3, [1]);
            beta.Closed();
        }

        await fixture.StopAsync();
        Console.WriteLine("PASS C# TCP presence: private/public scenes, snapshots, movement, scene clear, takeover, online GM and return selection");
    }

    internal static void PvpGrade(byte[] body, byte expected, byte powerTitle = 0)
    {
        int offset = checked(130 + (int)U32(body, 123));
        That(body[0] == 0 && body[offset] == expected && body[offset + 1] == powerTitle,
            "Actor分别保留决斗等级与战斗力称谓: " + expected + "/" + powerTitle);
    }

    static byte[] Position(ushort id, ushort x, ushort y, byte direction = 0, ushort speed = 100) => Build(w =>
    {
        w.Write(id);
        w.Write(x);
        w.Write(y);
        w.Write(direction);
        w.Write(speed);
    });
}
