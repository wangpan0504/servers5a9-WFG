using DfoServer.Game.Auction;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders.Inventory;

namespace DfoServer.Network.Builders;

// 拍卖行（AUCTION）线格式。
//
// 已确认：NOTI 183 AUCTION_NOTIFY_AUCTION_SERVICE（C2S 183 是另一个语义），
//   body = serviceType:u8 + openState:u8；服务开启时发 serviceType 0x00 与 0x01 两条。
//   依据：架构参考项目 ServerS4A21 的 Network/Builders/Auction/AuctionServiceNotificationBuilder.cs，
//   并已在本仓库客户端实机确认「两条都发才能打开拍卖行 UI」。
// 已确认：搜索应答一律走 CMD 路径、opcode 186（187 请求也用 186 应答），记录 168B、道具块在 [43] 宽 83B，
//   块内为物品核心布局且 value/attr 反排；依据见 Docs/协议/拍卖行.md。
internal static class AuctionPackets
{
    public const byte ServiceTypeZero = 0x00;
    public const byte ServiceTypeOne = 0x01;
    public const byte OpenState = 0x01;

    // —— 搜索应答记录合同（匹配客户端实机定案，不再作为候选）——
    /// <summary>一条记录的固定宽度。写成 176/208 时客户端第二行会从第一条记录中间开始读，只渲染第一行。</summary>
    public const int RecordSize = 168;
    /// <summary>道具块在记录内的偏移。</summary>
    public const int ItemOffset = 43;
    /// <summary>道具块宽度。它是「物品核心」布局，不是普通物品条目的前 83 字节。</summary>
    public const int ItemBlockSize = 83;
    const int ListingIdOffset = 0;
    const int ExpiresOffset = 4;
    const int PriceOffset = 9;
    const int BuyoutOffset = 13;
    // 道具块内字段：物品种类 1B、itemId 4B、attr(强化) 1B、value(数量/品级 seed) 4B、耐久 2B、封印 1B，其余为 0。
    // 与普通物品条目相比，value 与 attr 的位置互换了，这就是「反排」的实际含义。
    const int BlockKindOffset = 0;
    const int BlockItemIdOffset = 1;
    const int BlockAttrOffset = 5;
    const int BlockValueOffset = 6;
    const int BlockDurabilityOffset = 10;
    const int BlockSealOffset = 12;
    /// <summary>
    /// 道具块 <c>+6</c>（= 记录 <c>[49..52]</c>）在 itemId 合法时的**下限**：见下方「丢行过滤器」注释。
    /// 取 1 只是为了非 0，不是「最小合法数量」的业务断言。
    /// </summary>
    const int MinimumBlockValue = 1;
    // 普通物品条目（ItemListProtocolWriter.Write）的字段偏移：slot:u16@0、itemId@2、value@6、attr@10、耐久@11、封印@13。
    const int EntryItemIdOffset = 2;
    const int EntryValueOffset = 6;
    const int EntryAttrOffset = 10;
    const int EntryDurabilityOffset = 11;
    const int EntrySealOffset = 13;
    /// <summary>读取条目快照字段所需的最小长度（最大用到封印 @13）。</summary>
    const int EntryMinimumLength = 14;
    const int EpicExchangeTrailerSize = 12;
    const uint EpicExchangeTrailerMagic = 0x4F585045; // 持久BLOB专用ASCII "EPXO"，不属于客户端条目。

    public static byte[] ServiceNotice(byte serviceType)
    {
        if (serviceType is not (ServiceTypeZero or ServiceTypeOne))
            throw new InvalidDataException("183 仅有两条已确认的 serviceType(0/1)");
        return [serviceType, OpenState];
    }

    /// <summary>
    /// 搜索应答（合同编码）：
    /// <c>flag:u8(=1) + mode:u8(回显请求首字节) + 匹配总件数:i32 + 本批条数:u16 + N×168B 记录</c>。
    ///
    /// 第三个字段是**匹配总数**而不是本批条数：客户端按每页 10 条算总页数决定「下一页」是否可用。
    /// <paramref name="mode"/> 默认回显请求首字节：客户端 186 回调 <c>0x023524E0</c> 按这个字节分流到
    /// **两个不同的窗口类型与两套填充函数**（<c>1</c> → GetWindow(7) + <c>0x26FF7D0</c>；
    /// <c>0</c> → GetWindow(6) + <c>0x371A6C0</c>），回错窗口 ⇒ 记录被灌进另一类窗口的面板。
    /// 开发诊断 <c>--auction-probe bidmode=</c> 会刻意覆盖它做窗口对照，正常路径一律回显。
    /// 未知的剩余时间字段不写（写参考实现的 [156] 界面恒显示「0 小时」，位置与前置门闸都还没确认），
    /// 记录里除已确认字段外全部保持 0。
    /// </summary>
    public static byte[] SearchReply(byte mode, int totalCount, IReadOnlyList<AuctionListing> listings,
        AuctionSearchRecordProbe? recordProbe = null, long now = 0) => Build(writer =>
    {
        writer.Write((byte)1);
        writer.Write(mode);
        writer.Write(totalCount);
        writer.Write(checked((ushort)Math.Min(listings.Count, ushort.MaxValue)));
        foreach (var listing in listings)
            writer.Write(EncodeListing(listing, recordProbe, now));
    });

