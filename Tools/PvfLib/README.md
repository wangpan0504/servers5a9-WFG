# PvfLib

面向 .NET 10 的独立、只读 PVF 解析库。通过项目引用使用，不依赖 DfoServer、SQLite 或外部解析进程。支持 `nkpi`、`protected_nkpi` 两种归档布局，包含文件索引、字符串池、分块读取、原始 token 和脚本文本查询。

## 代码分层

| 位置 | 职责 |
|---|---|
| [PvfArchive](PvfArchive.cs) | 公共读取入口、文件索引、缓存与资源生命周期 |
| [IO](IO) | 文件偏移读取、内存输入与路径归一化 |
| [Format](Format) | 格式编解码、解压边界和字符串池 |
| [Models](Models) | 文件元数据、原始 token、物品索引和装备/堆叠物品声明 |
| [Scripting](Scripting) | token 解码、文本展示、字段区间索引及 Section/Block/List 查询 |
| [Caching](Caching) | 按字节预算淘汰的 LRU 缓存；同键并发共享加载 |

库描述资源结构；装备槽位、物品准入、职业条件、价格计算及缺省策略属于服务端 [GameWorld](../../Server/DfoServer/GameWorld) 的各类 Catalog。职业、技能、任务和副本领域模型也在 GameWorld。库不依赖服务端模型，也不负责写回或打包 PVF。

## 使用

引用 [PvfLib.csproj](PvfLib.csproj)，所有公共类型位于 `PvfLib` 命名空间：

```csharp
using PvfLib;

using var archive = PvfArchive.Open("Data/Pvf/Script.pvf");
PvfToken[] tokens = archive.ReadTokens("equipment/equipment.lst");
string text = archive.ReadText("character/character.lst");
IReadOnlyDictionary<int, string> characters = PvfScript.List(text);
```

- `Files`、`FindFile`、`Contains` 查询归一化目录。路径忽略大小写、统一分隔符和相对段；越过归档根的路径会失败。同名归一化条目取首项。
- `ReadRaw` 返回调用方独占的字节数组；`ReadTokens` 返回完整的 5 字节记录序列，不丢弃未知类型。两者都不能修改内部缓存。
- `ReadText` 对 DataType 3 解码 UTF-16LE，对 DataType 1 提供兼容文本展示。浮点展示保留两位小数；类型 5/7 是引用占位，类型 9 等组合值不展开。需要精确数值或引用含义时必须使用 `ReadTokens`，不能从展示文本反推。
- `ResolveString` 按偏移读取字符串池。负值表示空字符串；窄字符串优先 UTF-8，无法解码时使用 GB18030；宽字符串使用 UTF-16LE。
- `ScanRawFiles` 按压缩块扫描，不把扫描产生的块填入常规读取缓存。回调中的读取函数按需解码并返回独立副本；读取错误向回调传播。读取函数应在当前回调内使用，长期保存会延长分块的存活时间。

`Open` 持有只读文件句柄，按偏移读取压缩内容；同一归档支持并发读取。所有 Catalog 及读取任务结束后，由归档 owner 调用 `Dispose`。不要与尚未完成的读取并发释放，也不要在归档打开期间改写源文件。服务端由应用入口管理该生命周期。

`OpenBytes` 复制输入并持有独立快照，适合合成测试和较小输入。格式解析检查表范围、字符串终止、文件范围、token 完整性及解压长度；当前单份归档及单块解压上限为 512 MiB。解析异常直接向调用方报告。

## 物品模型与字段查询

