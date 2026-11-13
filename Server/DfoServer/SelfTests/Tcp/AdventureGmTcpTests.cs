using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Network.Management;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class AdventureGmTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        AdventureGmTests.RunCore();
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "adventure-gm"), gm: true, resources: resources);
        await fixture.StartAsync();
        new AuthenticationService(new AuthenticationRepository(fixture.Database)).Create("gm-adventure", NativeLoginWire.Digest);
        var repo = new CharacterRepository(fixture.Database, adventureCatalog: resources.Adventure);
        using (var owner = new AccountOwners().Acquire("gm-adventure", () => { }))
            repo.InitializeAdventure(owner, resources.Adventure, DateTimeOffset.UtcNow);
        JsonElement Command(string tail)
        {
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "account gm-adventure adventure " + tail, out string response) && response.StartsWith("OK "), "冒险团真实GM提交成功：" + tail);
            return JsonDocument.Parse(response[3..]).RootElement.Clone();
        }
        var state = Command("status");
        That(state.GetProperty("maximumLevel").GetInt32() == 35 && state.GetProperty("scope").GetString() == "account-adventure", "真实资源35级上限与账号范围凭据");
        state = Command("level 35 " + state.GetProperty("revision").GetInt64());
        state = Command("name 全新冒险团 " + state.GetProperty("revision").GetInt64());
        using (var peer = Open(fixture, "gm-adventure"))
        {
            peer.Create("GmGroupHero"); peer.Select();
            int size = checked((int)U32(peer.AdventureName));
            That(DecodeName(peer.AdventureName, 4, size) == "全新冒险团" && U32(peer.AdventureInfo, 18) == 35 && U32(peer.AdventureRefresh, 6) == 35,
                "下次登录原生1375、1343、1349读取实际GM名称和等级");
            That(DevelopmentGmClient.TryExecute(fixture.Options.GmPort, "account gm-adventure adventure level 1 " + state.GetProperty("revision").GetInt64(), out string denied) && denied.StartsWith("ERR "), "整个账号在线时拒绝改写，不断开玩家或刷新半份状态");
            var query = Command("status");
            That(query.GetProperty("online").GetBoolean() && query.GetProperty("level").GetInt32() == 35, "在线只读查询保持账号等级");
        }
    }
}
