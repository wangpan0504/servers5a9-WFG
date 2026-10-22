using System.Buffers.Binary;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Parsers;
using System.Text.Json;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class WeaponSkinTests
{
    public static void RunCore()
    {
        int checks = Count;
        That(SkinStorageRequests.Make([4, 2, 0, 0, 0, 0, 35, 0]) ==
            new MakeWeaponSkinRequest(4, 2, new(InventoryListType.Main, 35)), "1599使用原生8B类别、配方、容器和槽位");
        foreach (byte[] invalid in new byte[][] { [], new byte[7], new byte[9], [4, 2, 0, 0, 0, 0, 255, 255], new byte[8] })
            Reject<InvalidDataException>(() => SkinStorageRequests.Make(invalid), "1599拒绝正文边界和无效配方槽位");
        byte[] selected = new byte[44];
        BinaryPrimitives.WriteInt32LittleEndian(selected, 4);
        BinaryPrimitives.WriteInt32LittleEndian(selected.AsSpan(4), 32000);
        That(SkinStorageRequests.Select(selected) == new SelectSkinRequest(4, 32000), "1573保留44B原始结构中的武器类别与单个选择");
        selected[8] = 1;
        Reject<InvalidDataException>(() => SkinStorageRequests.Select(selected), "武器选择不偷偷丢弃额外九个位置");
        Reject<InvalidDataException>(() => SkinStorageRequests.Select(new byte[43]), "选择不接受缺尾正文");
        Bytes(SkinStoragePackets.Owned(new([32000])), [4, 1, 0, 0, 125, 0, 0], "1557武器类别仅有u16数量与每项u32编号");
        Bytes(SkinStoragePackets.Selection(32000), [4, 0, 125, 0, 0], "1558武器选择为u8类别和u32编号");
        Bytes(SkinStoragePackets.Recent(32000), [1, 4, 0, 125, 0, 0], "1559最近加入记录不是另一份完整仓库");
        Bytes(SkinStoragePackets.Recent([new(2,3),new(2,8)]), [2,2,3,0,0,0,2,8,0,0,0],
            "1559批量最近记录完整发送数量和每个类别/编号，不截成第一个");
        Bytes(TcpPeer.Build(w => SkinStoragePackets.WriteUserInfo(w, new([32000], 32000))),
            [1, 4, 1, 0, 125, 0, 0], "Player刷新携带原生武器选择组，不把幻化写进真实武器或装扮核心");
        var ack = SkinStoragePackets.SelectedAck(new(true, 0), 32000);
        That(ack.Length == 45 && ack[0] == 1 && BinaryPrimitives.ReadInt32LittleEndian(ack.AsSpan(1)) == 4 &&
            BinaryPrimitives.ReadInt32LittleEndian(ack.AsSpan(5)) == 32000 && ack.AsSpan(9).IndexOfAnyExcept((byte)0) < 0,
            "1573成功回执是状态字节加44B选择正文，不复用其他版本布局");
        Reject<InvalidDataException>(() => new SkinStorageState([32000, 32000]).Validate(), "所有权拒绝重复编号");
        Reject<InvalidDataException>(() => new SkinStorageState([32000], 32001).Validate(), "未拥有外观不能成为当前选择");
        byte[] core = TcpPeer.Build(w => ItemListProtocolWriter.Write(w, 65,
            new ItemCore(10308800, ItemKind.Consumable, 1) { EnchantCardId = 32000 }));
        That(core.Length == 123 && BinaryPrimitives.ReadInt32LittleEndian(core.AsSpan(14)) == 32000 &&
            BinaryPrimitives.ReadInt32LittleEndian(core.AsSpan(6)) == 1, "外观券来源复用STK的14偏移，数量仍为1，不套用装备品级");

        string[] strings = ["[make enchanted skin]", "[recipe]", "[enchanted result]", "[material]", "fixed",
            "[/material]", "[/recipe]", "[/make enchanted skin]", "LV.1 Test"];
        PvfToken[] tokens = [new(3, 0), new(3, 1), new(0, 7), new(3, 2), new(0, 7001), new(3, 3),
            new(0, 8001), new(6, 4), new(0, 2), new(3, 5), new(3, 6), new(3, 7)];
        var recipes = WeaponSkinFile.ReadRecipes(tokens, id => strings[id]);
        That(recipes is [{ Id: 7, ResultItemId: 7001, Materials: [{ ItemId: 8001, Count: 2 }] }],
            "配方和耗材数量来自PVF，不把当前1/2号和单个模具写死");
        var broken = (PvfToken[])tokens.Clone(); broken[8] = new(0, 0);
        Reject<InvalidDataException>(() => WeaponSkinFile.ReadRecipes(broken, id => strings[id]), "配方拒绝非正材料数量");
        PvfToken[] rows = [new(0, 32000), new(0, 55), new(6, 8), new(0, 35104), new(0, 2)];
        That(WeaponSkinFile.ReadItems(rows, id => strings[id]) is [{ ItemId: 32000, Level: 55, Job: 2 }],
            "幻化目录保持五列，不混淆武器编号、等级、类型和职业");
        Reject<InvalidDataException>(() => WeaponSkinFile.ReadItems([.. rows, .. rows], id => strings[id]), "幻化目录拒绝重复索引");
        Reject<InvalidDataException>(() => WeaponSkinFile.ReadItems(rows[..4], id => strings[id]), "幻化目录拒绝残缺记录");

        var oldState = JsonSerializer.Deserialize<SkinStorageState>("{\"Owned\":[32000],\"Selected\":32000}")!;
        That(oldState.Owned.SequenceEqual([32000]) && oldState.Selected == 32000 && oldState.DamageFonts.IsEmpty &&
            oldState.SelectedDamageFont == 0, "武器JSON缺少字体字段时恢复为空字体仓库，不覆盖武器所有权");
        var fonts = oldState with { DamageFonts = [new(3, 2000000000), new(8, 0)], SelectedDamageFont = 3 };
        fonts.Validate();
        That(!oldState.Same(fonts) && fonts.DamageFontSelection(1999999999) == 3 && fonts.DamageFontSelection(2000000000) == 1,
            "字体参与同一库存快照；到期精确回退原生默认SKN1");
        Reject<InvalidDataException>(() => (fonts with { DamageFonts = [new(3, 0), new(3, 0)] }).Validate(), "字体所有权不重复");
        Reject<InvalidDataException>(() => (fonts with { SelectedDamageFont = 4 }).Validate(), "不能保存未拥有字体选择");
        Bytes(SkinStoragePackets.DamageFonts(fonts), TcpPeer.Build(w =>
        {
            w.Write((byte)2); w.Write((ushort)3); w.Write(1); w.Write(0);
            w.Write(3); w.Write(2000000000); w.Write(8); w.Write(0); w.Write((ushort)0);
        }), "1557字体使用SKN编号与角色绝对期限，并保留空账号列表，不复用旧1243布局");
        Bytes(TcpPeer.Build(w => SkinStoragePackets.WriteUserInfo(w, fonts, 2000000000)),
            [2, 2, 1, 1, 0, 0, 0, 4, 1, 0, 125, 0, 0], "Player的字体与武器分组共存，过期字体不能覆盖成过期选择");
        That(StackableActionRequests.TryUse([90,0,0,0,0,0,0,199,0,0,0,0,0,0,0,0,0,0,0], out var use) &&
            use.Slot == 90 && use.Action == 199 && use.List == 0 && use.Reserved == 0,
            "真实幻化消耗品抓包是19B动作199，不是普通44使用");
        string[] actionStrings = ["[stackable type]", "[waste]", "[action type]", "[add skin storage]", "[/action type]",
            "[action target]", "[character]", "[action expiration info]", "[period]", "[action usable place]", "[village]", "[/action usable place]"];
        PvfToken[] actionTokens = [new(3,0),new(6,1),new(0,0),new(3,2),new(6,3),new(0,3),new(3,4),
            new(3,5),new(6,6),new(3,7),new(6,8),new(0,45),new(3,9),new(6,10),new(3,11)];
        That(SkinStorageFile.ReadAction(1,"test.stk",actionTokens,id => actionStrings[id]) is
            { SkinId: 3, Target: "[character]", Days: 45, ExpirationMode: "[period]", Places: ["[village]"] },
            "入库读取动作SKN3和期限45天，不从旧字体序号2推断新ID");
        var multipleTokens = actionTokens.Take(6).Concat(new[] { new PvfToken(0,8) }).Concat(actionTokens.Skip(6)).ToArray();
        That(SkinStorageFile.ReadAction(1,"test.stk",multipleTokens,id => actionStrings[id])!.SkinIds.SequenceEqual([3,8]),
            "多ID入库声明保留完整原序，归属和期限仍独立解析");
        var batch = new SkinStorageToken(2,3,new(Days:45),[]) { SkinIds=[3,8] };
        var time = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        That(InventorySkinStorageService.TryAddTimed(SkinStorageState.Empty,batch,time,out var granted) &&
            granted.DamageFonts.Select(row=>row.SkinId).SequenceEqual([3,8]) &&
            granted.DamageFonts.All(row=>row.ExpiresAt==time.AddDays(45).ToUnixTimeSeconds()),
            "批量字体在同一快照完整入库，并为每个ID应用实际期限");
        var owned = SkinStorageState.Empty with { DamageFonts=[new(8,0)] };
        That(!InventorySkinStorageService.TryAddTimed(owned,batch,time,out var rejected) && ReferenceEquals(owned,rejected) &&
            owned.DamageFonts.All(row=>row.SkinId!=3), "后项已永久拥有时整批失败，不先写入前项");
        That(InventorySkinStorageService.TryAddTimed(granted,batch,time,out var extended) &&
            extended.DamageFonts.All(row=>row.ExpiresAt==time.AddDays(90).ToUnixTimeSeconds()), "多个已有相对期限一起按原截止续期");
        actionTokens[11] = new(0,0);
        Reject<InvalidDataException>(() => SkinStorageFile.ReadAction(1,"test.stk",actionTokens,id => actionStrings[id]),
            "期限声明不能把无效相对天数默认为永久");
        var cutRequest = SkinStorageRequests.Select(TcpPeer.Build(w =>
        { w.Write(1); w.Write(30000); w.Write(30001); w.Write(new byte[32]); }));
        That(cutRequest.Type == 1 && cutRequest.Ids.SequenceEqual(new[] {30000,30001,0,0,0,0,0,0,0,0}),
            "1573技能插画保留全部十个位置，不套用武器的单项校验");
        Bytes(SkinStoragePackets.SelectedAck(new(true,0),cutRequest),TcpPeer.Build(w =>
        { w.Write((byte)1); w.Write(1); w.Write(30000); w.Write(30001); w.Write(new byte[32]); }),
            "技能插画ACK固定45B并保留多个选择，不只回首个编号");
        Bytes(SkinStoragePackets.CutsceneSelection([30000,30001]),[1,2,0,48,117,0,0,49,117,0,0],
            "1558技能插画是u16数量与编号集合，不是字体的5B选择形状");
        var cutState = oldState with { Cutscenes = [new(30001,2000000000)], SelectedCutscenes = [30001] };
        cutState.Validate();
        That(!cutState.Same(oldState) && cutState.CutsceneSelection(1999999999).SequenceEqual([30001]) &&
            cutState.CutsceneSelection(2000000000).SequenceEqual([30000]), "技能插画保存独立选择，到期回退原生默认30000");
        Reject<InvalidDataException>(() => (cutState with { SelectedCutscenes=[30001,30001] }).Validate(), "技能插画不能重复选择同一皮肤");
        Reject<InvalidDataException>(() => (cutState with { SelectedCutscenes=[30002] }).Validate(), "技能插画不能选择未拥有皮肤");
        Bytes(TcpPeer.Build(w=>SkinStoragePackets.WriteUserInfo(w,cutState,1999999999)),
            [2,1,1,49,117,0,0,4,1,0,125,0,0], "技能插画和武器同在Player选择组中，各自保持类别与完整编号");
        var clone = new InventoryItem(239, new(InventoryListType.Equipment, 10), new(404600048, ItemKind.Avatar, 239))
            { Avatar = new() { ClearAvatarId = 104600021, ClearAvatarSourceUid = 245 } };
        var weapon = new InventoryItem(291, new(InventoryListType.Equipment, 12), new(31440, ItemKind.Equipment, 999999998)
            { Attr = 7, EnchantCardId = 10001, AmplifyType = 3, AmplifyValue = 150 });
        var role = new Character(4, 0, "Layer", 1, new(1, 0, 1, 1), null)
            { Job = 2, Level = 75, Emotion = 0xABCD, Inventory = new([clone, weapon]) { Skins = new([32000], 32000) } };
        foreach (var scene in new[] { ActorSceneState.Town, ActorSceneState.Dungeon })
        {
            byte[] actor = TownPackets.Actor(role, sceneState: scene);
            That(BinaryPrimitives.ReadInt32LittleEndian(actor.AsSpan(115)) == 32000 &&
                BinaryPrimitives.ReadUInt16LittleEndian(actor.AsSpan(119)) == 0xABCD && actor[137] == (byte)scene,
                "城镇及副本Actor在116B头部独立恢复幻化，不覆盖相邻心情或场景字段");
            That(actor[138] == 2 && actor[139] == 10 && BinaryPrimitives.ReadInt32LittleEndian(actor.AsSpan(140)) == 404600048 &&
                BinaryPrimitives.ReadInt32LittleEndian(actor.AsSpan(155)) == 104600021 && actor[164] == 12 &&
                BinaryPrimitives.ReadInt32LittleEndian(actor.AsSpan(165)) == 31440,
                "武器幻化不替换克隆实例、克隆来源或真实武器，三层外观分别交付原生消费者");
            var unselected = role with { Inventory = role.Inventory with { Skins = new([32000]) } };
            byte[] canceled = TownPackets.Actor(unselected, sceneState: scene);
            That(BinaryPrimitives.ReadInt32LittleEndian(canceled.AsSpan(115)) == 0 &&
                actor.AsSpan(0,115).SequenceEqual(canceled.AsSpan(0,115)) && actor.AsSpan(119).SequenceEqual(canceled.AsSpan(119)),
                "取消幻化只清独立选择，不清克隆来源、真实装备及相邻字段");
        }
        Console.WriteLine($"PASS {Count - checks} 武器幻化资源、原生正文、来源核心与所有权边界断言");
    }
}