- [PvfItemIndex](Models/PvfItemIndex.cs) 读取装备或堆叠物品列表，检查 ID、路径及资源类型。堆叠列表允许 ID 0；列表归属不取决于文件扩展名。资源身份不代表游戏中允许创建该物品实例。
- [EquipmentFile](Models/EquipmentFile.cs) 和 [StackableFile](Models/StackableFile.cs) 的 `Parse` 读取单份脚本的原始声明，保留类型参数和显式零值。缺失、重复或尚未支持的声明记入 `UnsupportedFields`，不补业务默认值；堆叠物品明确声明的单值价格、上限、等级若不是非负整数，解析失败。模型不会保留整个 token 数组。
- [PvfDocument](Scripting/PvfDocument.cs) 一次索引行首标签，`Has` / `Section` 按标签查找，`Block` 读取成对标签。标签忽略大小写，同名 Section 取首个声明，重复完整 Block 拒绝读取。它是字段视图，不是完整的嵌套语法树；精确数值与复杂引用仍应从原始 token 解释。
- `EquipmentFile.ReadScript` 返回合并导入后的 `PvfDocument`。导入路径相对 `equipment/`；子脚本优先，与导入标签的位置无关。父模板只补未声明字段，显式空字段、空块和零值均覆盖父模板。重复导入、循环、缺失或畸形引用直接失败。

```csharp
var declared = EquipmentFile.Parse(1, "equipment/example.equ",
    archive.ReadTokens("equipment/example.equ"), archive.ResolveString);
var effective = EquipmentFile.ReadScript(archive.ReadText, "equipment/example.equ");
string name = effective.Section("name");
var stackable = new PvfDocument(archive.ReadText("stackable/example.stk"));
```

原始声明用于资源检查，有效字段用于业务投影；不能把 `UnsupportedFields` 直接当作游戏准入条件。Catalog 缓存紧凑的声明或业务模板，字段视图在投影后即可释放，不为所有物品长期保留全文、字段索引或 token。服务端物品模板的同键首次请求共用解析，完成后移除在途记录；失败结果不进入模板缓存。

## 内存与缓存

文件模式只在打开时读取目录和字符串池，压缩内容按需读取。已知长度的块直接解压到最终数组；token 和文本从内部只读块解析，避免再次复制整份脚本。

文件索引以共享目录前缀和文件名组成键，条目直接保存在字典中；同名不同目录相互独立，规范化后的重复路径仍保留首项。查询使用字符串切片匹配目录和文件名，不为两段额外分配字符串。`Files` 保持首项在归档中的顺序，枚举时重建完整路径；全量枚举与调用方长期保存这些路径会产生额外分配，应按实际需要选择查询或扫描。释放归档时同时释放索引与字符串池。

[PvfReadOptions](PvfReadOptions.cs) 默认给解压块 64 MiB、文本 16 MiB 的缓存预算。超预算按最近使用顺序淘汰；单项大于预算时仍正常读取，但不缓存。设置某项预算为 0 可关闭该缓存。预算按数组长度或文本大小估算，每项至少计 64 字节；`CachedChunkBytes`、`CachedTextBytes` 可查看当前计量。

这两个预算不是整个进程的内存上限：文件索引、字符串池、字典节点、调用方返回值、在途解码和 Catalog 另占内存。全量扫描还会建立分组及业务输出。不要为每个角色重新打开归档，也不要将全部文本或 token 再复制进无界缓存。

## 构建与验证

从仓库根目录执行：

```powershell
dotnet build Tools/PvfLib/PvfLib.csproj -c Release
dotnet run --project Tools/PvfLib.Tests -c Debug
```

[独立测试](../PvfLib.Tests)只引用 PvfLib，用合成输入覆盖格式、路径、token、文本、字段边界、物品声明、装备继承、损坏数据、缓存、并发及句柄生命周期，不需要实际 PVF。修改库后还须按[贡献指南](../../CONTRIBUTING.md#验证)运行服务端资源与 TCP 回归，确认 Catalog 和发布包集成。

只读性能测量：

```powershell
dotnet run --project Tools/PvfLib.Tests -c Release -- --benchmark ./Script.pvf 5000
```

输出 JSON 行，依次记录打开归档、读取三个索引、枚举和选取文件、首次读取及重复读取指定数量的装备/堆叠脚本；包含耗时、分配量、GC 后保留的托管内存、进程提交量和累计工作集峰值。文件按路径排序选取，末行记录实际工作量与缓存用量。

对比须使用同一输入、工作量和 Release 配置，在独立进程中重复运行，记录全部样本并比较中位数。GC 在计时区间之外执行；操作系统文件缓存会影响结果，新进程不等于磁盘冷读。库基准不代表完整服务启动时间或玩家请求延迟。
