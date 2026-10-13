using DfoServer.Game.Auction;

namespace DfoServer.Development.Auction;

/// <summary>
/// 拍卖行开发诊断：把「记录布局 + 物品模板 + 应答 opcode」与「均价查询应答候选」做成两组候选，
/// 分别按下标轮换，用于在实机上收敛尚未确认的布局与结构。
///
/// 搜索应答的记录布局与通道已定案（CMD 186、168B 记录、道具块 @43 宽 83B），因此搜索候选轮换不再是默认行为：
/// 探针关闭（缺省）时拍卖行搜索走真实挂牌数据，只有显式给出 <c>presets</c>/<c>rows</c>/<c>total</c> 才覆盖。
///
/// ★ CMD 182 均价查询是本开关的主要用途，且**只影响 182**：
/// 搜索候选与均价候选各有**独立**的轮换下标 —— 每次搜索请求只推进搜索下标，
/// 每次 182 请求只推进均价下标。因此「每拖一次物品前进一个均价候选」不会顺带改掉搜索返回什么，
/// 用户不会因为切均价候选而看到空列表（<c>avgonly=1</c> 可把搜索整条路径也钉在真实数据上）。
///
/// 用法（运行参数，放在命令之前）：
///   <c>--auction-probe "avg=zero8,zero13,recount15;avgonly=1"</c>
///   <c>--auction-probe off</c>（或缺省）= 关闭：186/187 走真实挂牌数据，182 走参考实现的默认候选。
///
/// 键（除 presets/avg 外都接受逗号列表，按各自下标轮换取模，因此「看到哪件物品 / 页数是否变化」能反推是哪一套组合）：
///   rows 行数、items 物品模板 id、count 堆叠数、price 当前价、buyout 一口价（-1 = 纯竞拍）、
///   total 头部「匹配总件数」覆盖值（默认 -1 = 用本批条数）、
///   reply 应答 opcode（186 或 187）、kind 包种类（cmd 应答 / noti 通知）、
///   pad 应答尾部补零字节数（默认 512）、
///   presets 搜索候选布局名（见 AuctionReplyLayout.Names；默认只用已定案的合同布局）、
///   avg 均价查询（CMD 182）应答候选名（见 AuctionAveragePriceReply.Names；默认只用参考实现的结论）、
///   mylist 「我的拍卖品」（CMD 188/189/190）记录布局候选名（见 AuctionMyListReply.Names；
///     默认每个页签各用参考实现的候选，见 AuctionMyListReply.DefaultFor）、
///   bid 搜索记录**逐字段**探针候选名（见 AuctionSearchRecordProbe.Names；每个候选只改记录里的一个字段。
///     ★ 已定案：本版 D95 客户端没有竞拍入口，这七个候选与入口无关（判据不存在，不是位置未解），
///     因此它们已退出默认轮换，只按名字显式可达 —— 保留的是排查现场与「只改一个字段」的编码回归；
///     默认 contract = 一个字段都不改）。
///   bidmode 搜索应答的 **mode 轮换集**（0/1，见 AuctionProbeSettings.SearchMode；
///     默认回显请求首字节）。同一个搜索请求交替用 mode=0 / mode=1 应答、**记录内容完全相同**，
///     用于验证**应答 mode 决定窗口 6/7 分流**（已实机闭环：客户端按应答 mode 分流到
///     GetWindow(6)/(7) 两套填充）；它**不影响竞拍入口**（本版客户端没有该控件）。
///     只配 avg=/mylist=/bid=/bidmode= 时**不会**替换真实搜索结果（见 AuctionProbeSettings.SearchOverride）。
///   avgonly 只轮换均价候选：1 = 搜索一律走真实挂牌数据（不覆盖行数/总数/opcode），0 = 允许搜索候选覆盖。
///
/// 服务端日志 <c>auction_search</c> / <c>auction_average_price_reply</c> 分别记录本次实际使用的候选名、
/// 数据体长度与十六进制，因此实机每拖一次物品就换一个均价候选，一轮即可分辨哪种结构能让价格输入框解锁。
/// <c>auction_my_list</c> 同样记录「哪个页签用了哪个候选、体长、count 与实际下发的十六进制」；
/// <c>auction_search_result</c> 记录本次搜索记录探针的候选名与被改写的字段。
/// <c>C2S 217 OVERFLOW_INFO</c> 的 body 是出问题包的头 3 字节（<c>01 B6 00</c> = 182 包体读超），是「结构不对」的机器判据。
/// 这是开发诊断设施，不表达任何业务规则。
/// </summary>
internal sealed class AuctionProbe(AuctionProbeSettings settings)
{
    int search;
    int average;
    // 搜索记录字段探针、应答 mode 轮换与「我的拍卖品」各有自己的下标（与搜索布局/均价候选一样互不干扰）。
    int searchRecord;
    int searchMode;
    // 「我的拍卖品」三个页签各有自己的下标：点「我的上架」换掉的是 188 的候选，189/190 仍从各自第一项开始。
    readonly int[] myLists = new int[3];

