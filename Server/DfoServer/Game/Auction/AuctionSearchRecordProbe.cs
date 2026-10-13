namespace DfoServer.Game.Auction;

/// <summary>
/// 搜索应答（CMD 186）记录里被探针改动的那**一个**字段。
///
/// ★ **已定案（2026-10-12，D95 转储离线反汇编，《附录 C》）：本版客户端没有「竞拍」按钮，
/// 这些字段与入口无关** —— 逐个改它们不可能有任何效果，判据不是「位置未解」而是**不存在**：
///   - 「竞拍」文案的字符串 id `0xEA99` 在**全地址空间零代码引用**（`push imm32` 与 `mov r32,imm32` 两种编码全扫，
///     命中 0；dword 只出现在它自己的字符串树节点里），同组的竞价文案
///     `0xEAA3`/`0xEAA4`/`0xEAAA`/`0xB557`/`0xB558`/`0xB553` **同样零引用**；而同块其它串都有引用
///     （一口价 `0xEA9A` 1 处、关闭 `0xEA9B` 14 处…）⇒ 该构建**裁掉了竞价 UI**，不是「入口被隐藏」；
///   - 186 回调 `0x023524E0` 的指令级读点只有 `rec[44..47]`（= 道具块 `+1`，itemId）与
///     `rec[49..52]`（= 道具块 `+6`，value，丢行过滤器用），**完全不读**本枚举里那七个位置；
///   - 两个填充函数（`0x26FF7D0` 724B / `0x371A6C0` 1268B）全文递归下降证明它们**不读记录的任何一个偏移**
///     （只有整条 168B 深拷贝 + 链表操作）。
/// ⇒ 服务端**无法**用任何记录字段（也不能用 `mode`）让竞拍入口出现；竞价 / 一口价的分支只能由服务端在
/// CMD `0x00B9` 的字段上决定。依据在仓库外的 `_review/07-客户端只读分析.md` 附录 C（只读离线分析）。
/// </summary>
internal enum AuctionSearchRecordField
{
    /// <summary>对照：一个字段都不改（当前合同编码）。</summary>
    None,

    /// <summary>
    /// `[13..16]` 一口价总额。参考实现反汇编（§4.4）：客户端 `cmp [row+0x365], -1` 判断「有无一口价」，`-1` = 纯竞拍单。
    /// **已定案：与竞拍入口无关** —— 该值在本客户端已证实的唯一界面效果是「价格列不显示『售价』行」。
    /// </summary>
    BuyoutTotal,

    /// <summary>`[8]` 保留字节。**已定案：与竞拍入口无关**（186 回调不读它；参考实现也写 0）。</summary>
    Reserved8,

    /// <summary>
    /// `[17..20]` 平均市价（保持 0 时客户端跳过「高于市价 2 倍」提示）。
    /// **已定案：与竞拍入口无关**；保留这个候选只服务于「市价非 0 时提示是否出现」这一个诊断。
    /// </summary>
    MarketPrice17,

    /// <summary>
    /// `[156..159]`。**已定案：与竞拍入口无关，且该位置不是裸 int32** —— 它是 12B 子对象
    /// （`[156..167]`）的头 dword（ctor `0x3FA9B10` 只写这一个 dword），渲染器按子对象取值，
    /// 写剩余秒数进去没有意义（「剩余时间」列读的是全局单例属性，见 <see cref="AuctionProbeSettings"/>）。
    /// </summary>
    RemainSeconds156,

    /// <summary>`[34]` 保留区 `[34..41]` 的首字节。**已定案：与竞拍入口无关**（186 回调不读它）。</summary>
    Tail34,

    /// <summary>`[38]` 保留区 `[34..41]` 的第二个 dword 首字节。**已定案：与竞拍入口无关**（186 回调不读它）。</summary>
    Tail38,