    /// <summary>
    /// 一条 168B 挂牌记录。
    /// <paramref name="recordProbe"/> 只服务开发诊断：它在合同编码**之后**改写记录里的**一个**字段
    /// （候选见 <see cref="AuctionSearchRecordProbe"/>），对照候选下逐字节等于合同编码。
    ///
    /// 道具块 <c>+6</c>（= 记录 <c>[49..52]</c>）由 <see cref="ItemBlock(ReadOnlySpan{byte}, int)"/> 兜住非 0
    /// （见该类里的「丢行过滤器」注释）：itemId 合法而该字段 ≤ 0 时客户端会**整行静默丢弃**。
    /// 那个过滤器**只在 186（搜索）路径上被证实存在**（188/189 没有），本方法只用于搜索应答。
    /// </summary>
    public static byte[] EncodeListing(AuctionListing listing, AuctionSearchRecordProbe? recordProbe = null, long now = 0)
    {
        var record = new byte[RecordSize];
        WriteInt32(record, ListingIdOffset, listing.ListingId);
        WriteInt32(record, ExpiresOffset, checked((int)listing.ExpiresAtUnix));
        WriteInt32(record, PriceOffset, listing.DisplayPrice);
        // 一口价**总额**（单价 × 数量）：客户端 `cmp [row+0x365], -1` 判「有无一口价」，并把该字段当总额
        // 用于「比一口价高」校验与购买确认框。纯竞拍必须原样写 -1（写 0 会被判成「出价高于一口价」）。
        WriteInt32(record, BuyoutOffset, BuyoutTotal(listing));
        Array.Copy(ItemBlock(listing.ItemEntry, listing.ItemKind), 0, record, ItemOffset, ItemBlockSize);
        recordProbe?.Apply(record, listing, now);
        return record;
    }

    /// <summary>
    /// 道具块（83B）：从物品条目的字节快照逐字段取值后写入。
    ///
    /// **不能把普通条目直接截断/移位**：条目以 <c>slot:u16</c> 开头、拍卖行块以物品种类开头，
    /// 且块内 value 与 attr 的顺序与条目相反。位置错一位客户端读到的就是无效 itemId，
    /// 结果是「总数正常显示、列表一行都没有」——行被静默丢弃，不会报错。
    /// </summary>
    public static byte[] ItemBlock(ReadOnlySpan<byte> entry, int itemKind)
    {
        if (entry.Length < EntryMinimumLength)
            throw new InvalidDataException("拍卖行挂牌的物品条目快照过短");
        var block = new byte[ItemBlockSize];
        block[BlockKindOffset] = unchecked((byte)itemKind);
        int itemId = ReadInt32(entry, EntryItemIdOffset);
        WriteInt32(block, BlockItemIdOffset, itemId);
        block[BlockAttrOffset] = entry[EntryAttrOffset];
        WriteInt32(block, BlockValueOffset, BlockValue(itemId, ReadInt32(entry, EntryValueOffset)));
        block[BlockDurabilityOffset] = entry[EntryDurabilityOffset];
        block[BlockDurabilityOffset + 1] = entry[EntryDurabilityOffset + 1];
        block[BlockSealOffset] = entry[EntrySealOffset];
        return block;
    }

    /// <summary>同上，直接从物品核心取值（种子与自测用）。</summary>
    public static byte[] ItemBlock(ItemCore core)
    {
        var block = new byte[ItemBlockSize];
        block[BlockKindOffset] = (byte)core.Kind;
        WriteInt32(block, BlockItemIdOffset, core.ItemId);
        block[BlockAttrOffset] = core.Attr;
        WriteInt32(block, BlockValueOffset, BlockValue(core.ItemId, core.Value));
        block[BlockDurabilityOffset] = (byte)core.Durability;
        block[BlockDurabilityOffset + 1] = (byte)(core.Durability >> 8);
        block[BlockSealOffset] = core.SealFlag;
        return block;
    }

    // —— ★ 道具块 +6（= 记录 [49..52]）在 itemId 合法时必须 > 0：客户端的静默丢行过滤器 ——
    //
    // 客户端**搜索**应答回调 0x023524E0 在把每条 168B 记录拷进行缓冲之前有一个过滤器
    // （0x0235264C–0x023526D3，与参考实现 rep movsd 0x2A 的拷贝点同处）：
    //
    //     ecx = rec[44..47]（= 道具块 +1，itemId）; call 0x20BE8C0   ; 该 id 能否查到道具资源 → 1/0
    //     if (结果 == 1) and (rec[49..52] <= 0)  →  查 3 条字符串并打日志，**整行不拷贝**（丢弃，count 不增）
    //     else                                   →  mov ecx,0x2A; rep movsd（原样拷 168B）
    //
    // rec[44..47] = 道具块 +1（itemId），rec[49..52] = 道具块 +6（value）。
    // ⇒ **只要 itemId 是客户端认得出来的道具，块 +6 就必须 > 0**，否则这一行会凭空消失：
    //   界面上的现象是「匹配总数正常、列表少行甚至一行都没有」，而且客户端不报任何错误码。
    //   这也是「改了别的字段界面却没反应」的一个可能解释，所以它必须在**编码器**里兜住，
    //   而不是指望调用方都传对。
    //
    // ★ **这个过滤器只有 186（搜索）有**（D95 转储离线反汇编定案，《附录 C》）：
    //   判据是同一个回调里「有没有那个道具资源查询调用 + 有没有 rep movsd」——
    //   186 两者皆有（`call 0x20BE8C0`@`0x02352659`、`rep movsd ecx=0x2A`@`0x023526B6`），
    //   188（`0x02352810`）与 189（`0x02352A00`）**两者皆无**（各自只有 `push 0x97/0x9F; call 0x3C85300`
    //   原样读记录），因此 188/189 的块 +6 写 0 **不会**被丢行。
    //   ⇒ 本兜底在「我的拍卖品」路径上是**防御性保留**，不是已被证实的必要约束：
    //     它的理由是「同一份记录契约里同一个字段不该有两种含义」+「将来某版客户端补上过滤器时不会静默丢行」，
    //     而不是「188/189 会丢行」。搜索路径（186）上它仍是**必需**的。
    //
    // ⚠ **这是防丢行，不是业务规则**（不新增、也不改任何业务语义）：
    //   - 堆叠类物品的数量本来就由 Value 携带，而 ItemCore.Validate 拒绝「可堆叠且 Value == 0」、
    //     也拒绝负数 ⇒ 合法数据走这条兜底**一个字节都不会变**，堆叠数量语义不受影响；
    //   - 它只在「itemId 合法、但 value ≤ 0」这种退化数据上生效（例如绕过物品模块直接写库的挂牌行、
    //     非堆叠类物品的品质 seed 为 0）；此时唯一的替代是**整行被客户端丢掉**，兜底更接近服务端意图。
    //   - itemId ≤ 0（无效道具）时**不兜底**：客户端查不到道具资源，那一行不会被过滤器丢掉，
    //     此处不替业务数据做补救。

