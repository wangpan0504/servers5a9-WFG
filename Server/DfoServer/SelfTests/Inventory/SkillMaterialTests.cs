using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class SkillMaterialTests
{
    public static void Run()
    {
        int start = Count;
        byte[] body = Convert.FromHexString("1000000010001A0A080210E60218DD172005200001");
        var request = InventoryRequests.Delete(body);
        That(request.List == InventoryListType.Main && request.Flag == 0 && request.ClientState == 1 &&
            request.Items is [var entry] && entry == new InventoryDeleteEntry(2, 358, 3037, 5), "真实无色消耗正文与独立尾状态完整保留");
        That(InventoryRequests.Delete(body[..^1]).ClientState is null, "原有不带尾状态的protobuf仍有效");
        body[^1] = 255;
        That(request.ClientState == 1 && InventoryRequests.Delete(body).ClientState == 255, "未知状态只保留，不解释为消费资格");
        for (int length = 0; length < 20; length++)
            Reject(() => InventoryRequests.Delete(body[..length]), "材料正文截断" + length);
        Reject(() => InventoryRequests.Delete([.. body, 0]), "不是一个字节尾状态的超长正文拒绝");
        Reject(() => InventoryRequests.Sort(body), "删除尾状态不能放宽整理长度校验");
        var inventory = InventorySnapshot.Empty with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.ClearCube, 20) };
        var spent = InventoryDeleteService.PlanBatch(inventory, request);
        That(spent.Accepted && spent.Plan!.After.AccountWallet.Get(CurrencyKind.ClearCube) == 15 &&
            spent.Plan.After.Items.IsEmpty && inventory.AccountWallet.Get(CurrencyKind.ClearCube) == 20, "无色足额扣钱包，不建立实体、不污染原快照");
        Bytes(InventoryPackets.Delete(request, spent, spent.Plan!.After),
            Convert.FromHexString("010D0000000800120708E602100F18021800"), "ACK18返回槽358真实剩余15与type2");
        foreach (var invalid in new InventoryDeleteEntry[] { new(2, 358, 3037, 0), new(2, 358, 3037, -1),
            new(2, 358, 3037, 21), new(2, 357, 3037, 1), new(2, 358, 3036, 1), new(0, 358, 3037, 1), new(3, 358, 3037, 1) })
            That(!InventoryDeleteService.PlanBatch(inventory, request with { Items = [invalid] }).Accepted, "错误数量、钱包映射或未实现类型不扣无色");
        That(!InventoryDeleteService.PlanBatch(inventory, request with { List = InventoryListType.PersonalCargo }).Accepted &&
            !InventoryDeleteService.PlanBatch(inventory, request with { Items = [request.Items[0], request.Items[0]] }).Accepted,
            "消费容器和重复槽拒绝");
        var material = new InventoryItem(1, new(InventoryListType.Main, 121), new(50000, ItemKind.Material, 3));
        var mixed = inventory with { Items = [material] };
        That(!InventoryDeleteService.PlanBatch(mixed, request with { Items = [request.Items[0], new(2, 121, 3, 4)] }).Accepted &&
            mixed.AccountWallet.Get(CurrencyKind.ClearCube) == 20 && mixed.Items[0] == material, "后一项不足整批拒绝，不部分扣钱包");
        var ordinary = InventoryRequests.Delete(Convert.FromHexString("10000000081210001A0808001041181420032000"));
        That(ordinary.ClientState is null && ordinary.Items.Single() == new InventoryDeleteEntry(0, 65, 20, 3), "原普通丢弃协议未改变");
        var after = InventoryDeleteService.PlanBatch(spent.Plan.After, request);
        That(after.Accepted && after.Plan!.After.AccountWallet.Get(CurrencyKind.ClearCube) == 10, "相同正文的下一次施法按本次消耗扣除");
        Console.WriteLine($"PASS {Count - start} 技能材料正文、钱包、原子规划与ACK断言");
    }
    static void Reject(Action action, string name)
    {
        try { action(); }
        catch (InvalidDataException) { That(true, name); return; }
        throw new Exception("FAIL: " + name);
    }
}
