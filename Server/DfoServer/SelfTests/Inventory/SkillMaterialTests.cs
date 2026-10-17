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
        Bytes(InventoryPackets.Delete(request, spent),
            Convert.FromHexString("010D0000000800120708E602100518021800"), "ACK18返回本次消耗5，不能把剩余15再次扣除");
        var preserved = InventoryDeleteService.PlanBatch(inventory,request,preserveMaterials: true);
        That(preserved.Accepted && ReferenceEquals(preserved.Plan!.Before,preserved.Plan.After) && !preserved.Plan.Changed,
            "修炼场足额无色保持原钱包快照，不生成写入");
        Bytes(InventoryPackets.Delete(request,preserved),Convert.FromHexString("010D0000000800120708E602100018021800"),
            "修炼场ACK18删除量为0，客户端不减本地无色");
        var single = request with { Items = [request.Items[0] with { Count = 1 }] };
        var large = inventory with { AccountWallet = AccountWallet.Empty.Set(CurrencyKind.ClearCube,1830) };
        var consumed = InventoryDeleteService.PlanBatch(large,single);
        Bytes(InventoryPackets.Delete(single,consumed),Convert.FromHexString("010D0000000800120708E602100118021800"),
            "余额1830消费1回删除量1，不发送余额1829让客户端只剩1");
        That(consumed.Plan!.After.AccountWallet.Get(CurrencyKind.ClearCube) == 1829,"修正应答不改变实际扣款");
        foreach (var invalid in new InventoryDeleteEntry[] { new(2, 358, 3037, 0), new(2, 358, 3037, -1),
            new(2, 358, 3037, 21), new(2, 357, 3037, 1), new(2, 358, 3036, 1), new(0, 358, 3037, 1), new(4, 358, 3037, 1) })
            That(!InventoryDeleteService.PlanBatch(inventory, request with { Items = [invalid] }).Accepted, "错误数量、钱包映射或未实现类型不扣无色");
        That(!InventoryDeleteService.PlanBatch(inventory, request with { List = InventoryListType.PersonalCargo }).Accepted &&
            !InventoryDeleteService.PlanBatch(inventory, request with { Items = [request.Items[0], request.Items[0]] }).Accepted,
            "消费容器和重复槽拒绝");
        int[] cubeIds = [3033, 3034, 3035, 3036, 3037, 3262];
        for (int index = 0; index < cubeIds.Length; index++)
        {
            var kind = CurrencyWallet.RewardKind(cubeIds[index])!.Value;
            var before = inventory with { AccountWallet = inventory.AccountWallet.Set(kind, 2) };
            var scripted = request with { Items = [new(3, (short)(354 + index), cubeIds[index], 1)] };
            var result = InventoryDeleteService.PlanBatch(before, scripted);
            That(result.Accepted && result.Plan!.Reason == "delete_equipment_material" &&
                result.Plan.After.AccountWallet.Get(kind) == 1 && before.AccountWallet.Get(kind) == 2,
                "普通模式六种晶块的装备脚本材料消费沿账号钱包，只减一");
            var response = InventoryPackets.Delete(scripted, result);
            That(response[^3] == 3 && response[13] == 1, "装备脚本ACK保留type3和本次消耗量，不写余额");
            That(!InventoryDeleteService.PlanBatch(before, scripted with { Items = [scripted.Items[0] with { Count = 3 }] }).Accepted,
                "装备脚本材料不足拒绝，不能按整堆归一少扣");
            var free = InventoryDeleteService.PlanBatch(before,scripted,preserveMaterials: true);
            That(free.Accepted && !free.Plan!.Changed && free.Plan.After.AccountWallet.Get(kind) == 2 &&
                InventoryPackets.Delete(scripted,free)[13] == 0,"免耗玩法的六种装备脚本晶块均回删除量0，原钱包不变");
        }
        var material = new InventoryItem(1, new(InventoryListType.Main, 121), new(50000, ItemKind.Material, 3));
        var mixed = inventory with { Items = [material] };
        var entityRequest = request with { Items = [new(2,121,3,1)] };
        var entitySpent = InventoryDeleteService.PlanBatch(mixed,entityRequest);
        That(entitySpent.Accepted && entitySpent.Plan!.After.Items[0].Core.Count == 2,
            "普通模式实体技能材料正常消耗");
        Bytes(InventoryPackets.Delete(entityRequest,entitySpent),Convert.FromHexString("010C000000080012060879100118021800"),
            "普通模式实体材料ACK18回真实删除1");
        var entityKept = InventoryDeleteService.PlanBatch(mixed,entityRequest,preserveMaterials: true);
        That(entityKept.Accepted && !entityKept.Plan!.Changed && entityKept.Plan.After.Items[0].Core.Count == 3,
            "免耗玩法实体技能材料同样保持原数量");
        var colored = inventory with { AccountWallet = inventory.AccountWallet.Set(CurrencyKind.BlueCube,8) };
        var coloredRequest = request with { Items = [new(2,357,3036,5)] };
        var coloredSpent = InventoryDeleteService.PlanBatch(colored,coloredRequest,preserveMaterials: true);
        That(coloredSpent.Accepted && coloredSpent.Plan!.After.AccountWallet.Get(CurrencyKind.BlueCube) == 8 &&
            coloredSpent.Plan.After.AccountWallet.Get(CurrencyKind.ClearCube) == 20,
            "免耗玩法无色与有色晶体均保留，不再仅豁免无色");
        Bytes(InventoryPackets.Delete(coloredRequest,coloredSpent),Convert.FromHexString("010D0000000800120708E502100018021800"),
            "免耗玩法有色晶体ACK18回删除量0");
        var mixedRequest = request with { Items = [request.Items[0],new(0,121,3,1)] };
        var mixedPreserved = InventoryDeleteService.PlanBatch(mixed,mixedRequest,preserveMaterials: true);
        That(mixedPreserved.Accepted && mixedPreserved.Plan!.After.AccountWallet.Get(CurrencyKind.ClearCube) == 20 &&
            mixedPreserved.Plan.After.Items[0].Core.Count == 2,
            "免耗玩法混合包只豁免脚本材料，真实丢弃仍扣原实体");
        That(!InventoryDeleteService.PlanBatch(mixed, request with { Items = [request.Items[0], new(2, 121, 3, 4)] }).Accepted &&
            mixed.AccountWallet.Get(CurrencyKind.ClearCube) == 20 && mixed.Items[0] == material, "后一项不足整批拒绝，不部分扣钱包");
        var ordinary = InventoryRequests.Delete(Convert.FromHexString("10000000081210001A0808001041181420032000"));
        That(ordinary.ClientState is null && ordinary.Items.Single() == new InventoryDeleteEntry(0, 65, 20, 3), "原普通丢弃协议未改变");
        foreach (int quantity in new[] { 0,9 })
        {
            var discard = ordinary with { Items = [new(0,121,3,quantity)] };
            Bytes(InventoryPackets.Delete(discard,InventoryDeleteService.PlanBatch(mixed,discard)),
                Convert.FromHexString("010C000000080012060879100318001800"),"整堆丢弃回实际删除3，不能回请求0或超额9");
        }
        var after = InventoryDeleteService.PlanBatch(spent.Plan!.After, request);
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