    /// <summary>
    /// 道具块 <c>+6</c>（= 记录 <c>[49..52]</c>）的取值：<paramref name="itemId"/> 合法（&gt; 0）时保证 &gt; 0，
    /// 否则客户端会把整行静默丢弃（依据见上方「丢行过滤器」注释）。
    /// ★ 该过滤器**只在 186（搜索）路径上被证实存在**；188/189 没有它，走这两条路径时这里是防御性保留
    /// （见上方注释的「这个过滤器只有 186 有」一节），不因此新增或收紧任何业务语义。
    /// </summary>
    static int BlockValue(int itemId, int value) => itemId > 0 && value <= 0 ? MinimumBlockValue : value;

    // —— CMD 188 / 189 / 190「我的拍卖品」应答 ——
    //
    // 体 = `flag(1B) + mode(1B) + count(1B 或 2B) + N×记录`。
    //   ⚠ mode 必须**回显**请求首字节：参考实现里 mode=1 建 type 8 窗口（金币寄售）、mode=0 建 type 7 窗口，
    //     记录被灌进对应窗口类的面板，回错窗口 ⇒ 用户看到的网格始终为空。
    //   ⚠ flag 必须在体首（CMD 路径的通用约束，帧头 kind/opcode/CRC 不承担它）。
    // 记录布局：**记录尺寸与道具块偏移已由 D95 转储的反汇编独立证实**（188 = 151B、道具块 @30；
    // 189 = 159B、道具块 @38：步长 `add esi,0x97` / `0x9F`，块指针 `lea ecx,[esi-0x53]`），
    // 与参考实现给另一个客户端版本的量一致。**计数宽度（188 u8 / 189 u16 WORD）与
    // 190「按 188 镜像」仍是参考实现的候选**（反汇编只证实了记录尺寸与块偏移）。
    // 候选与安全底线见 AuctionMyListReply；本类只负责「按候选声明的结构编码，并自检」。

    /// <summary>CMD 路径应答体的 flag：非 0 才走客户端成功分支（与 183/186 同一约束）。</summary>
    public const byte MyListBodyFlag = 0x01;

    /// <summary>
    /// 「我的拍卖品」应答：`flag + mode(回显) + count + N×记录`。
    ///
    /// 空列表也必须发满 `flag + mode + count`（3 或 4 字节）——页签至少不再「点了没反应」，
    /// 这也是先跑通的第一步。体长由候选**推导**而不是手写：记录数、计数宽度、记录尺寸三者不一致
    /// 正是客户端读超/读偏的来源（182 上已经吃过这个亏）。
    /// </summary>
    public static byte[] MyListReply(byte mode, IReadOnlyList<AuctionListing> listings, AuctionMyListReply layout, long now)
    {
        AuctionMyListReply.Validate(layout);
        // 哨兵候选故意不发行记录；其余按计数宽度限幅（条数与本条数永远由同一个 count 决定）。
        int count = layout.CountFor(listings.Count);
        int header = layout.MinimumBodyBytes;
        var body = new byte[layout.BodyLength(count)];
        body[0] = MyListBodyFlag;
        body[1] = mode;
        if (layout.CountWidth == 1)
            body[2] = (byte)count;
        else
        {
            body[2] = (byte)count;
            body[3] = (byte)(count >> 8);
        }
        for (int index = 0; index < count; index++)
            EncodeMyListing(body.AsSpan(header + index * layout.RecordSize), listings[index], layout, now);
        // 编码自检（安全底线第二道）：flag 在首字节、体长不短于头部、且与候选声明的结构完全一致。
        if (body[0] != MyListBodyFlag || body[1] != mode || body.Length < layout.MinimumBodyBytes ||
            body.Length != layout.BodyLength(count))
            throw new InvalidDataException($"「我的拍卖品」应答编码与候选 {layout.Name} 的声明不符");
        return body;
    }

    /// <summary>一条「我的拍卖品」记录（供自测直接断言字段位置）。</summary>
    public static byte[] EncodeMyListing(AuctionListing listing, AuctionMyListReply layout, long now)
    {
        var record = new byte[layout.RecordSize];
        EncodeMyListing(record, listing, layout, now);
        return record;
    }

