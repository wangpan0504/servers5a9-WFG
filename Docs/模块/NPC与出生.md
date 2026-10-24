# NPC传送与选角出生

本页定义 NPC 传送、最后地区存档和选角出生合同，目标为 S5A9 客户端与 PVF。帧格式见[基础协议](../协议/帧与频道.md)；结构、owner/revision 和提交后通知按[业务规范](../规范/服务端架构.md)执行。

## 1. NPC请求与准入

NPC使用C2S `(1,36)` 的16B区域请求，不是独立新opcode：

```text
town:u8 area:u8 x:u16 y:u16 movement:u8=5
previousTown:u16 previousArea:u16 targetNpc:u32 pathKind:u8
```

Parser保留完整targetNpc、pathKind及坐标。GameSession校验当前owner和就绪状态，TownTravel核对源区域并规划目标；targetNpc非零时，TownCatalog根据传入的等级和任务集合，验证来源当前阶段地图中的NPC存在move town关系指向该ID、目标当前阶段地图实际放置该NPC，以及目标等级和AND/OR/排除任务条件。关系和放置从匹配PVF加载，不硬编码NPC白名单。

| 当前实例 | 来源NPC / 目标NPC | 实收C2S36 |
| --- | --- | --- |
| 灰色墓地60/1 → 普雷普通等候区118/0 | 8001亚瑟 → 400000062亚瑟 | `76007B01E500053C0001003E84D71700`，目标379/229、kind0 |
| 普雷等候区118/0 → 灰色墓地60/1 | 400000062 → 8001 | `3C0174045A000576000000411F000000`，目标1140/90、kind0 |

NPC地图坐标只用于资源身份，不能推出传送坐标公式。上述反向1140/90不等于静态NPC位置或NPC+30；所有C2S36原样保存和回送x/y。未知NPC关系在领域层拒绝、保留连接，不因targetNpc非零关闭会话。该传送不意味着普雷副本/团本已实现。

## 2. 出生点、返回地区和提交

[GameSession.Select](../../Server/DfoServer/Game/Session/GameSession.cs)按账号与 slot 装载目标角色，[CharacterSelection](../../Server/DfoServer/Game/Characters/CharacterSelection.cs)负责入城准备规则：

1. 从当前地区、已有Return和PVF解析最后可返回地区。街道优先自身城镇可用房间入口；房间优先已有Return；无Return的NPC特殊区尝试反向NPC所在地区；最终默认艾尔文防线。
2. 普通名册选角统一出生共享赛丽亚房间38/1的gate 450,234，Direction按Room默认值重置，不恢复离线站立x/y。原生换频道直接选角保留合法的已提交城镇位置，包序见[原生换频道重连](../协议/角色与入城.md#原生换频道重连)。
3. 若出生和返回点尚未规范化，在当前generation及角色revision内提交位置/返回字段；已规范化时不重复写入。
4. 按[物品规则](背包与装备.md#owner生命周期与并发)清理到期实例，再按资源初始化金币携带上限；每一步使用上一笔提交返回的实际角色，无变化不重复写库。
5. 全部准备成功后由 Session 安装 Selected 和完成任务集合、递增选角 epoch，发送完整入城序列；房间24后发送1379返回town，最后空124，随后Ready=true。

出生、到期清理与金币上限沿用独立的顺序提交。准备期间保持原 owner 锁，失败不安装 Selected；先前已经提交的阶段保留，重连装载后只处理尚未完成的变化。每次物品清理的实例与审计仍在同一事务中原子提交，不把整个选角流程描述为一笔事务。技能初始化与发点边界见[技能学习](技能学习.md)。

Return在街道也保留地区门口，进入无赛丽亚出口的NPC区域时保持原地区。例如60/1→118/0仍记住灰色墓地。出生重置不修改职业、等级、任务、CG或技能；提交失败不发布Selected、不发送选角成功。

空body1426只允许Ready且在房间的当前owner，落在Return对应的PVF门口。C2S36出房仍使用客户端请求坐标。普通切图成功后发23→24，入房追加1379；不重发22或124。旧记录的Return为空时由领域解析恢复，不需要为了该规则批量改写旧库。

### 月轮山世界地图显示

匹配资源已注册月轮山 `town18/area0 → worldMap30`，`region/minimap/shonan.mm` 的节点却仍引用任务13700，两端任务目录均无该定义。客户端 `028E322A` 查询完成集合；缺少该号时节点保持默认状态1，`028CBE70` 的状态3谓词为假，绘制调度 `028D7D12` 和悬浮调度 `028D8B76` 同时跳过节点覆盖层，因此双剑、等级和地下城预览一起缺失。素喃其他两节点3616、3622仍使用真实任务进度。

服务端在所有 NOTI356 完成表中补 `flags[13700]=1`，作为该构建孤立资源引用的显示兼容规则。空历史角色也发送初始化完成表，任务列表刷新、完成或放弃后的完成表继续保留它。`023A009B → 02038090` 在查任务定义前安装此完成位；回调经 `028DD100` 清地图状态及等级缓存，再经 `028E3EA0` 重建状态3。此规则不代表恢复了原服任务13700的业务定义，也不写角色/账号完成记录，不发奖励，不改变服务器城镇或副本准入。

协议与回归见[角色与入城](../协议/角色与入城.md)、[WorldMapHoverTests](../../Server/DfoServer/SelfTests/WorldMapHoverTests.cs)和[城镇 TCP 回归](../../Server/DfoServer/SelfTests/Tcp/TownProtocolTcpTests.cs)。静态调用链和隔离机器码回放确认共同门禁，TCP确认真实包序及存档隔离；匹配客户端实际画面仍需用户验收。

## 3. 副本入口边界

118/0右侧的负数目标属于副本选择，不作为普通城镇门。当前应答、诊断和失败格式由[副本模块](副本.md)与[副本协议](../协议/副本.md#ack15成功与失败)统一维护；普通 NPC 传送不复用副本加载通知。

## 4. 入口和验证

实现入口：[TownRequests](../../Server/DfoServer/Network/Parsers/TownRequests.cs)、[GameSession](../../Server/DfoServer/Game/Session/GameSession.cs)、[TownCatalog](../../Server/DfoServer/GameWorld/Towns/TownCatalog.cs)、[CharacterRepository](../../Server/DfoServer/Persistence/CharacterRepository.cs)、[TownHandler](../../Server/DfoServer/Network/Handlers/TownHandler.cs)及[TownProjection](../../Server/DfoServer/Network/Projection/TownProjection.cs)。

[NpcTravelSpawnTests](../../Server/DfoServer/SelfTests/NpcTravelSpawnTests.cs)覆盖双向NPC、权限、未知NPC、完整u16落点、最后地区、不同街道/NPC区域重登、出生事务失败、stale owner及已规范化存档不重复写入。[隔离TCP回归](../../Server/DfoServer/SelfTests/Tcp/TownProtocolTcpTests.cs)覆盖真实body、23/24原样坐标、1379地区、ACK15、后续移动/NPC返回和重新选角，均使用独立数据库。

已确认部分 NPC 往返请求提交原样坐标，之后仍可进行城镇传送与移动。新增NPC、地图阶段和重登UI仍由匹配客户端实际操作确认；窗口Ready、自动化与用户画面确认分别记录。
