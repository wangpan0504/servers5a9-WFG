namespace DfoServer.Game.Auction;

/// <summary>
/// 拍卖行开发诊断的一组不可变参数（默认关闭）。
/// 由 <c>--auction-probe</c> 解析产生，每次拍卖行请求取一份快照；关闭时拍卖行搜索走真实挂牌数据。
/// 这些参数只服务于应答通道、记录布局与 CMD 182 应答结构的实机收敛，不表达业务规则。
///
/// ★ 两组候选各自轮换，互不影响（<c>PresetIndex</c> 走搜索、<c>AverageIndex</c> 走 CMD 182）：
/// 「每拖一次物品前进一个均价候选」不会顺带改掉搜索回什么，用户不会因为切均价候选看到空列表；
/// <see cref="AverageOnly"/> = 1 时把搜索整条路径也钉在真实挂牌数据上。
/// 记录布局已经定案（见 <see cref="Contract"/>），默认候选即合同值，候选轮换只在显式开启时使用。
/// </summary>
internal sealed record AuctionProbeSettings(
    bool Enabled,
    bool AverageOnly,
    IReadOnlyList<AuctionReplyLayout> Presets,
    int PresetIndex,
    IReadOnlyList<int> ReplyOpcodes,
    IReadOnlyList<bool> NotifyKinds,
    int Rows,
    IReadOnlyList<int> TotalCounts,
    IReadOnlyList<int> ItemTemplateIds,
    int StackCount,
    int CurrentPrice,
    int BuyoutTotal,
    IReadOnlyList<int> Paddings,
    IReadOnlyList<AuctionAveragePriceReply> AverageCandidates)
{
    /// <summary>
    /// 已定案的合同布局：168B 记录、道具块 @43 宽 83B、物品核心布局 + value/attr 反排。
    /// 剩余时间字段位置与语义仍未确认（写参考实现的 [156] 界面恒显示「0 小时」），因此 RemainOffset 为 -1，不写该字段。
    /// </summary>
    public static readonly AuctionReplyLayout Contract = new("contract", 168, 43, 83, 0, 5, -1, true);

    public static readonly AuctionProbeSettings Disabled =
        new(false, false, [Contract], 0, [186], [false], 0, [-1], [490704576], 1, 0, -1, [0], [AuctionAveragePriceReply.Default]);

    public AuctionReplyLayout Layout => Presets[Index(Presets.Count)];

    public int ReplyOpcode => ReplyOpcodes[Index(ReplyOpcodes.Count)];

    public bool Notify => NotifyKinds[Index(NotifyKinds.Count)];

    public int ItemTemplateId => ItemTemplateIds[Index(ItemTemplateIds.Count)];

    public int Padding => Paddings[Index(Paddings.Count)];

    /// <summary>CMD 182 本次使用的应答候选（用独立的 <see cref="AverageIndex"/>，不受搜索轮换影响）。</summary>
    public AuctionAveragePriceReply Average => AverageCandidates[Index(AverageCandidates.Count, AverageIndex)];

    /// <summary>
    /// 本次「我的拍卖品」（CMD 188/189/190）请求使用的记录布局候选。由 <c>--auction-probe mylist=</c> 轮换，
    /// **每个页签有各自的下标**（点「我的上架」不会推进「我的竞拍/拍卖历史」的候选），
    /// 因此不会出现「同一轮里三个页签各拿到一种记录形状」。
    /// 探针关闭或未给 <c>mylist=</c> 时按页签取参考实现的默认候选（<see cref="AuctionMyListReply.DefaultFor"/>）。
    /// </summary>
    public AuctionMyListReply MyList { get; init; } = AuctionMyListReply.Registed151;

    /// <summary>`mylist=` 给出的候选轮换集；为空表示每个页签各用自己的默认候选。</summary>
    public IReadOnlyList<AuctionMyListReply> MyLists { get; init; } = [];

    /// <summary>
    /// 本次搜索应答使用的**搜索记录逐字段探针**（`bid=`）：每个候选只改记录里的一个字段。
    /// 它原本用于判断「竞拍按钮由哪个字段决定」，**该问题已定案关闭**：本版 D95 客户端没有竞拍入口，
    /// 七个候选字段与入口无关（见 <see cref="AuctionSearchRecordProbe"/>），因此这组候选已退出默认轮换、
    /// 只按名字显式可达。由独立下标轮换，每来一次搜索请求前进一个。
    /// 未配置时是 <see cref="AuctionSearchRecordProbe.Contract"/>（逐字节等于合同编码）。
    /// </summary>
    public AuctionSearchRecordProbe SearchRecord { get; init; } = AuctionSearchRecordProbe.Default;

    /// <summary>`bid=` 给出的候选轮换集；为空表示不改记录（合同编码）。</summary>
    public IReadOnlyList<AuctionSearchRecordProbe> SearchRecords { get; init; } = [];

    /// <summary>
    /// 本次搜索应答实际写进 <c>mode</c> 字节的值，或 <see cref="EchoRequestMode"/>（= 回显请求首字节，既有行为）。
    /// 由 <c>bidmode=</c> 轮换（见 <see cref="SearchModes"/>）。
    /// </summary>
    public int SearchMode { get; init; } = EchoRequestMode;

    /// <summary>
    /// `bidmode=` 给出的搜索应答 mode 轮换集（只接受 0/1）。为空 = 一切照旧回显请求里的 mode。
    ///
    /// **为什么单独轮换 mode**：客户端 186 回调 <c>0x023524E0</c> 按**应答的 mode 字节**分流到
    /// 两个不同的窗口与两个不同的填充函数（<c>mode==1</c> → GetWindow(7) + <c>0x26FF7D0</c>；
    /// <c>mode==0</c> → GetWindow(6) + <c>0x371A6C0</c>）。同一个搜索请求交替用 <c>mode=0</c>/<c>mode=1</c>
    /// 应答、**记录内容完全相同**，用于验证「应答 mode 决定窗口 6/7 分流」—— 这一项**已实机闭环**。
    /// ★ 它**不影响竞拍入口**：本版 D95 客户端根本没有竞拍按钮（「竞拍」文案 id `0xEA99` 全地址空间零引用，
    /// 两个填充函数也不读记录的任何偏移，见 <see cref="AuctionSearchRecordProbe"/> 与
    /// [拍卖行协议](../../../Docs/协议/拍卖行.md)），所以不要再指望用 mode 对照把入口「试出来」。
    /// </summary>
    public IReadOnlyList<int> SearchModes { get; init; } = [];

    /// <summary>回显请求首字节的 mode（<see cref="SearchMode"/> 的默认值，即未开探针时的行为）。</summary>
    public const int EchoRequestMode = -1;

    /// <summary>本次搜索应答要写的 mode 字节：未开 <c>bidmode=</c> 时回显请求，否则用轮换到的候选。</summary>
    public byte ReplyMode(byte requestMode) => SearchMode == EchoRequestMode ? requestMode : (byte)SearchMode;

    /// <summary>
    /// 是否**显式覆盖**了搜索应答（`rows`/`presets`/`total`/`reply`/`kind`/`pad`/`items`/`count`/`price`/`buyout`）。
    /// 只有它为真才把搜索路径切到合成探针行；只配 `avg=` / `mylist=` / `bid=` / `bidmode=` 时搜索仍读真实挂牌数据
    /// —— 否则「为了看某个字段而开探针」会把真实搜索结果替换掉，反而看不到要观察的行。
    /// （`bidmode=` 尤其需要真实行：它要判的是**同一批记录**在 mode=0/1 两个窗口里的差异。）
    /// </summary>
    public bool SearchOverride { get; init; }

    /// <summary>均价候选的轮换下标；与搜索下标分开，避免两个门互相推进。</summary>
    public int AverageIndex { get; init; }

    public int Total => TotalCounts[Index(TotalCounts.Count)] is var total && total >= 0
        ? total
        : Rows <= 0 || Layout.RecordSize == 0 ? 0 : Rows;

    int Index(int count) => Index(count, PresetIndex);

    static int Index(int count, int index) => count <= 0 ? 0 : Math.Clamp(index, 0, int.MaxValue) % count;
}