    /// <summary>
    /// 写入调用方给的记录切片。<c>Array.Copy</c> 不接受 Span，因此道具块走 <c>CopyTo</c>；
    /// 块内容复用搜索合同的同一个 <see cref="ItemBlock(ReadOnlySpan{byte}, int)"/>（不另建一套物品编码）。
    /// </summary>
    static void EncodeMyListing(Span<byte> record, AuctionListing listing, AuctionMyListReply layout, long now)
    {
        // 参考实现的跳过判定：两个首字段同时为 0 时整行被客户端静默隐藏。
        // 真实挂牌的 listing_id 是库里的主键（> 0），因此这条只可能在「以后有人拿占位行来发」时触发：
        // 与其发一行永远不显示、让人以为布局错了，不如直接失败（哨兵候选不发行记录，不受这条约束）。
        if (layout.KeepsRowVisible && !AuctionMyListReply.RowVisible(listing.ListingId, listing.ExpiresAtUnix))
            throw new InvalidDataException("「我的拍卖品」记录的两个首字段同时为 0：客户端会隐藏整行");
        WriteInt32(record, AuctionMyListReply.ListingIdOffset, listing.ListingId);
        WriteInt32(record, AuctionMyListReply.ExpiresOffset, checked((int)listing.ExpiresAtUnix));
        // [8..11] 是**单价**（界面「价格」列），与搜索合同的「当前竞拍」同源：有出价取最高出价、否则起拍价。
        WriteInt32(record, AuctionMyListReply.PriceOffset, listing.DisplayPrice);
        WriteInt32(record, AuctionMyListReply.BuyoutOffset, BuyoutTotal(listing));
        ItemBlock(listing.ItemEntry, listing.ItemKind).CopyTo(record[layout.ItemOffset..]);
        if (layout.RemainOffset >= 0)
            WriteInt32(record, layout.RemainOffset, RemainingSeconds(listing.ExpiresAtUnix, now));
    }

    /// <summary>
    /// 一口价**总额**（单价 × 数量，上限夹 int.MaxValue）：参考实现的这两个面板读的就是总额
    /// （购买确认框直接显示该值）。纯竞拍（一口价 &lt; 0）必须原样写 `-1`——写 0 会被判成「出价高于一口价」。
    /// </summary>
    static int BuyoutTotal(AuctionListing listing) => listing.BuyoutPrice < 0
        ? -1
        : (int)Math.Min((long)listing.BuyoutPrice * listing.ItemCount, int.MaxValue);

    /// <summary>装备类的剩余秒数（参考实现按「到期时间 − 当前时间」格式化）；已过期按 0。</summary>
    static int RemainingSeconds(long expiresAtUnix, long now) => (int)Math.Clamp(expiresAtUnix - now, 0, int.MaxValue);