    public static AuctionProbe Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec) || string.Equals(spec.Trim(), "off", StringComparison.OrdinalIgnoreCase))
            return new AuctionProbe(AuctionProbeSettings.Disabled);
        int rows = 3, count = 1, price = 12_000, buyout = -1;
        bool averageOnly = false;
        // 只有显式给出下列键才算「覆盖搜索应答」：否则搜索仍读真实挂牌（为了看字段而开探针不该换掉真实数据）。
        bool searchOverride = false;
        IReadOnlyList<int> pads = [512], totals = [-1];
        IReadOnlyList<int> items = [490704576];
        IReadOnlyList<int> replies = [186];
        IReadOnlyList<bool> kinds = [false];
        IReadOnlyList<AuctionReplyLayout> presets = [AuctionProbeSettings.Contract];
        IReadOnlyList<AuctionAveragePriceReply> averages = [AuctionAveragePriceReply.Default];
        IReadOnlyList<AuctionMyListReply> myListCandidates = [];
        IReadOnlyList<AuctionSearchRecordProbe> recordCandidates = [];
        IReadOnlyList<int> searchModes = [];

        foreach (string field in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = field.Split('=', 2);
            if (pair.Length != 2)
                throw new InvalidDataException("--auction-probe 需要 key=value 片段：" + field);
            string key = pair[0].Trim().ToLowerInvariant();
            string value = pair[1].Trim();
            switch (key)
            {
                case "rows": rows = Number(value, key, 0, 200); searchOverride = true; break;
                case "count": count = Number(value, key, 1, 10_000); searchOverride = true; break;
                case "price": price = Number(value, key, 0, int.MaxValue); searchOverride = true; break;
                case "buyout": buyout = Number(value, key, -1, int.MaxValue); searchOverride = true; break;
                case "pad": pads = Numbers(value, key, 0, 4096); searchOverride = true; break;
                case "total": totals = Numbers(value, key, -1, int.MaxValue); searchOverride = true; break;
                case "items": items = Numbers(value, key, 1, int.MaxValue); searchOverride = true; break;
                case "avgonly": averageOnly = Number(value, key, 0, 1) == 1; break;
                case "kind":
                    var parsedKinds = new List<bool>();
                    foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        parsedKinds.Add(part.ToLowerInvariant() switch
                        {
                            "cmd" => false,
                            "noti" => true,
                            _ => throw new InvalidDataException("--auction-probe kind 只接受 cmd/noti：" + part)
                        });
                    if (parsedKinds.Count == 0)
                        throw new InvalidDataException("--auction-probe kind 不能为空");
                    kinds = parsedKinds;
                    searchOverride = true;
                    break;
                case "reply":
                    var opcodes = new List<int>();
                    foreach (int opcode in Numbers(value, key, 0, ushort.MaxValue))
                    {
                        if (opcode is not (186 or 187))
                            throw new InvalidDataException("--auction-probe reply 只接受 186/187：" + opcode);
                        opcodes.Add(opcode);
                    }
                    replies = opcodes;
                    searchOverride = true;
                    break;
                case "presets":
                    var parsed = new List<AuctionReplyLayout>();
                    foreach (string name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        parsed.Add(Preset(name));
                    if (parsed.Count == 0)
                        throw new InvalidDataException("--auction-probe presets 不能为空");
                    presets = parsed;
                    searchOverride = true;
                    break;
                case "avg":
                    var parsedAverages = new List<AuctionAveragePriceReply>();
                    foreach (string name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        // 不在 AuctionAveragePriceReply.All 里的名字一律拒绝：不安全候选（数据体 < 8B 会读崩客户端）
                        // 根本不进表，因此这里也点不到它们。
                        parsedAverages.Add(AuctionAveragePriceReply.Find(name) ??
                            throw new InvalidDataException(
                                $"未知或已禁用（不安全）的均价应答候选：{name}；可用：{AuctionAveragePriceReply.Names}"));
                    }
                    if (parsedAverages.Count == 0)
                        throw new InvalidDataException("--auction-probe avg 不能为空");
                    averages = parsedAverages;
                    break;
                case "mylist":
                    var parsedLists = new List<AuctionMyListReply>();
                    foreach (string name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        // 与 avg= 同一口径：不在 AuctionMyListReply.All 里的名字一律拒绝。
                        // 不安全候选（记录放不下 83B 道具块、或体长会短于空列表头部）根本不进表，因此也点不到。
                        parsedLists.Add(AuctionMyListReply.Find(name) ??
                            throw new InvalidDataException(
                                $"未知或已禁用的「我的拍卖品」候选：{name}；可用：{AuctionMyListReply.Names}"));
                    }
                    if (parsedLists.Count == 0)
                        throw new InvalidDataException("--auction-probe mylist 不能为空");
                    myListCandidates = parsedLists;
                    break;
                case "bid":
                    var parsedRecords = new List<AuctionSearchRecordProbe>();
                    foreach (string name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        // 与 avg=/mylist= 同一口径：不在候选表里的名字一律拒绝（探针类型未知就无法保证「只改一个字段」）。
                        parsedRecords.Add(AuctionSearchRecordProbe.Find(name) ??
                            throw new InvalidDataException(
                                $"未知的搜索记录探针候选：{name}；可用：{AuctionSearchRecordProbe.Names}"));
                    }
                    if (parsedRecords.Count == 0)
                        throw new InvalidDataException("--auction-probe bid 不能为空");
                    recordCandidates = parsedRecords;
                    break;
                case "bidmode":
                    // 只接受 0/1：客户端按应答 mode 字节分流到两个窗口类型，其它取值没有对应分支可判。
                    searchModes = Numbers(value, key, 0, 1);
                    break;
                default: throw new InvalidDataException("未知的 --auction-probe 键：" + key);
            }
        }
        return new AuctionProbe(new AuctionProbeSettings(true, averageOnly, presets, 0, replies, kinds, rows, totals,
            items, count, price, buyout, pads, averages)
        {
            MyLists = myListCandidates,
            SearchRecords = recordCandidates,
            SearchModes = searchModes,
            SearchOverride = searchOverride
        });
    }

    /// <summary>按候选名取布局：已定案的合同布局不在候选表里，需要单独识别，便于与旧候选对照。</summary>
    static AuctionReplyLayout Preset(string name) =>
        AuctionReplyLayout.Find(name) ?? (string.Equals(name, AuctionProbeSettings.Contract.Name, StringComparison.OrdinalIgnoreCase)
            ? AuctionProbeSettings.Contract
            : throw new InvalidDataException($"未知的拍卖行布局候选：{name}；可用：{AuctionProbeSettings.Contract.Name}/{AuctionReplyLayout.Names}"));

    /// <summary>取本次搜索请求使用的参数；开启时每调用一次推进一个**搜索**候选。</summary>
    public AuctionProbeSettings NextSearch()
    {
        // replyMode 用独立下标：开 bidmode= 不会推进 presets/rows 的下标，反之亦然，
        // 两个自变量各自的轮换互不干扰。做窗口 6/7 分流对照时**只**配 bidmode=，
        // 记录内容（真实挂牌行、记录字段探针）全程不变，界面差异只能来自应答 mode。
        // ★ 该分流已实机闭环；它与「竞拍入口」**无关** —— 本版客户端没有那个控件。
        int replyMode = NextSearchMode();
        // 没有显式覆盖搜索（rows/presets/total/reply/kind/pad/items/count/price/buyout）时搜索走**真实挂牌数据**：
        // 只是为了看「某个记录字段如何影响按钮」而开探针，不该把真实搜索结果换成合成行。
        // 搜索记录探针与 mode 轮换仍然生效（它们只改真实应答里的一个字段 / 一个字节），所以这里把它们带上。
        if (!settings.Enabled || settings.AverageOnly || !settings.SearchOverride)
            return AuctionProbeSettings.Disabled with { SearchRecord = NextRecord(), SearchMode = replyMode };
        int index = Interlocked.Increment(ref search) - 1;
        return settings with { PresetIndex = index, SearchRecord = NextRecord(), SearchMode = replyMode };
    }

    /// <summary>
    /// 取本次搜索应答要写的 mode 字节：每来一次搜索请求前进一个，与搜索布局/记录字段探针互相独立。
    /// 没有给 <c>bidmode=</c> 时返回 <see cref="AuctionProbeSettings.EchoRequestMode"/>（回显请求首字节，既有行为）。
    /// </summary>
    int NextSearchMode()
    {
        if (!settings.Enabled || settings.SearchModes.Count == 0)
            return AuctionProbeSettings.EchoRequestMode;
        int index = Interlocked.Increment(ref searchMode) - 1;
        return settings.SearchModes[index % settings.SearchModes.Count];
    }

    /// <summary>
    /// 取本次搜索请求使用的**记录字段探针**候选：每来一次搜索请求前进一个，与搜索布局下标互相独立
    /// （布局定案后 `presets` 通常不动，而字段探针要靠连续搜索逐个试）。
    /// 没有给 <c>bid=</c> 时返回对照候选（一个字段都不改）。
    /// </summary>
    AuctionSearchRecordProbe NextRecord()
    {
        if (!settings.Enabled || settings.SearchRecords.Count == 0)
            return AuctionSearchRecordProbe.Default;
        int index = Interlocked.Increment(ref searchRecord) - 1;
        return settings.SearchRecords[index % settings.SearchRecords.Count];
    }

    /// <summary>
    /// 取本次 CMD 182 请求使用的参数；开启时每调用一次推进一个**均价**候选。
    /// 与 <see cref="NextSearch"/> 共用同一份设置但用独立下标：拖物品换均价候选不会改变搜索返回什么。
    /// </summary>
    public AuctionProbeSettings NextAverage()
    {
        if (!settings.Enabled)
            return AuctionProbeSettings.Disabled;
        int index = Interlocked.Increment(ref average) - 1;
        return settings with { AverageIndex = index };
    }

    /// <summary>
    /// 取本次「我的拍卖品」（CMD 188/189/190）请求使用的记录布局候选。
    ///
    /// **每个页签一个独立下标**：点「我的上架」只推进 188 的候选，189/190 各自从第一项开始，
    /// 因此一次点击换一种记录形状，用户不会在同一轮里对着三个页签比较三种不同的假设。
    /// 探针关闭、或没有给 <c>mylist=</c> 时返回该页签的默认候选（参考实现的量）。
    /// </summary>
    public AuctionProbeSettings NextMyList(AuctionMyListPanel panel)
    {
        var fallback = AuctionMyListReply.DefaultFor(panel);
        if (!settings.Enabled || settings.MyLists.Count == 0)
            return AuctionProbeSettings.Disabled with { MyList = fallback };
        int slot = (int)panel - (int)AuctionMyListPanel.Registed;
        if (slot < 0 || slot >= myLists.Length)
            throw new InvalidDataException("未知的拍卖行页签：" + panel);
        int index = Interlocked.Increment(ref myLists[slot]) - 1;
        return settings with { MyList = settings.MyLists[index % settings.MyLists.Count] };
    }

    static IReadOnlyList<int> Numbers(string text, string key, int min, int max)
    {
        var values = new List<int>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            values.Add(Number(part, key, min, max));
        if (values.Count == 0)
            throw new InvalidDataException($"--auction-probe {key} 不能为空");
        return values;
    }

    static int Number(string text, string key, int min, int max)
    {
        if (!int.TryParse(text, out int value) || value < min || value > max)
            throw new InvalidDataException($"--auction-probe {key} 需要 {min}..{max} 之间的整数：" + text);
        return value;
    }
}
