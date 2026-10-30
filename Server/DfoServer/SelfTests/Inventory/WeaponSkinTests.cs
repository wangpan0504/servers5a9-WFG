using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers.Inventory;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using System.Text.Json;
using PvfLib;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing.Inventory;

internal static class WeaponSkinTests
{
    public static void RunCore()
    {
        That(SkinStorageRequests.Emoticon([46, 27, 112, 24]) == 410000174,
            "1559发送的是完整小端i32皮肤编号，不是方向或预设槽位");
        foreach (int length in new[] { 0, 1, 3, 5, 8 })
            Reject<InvalidDataException>(() => SkinStorageRequests.Emoticon(new byte[length]), "表情发送拒绝非4字节正文");
        Bytes(SkinStoragePackets.Emoticon(0x1234, 410000174), [0x34, 0x12, 46, 27, 112, 24],
            "1535播放正文只有成员u16与皮肤i32，不带ACK、数量或方向");
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

        var appearanceWeapon = new ItemTemplate(1, ItemKind.Equipment, 12, 95, 0, 1, 0, 3, 95, "[trade]", 1 << 2, 65535, "Test gun");
        var novice = new Character(1, 0, "SkinLow", 0, new(1, 0, 1, 1), null) { Job = 2, Level = 1, Skills = [] };
        That(!InventoryMoveService.WearLevelSatisfied(novice, appearanceWeapon, 1800000000) &&
            !InventoryMoveService.RequiredSkillSatisfied(novice, appearanceWeapon) &&
            InventorySkinStorageService.SupportsCharacter(novice, appearanceWeapon),
            "外观仅匹配职业，等级与技能不满足普通武器佩戴时仍有外观资格");
        That(!InventorySkinStorageService.SupportsCharacter(novice with { Job = 0 }, appearanceWeapon) &&
            !InventorySkinStorageService.SupportsCharacter(novice, appearanceWeapon with { EquipmentSlot = 14 }) &&
            !InventorySkinStorageService.SupportsCharacter(novice, appearanceWeapon with { Kind = ItemKind.Avatar }),
            "职业不符、非武器和武器装扮不能借普通武器外观资格放行");

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
        Bytes(SkinStoragePackets.DamageFonts(fonts, SkinStorageState.Empty), TcpPeer.Build(w =>
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
        // 组队头像框（类别0）与即时表情（类别3）：类别编号来自客户端0429E5F0的SKN[type]映射。
        var frameRequest = SkinStorageRequests.Select(TcpPeer.Build(w =>
        { w.Write(0); w.Write(420000002); w.Write(new byte[36]); }));
        That(frameRequest.Type == 0 && frameRequest.SkinId == 420000002 && frameRequest.AdditionalIds.All(id => id == 0),
            "1573组队头像框沿用单编号形状，类别0不与武器4混淆");
        var emoteRequest = SkinStorageRequests.Select(TcpPeer.Build(w =>
        { w.Write(3); w.Write(410000001); w.Write(410000002); w.Write(410000003); w.Write(410000004); w.Write(new byte[24]); }));
        That(emoteRequest.Type == 3 && emoteRequest.Ids.Take(4).SequenceEqual([410000001, 410000002, 410000003, 410000004]) &&
            emoteRequest.Ids.Skip(4).All(id => id == 0), "1573即时表情保留4个栏位，其余位置留空");
        var frameState = oldState with { PartyFrames = [new(420000001, 2000000000)], SelectedPartyFrame = 420000001 };
        frameState.Validate();
        That(frameState.PartyFrameSelection(1999999999) == 420000001 && frameState.PartyFrameSelection(2000000000) == 20000 &&
            !oldState.Same(frameState), "组队头像框到期回退原生默认20000，并参与同一库存快照");
        Reject<InvalidDataException>(() => (frameState with { PartyFrames = [new(20000, 0)] }).Validate(), "原生默认头像框不能作为所有权保存");
        Reject<InvalidDataException>(() => (frameState with { SelectedPartyFrame = 420000002 }).Validate(), "未拥有头像框不能成为当前选择");
        Bytes(SkinStoragePackets.PartyFrames(frameState, SkinStorageState.Empty), TcpPeer.Build(w =>
        {
            w.Write((byte)0); w.Write((ushort)2); w.Write(20000); w.Write(0);
            w.Write(420000001); w.Write(2000000000); w.Write((ushort)0);
        }), "1557头像框补原生默认20000条目并保留空账号期限列表");
        var emoteState = oldState with { Emoticons = [new(410000001, 0)], SelectedEmoticons = [410000001, 0, 0, 0] };
        emoteState.Validate();
        That(emoteState.EmoticonSelection(1).SequenceEqual([410000001, 0, 0, 0]) && !oldState.Same(emoteState),
            "即时表情按4个栏位保存，空栏位保持0且参与快照比较");
        Reject<InvalidDataException>(() => (emoteState with { SelectedEmoticons = [410000001, 410000001] }).Validate(),
            "即时表情不能重复选择同一皮肤");
        Reject<InvalidDataException>(() => (emoteState with { SelectedEmoticons = [410000002, 0, 0, 0] }).Validate(),
            "即时表情不能选择未拥有皮肤");
        Reject<InvalidDataException>(() => (emoteState with { SelectedEmoticons = [410000001, 0, 0, 0, 0] }).Validate(),
            "即时表情最多4个栏位");
        Bytes(SkinStoragePackets.Emoticons(emoteState, SkinStorageState.Empty), TcpPeer.Build(w =>
        {
            w.Write((byte)3); w.Write((ushort)1); w.Write(410000001); w.Write(0); w.Write((ushort)0);
        }), "1557即时表情没有原生默认条目，数量等于实际拥有项");
        Bytes(SkinStoragePackets.EmoticonSelection([410000001, 410000002]), TcpPeer.Build(w =>
        { w.Write((byte)3); w.Write(410000001); w.Write(410000002); w.Write(0); w.Write(0); }),
            "1558即时表情固定4个u32且没有数量字段");
        Bytes(TcpPeer.Build(w => SkinStoragePackets.WriteUserInfo(w, frameState, 1999999999)),
            TcpPeer.Build(w =>
            {
                w.Write((byte)2); w.Write((byte)0); w.Write((byte)1); w.Write(420000001);
                w.Write((byte)4); w.Write((byte)1); w.Write(32000);
            }), "Player选择组同时携带头像框与武器，各自保持类别编号");
        Bytes(TcpPeer.Build(w => SkinStoragePackets.WriteUserInfo(w, emoteState, 1)),
            TcpPeer.Build(w =>
            {
                w.Write((byte)2); w.Write((byte)3); w.Write((byte)4);
                w.Write(410000001); w.Write(0); w.Write(0); w.Write(0);
                w.Write((byte)4); w.Write((byte)1); w.Write(32000);
            }), "Player选择组携带即时表情4栏与武器，类别互不覆盖");
        var frameToken = new SkinStorageToken(0, 420000001, new(Days: 30), []);
        That(InventorySkinStorageService.TryAddTimed(SkinStorageState.Empty, frameToken, time, out var frameGranted) &&
            frameGranted.PartyFrames is [{ SkinId: 420000001 }], "头像框动作按类别0单独入库");
        var emoteToken = new SkinStorageToken(3, 410000001, new(Days: 30), []) { SkinIds = [410000001, 410000002] };
        That(InventorySkinStorageService.TryAddTimed(SkinStorageState.Empty, emoteToken, time, out var emoteGranted) &&
            emoteGranted.Emoticons.Select(row => row.SkinId).SequenceEqual([410000001, 410000002]),
            "即时表情动作按类别3批量入库");
        That(!InventorySkinStorageService.TryAddTimed(SkinStorageState.Empty,
                new SkinStorageToken(0, 20000, new(Days: 30), []), time, out _), "原生默认头像框不能由动作入库");
        // 账号皮肤仓只保存所有权，选中项始终在角色存档；不带account的调用正是修复前的唯一路径，
        // 对账号条目做选择时必然抛异常，因此这里先断言旧行为会拒绝，再断言并集校验通过。
        var accountOwned = SkinStorageState.Empty with
        {
            DamageFonts = [new(9, 0)], Cutscenes = [new(31001, 0)],
            PartyFrames = [new(420000002, 0)], Emoticons = [new(410000005, 0)]
        };
        var accountSelected = SkinStorageState.Empty with
        {
            SelectedDamageFont = 9, SelectedCutscenes = [31001], SelectedPartyFrame = 420000002,
            SelectedEmoticons = [410000005, 0, 0, 0]
        };
        accountOwned.Validate();
        Reject<InvalidDataException>(() => accountSelected.Validate(), "账号仓条目的选择不在角色自身列表，单参数Validate拒绝修复前的存档");
        accountSelected.Validate(accountOwned);
        Reject<InvalidDataException>(() => (accountSelected with { SelectedPartyFrame = 420000003 }).Validate(accountOwned),
            "并集校验仍拒绝角色和账号都没有的头像框");
        Bytes(SkinStoragePackets.PartyFrames(accountSelected, accountOwned), TcpPeer.Build(w =>
        {
            w.Write((byte)0); w.Write((ushort)1); w.Write(20000); w.Write(0);
            w.Write((ushort)1); w.Write(420000002); w.Write(0);
        }), "1557头像框账号期限列表携带账号条目，角色列表为空不影响选择");
        Bytes(SkinStoragePackets.Emoticons(accountSelected, accountOwned), TcpPeer.Build(w =>
        { w.Write((byte)3); w.Write((ushort)0); w.Write((ushort)1); w.Write(410000005); w.Write(0); }),
            "1557即时表情账号条目来自账号皮肤仓，角色列表保持空");

        var clone = new InventoryItem(239, new(InventoryListType.Equipment, 10), new(404600048, ItemKind.Avatar, 239))
            { Avatar = new() { ClearAvatarId = 104600021, ClearAvatarSourceUid = 245 } };
        var weapon = new InventoryItem(291, new(InventoryListType.Equipment, 12), new(31440, ItemKind.Equipment, 999999998)
            { Attr = 7, EnchantCardId = 10001, AmplifyType = 3, AmplifyValue = 150 });
        var role = new Character(4, 0, "Layer", 1, new(1, 0, 1, 1), null)
            { Job = 2, Level = 75, Emotion = 0xABCD, Inventory = new([clone, weapon]) { Skins = new([32000], 32000) } };
        foreach (var scene in new[] { ActorSceneState.Town, ActorSceneState.Dungeon })
        {
            int Frame(SkinStorageState own, SkinStorageState account, long now) => BinaryPrimitives.ReadInt32LittleEndian(
                TownPackets.Actor(role with { Inventory = role.Inventory with { Skins = own, AccountSkins = account } },
                    0, utcSeconds: now, sceneState: scene).AsSpan(73));
            That(Frame(SkinStorageState.Empty, SkinStorageState.Empty, 1999999999) == 20000 &&
                Frame(frameState, SkinStorageState.Empty, 1999999999) == 420000001 &&
                Frame(frameState, SkinStorageState.Empty, 2000000000) == 20000 &&
                Frame(accountSelected, accountOwned, 1999999999) == 420000002,
                "真实组队Actor状态+68使用角色与账号并集，默认和到期回退20000，城镇副本一致");
            byte[] actor = TownPackets.Actor(role, 0, sceneState: scene);
            That(BinaryPrimitives.ReadInt32LittleEndian(actor.AsSpan(115)) == 32000 &&
                BinaryPrimitives.ReadUInt16LittleEndian(actor.AsSpan(119)) == 0xABCD && actor[137] == (byte)scene,
                "城镇及副本Actor在116B头部独立恢复幻化，不覆盖相邻心情或场景字段");
            That(actor[138] == 2 && actor[139] == 10 && BinaryPrimitives.ReadInt32LittleEndian(actor.AsSpan(140)) == 404600048 &&
                BinaryPrimitives.ReadInt32LittleEndian(actor.AsSpan(155)) == 104600021 && actor[164] == 12 &&
                BinaryPrimitives.ReadInt32LittleEndian(actor.AsSpan(165)) == 31440,
                "武器幻化不替换克隆实例、克隆来源或真实武器，三层外观分别交付原生消费者");
            var unselected = role with { Inventory = role.Inventory with { Skins = new([32000]) } };
            byte[] canceled = TownPackets.Actor(unselected, 0, sceneState: scene);
            That(BinaryPrimitives.ReadInt32LittleEndian(canceled.AsSpan(115)) == 0 &&
                actor.AsSpan(0,115).SequenceEqual(canceled.AsSpan(0,115)) && actor.AsSpan(119).SequenceEqual(canceled.AsSpan(119)),
                "取消幻化只清独立选择，不清克隆来源、真实装备及相邻字段");
        }
        byte[] roster = CharacterPackets.Roster([CharacterRosterEntry.FromCharacter(role)]);
        var rosterUnselected = role with { Inventory = role.Inventory with { Skins = new([32000]) } };
        byte[] bareRoster = CharacterPackets.Roster([CharacterRosterEntry.FromCharacter(rosterUnselected)]);
        That(roster.Length == bareRoster.Length, "名册包长不因武器幻化变化");
        That(RosterWeaponSkin(roster) == 32000 && RosterWeaponSkin(bareRoster) == 0 &&
            RosterWeaponSkin(roster) == BinaryPrimitives.ReadInt32LittleEndian(TownPackets.Actor(role, 0).AsSpan(115)),
            "名册 tail[42..45] 与入城 Actor 正文115 写同一 Selected；未选外观保持0");
        byte[] withTail = RosterTail(roster);
        byte[] bareTail = RosterTail(bareRoster);
        That(Enumerable.Range(0, 48).All(i => i is >= CharacterPackets.WeaponSkinTailOffset and < CharacterPackets.WeaponSkinTailOffset + 4 ||
                withTail[i] == bareTail[i]), "幻化只改 tail[42..45]，资格位、光环使能与克隆称号不变");
        That(withTail[46] == 1 && bareTail[46] == 1 && CharacterPackets.WeaponSkinTailOffset == 42, "光环使能不被幻化字段挤掉");
        RosterPriority(role, clone, weapon);
        RosterRepository();
        Console.WriteLine($"PASS {Count - checks} 武器幻化资源、原生正文、来源核心与所有权边界断言");
    }

    static void RosterPriority(Character role, InventoryItem clone, InventoryItem weapon)
    {
        var ordinary = clone with { Core = clone.Core with { ItemId = 104600021 }, Avatar = new() { Color1 = 3, Color2 = 7 } };
        var emptyClone = clone with { Avatar = new() };
        var missing = ordinary with { Core = ordinary.Core with { ItemId = int.MaxValue } };
        var items = new ItemCatalog(null!, new Dictionary<int, string>(), new Dictionary<int, string>(), 0);
        var template = new ItemTemplate(ordinary.Core.ItemId, ItemKind.Avatar, 10, 1, 0, 1, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "Weapon avatar");
        items.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [ordinary.Core.ItemId] = template,
            [clone.Core.ItemId] = template with { Id = clone.Core.ItemId, CloneAvatar = true }
        });
        var cases = new (InventoryItem[] Items, int Skin, int Visible)[]
        {
            ([clone, weapon], 32000, 32000),
            ([clone, weapon], 0, 104600021),
            ([ordinary, weapon], 0, 104600021),
            ([weapon], 0, 31440),
            ([weapon], 32000, 32000),
            ([ordinary], 0, 104600021),
            ([emptyClone, weapon], 0, 31440),
            ([emptyClone, weapon], 32000, 32000),
            ([emptyClone], 0, 0),
            ([emptyClone], 32000, 32000),
            ([missing, weapon], 0, 31440),
            ([missing, weapon], 32000, 32000),
            ([missing], 0, 0),
            ([], 32000, 32000),
            ([], 0, 0)
        };
        foreach (var sample in cases)
        {
            var character = role with { Inventory = new([.. sample.Items]) { Skins = new([32000], sample.Skin) } };
            var before = character.Inventory;
            var row = CharacterRosterEntry.FromCharacter(character);
            byte[] actor = TownPackets.Actor(character, 0);
            byte[] body = CharacterPackets.Roster([row], items: items);
            using var reader = new BinaryReader(new MemoryStream(body));
            reader.ReadBytes(15);
            reader.ReadUInt16();
            reader.ReadBytes(reader.ReadInt32());
            reader.ReadBytes(15);
            int count = reader.ReadByte();
            var received = new Dictionary<byte, (int Id, int Marker)>();
            for (int i = 0; i < count; i++)
            {
                byte slot = reader.ReadByte();
                int id = reader.ReadInt32();
                That(reader.ReadInt32() == 4, "名册显示条目保留4B染色扩展");
                reader.ReadInt32();
                reader.ReadBytes(3);
                int marker = reader.ReadInt32();
                reader.ReadBytes(5);
                That(received.TryAdd(slot, (id, marker)), "名册不重复投影同一穿戴槽");
            }
            That(sample.Visible == 0 ? !received.ContainsKey(12) : received[12].Id == sample.Visible,
                "选角动画读取槽12时按幻化、克隆来源或武器装扮、真实武器回退");
            if (sample.Skin != 0 || sample.Visible == ordinary.Core.ItemId)
                That(received[12].Marker == 0, "已解析的选角显示编号不再次套用克隆来源");
            That(reader.ReadBytes(48).Length == 48 && reader.ReadBytes(10).SequenceEqual(new byte[] { 1,0,0,0,0,0,0,0,0,0 }) &&
                reader.BaseStream.Position == body.Length, "有效外观替换保持名册尾部及边界");
            That(character.Inventory == before && row.Equipment.SequenceEqual(sample.Items.OrderBy(item => item.Position.Index)) &&
                actor.SequenceEqual(TownPackets.Actor(character, 0)), "选角仅改wire视图，不改库存、原始名册或城镇Actor");
        }
    }

