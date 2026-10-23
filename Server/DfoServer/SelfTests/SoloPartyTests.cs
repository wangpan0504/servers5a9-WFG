using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Parties;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class SoloPartyTests
{
    public static void RunCore()
    {
        var request = PartyRequests.Set(Convert.FromHexString("000304000000000500000000000007"));
        That(request == new SoloPartyRequest(0, new(3, null, 4, 0, 5, 0, 0, 0, 0, 7)),
            "实收15B创建请求保留预置名称、容量、目标设置及本版末尾参数7");
        foreach (int width in new[] { 0, 13, 14, 16 })
            Reject<InvalidDataException>(() => PartyRequests.Set(new byte[width]), "不把旧版14B或追加垃圾当作本版完整创建请求");
        Reject<InvalidDataException>(() => PartyRequests.Leave([0]), "退队请求不接受未知尾部");
        var custom = DfoServer.Testing.TcpPeer.Build(w =>
        {
            w.Write((byte)0); w.Write((byte)0); PacketEncoding.WriteString(w, "测试队伍");
            w.Write(Convert.FromHexString("04000000000500000000000007"));
        });
        That(PartyRequests.Set(custom).Settings.Title == "测试队伍", "自定义队伍名按DSTR/CP936解码，不硬编码预置中文文案");
        byte[] expected = new byte[56];
        expected[0] = 1; expected[2] = 15; expected[3] = 39; expected[4] = 232; expected[5] = 253;
        expected[6] = 2; expected[7] = 10; expected[12] = 3; expected[14] = 4; expected[19] = 5;
        expected[36] = 7; expected[37] = 1; expected[39] = 232; expected[40] = 253; expected[42] = 100;
        var wire = new SoloPartyReceipt(new(65000, request.Settings), 1, 1, true);
        Bytes(PartyPackets.Info(wire, new(2, 10)), expected, "本版9包含完整设置、8槽域中的唯一0号成员及附加空结构，不截成旧端格式");
        var selection = PartyPackets.Info(wire with { Party = wire.Party with { Settings = request.Settings with { SelectionId = 0x12345678 } } }, new(2, 10));
        That(selection.AsSpan(15, 4).SequenceEqual(new byte[4]) && selection.AsSpan(30, 4).SequenceEqual(new byte[] { 120, 86, 52, 18 }),
            "目标selectionId按本版模型+116回放，不套用NoPack前部同宽字段");
        byte[] update = expected[..38]; update[9] = 1; update[37] = 0;
        Bytes(PartyPackets.Info(wire with { Created = false }, new(2, 10)), update, "更新kind1保留设置，不重建队伍或改动成员槽");
        byte[] clear = expected[..11]; clear[9] = 3;
        Bytes(PartyPackets.Info(wire with { Removed = true }, new(2, 10)), clear, "清理kind3仍保留本版完整记录前缀");
        string root = Path.Combine(Path.GetTempPath(), "s5a9-solo-party-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var point = new Location(1, 0, 10, 10);
            var map = new TownMap("party.map", [new(new(0, 0, 20, 20), [], [])], [], false, []);
            var towns = new TownCatalog(new() { [(1, 0)] = new(1, 0, 1, [], [], [], point, map, [], []) },
                point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("solo-party", () => { });
            repository.Create(owner, new("SoloParty", point, point));
            var session = new GameSession(repository, towns, owner);
            That(session.SetSoloParty(request) is null, "未选角色不能建队");
            session.Select(0);
            That(session.SetSoloParty(request) is null, "入城尚未就绪不能提前建立客户端队伍上下文");
            session.MarkReady();
            string stored = JsonSerializer.Serialize(repository.LoadCharacter(owner, 0));
            That(session.SetSoloParty(request with { Mode = 1 }) is null && session.SetSoloParty(request, blocked: true) is null,
                "无队伍不能修改，交互占用门禁不被创建入口绕过");
            var created = session.SetSoloParty(request)!;
            That(created.Created && created.Party.LeaderId == session.Selected!.Id && session.CanPublishSoloParty(created) &&
                session.GetState() == UserState.Town && session.Phase == SessionPhase.Town, "建队由原Session持有唯一成员，粗状态和细阶段仍为城镇");
            var repeated = session.SetSoloParty(request)!;
            That(!repeated.Created && ReferenceEquals(repeated.Party, created.Party), "相同请求复用队伍，不重复分配身份");
            var changed = session.SetSoloParty(request with { Mode = 1, Settings = request.Settings with { TitlePreset = 5 } })!;
            That(!session.CanPublishSoloParty(created) && session.CanPublishSoloParty(changed), "设置更新使旧队伍结果失效");
            That(JsonSerializer.Serialize(repository.LoadCharacter(owner, 0)) == stored, "建队和设置更新不写角色或资产数据");
            session.Move(new(11, 10, 0, 0, 0, 0));
            That(session.CanPublishSoloParty(changed), "正常城镇移动不丢失或重建单人队伍");
            session.BeginDungeonSelection();
            That(session.SetSoloParty(request) is null && !session.CanPublishSoloParty(changed), "选图阶段不能修改队伍或发布旧城镇创建结果");
            session.ReturnFromDungeonSelection();
            var removed = session.LeaveSoloParty()!;
            That(session.ReadSoloParty() is null && session.CanPublishSoloParty(removed), "单人退队清理上下文并保留匹配的清理回执");
            session.SetSoloParty(request); session.LeaveSoloParty();
            That(!session.CanPublishSoloParty(removed), "新队伍再次退出后，旧清理回执不能因状态同为空而重新生效");
            var beforeReturn = session.SetSoloParty(request)!;
            session.ReturnToRoster(); session.Select(0); session.MarkReady();
            That(session.ReadSoloParty() is null && !session.CanPublishSoloParty(beforeReturn), "重选不继承旧队伍或旧选角结果");
            string finalStored = JsonSerializer.Serialize(repository.LoadCharacter(owner, 0));
            var final = session.SetSoloParty(request)!;
            using var nextOwner = owners.Acquire(owner.Account, () => { });
            Reject<InvalidOperationException>(() => session.CanPublishSoloParty(final), "旧generation不能发布队伍信息");
            var next = new GameSession(repository, towns, nextOwner);
            next.Select(0); next.MarkReady(); next.SetSoloParty(request);
            session.End();
            That(next.ReadSoloParty() is not null, "旧连接结束只清理自己的Session，不删除新会话队伍");
            next.End();
            var actual = repository.LoadCharacter(nextOwner, 0);
            That(JsonSerializer.Serialize(actual) == finalStored, "队伍创建、接管和收尾均不额外写角色revision或资产");
        }
        finally { Directory.Delete(root, true); }
    }
}
