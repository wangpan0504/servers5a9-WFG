using DfoServer.Game.Chat;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class ChatEmoticonTests
{
    public static void Run()
    {
        var catalog = new ChatEmoticonCatalog([new(1,0,"COMMON"),new(42,10,"LEVEL"),new(60,5,"WEDDING"),new(10,1,"CASH",88)]);
        That(catalog.Eligible(new ChatEmoticonEligibility(9,false,0,0,0,0)).Select(row=>row.Id).SequenceEqual(new ushort[]{1}),
            "表情资格不因配置指令而开放");
        var key = new ChatEmoticonEligibility(10,true,5,0,1UL<<(88-64),0);
        That(catalog.Eligible(key).Select(row=>row.Id).SequenceEqual(new ushort[]{1,42,60,10}), "等级、婚姻和账号契约使用各自资格");
        HashSet<ushort> allowed = [1,42];
        string native = "A\u007f\u001e " + (char)42 + "\u001fB\u001e " + (char)1 + "\u001f";
        That(ChatEmoticonRules.Prepare(native,allowed,200,out bool changed)==native && !changed,"原生内嵌表情逐字保留");
        That(ChatEmoticonRules.Prepare(native,allowed,5,out changed)=="A" && changed,"截断不能切开第一个带前缀的表情");
        string denied = "A\u007f\u001e " + (char)60 + "\u001fB";
        That(ChatEmoticonRules.Prepare(denied,allowed,200,out changed)=="AB" && changed,"未获得资格只移除表情，不丢弃普通正文");
        That(ChatEmoticonRules.Prepare(new string('\u001e',60000)+"TEST",allowed,200,out changed)=="TEST" && changed,
            "连续畸形起始标记按固定窗口推进，不向后重复搜索整条消息");
        byte[] bytes = ChatText.Encoding.GetBytes(native);
        Bytes(ChatEmoticonRules.FilterBytes(bytes,allowed),bytes,"链接混合正文的合法表情保留原字节");
        Bytes(ChatEmoticonRules.FilterBytes(ChatText.Encoding.GetBytes(denied),allowed),"AB"u8,"链接混合正文移除未授权标记");
        byte[] firstDenied=[0x7f,0x1e,0x20,60,0x1f,0x1e,0x20,42,0x1f];
        byte[] retained=[0x7f,0x1e,0x20,42,0x1f];
        Bytes(ChatEmoticonRules.FilterBytes(firstDenied,allowed),retained,"首项被拒绝后仍保留后续表情的整段格式标志");
        That(ChatEmoticonRules.Prepare(ChatText.Encoding.GetString(firstDenied),allowed,200,out _) == ChatText.Encoding.GetString(retained),
            "文本聊天和原字节聊天采用相同的整段格式标志规则");
        byte[] raw = [0xff,0x7f,0x1e,0x20,42,0x1f,0x81,0x40];
        Bytes(ChatEmoticonRules.FilterBytes(raw,allowed),raw,"非表情原字节不经CP936往返");
        byte[] onlyEmote=[0x7f,0x1e,0x20,42,0x1f];
        var plain=ItemLinkPackets.Design(ChatKind.Normal,17,"Tester",onlyEmote,[]);
        That(!plain.HyperLink && !plain.MarkersStripped && plain.MarkerPairs==0,"段首7F是格式标志，纯表情不是未闭合物品链接");
        Bytes(plain.Body.AsSpan(8,onlyEmote.Length),onlyEmote,"退化普通聊天仍保留表情格式标志");
        byte[] mixed=[0x7f,0x1e,0x20,42,0x1f,0x7f,(byte)'[',(byte)'W',(byte)']',0x7f];
        var hyperlink=ItemLinkPackets.Design(ChatKind.Normal,17,"Tester",mixed,new byte[148]);
        That(hyperlink.HyperLink && hyperlink.MarkerPairs==1,"内嵌表情不占用真实物品链接记录");
        Bytes(DfoServer.Network.Builders.Inventory.SkinStoragePackets.Emoticon(17,410000046),new byte[]{17,0,0xae,0x1a,0x70,0x18},"1535严格为角色u16和皮肤u32");
        byte[] tail = ChatPackets.Message(ChatKind.Normal,17,"hi","Tester");
        Bytes(tail,Convert.FromHexString("0311000002000000686906000000546573746572"),"普通聊天保留客户端必读发言人尾串");
        var contracts = PremiumContractCatalog.Parse("[type] 88 [target item] 2660544 [term] 1 [item] 2660543 [term] 3 [/item] " +
            "[type] 92 [target item] 2660546 [term] 1 [item] 2660545 [term] 3 [/item]");
        foreach (int id in new[]{2660543,2660544,2660545,2660546})
            That(contracts.TryAcquisitionContract(id,out var definition) && definition.DurationSeconds==(id is 2660543 or 2660545 ? 259200:86400),
                "表情礼包沿PVF父子期限获得即生效");
    }
}