    internal static int RosterWeaponSkin(byte[] body, int rowIndex = 0) => ReadRosterRow(body, rowIndex).Skin;
    internal static int RosterWeaponAppearance(byte[] body, int rowIndex = 0) => ReadRosterRow(body, rowIndex).Weapon;

    static (int Skin, int Weapon) ReadRosterRow(byte[] body, int rowIndex)
    {
        using var reader = new BinaryReader(new MemoryStream(body));
        reader.ReadBytes(15);
        for (int row = 0; row < body[1]; row++)
        {
            reader.ReadUInt16();
            reader.ReadBytes(reader.ReadInt32());
            reader.ReadBytes(15);
            int items = reader.ReadByte();
            int weapon = 0;
            for (int i = 0; i < items; i++)
            {
                byte slot = reader.ReadByte();
                int item = reader.ReadInt32();
                if (slot == 12) weapon = item;
                reader.ReadBytes(reader.ReadInt32());
                reader.ReadBytes(3);
                int clear = reader.ReadInt32();
                if (slot == 9 && clear > 0)
                    reader.ReadBytes(reader.ReadInt32());
                reader.ReadInt32();
                reader.ReadByte();
            }
            byte[] tail = reader.ReadBytes(48);
            if (row == rowIndex)
                return (BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(CharacterPackets.WeaponSkinTailOffset)), weapon);
        }
        throw new InvalidDataException("名册行不存在");
    }

    static byte[] RosterTail(byte[] body) => body.AsSpan(body.Length - 10 - 48, 48).ToArray();

    static void RosterRepository()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-weapon-skin-roster-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new CharacterRepository(Path.Combine(root, "game.db"));
            var owners = new AccountOwners();
            using var owner = owners.Acquire("weapon-skin-roster", () => { });
            var point = new Location(1, 0, 10, 10);
            var skinned = repository.Create(owner, new("SkinRoster", point, point) { Level = 75 });
            var bare = repository.Create(owner, new("BareRoster", point, point) { Level = 75 });
            using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "game.db") + ";Pooling=False"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE characters SET weapon_skin_state=$state WHERE id=$id";
                command.Parameters.AddWithValue("$state", JsonSerializer.Serialize(new SkinStorageState([32000], 32000)));
                command.Parameters.AddWithValue("$id", skinned.Id);
                command.ExecuteNonQuery();
            }
            var roster = repository.ListRoster(owner);
            That(roster.Single(row => row.Id == skinned.Id).WeaponSkin == 32000 &&
                roster.Single(row => row.Id == bare.Id).WeaponSkin == 0, "名册查询读取 weapon_skin_state.Selected，无幻化角色为0");
            var full = repository.List(owner);
            That(CharacterPackets.Roster(roster).SequenceEqual(CharacterPackets.Roster(full.Select(CharacterRosterEntry.FromCharacter).ToArray())),
                "轻量名册与完整角色的幻化字段一致");
            That(RosterWeaponSkin(CharacterPackets.Roster(roster), 0) == 32000 &&
                RosterWeaponSkin(CharacterPackets.Roster(roster), 1) == 0, "按槽位顺序第一行有幻化、第二行保持0");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