    /// <summary>`[42]` 标志字节。**已定案：与竞拍入口无关**（186 回调不读它；参考实现写 0）。</summary>
    Flag42
}

/// <summary>
/// 搜索应答记录（168B）的**逐字段探针候选**：每个候选只改一个字段、其余字节与合同编码完全一致，
/// 由 <c>--auction-probe bid=</c> 按搜索请求轮换，日志记下本次用的是哪一个。
///
/// **已定案（D95 转储离线反汇编，《附录 C》）：这一组候选对「竞拍入口」这件事已经作废，且全部退出默认轮换。**
/// 起因是用户反馈「搜索结果里只有一口价按钮、没有竞拍按钮」；参考实现（另一个客户端版本）的反汇编给出
/// 「`cmp [row+0x365], -1` 判纯竞拍单」与「`[8]`/`[17..20]`/`[34..41]`/`[42]` 都保持 0」两个线索，
/// 因此把七个字段做成逐字段候选。本客户端实测：七个字段逐个改过一遍，竞拍入口一次都没出现；
/// 随后的离线反汇编把原因定死：**本版客户端没有竞拍按钮**
/// （文案 id `0xEA99` 全地址空间零代码引用，同组竞价文案也全零引用），
/// 186 回调只读 `rec[44..47]` / `rec[49..52]`，两个填充函数不读记录的任何偏移 ⇒ 七个字段与入口无关。
/// ⇒ **默认行为 = 一个字段都不改**（<see cref="DefaultSequence"/> 只剩 <see cref="Contract"/>）：
/// 继续默认轮换只会让每次搜索的记录白白偏离合同编码，而它要回答的问题已经不存在。
///
/// **为什么仍保留这七个候选（只按名字显式可达）**：
///   ① 它们保留的是这段排查的现场与「每个候选只改一个字段、差异字节正好落在该字段宽度内」的编码回归，
///      而那段回归保护的是编码器的字段偏移；删掉候选会连带删掉回归，收益为零；
///   ② <see cref="MarketPrice"/> 还兼作另一个独立诊断 —— 「平均市价非 0 时客户端是否跳过『高于市价 2 倍』提示」；
///   ③ <c>Find</c>/<c>Names</c> 的按名可达性是 <c>--auction-probe bid=</c> 的既有接口，删名会变成解析期报错，
///      与「这个键只在显式给出时生效」的约定不符。
/// 它们**不再是任何默认轮换的一部分**，也不应再被用来找竞拍入口。
///
/// ⚠ 这是**开发诊断**（默认关闭）：未给 <c>bid=</c> 时本类只提供 <see cref="Contract"/>。
///
/// 另一条相邻的探针 <c>bidmode=</c>（<see cref="AuctionProbeSettings.SearchModes"/>）**保留**：
/// 它验证的是「应答 `mode` 决定窗口 6/7 分流」（已实机闭环），与竞拍入口无关。
/// </summary>
internal sealed record AuctionSearchRecordProbe(string Name, AuctionSearchRecordField Field, int Value, string Note)
{
    /// <summary>对照：不改任何字段（探针关闭、或没给 <c>bid=</c> 时就是它）。</summary>
    public static readonly AuctionSearchRecordProbe Contract =
        new("contract", AuctionSearchRecordField.None, 0, "对照：不改任何字段（当前合同编码）");

    /// <summary>
    /// 把每行的一口价总额改写成 -1（纯竞拍）。
    /// **已定案：与竞拍入口无关**（本版客户端没有该入口，见类型注释），且它把库内真实挂牌（多数带一口价）
    /// 改成「每行都是纯竞拍」，会让界面分不清是字段起作用还是这种假数据带出来的 ⇒ **不进默认轮换序**
    /// （`bid=allpure` 仍可显式点出，但已不回答任何待解问题，只留作对照）。
    /// </summary>
    public static readonly AuctionSearchRecordProbe AllPureAuction =
        new("allpure", AuctionSearchRecordField.BuyoutTotal, -1,
            "只改 [13..16] = -1（全部行看起来都是纯竞拍）：★ 已定案——本版客户端无竞拍入口，此候选与入口无关，仅存史/对照，不进默认轮换");

