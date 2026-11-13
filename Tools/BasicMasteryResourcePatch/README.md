# 基础精通95级上限

匹配本版16职业 `skill/<job>/basicattackup.skl`，仅将唯一 `[maximum level]` 整数100改为95。原有固定成长为 required=1、interval=1、add=1、不适用达人，保持全部等级系数、TP技能、技能定义及引用。

用PowerShell7先运行 `Prepare-Delta.ps1 -PvfPath <累计PVF> -PvfLibPath <PvfLib.dll> -OutputDirectory <新差量目录>`，再运行 `Pack-And-Verify.ps1 -ManifestPath <delta.json> -CandidatePath <新候选PVF> -PvfLibPath <PvfLib.dll> -PvfReplacePath <pvfreplace.exe> -ReceiptPath <新收据>`。输出不得覆盖源或已有产物；源metadata/目标raw摘要必须匹配，重打前确认除上限整数外没有变化，默认全raw审计仅允许16目标。其他累计差量应由资源owner按前置raw摘要合并。

服务端已有固定技能免费基线与双树同步，创建、登录初始化、成长和方案学习共用资源公式，无需硬编码技能ID。`skills` TCP组包含全职业1–95级、达人不加级、超上限封顶、创建/成长双树及旧100级校正；既有协议与持久化重登回归继续执行。资源和TCP通过不等于客户端实际显示验收。
