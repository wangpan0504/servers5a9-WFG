namespace DfoServer.Game.Dungeons;

// 本服兼容资格：同角色、入场业务日、资源源怪模板一次；不以临时对象键或房间规避。
// Eligible为原死亡冻结时的查询结果；存储必须在原死亡事务内再次核验，空抽样同样消费。
internal sealed record OrdinaryLukeDropClaim(int Day, int SourceMonsterId, bool Eligible);