    /// <summary>`[8] = 1`。★ 已定案：与竞拍入口无关（186 回调不读 `[8]`），仅存史/对照。</summary>
    public static readonly AuctionSearchRecordProbe Reserved8 =
        new("flag8", AuctionSearchRecordField.Reserved8, 1, "只改 [8] = 1（参考实现写 0）：★ 已定案——186 回调不读该字节，与竞拍入口无关，仅存史/对照");

    /// <summary>
    /// `[17..20] = 该行起拍价`（非 0 平均市价）。★ 已定案：与竞拍入口无关；
    /// 保留它是因为它还兼作「平均市价非 0 时客户端是否跳过『高于市价 2 倍』提示」这一个独立诊断。
    /// </summary>
    public static readonly AuctionSearchRecordProbe MarketPrice =
        new("market17", AuctionSearchRecordField.MarketPrice17, 0,
            "只把 [17..20] 平均市价写成非 0（该行起拍价）：★ 已定案——与竞拍入口无关，仅用于「高于市价 2 倍提示」诊断");

    /// <summary>`[42] = 1`。★ 已定案：与竞拍入口无关（186 回调不读 `[42]`），仅存史/对照。</summary>
    public static readonly AuctionSearchRecordProbe Flag42 =
        new("flag42", AuctionSearchRecordField.Flag42, 1, "只改 [42] = 1（参考实现写 0）：★ 已定案——186 回调不读该字节，与竞拍入口无关，仅存史/对照");

    /// <summary>`[34] = 1`。★ 已定案：与竞拍入口无关（186 回调不读保留区），仅存史/对照。</summary>
    public static readonly AuctionSearchRecordProbe Tail34 =
        new("tail34", AuctionSearchRecordField.Tail34, 1, "只改 [34] = 1（保留区 [34..41] 首字节）：★ 已定案——与竞拍入口无关，仅存史/对照");

    /// <summary>`[38] = 1`。★ 已定案：与竞拍入口无关（186 回调不读保留区），仅存史/对照。</summary>
    public static readonly AuctionSearchRecordProbe Tail38 =
        new("tail38", AuctionSearchRecordField.Tail38, 1, "只改 [38] = 1（保留区第二个 dword 首字节）：★ 已定案——与竞拍入口无关，仅存史/对照");

    /// <summary>
    /// `[156..159] = 剩余秒数`（非 0）。★ 已定案：**既不产生竞拍入口，也不是剩余时间列的取值处** ——
    /// `[156..159]` 是 12B 子对象的头 dword，而「剩余时间」列读的是全局单例的 64 位属性
    /// （`G=[0x62EE560]` 的 `+0x7C8+0x1D*0x10+8`，语义 = 剩余秒数），记录里保持不写才是正确行为。
    /// </summary>
    public static readonly AuctionSearchRecordProbe RemainSeconds =
        new("remain156", AuctionSearchRecordField.RemainSeconds156, 0,
            "只把 [156..159] 写成剩余秒数：★ 已定案——该位置是 12B 子对象头，且本版客户端无竞拍入口，此候选无诊断价值，仅存史/对照");

    /// <summary>
    /// 按名字可达的全部候选（含只在命名引用里保留的七个单字段候选）。
    /// ★ 这一组的用途已收敛为「存史 + 编码回归 + 市价提示诊断」，**不参与任何默认轮换**（见 <see cref="DefaultSequence"/>）。
    /// </summary>
    public static readonly IReadOnlyList<AuctionSearchRecordProbe> All =
        [Contract, AllPureAuction, Reserved8, MarketPrice, Flag42, Tail34, Tail38, RemainSeconds];

