namespace DfoServer.Game.Auction;

/// <summary>
/// CMD 182 `AUCTION_ASK_AVERAGE_PRICE`（均价查询）应答的**结构候选**：应答 opcode、双应答的 mode 取值与顺序、
/// 数据体**结构形状**（模式/key/计数字段的位置与宽度、列表条数与条目宽度）、是否补发均价列表刷新。
///
/// ★ 为什么从「长度阶梯」改成「结构候选」：
/// D95 实测（见 <c>Docs/协议/拍卖行.md</c> 的 182 节）里 33B / 37B / 57B 三种**长度完全不同**的候选都被客户端
/// 回了 <c>C2S 217 OVERFLOW_INFO</c>（body <c>01 B6 00</c>，指名 182 包体读超），
/// 而且 57B 候选在 key=0 与 key=17000 两种取值下表现一致 ⇒ **客户端不是按某个字段的数值决定读多少字节**，
/// 而是按一套**固定结构**读取：长度不对或字段位置不对都会读越界。因此本轮只按结构收敛：
/// 每个候选明确写出「模式在哪、key 在哪、有没有计数字段、计数字段多宽、列表几条、条目多宽」，
/// 候选之间的差异是**结构假设**而不是长度数字。
///
/// ★★ 安全底线（<see cref="MinimumBodyBytes"/>）：短包体会让客户端按计数读到未初始化内存并**崩溃**
/// （实测 1B 候选：不回 217，12 秒后连接被 IOException 强杀）。任何数据体小于该常量的候选一律禁止下发：
/// <see cref="All"/> 的构造会拒绝，<see cref="Find"/> 查不到，编码器与发送处各有一道断言。
/// 候选声明的计数值必须与它实际提供的条目数一致（不能声明 N 条只给 M&lt;N 条），
/// 这是 D95 报 OVERFLOW 最可能的来源。
///
/// 本类型属于 Game 层：探针参数是业务侧的开发诊断设置，线格式编码在 Network/Builders。
/// </summary>
internal sealed record AuctionAveragePriceReply(
    string Name,
    int ReplyOpcode,
    int FirstMode,
    int SecondMode,
    int Shape,
    bool SendListRefresh,
    string Note)
{
    // —— 结构形状编码（不是长度！长度由形状与列表条数推导）——
    /// <summary>参考实现「格式 20」：mode + key + unused + vector1(i32×6) + vector2(i32×6) = 57B。</summary>
    public const int ShapeReference57 = 20;

    /// <summary>
    /// ★ **flag + 参考 57B 结构** = 58B：
    /// <c>flag:u8(=0x01) + mode + key + unused + vector1[6×i32] + vector2[6×i32]</c>。
    /// 参考实现认定 CMD 路径在进回调前先吃掉 1 字节 flag，回调真正读的是其后 57B
    /// （`auction_avg_reply.txt:83-96, 149-152`：`prefix=1` 才是正确值；`prefix=0` 时 flag 取到 `mode=0` 会走错误分支）。
    /// 本仓库自己的协议证据是 186 搜索应答：实际下发的体是 <c>01 00 &lt;total:i32&gt; &lt;count:u16&gt; …</c>，
    /// 同样以 flag 开头 —— 而 [GameFrame] 帧头第 0 字节是 **kind/CRC 区**，不承担这个 flag
    /// ⇒ **flag 属于应答体**，不能指望传输层补。
    /// </summary>
    public const int ShapeFlagReference57 = 93;

    /// <summary>flag + 计数头：<c>flag(=0x01) + mode + key + unused + u16 计数 + N×4B 价格条目</c>。</summary>
    public const int ShapeFlagCountedU16 = 94;

    /// <summary>参考实现「格式 10」旧版回退档：mode + key + unused + [price,0]×3 + i32(1) = 37B。</summary>
    public const int ShapeLegacy37 = 10;

    /// <summary>参考实现「格式 21」：只带 vector1 = 33B。</summary>
    public const int ShapeFirstVector = 21;

    /// <summary>参考实现「格式 13」纯 1 字节 ACK（**不安全**，只保留常量给安全底线断言用）。</summary>
    public const int ShapeAck = 13;

    /// <summary>全 0 数据体：把「长度」与「字段取值」两个变量分开的探针（长度见 <see cref="ZeroBytes"/>）。</summary>
    public const int ShapeZeroFill = 90;

    /// <summary>「头部 + 计数 + 条目」列表结构：mode + key + unused + <b>u16 计数</b> + N×4B 价格条目。</summary>
    public const int ShapeCountedU16 = 91;

    /// <summary>参考 57B 布局整体 +1 字节尾部（探「结构对、只差 1 字节」）。</summary>
    public const int ShapeReferencePlusTail = 92;

    /// <summary>
    /// 该候选的数据体长度。**这是布局的固有属性**：编码器必须按此长度输出，安全底线也按它判定。
    /// </summary>
    public int BodyLength => Shape switch
    {
        ShapeReference57 => 1 + 4 + 4 + VectorLength * 4 * 2,     // 57
        ShapeFlagReference57 => 1 + 1 + 4 + 4 + VectorLength * 4 * 2, // 58（flag + 57B 结构）
        ShapeFlagCountedU16 => 1 + 1 + 4 + 4 + 2 + CountEntries * 4 + TailBytes, // 13 / 33
        ShapeLegacy37 => 1 + 4 + 4 + 3 * 8 + 4,                   // 37
        ShapeFirstVector => 1 + 4 + 4 + 6 * 4,                    // 33
        ShapeAck => 1,                                            // 1（不安全，仅用于断言）
        ShapeZeroFill => ZeroBytes,
        ShapeCountedU16 => 1 + 4 + 4 + 2 + CountEntries * 4 + TailBytes,
        ShapeReferencePlusTail => 1 + 4 + 4 + VectorLength * 4 * 2 + 1, // 58（尾部补字节，与 flag 假设不同）
        _ => throw new InvalidDataException("未知的均价应答结构形状：" + Shape)
    };

    /// <summary>
    /// CMD 应答体首字节的 flag 取值：非 0 才走客户端成功分支。
    /// 参考实现用 <c>0x01</c>（`auction_avg_reply.txt:149-152`），本仓库 186 搜索应答也写 <c>0x01</c>，
    /// 因此所有带 flag 的候选统一用它，避免多一个自变量。
    /// </summary>
    public const byte BodyFlag = 0x01;

    /// <summary><see cref="ShapeZeroFill"/> 的填充长度（只用于「长度本身是不是门槛」的探针）。</summary>
    public int ZeroBytes { get; init; }

    /// <summary><see cref="ShapeCountedU16"/> 声明的条目数：**必须与实际提供的条目数一致**。</summary>
    public int CountEntries { get; init; }

    /// <summary>
    /// <see cref="ShapeCountedU16"/> 的尾部补零长度：让 count=0 的头部也达到自己的声明长度
    /// （也用于把 count=0 的头部抬到安全底线之上）。
    /// </summary>
    public int TailBytes { get; init; }

    /// <summary>参考结构的向量长度（A21 逆向为 6；<see cref="Ref65"/> 用 7 探「6 是不是版本特有」）。</summary>
    public int VectorLength { get; init; } = 6;

    /// <summary>
    /// **安全底线**：数据体不得小于该字节数。
    ///
    /// 依据（D95 实测 2026-10-11 04:14–04:15）：1 字节候选不回 <c>217 OVERFLOW_INFO</c>，
    /// 而是 12 秒后连接被 <c>IOException</c>（远程主机强迫关闭）强杀 = 客户端崩溃；
    /// 33B/37B/57B 候选都回 217 但不崩。⇒ 过短包体会让客户端在「读超」判定生效之前按计数/指针读到未初始化内存。
    /// 取 8 字节：短于一个最小头部 dword 的包体没有任何可用字段空间，风险与收益不成比例。
    /// </summary>
    public const int MinimumBodyBytes = 8;

    /// <summary>候选是否满足安全底线（定义期、编码期与发送期共用同一判据）。</summary>
    public bool IsSafe => BodyLength >= MinimumBodyBytes;

    /// <summary>参考实现里均价列表刷新的应答 opcode（CMD 795）；D95 是否需要未验证。</summary>
    public const int ListRefreshOpcode = 795;

    /// <summary>不发第二个应答（用于分辨「双应答是否必需」）。</summary>
    public const int NoSecondMode = -1;

    /// <summary>不发任何应答的哨兵（用于确认「客户端是否真的在等 182 的应答」）。</summary>
    public const int NoReplyOpcode = 0;

    // —— 候选集：**每个 ≥ 8B 且自洽**；差异是结构假设，不是长度阶梯 ——
    // 顺序即轮换顺序：带 flag 的候选排在最前（当前第一假设），其后是头部 flag 与尾部补字节的对照，
    // 再往后是已被实测证伪、只作基准的旧候选。

    /// <summary>
    /// ★★ 当前第一候选：**flag + 参考 57B 结构 = 58B**。
    ///
    /// 假设：客户端回调读的 57B 结构不变，但 CMD 路径在进回调前先吃掉 1 字节 flag，
    /// 而这 1 字节**属于应答体**（186 的实测体以 <c>01</c> 开头；帧头第 0 字节是 kind/CRC 区，不承担它）。
    /// 旧候选全部从 <c>mode</c> 起算 ⇒ 少给 1 字节，客户端按 <c>flag + 57B</c> 读就恰好读超
    /// （33B/37B/57B 全回 217，且 57B 时"只差 1"）。判据：**217 消失**即可认定 flag 假设成立。
    /// </summary>
    public static readonly AuctionAveragePriceReply Flag58 =
        new("flag58", 182, 0, 1, ShapeFlagReference57, false,
            "★ flag(0x01)+mode+key+unused+vector1/2 各 6 个 i32 = 58B：CMD 应答体的 flag 由服务端写入");

    /// <summary>
    /// flag + 计数头 = **33B**：<c>flag(1) + mode(1) + key(4) + unused(4) + u16 计数(2) + 5×4B 条目(20) + 1B 尾部(1)</c>。
    /// 与 <see cref="Flag58"/> 长度差 25B，用来把「flag 位置」与「长度够不够」两个变量分开。
    /// 判据：若 flag58 不溢而它仍溢 ⇒ 差的是长度/结构而不是 flag；两者都不溢 ⇒ 计数结构才是正解。
    /// </summary>
    public static readonly AuctionAveragePriceReply Flag33 =
        new("flag33", 182, 0, 1, ShapeFlagCountedU16, false,
            "flag+头部+u16 计数(=5)+5×4B 条目+1B 尾部 = 33B（flag 组短体对照）")
        { CountEntries = 5, TailBytes = 1 };

    /// <summary>flag + 计数头（u16 计数 = 3、3×4B 条目 + 1B 尾部）= 25B：flag 组的最小自洽短体。</summary>
    public static readonly AuctionAveragePriceReply Flag25 =
        new("flag25", 182, 0, 1, ShapeFlagCountedU16, false,
            "flag+头部+u16 计数(=3)+3×4B 条目+1B 尾部 = 25B（flag 组最小自洽短体）")
        { CountEntries = 3, TailBytes = 1 };

    /// <summary>参考实现结论（A21 客户端已验证）：双应答 mode=0→1、57B 结构、不发 795；**无 flag**。</summary>
    public static readonly AuctionAveragePriceReply A21Dual =
        new("a21-dual", 182, 0, 1, ShapeReference57, false,
            "参考结构（无 flag）：mode+key+unused+vector1/2 各 6 个 i32（57B）；D95 实测回 217");

    /// <summary>只发 mode=0：分辨「解锁输入框到底靠哪一条」。结构同参考实现。</summary>
    public static readonly AuctionAveragePriceReply A21FirstOnly =
        new("a21-first", 182, 0, NoSecondMode, ShapeReference57, false,
            "只发 mode=0（参考实现的价格入库点）；结构同 a21-dual");

    /// <summary>只发 mode=1：参考实现的反例——预期「物品名出现但输入框仍灰」。</summary>
    public static readonly AuctionAveragePriceReply A21SecondOnly =
        new("a21-second", 182, 1, NoSecondMode, ShapeReference57, false,
            "只发 mode=1（参考实现的反例）；结构同 a21-dual");

    /// <summary>双应答 + 补发 CMD 795 均价列表刷新（D95 是否需要未验证）。</summary>
    public static readonly AuctionAveragePriceReply A21DualList =
        new("a21-dual-list", 182, 0, 1, ShapeReference57, true,
            "a21-dual 结构 + 补发 795 列表刷新，分辨 795 是不是必需（781 已定案不发）");

    /// <summary>参考实现旧档结构：mode + key + unused + [price,0]×3 + i32(1) = 37B。</summary>
    public static readonly AuctionAveragePriceReply A21Legacy37 =
        new("a21-legacy37", 182, 0, 1, ShapeLegacy37, false,
            "参考实现格式 10 旧档：3 组 [price,0] + 尾部 i32(1) = 37B（D95 实测同样回 217）");

    /// <summary>
    /// 结构 1（首选）：**镜像 186 搜索应答头部**——mode + key + unused + u16 计数 + 1×4B 价格条目 = 15B。
    /// 假设：182 与 `0x00BA` 一样是「头部 + 计数 + 条目」的可变长列表，计数是 u16 且与实际条目数一致。
    /// 判据：217 消失 ⇒ 结构对；仍回 217 ⇒ 计数位置/宽度不对。
    /// </summary>
    public static readonly AuctionAveragePriceReply Recount15 =
        new("recount15", 182, 0, 1, ShapeCountedU16, false,
            "mode+key+unused+u16 计数(=1)+1×4B 价格条目 = 15B（镜像 186 头部：计数 + 条目列表）")
        { CountEntries = 1 };

    /// <summary>
    /// 结构 1 的零条数变体：u16 计数 = 0 + 2B 尾部 = 12B。
    /// 假设：客户端只读固定头部、不按计数循环。判据：217 消失 ⇒ 182 应答是「定长头部 + 计数」。
    /// </summary>
    public static readonly AuctionAveragePriceReply Recount12 =
        new("recount12", 182, 0, 1, ShapeCountedU16, false,
            "mode+key+unused+u16 计数(=0)+2B 尾部 = 12B：与 recount15 对比可分辨「计数循环」假设")
        { CountEntries = 0, TailBytes = 2 };

    /// <summary>
    /// 结构 2：**全 0 的 8B 数据体**。把「长度不足」与「字段取值引发计数循环」两个变量彻底分开：
    /// 全 0 ⇒ 任何被当计数读的字段都是 0 ⇒ 仍回 217 就是**长度门槛**，与字段取值无关；
    /// 217 消失 ⇒ 失败原因一直是某个字段被当成计数。
    /// </summary>
    public static readonly AuctionAveragePriceReply Zero8 =
        new("zero8", 182, 0, NoSecondMode, ShapeZeroFill, false,
            "全 0 的 8B 数据体：217 仍在 = 长度门槛；217 消失 = 某字段被当计数") { ZeroBytes = 8 };

    /// <summary>结构 2 的长度梯度（全 0）：13B，等于请求长度。</summary>
    public static readonly AuctionAveragePriceReply Zero13 =
        new("zero13", 182, 0, NoSecondMode, ShapeZeroFill, false,
            "全 0 的 13B 数据体（=请求长度）：与 zero8 一起定位长度门槛") { ZeroBytes = 13 };

    /// <summary>结构 2 的长度梯度（全 0）：33B，等于已被证伪的最小候选长度。</summary>
    public static readonly AuctionAveragePriceReply Zero33 =
        new("zero33", 182, 0, NoSecondMode, ShapeZeroFill, false,
            "全 0 的 33B 数据体：217 仍在 = 门槛 ≥34，排除「计数取值」假设") { ZeroBytes = 33 };

    /// <summary>结构 2 的长度梯度（全 0）：58B，参考实现带 flag 时的实际下发长度。</summary>
    public static readonly AuctionAveragePriceReply Zero58 =
        new("zero58", 182, 0, NoSecondMode, ShapeZeroFill, false,
            "全 0 的 58B 数据体：217 仍在 = 门槛 >58；消失 = 门户就在 58B 附近") { ZeroBytes = 58 };

    /// <summary>结构 2 的长度梯度（全 0）：64B，覆盖「客户端按 64B 缓冲精确读取」这一常见实现。</summary>
    public static readonly AuctionAveragePriceReply Zero64 =
        new("zero64", 182, 0, NoSecondMode, ShapeZeroFill, false,
            "全 0 的 64B 数据体：217 消失则客户端按 64B 固定结构读取") { ZeroBytes = 64 };

    /// <summary>结构 2 的长度梯度（全 0）：128B，确认「越长越安全」不是无限成立。</summary>
    public static readonly AuctionAveragePriceReply Zero128 =
        new("zero128", 182, 0, NoSecondMode, ShapeZeroFill, false,
            "全 0 的 128B 数据体：64B 不溢出而 128B 溢出 ⇒ 门槛就是精确值") { ZeroBytes = 128 };

    /// <summary>
    /// 参考 57B 布局 **+1 字节尾部** = 58B。★ 与 <see cref="Flag58"/> 是**两个不同的假设**：
    /// 本候选说「客户端读 58B，多出的那一字节在末尾」，<see cref="Flag58"/> 说「客户端先吃 1 字节 flag，
    /// 结构本身仍是 57B」。两者长度相同但字节分布不同（本候选 <c>[0]=mode</c>，flag 候选 <c>[0]=flag</c>），
    /// 因此哪个不溢就能直接判定多出的字节在头还是在尾。
    /// </summary>
    public static readonly AuctionAveragePriceReply Ref58 =
        new("ref58", 182, 0, 1, ShapeReferencePlusTail, false,
            "参考结构 +1B **尾部** = 58B（[0] 仍是 mode）：与 flag58 的「1B 在头部」是不同假设");

    /// <summary>参考结构但向量各 7 个 i32 = 65B：分辨「向量长度 6 是 A21 特有」。</summary>
    public static readonly AuctionAveragePriceReply Ref65 =
        new("ref65", 182, 0, 1, ShapeReference57, false,
            "参考结构但 vector 各 7 个 i32（65B）：分辨「向量长度 6」是不是 A21 特有")
        { VectorLength = 7 };

    /// <summary>应答 opcode 换成 183：分辨 D95 是否把 182 的回调注册在别的 opcode 上。</summary>
    public static readonly AuctionAveragePriceReply Opcode183Dual =
        new("op183-dual", 183, 0, 1, ShapeReference57, false,
            "参考结构但应答 opcode 用 183：分辨回调注册在哪个 opcode");

    /// <summary>
    /// 可轮换候选：**一律 ≥ <see cref="MinimumBodyBytes"/>**。已知不安全的实现（1B ACK）只在常量里保留，
    /// 不进表；构造时若有不满足底线的项直接抛异常，避免"悄悄发出去把客户端读崩"。
    /// </summary>
    public static readonly IReadOnlyList<AuctionAveragePriceReply> All = BuildAll();

    static IReadOnlyList<AuctionAveragePriceReply> BuildAll()
    {
        AuctionAveragePriceReply[] declared =
        [
            // 第一假设：应答体自带 flag（flag 在头部，结构不变）。
            Flag58, Flag33, Flag25,
            // 对照：同样 58B 但多余字节放在尾部（与 flag 假设互相独立）。
            Ref58,
            // 结构假设（无 flag）。
            Recount15, Recount12,
            // 长度门槛探针（全 0，无 flag）：已被 flag 假设取代，保留作长度基线。
            Zero8, Zero13, Zero33, Zero58, Zero64, Zero128,
            // 已实测回 217 的旧候选：只作基准。
            A21Dual, A21FirstOnly, A21SecondOnly, A21DualList, A21Legacy37, Ref65, Opcode183Dual
        ];
        var safe = new List<AuctionAveragePriceReply>(declared.Length);
        foreach (var candidate in declared)
        {
            if (!candidate.IsSafe)
                throw new InvalidDataException(
                    $"均价应答候选 {candidate.Name} 的数据体 {candidate.BodyLength}B 低于安全底线 {MinimumBodyBytes}B，禁止进入轮换");
            safe.Add(candidate);
        }
        return safe;
    }

    /// <summary>缺省候选：**当前第一假设 = 带 flag 的 58B**（见 <see cref="Flag58"/>）。</summary>
    public static AuctionAveragePriceReply Default => Flag58;

    /// <summary>
    /// 按名字取候选。**不安全候选不在表里**，因此这里返回 null（调用方报错），
    /// 而不是让一个会读崩客户端的候选通过。
    /// </summary>
    public static AuctionAveragePriceReply? Find(string name)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    /// <summary>可用候选名（轮换顺序即此顺序，实机按此表逐个前进）。</summary>
    public static string Names => string.Join('/', All.Select(candidate => candidate.Name));

    /// <summary>
    /// 候选自洽性校验：长度 ≥ 安全底线，且**声明的计数值与实际提供的条目数一致**。
    /// 编码器在真正写字节前调用，避免「声明 N 条只给 M 条」这种 D95 必读超的包。
    /// </summary>
    public static void Validate(AuctionAveragePriceReply? candidate)
    {
        if (candidate is null)
            throw new InvalidDataException("均价应答候选为空");
        if (candidate.BodyLength < MinimumBodyBytes)
            throw new InvalidDataException(
                $"均价应答候选 {candidate.Name} 的数据体 {candidate.BodyLength}B 低于安全底线 {MinimumBodyBytes}B");
        if (candidate.Shape == ShapeCountedU16 && candidate.CountEntries < 0)
            throw new InvalidDataException($"均价应答候选 {candidate.Name} 的条目数不能为负");
    }
}