    /// <summary>
    /// 挂牌持久快照：旧核心123B；带装备来源时另附12B服务端trailer。
    /// 前123B仍由唯一物品编码入口产生；trailer不能作为客户端条目发送。
    /// </summary>
    public static byte[] ItemEntrySnapshot(ItemCore core)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        ItemListProtocolWriter.Write(writer, 0, core);
        if (core.EpicExchangeOrigin is { } origin)
        {
            writer.Write(EpicExchangeTrailerMagic);
            writer.Write(origin.Version);
            writer.Write(origin.SourceItemId);
        }
        return stream.ToArray();
    }

    /// <summary>
    /// 物品条目字节快照 → 物品核心：**取消上架（CMD 184）要把道具还回背包时用**。
    ///
    /// 与 <see cref="ItemEntrySnapshot"/> 严格互逆：字段顺序与宽度逐项对齐
    /// <see cref="ItemListProtocolWriter"/>（该文件是条目排列的唯一事实来源），
    /// 因此不需要给挂牌表加一列来存核心 JSON，也不新建第二套物品编码。
    ///
    /// **读得回来的**（<see cref="ItemCore"/> 的全部持久字段）：itemId、value（数量/品级 seed）、
    /// attr（强化低 5 位）、耐久、封印、附魔卡与升级次数、增幅、两个附魔/编年史槽（Id/职业/成长/部位/编号）、
    /// 期限、徽章孔与徽章、符文、三条随机属性与随机状态、品级锻造（genuine/emancipate）、交易限制、
    /// 使用次数、排序锁与装备锁，以及那些尚未恢复语义的扩展域。
    /// **没有写在条目里的**：物品种类（`Kind`，挂牌表单独一列，因此由调用方传入）、
    /// `IsRental`（租赁标记不入客户端核心；上架本来就拒绝带期限与租赁实例，还回来的实例不会是租赁）。
    ///
    /// 来源仅从完整135B持久trailer恢复，旧123B不推断来源。未知长度、magic、版本、源ID或种类拒绝。
    /// 旧程序只能读123B，回退前须处理135B存档，不能静默截断来源。
    /// 快照损坏（长度不为123/135B、两个计数字节与实际内容不符）时抛
    /// <see cref="InvalidDataException"/>：调用方必须**拒绝取消**而不是拿半个核心去发放。
    /// </summary>
    public static ItemCore ItemCoreFromEntry(byte[] entry, ItemKind kind)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Length != ItemListProtocolWriter.EntrySize &&
            entry.Length != ItemListProtocolWriter.EntrySize + EpicExchangeTrailerSize)
            throw new InvalidDataException($"拍卖行物品条目快照长度应为123B或135B，实际 {entry.Length}B");
        using var stream = new MemoryStream(entry, writable: false);
        using var reader = new BinaryReader(stream);
        _ = reader.ReadInt16(); // [0..1] 槽位：挂牌保存时固定写 0，不属于核心。
        int itemId = reader.ReadInt32();
        int value = reader.ReadInt32();
        byte attr = reader.ReadByte();
        ushort durability = reader.ReadUInt16();
        byte sealFlag = reader.ReadByte();
        int enchantCardId = reader.ReadInt32();
        byte enchantUpgrade = reader.ReadByte();
        byte amplifyType = reader.ReadByte();
        ushort amplifyValue = reader.ReadUInt16();
        int marker16 = reader.ReadInt32();
        byte extension26 = reader.ReadByte();
        uint extension27 = reader.ReadUInt32();
        uint extension31 = reader.ReadUInt32();
        uint extension35 = reader.ReadUInt32();
        byte chronicleCount = reader.ReadByte();
        var chronicle0 = new ChronicleOption(reader.ReadInt32(), 0, 0, 0, 0);
        var chronicle1 = new ChronicleOption(reader.ReadInt32(), 0, 0, 0, 0);
        chronicle0 = chronicle0 with { Job = reader.ReadByte() };
        chronicle1 = chronicle1 with { Job = reader.ReadByte() };
        chronicle0 = chronicle0 with { GrowType = reader.ReadByte() };
        chronicle1 = chronicle1 with { GrowType = reader.ReadByte() };
        chronicle0 = chronicle0 with { EquipmentType = reader.ReadByte() };
        chronicle1 = chronicle1 with { EquipmentType = reader.ReadByte() };
        chronicle0 = chronicle0 with { Number = reader.ReadByte() };
        chronicle1 = chronicle1 with { Number = reader.ReadByte() };
        int expireTime = reader.ReadInt32();
        byte emblemSocketCount = reader.ReadByte();
        int emblem1 = reader.ReadInt32();
        int emblem2 = reader.ReadInt32();
        ushort rune = reader.ReadUInt16();
        byte randomCount = reader.ReadByte();
        var randomTypes = new byte[3];
        var randomValues1 = new byte[3];
        var randomValues2 = new byte[3];
        for (int index = 0; index < 3; index++)
            randomTypes[index] = reader.ReadByte();
        for (int index = 0; index < 3; index++)
            randomValues1[index] = reader.ReadByte();
        for (int index = 0; index < 3; index++)
            randomValues2[index] = reader.ReadByte();
        byte randomState = reader.ReadByte();
        byte randomChangedIndex = reader.ReadByte();
        byte randomChangeState = reader.ReadByte();
        var randomChange = new RandomOption(reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
        byte genuineUpgrade = reader.ReadByte();
        byte emancipateLevel = reader.ReadByte();
        byte tradeRestriction = reader.ReadByte();
        ushort extension90 = reader.ReadUInt16();
        byte extension92 = reader.ReadByte();
        byte extension93 = reader.ReadByte();
        byte remainUseCount = reader.ReadByte();
        byte sortLockFlag = reader.ReadByte();
        byte equipmentLockId = reader.ReadByte();
        uint extension97 = reader.ReadUInt32();
        ulong guardianGemKeys = reader.ReadUInt64();
        byte extension109 = reader.ReadByte();
        uint extension110 = reader.ReadUInt32();
        // ⚠ 顺序与字段声明相反：写入端先写 Extension114.Value（u32）再写 Flag（u8），这里必须照抄。
        uint extension114Value = reader.ReadUInt32();
        byte extension114Flag = reader.ReadByte();
        byte extension119 = reader.ReadByte();
        byte extension120 = reader.ReadByte();
        ushort extension121 = reader.ReadUInt16();
        if (stream.Position != ItemListProtocolWriter.EntrySize)
            throw new InvalidDataException("拍卖行物品条目快照的字段宽度与编码入口不一致");

        EpicExchangeOrigin? origin = null;
        if (entry.Length != ItemListProtocolWriter.EntrySize)
        {
            if (reader.ReadUInt32() != EpicExchangeTrailerMagic)
                throw new InvalidDataException("拍卖行装备来源trailer标识无效");
            origin = new(reader.ReadInt32(), reader.ReadInt32());
            if (kind != ItemKind.Equipment || origin.Version != 1 || origin.SourceItemId <= 0)
                throw new InvalidDataException("拍卖行装备来源版本、源编号或物品种类无效");
        }

        var core = new ItemCore(itemId, kind, value)
        {
            Attr = attr,
            Durability = durability,
            SealFlag = sealFlag,
            EnchantCardId = enchantCardId,
            EnchantUpgrade = enchantUpgrade,
            AmplifyType = amplifyType,
            AmplifyValue = amplifyValue,
            Marker16 = marker16,
            Extension26 = extension26,
            Extension27 = extension27,
            Extension31 = extension31,
            Extension35 = extension35,
            Chronicle0 = chronicle0,
            Chronicle1 = chronicle1,
            ExpireTime = expireTime,
            EmblemSocketCount = emblemSocketCount,
            Emblem1 = emblem1,
            Emblem2 = emblem2,
            Rune = rune,
            Random0 = new RandomOption(randomTypes[0], randomValues1[0], randomValues2[0]),
            Random1 = new RandomOption(randomTypes[1], randomValues1[1], randomValues2[1]),
            Random2 = new RandomOption(randomTypes[2], randomValues1[2], randomValues2[2]),
            RandomState = randomState,
            RandomChangedIndex = randomChangedIndex,
            RandomChangeState = randomChangeState,
            RandomChange = randomChange,
            GenuineUpgrade = genuineUpgrade,
            EmancipateLevel = emancipateLevel,
            TradeRestriction = tradeRestriction,
            Extension90 = extension90,
            Extension92 = extension92,
            Extension93 = extension93,
            RemainUseCount = remainUseCount,
            SortLockFlag = sortLockFlag,
            EquipmentLockId = equipmentLockId,
            Extension97 = extension97,
            GuardianGemKeys = guardianGemKeys,
            Extension109 = extension109,
            Extension110 = extension110,
            Extension114 = new ItemExtension(extension114Flag, extension114Value),            Extension119 = extension119,
            Extension120 = extension120,
            Extension121 = extension121,
            EpicExchangeOrigin = origin
        };
        // 两个计数字节是冗余信息（写入端按「非零个数」算）：与读回来的内容不符说明快照被改坏，
        // 宁可拒绝发道具，也不把半个核心还进背包。
        if (chronicleCount != ItemListProtocolWriter.Chronicles(core).Length ||
            randomCount != new[] { core.Random0, core.Random1, core.Random2 }.Count(option => option.Type != 0))
            throw new InvalidDataException("拍卖行物品条目快照的计数字节与实际内容不符");
        return core;
    }

    /// <summary>
    /// 探针候选编码（开发诊断，默认关闭时不会走到这里）：保留布局参数，供
    /// <c>--auction-probe</c> 继续轮换候选；正常读路径只用上面的合同重载。
    /// </summary>
    public static byte[] SearchReply(byte mode, int totalCount, IReadOnlyList<AuctionReplyRow> rows, AuctionReplyLayout layout, int padding = 0) =>
        Build(writer =>
        {
            writer.Write((byte)1);
            writer.Write(mode);
            writer.Write(totalCount);
            writer.Write(checked((ushort)Math.Min(rows.Count, ushort.MaxValue)));
            foreach (var row in rows)
                writer.Write(row.Encode(layout));
            if (padding > 0)
                writer.Write(new byte[padding]);
        });

    /// <summary>
    /// 探针候选的道具块：按候选布局选择「物品核心逐字段重排」或「直接复制条目字节」，
    /// 再按候选决定是否反排。合同编码不用这条路径。
    ///
    /// 反排后的 <c>legacy</c> 族布局把 value 放在块 <c>+6</c>（= 记录 <c>[49..52]</c>），
    /// 所以这一支同样走 <see cref="BlockValue"/> 的丢行底线：探针行是用 186（搜索）应答发出去的，
    /// 那条路径确有丢行过滤器，不兜住就分不清「布局不对」与「行被丢掉了」。
    /// 直接复制条目字节的那一支**保持原样**：它的假设就是「把条目字节当块用」，逐字节改写会毁掉这个假设。
    /// </summary>
    public static byte[] ItemBlock(byte[] entry, int itemKind, AuctionReplyLayout layout)
    {
        var block = new byte[layout.ItemSize];
        if (layout.LegacyCore)
        {
            if (block.Length > 0)
                block[0] = (byte)itemKind;
            int itemId = ReadInt32(entry, 2);
            WriteInt32(block, 1, itemId);
            WriteInt32(block, 5, BlockValue(itemId, ReadInt32(entry, 6)));
            if (block.Length > 9)
                block[9] = entry[10];
            if (block.Length > 13)
            {
                block[10] = entry[11];
                block[11] = entry[12];
                block[12] = entry[13];
            }
        }
        else
        {
            int available = Math.Max(0, Math.Min(entry.Length, ItemListProtocolWriter.EntrySize) - layout.EntryOffset);
            int copy = Math.Min(layout.ItemSize, available);
            Array.Copy(entry, layout.EntryOffset, block, 0, copy);
        }
        // 反排：参考实现把 [value:4B][attr:1B] 旋转成 [attr:1B][value:4B]，与普通条目顺序相反。
        if (layout.SwapRegionOffset >= 0 && layout.SwapRegionOffset + 5 <= block.Length)
        {
            int at = layout.SwapRegionOffset;
            byte attr = block[at + 4];
            for (int i = 4; i > 0; i--)
                block[at + i] = block[at + i - 1];
            block[at] = attr;
        }
        return block;
    }

    internal static int ReadInt32(ReadOnlySpan<byte> source, int offset) =>
        offset < 0 || offset + 4 > source.Length ? 0
        : source[offset] | (source[offset + 1] << 8) | (source[offset + 2] << 16) | (source[offset + 3] << 24);

    static void WriteInt32(Span<byte> target, int offset, int value)
    {
        if (offset < 0 || offset + 4 > target.Length)
            return;
        target[offset] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)(value >> 16);
        target[offset + 3] = (byte)(value >> 24);
    }

    static byte[] Build(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        return stream.ToArray();
    }

    // CMD183/184/185：成功为success:u8=1 + mode:u8；普通面板沿既有模式0。
    // 匹配公共分派028F8655失败先读error:u16，不是历史参考端的u8错误码。
    // 02359FF0的114显示后还读mode；184/185同值也落入读mode路径，故该码保留模式字节。
    // 兼容byte参数仅限定旧领域白名单，不代表线上只编码一个字节，亦不代表客户端只认识这些码。
    public const byte RegisterSuccessFlag = 0x01;
    public const byte RegisterFailureFlag = 0x00;
    public const byte RegisterModeNormal = 0x00;

    public static byte[] RegisterSuccess() => [RegisterSuccessFlag, RegisterModeNormal];

    public static byte[] RegisterFailure(byte errorCode)
    {
        if (!AuctionRegisterErrors.Whitelist.Contains(errorCode))
            throw new InvalidDataException($"上架失败码0x{errorCode:X2}不在旧领域兼容白名单内");
        return ClientFailurePackets.Create((ClientAuctionRegisterError)errorCode, RegisterModeNormal).Body;
    }

    public static byte[] CancelSuccess() => RegisterSuccess();
    public static byte[] CancelFailure(byte errorCode) => RegisterFailure(errorCode);
    public static byte[] BuySuccess() => RegisterSuccess();
    public static byte[] BuyFailure(byte errorCode) => RegisterFailure(errorCode);


    // —— CMD 182 `AUCTION_ASK_AVERAGE_PRICE`（均价查询）应答 ——
    //
    // 已确认（D95 抓包 + 实测）：客户端拖物品到上架框时发 182，请求体 13 字节
    //   [0]=00 | [1..4]=itemId 小端 i32 | [5..12]=00×8；服务端已收到 217 OVERFLOW_INFO 反馈，
    //   其 body 是出问题包的头 3 字节（cmd + opcode）⇒ `01 B6 00` 直指 182。
    //
    // 参考实现（另一个客户端版本，_review/06-拍卖行协议参考-S4A21.md §4.1/§6.10）给出：数据体 57 字节
    //   mode:u8 + key:i32 + unused:i32 + vector1:i32×6 + vector2:i32×6，
    // 走 CMD 路径，且**必须双应答且顺序不能颠倒**：先 mode=0（唯一把价格写进客户端全局价格表、
    // 置「价格就绪」标志的分支），再 mode=1（只填物品名、无条件启用控件）。
    //
    // ★ 这些结论在 D95 上**未验证**，而且 D95 对 57B/37B/33B 三种长度**都**回 217，
    //   说明差异不在长度而在**结构**：因此这里按候选声明的**结构形状**编码，编码前先过安全底线与自洽性校验
    //   （数据体 < 8B 的候选会把客户端读崩，见 AuctionAveragePriceReply.MinimumBodyBytes）。

    /// <summary>参考结构的向量 int32 个数（A21 逆向两个循环各 6 次）。</summary>
    public const int AverageVectorLength = 6;

    /// <summary>参考实现完整数据体长度：1 + 4 + 4 + 6×4 + 6×4 = 57（不含传输层前插的 flag）。</summary>
    public const int AverageFullLength = 1 + 4 + 4 + AverageVectorLength * 4 * 2;

    /// <summary>参考实现只带 vector1 的数据体长度：1 + 4 + 4 + 6×4 = 33。</summary>
    public const int AverageFirstVectorLength = 1 + 4 + 4 + AverageVectorLength * 4;

    /// <summary>参考实现旧档数据体长度 37（格式 10：mode + key + unused + [price,0]×3 + i32(1)）。</summary>
    public const int AverageLegacyLength = 1 + 4 + 4 + 3 * 8 + 4;

    /// <summary>参考结构 +1 字节尾部（58B）：探「字段序对、只差 1 字节」。</summary>
    public const int AverageReferencePlusTailLength = AverageFullLength + 1;

    /// <summary>
    /// 按候选的**结构形状**编码 182 应答数据体。返回值**不含**传输层附加内容（CMD 路径的帧头只有 kind/opcode/长度/CRC，
    /// 不承担 flag）：因此带 flag 的候选在这里就把 flag 写进体的第 0 字节（见
    /// <see cref="AuctionAveragePriceReply.ShapeFlagReference57"/>）。
    /// 长度必须与候选声明的 <see cref="AuctionAveragePriceReply.BodyLength"/> 完全一致，否则直接抛异常
    /// （宁可少发一个候选，也不能发出长度与声明不符的包 —— 那是 D95 读超的直接来源）。
    ///
    /// <paramref name="key"/> = 本次算出的均价（无在售时为 0，表示「无均价数据」）；key 与价格列表同源，
    /// 因为客户端把 key 当一口价输入框的预填值、把列表当价格表，两者不同源时界面会自相矛盾。
    /// </summary>
    public static byte[] AveragePriceReply(int mode, int key, AuctionAveragePriceReply? candidate)
    {
        AuctionAveragePriceReply.Validate(candidate);
        var shape = candidate!;
        var body = new byte[shape.BodyLength];
        // 长度自洽断言：编码器与候选定义脱节时宁可抛异常，也不能发出一个「声明 A 长度、实际 B 长度」的包
        // —— 声明与实际不符正是 D95 读超（217 OVERFLOW_INFO）最可能的来源。
        if (body.Length != shape.BodyLength)
            throw new InvalidDataException($"均价应答候选 {shape.Name} 声明 {shape.BodyLength}B，编码得到 {body.Length}B");
        switch (shape.Shape)
        {
            case AuctionAveragePriceReply.ShapeZeroFill:
                // 全 0：长度是唯一变量（mode/key 都不写），用于把「长度门槛」与「字段取值」分开。
                break;

            case AuctionAveragePriceReply.ShapeFlagReference57:
                // ★ CMD 应答体的 flag 由服务端写进体的第 0 字节，其后才是回调真正读的 57B 结构。
                // 依据：186 搜索应答的实测体是 01 00 <total:i32> <count:u16> …，而同一条帧的帧头第 0 字节
                // 是 kind（并参与 CRC），不承担这个 flag ⇒ 不加 flag 就会少 1 字节而被客户端读超。
                WriteBodyFlag(body);
                WriteModeAndKey(body, mode, key, BodyFlagLength);
                WriteVectors(body, key, vectorsOffset: BodyFlagLength + 9, candidate: shape);
                break;

            case AuctionAveragePriceReply.ShapeReference57:
            case AuctionAveragePriceReply.ShapeReferencePlusTail:
                WriteModeAndKey(body, mode, key, 0);
                // [5..8] unused 必须占位（参考实现读进局部变量但不使用），保持 0。
                // [9..] 两个价格向量，各 VectorLength 个 i32，与 key 同源。
                WriteVectors(body, key, vectorsOffset: 9, candidate: shape);
                break;

            case AuctionAveragePriceReply.ShapeLegacy37:
                WriteModeAndKey(body, mode, key, 0);
                // 旧档：[price, 0] ×3，然后一个 i32(1)。
                for (int index = 0; index < 3; index++)
                {
                    WriteInt32(body, 9 + index * 8, key);
                    WriteInt32(body, 13 + index * 8, 0);
                }
                WriteInt32(body, 33, 1);
                break;

            case AuctionAveragePriceReply.ShapeFirstVector:
                WriteModeAndKey(body, mode, key, 0);
                for (int index = 0; index < AverageVectorLength; index++)
                    WriteInt32(body, 9 + index * 4, key);
                break;

            case AuctionAveragePriceReply.ShapeCountedU16:
                WriteModeAndKey(body, mode, key, 0);
                WriteCountedPrices(body, key, shape, countOffset: 9, entryOffset: 11);
                break;

            case AuctionAveragePriceReply.ShapeFlagCountedU16:
                WriteBodyFlag(body);
                WriteModeAndKey(body, mode, key, BodyFlagLength);
                WriteCountedPrices(body, key, shape, countOffset: BodyFlagLength + 9, entryOffset: BodyFlagLength + 11);
                break;

            default:
                throw new InvalidDataException("未知的均价应答结构形状：" + shape.Shape);
        }
        return body;
    }

    /// <summary>CMD 应答体首字节 flag 的宽度；flag 的取值见 <see cref="AuctionAveragePriceReply.BodyFlag"/>。</summary>
    const int BodyFlagLength = 1;

    /// <summary>写应答体第 0 字节的 flag（非 0 = 客户端成功分支）。</summary>
    static void WriteBodyFlag(byte[] body)
    {
        if (body.Length < BodyFlagLength)
            throw new InvalidDataException("均价应答体放不下 flag 字节");
        body[0] = AuctionAveragePriceReply.BodyFlag;
    }

    /// <summary>写 mode:u8 + key:i32；key 之后的那个 dword（unused）由调用方保持 0（参考实现要求占位）。</summary>
    static void WriteModeAndKey(byte[] body, int mode, int key, int offset)
    {
        body[offset] = unchecked((byte)mode);
        WriteInt32(body, offset + 1, key);
    }

    /// <summary>写参考结构的两个价格向量（各 VectorLength 个 i32，与 key 同源）。</summary>
    static void WriteVectors(byte[] body, int key, int vectorsOffset, AuctionAveragePriceReply candidate)
    {
        for (int vector = 0; vector < 2; vector++)
        {
            for (int index = 0; index < candidate.VectorLength; index++)
            {
                int offset = vectorsOffset + vector * candidate.VectorLength * 4 + index * 4;
                if (offset + 4 > body.Length)
                    break;
                WriteInt32(body, offset, key);
            }
        }
    }

    /// <summary>
    /// 写「u16 计数 + N×4B 价格条目」：计数值与实际写出的条目数取自**同一处**
    /// （<see cref="AuctionAveragePriceReply.CountEntries"/>），因此不可能出现「声明 N 条只给 M 条」。
    /// </summary>
    static void WriteCountedPrices(byte[] body, int key, AuctionAveragePriceReply candidate, int countOffset, int entryOffset)
    {
        int count = candidate.CountEntries;
        body[countOffset] = unchecked((byte)count);
        body[countOffset + 1] = unchecked((byte)(count >> 8));
        for (int index = 0; index < count; index++)
            WriteInt32(body, entryOffset + index * 4, key);
    }

    /// <summary>
    /// CMD 795 均价列表刷新（参考实现 §4.3，**D95 未验证**）：
    /// <c>flag + 长度前缀 i32 + 字符串 + b0 + b1 + b2</c>。字符串必须 ≤ 0x1F 字节，否则客户端静默得到空串
    /// （不会报 OVERFLOW）。这里字符串留空、三个字节留 0，只用于分辨「D95 需不需要这条刷新」。
    /// </summary>
    public static byte[] AveragePriceListRefresh(byte b0 = 0, byte b1 = 0, byte b2 = 0)
    {
        var body = new byte[7];
        WriteInt32(body, 0, 0);
        body[4] = b0;
        body[5] = b1;
        body[6] = b2;
        return body;
    }
}