    /// <summary>
    /// 默认轮换序 = **只剩对照**（一个字段都不改）。
    /// 七个单字段候选已全部定案为「与竞拍入口无关」（本版客户端没有该入口，见类型注释），
    /// 再默认轮换它们只会让每次搜索的记录白白偏离合同编码，因此默认序里一个单字段候选都不留；
    /// 它们仍可按名字显式点出（保留理由见类型注释），但不进任何默认轮换。
    /// </summary>
    public static readonly IReadOnlyList<AuctionSearchRecordProbe> DefaultSequence = [Contract];

    /// <summary>探针关闭时的候选：逐字节等于合同编码。</summary>
    public static AuctionSearchRecordProbe Default => Contract;

    /// <summary>
    /// 把候选声明的**那一个字段**写进已按合同编码好的记录。
    /// 必须在合同编码之后调用：这样「候选只改一处」是结构上的保证，而不是靠两处编码保持一致。
    /// ★ 七个单字段候选已定案为「与竞拍入口无关」，本方法只服务按名显式点出的历史候选与编码回归，
    /// 默认路径（<see cref="Contract"/>）**不会**调用到任何一个写分支。
    /// </summary>
    public void Apply(Span<byte> record, AuctionListing listing, long now)
    {
        switch (Field)
        {
            case AuctionSearchRecordField.None:
                return;
            case AuctionSearchRecordField.BuyoutTotal:
                WriteInt32(record, BuyoutOffset, Value);
                return;
            case AuctionSearchRecordField.Reserved8:
                record[Reserved8Offset] = (byte)Value;
                return;
            case AuctionSearchRecordField.MarketPrice17:
                // Value = 0 表示「用该行自己的起拍价」，避免调用方为了写一个非 0 值再算一遍。
                WriteInt32(record, MarketPriceOffset, Value != 0 ? Value : listing.StartingPrice);
                return;
            case AuctionSearchRecordField.Flag42:
                record[FlagOffset] = (byte)Value;
                return;
            case AuctionSearchRecordField.Tail34:
                record[TailOneOffset] = (byte)Value;
                return;
            case AuctionSearchRecordField.Tail38:
                record[TailTwoOffset] = (byte)Value;
                return;
            case AuctionSearchRecordField.RemainSeconds156:
                long remain = listing.ExpiresAtUnix - now;
                WriteInt32(record, RemainOffset, (int)Math.Clamp(remain, 0, int.MaxValue));
                return;
            default:
                throw new InvalidDataException("未知的搜索记录探针字段：" + Field);
        }
    }

    // 与 AuctionPackets 的合同记录偏移同源（那边是 private const，这里按参考实现的字段表再声明一次，
    // 并由自测断言「探针候选在对照候选下逐字节等于合同编码」，因此两处不可能悄悄漂移）。
    // ★ 偏移只是字节位置，不代表那里的语义已经被证实：`[156..159]` 在客户端是 12B 子对象
    // （`[156..167]`）的头 dword，`[8]`/`[34]`/`[38]`/`[42]` 与 `[17..20]` 都不被 186 回调读取。
    const int BuyoutOffset = 13;
    const int Reserved8Offset = 8;
    const int MarketPriceOffset = 17;
    const int FlagOffset = 42;
    const int TailOneOffset = 34;
    const int TailTwoOffset = 38;
    const int RemainOffset = 156;

    static void WriteInt32(Span<byte> record, int offset, int value)
    {
        if (offset < 0 || offset + 4 > record.Length)
            throw new InvalidDataException($"搜索记录探针字段越界：@{offset}");
        record[offset] = (byte)value;
        record[offset + 1] = (byte)(value >> 8);
        record[offset + 2] = (byte)(value >> 16);
        record[offset + 3] = (byte)(value >> 24);
    }

    public static AuctionSearchRecordProbe? Find(string name)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    public static string Names => string.Join('/', All.Select(candidate => candidate.Name));
}