/// <summary>探针候选的一条搜索结果（未按合同编码，只服务于布局收敛）。</summary>
internal sealed record AuctionReplyRow(
    long ListingId,
    int ExpiresAtUnix,
    int CurrentPrice,
    int BuyoutTotal,
    byte[] ItemBlock,
    int RemainSeconds)
{
    public byte[] Encode(AuctionReplyLayout layout)
    {
        var record = new byte[layout.RecordSize];
        WriteInt32(record, 0, unchecked((int)ListingId));
        WriteInt32(record, 4, ExpiresAtUnix);
        WriteInt32(record, 9, CurrentPrice);
        WriteInt32(record, 13, BuyoutTotal);
        Array.Copy(ItemBlock, 0, record, layout.ItemOffset, Math.Min(ItemBlock.Length, layout.RecordSize - layout.ItemOffset));
        if (layout.RemainOffset >= 0 && layout.RemainOffset + 4 <= record.Length)
            WriteInt32(record, layout.RemainOffset, RemainSeconds);
        return record;
    }

    static void WriteInt32(byte[] record, int offset, int value)
    {
        if (offset < 0 || offset + 4 > record.Length)
            return;
        record[offset] = (byte)value;
        record[offset + 1] = (byte)(value >> 8);
        record[offset + 2] = (byte)(value >> 16);
        record[offset + 3] = (byte)(value >> 24);
    }
}
