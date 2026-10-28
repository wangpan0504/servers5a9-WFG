using DfoServer.Game.Raids;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 纯服务端状态机与合成观察输入；不证明clientpatch/native真实Town对象已就绪，也不发送原版协议。
internal static class LukeSceneReceiptTests
{
    internal static void RunCore()
    {
        int before=Count;ContractVectors();var clock=new LukeRaidClockTests.Clock();
        var identity=new LukeSceneReceiptIdentity("scene-account",1,42,1,1,Guid.NewGuid(),0,0,32,4,1);
        var disabled=new LukeSceneReceipt(clock:clock);disabled.SetCapability(true);
        That(disabled.Begin(identity,1000) is null,"默认关闭不创建scene请求");
        var gate=new LukeSceneReceipt(true,clock);
        That(gate.Begin(identity,1000) is null,"无已协商能力不创建请求");
        gate.SetCapability(true);
        foreach(int ttl in new[]{0,-1,30001})That(gate.Begin(identity,ttl) is null,"TTL越界不创建请求");
        foreach(var invalid in new[]{identity with{Account=""},identity with{ConnectionId=0},identity with{OwnerGeneration=0},identity with{SelectionEpoch=0},
            identity with{RaidInstance=Guid.Empty},identity with{Phase=-1},identity with{Phase=2},identity with{PhaseGeneration=-1},
            identity with{TargetRevision=0},identity with{TargetRevision=-1}})
            That(gate.Begin(invalid,1000) is null,"服务端权威身份结构失效不能创建请求");
        That(gate.Begin(identity with{Character=ushort.MaxValue,Town=byte.MaxValue,Area=byte.MaxValue,TargetRevision=long.MaxValue},30000) is not null,
            "逻辑合同ushort/byte/正int64上界；真实资源有效性仍由未来服务端入口校验");
        foreach(string? id in new[]{null,"","00000000000000000000000000000000","0123456789ABCDEF0123456789abcdef","01234567-89ab-cdef-0123-456789abcdef"})
            That(!LukeSceneReceipt.ValidRequestId(id),"requestId拒绝非规范或全零输入");
        foreach(string id in new[]{"00000000000000000000000000000001","0123456789abcdef0123456789abcdef"})
            That(LukeSceneReceipt.ValidRequestId(id),"规范128bit ID作为opaque小写hex，不依赖GUID端序");
        That(gate.Begin(identity with{Character=0},1) is not null,"角色0可作为ushort身份，不能用0代替未验证身份");
        var request=gate.Begin(identity,1000)!;
        That(LukeSceneReceipt.ValidRequestId(request.RequestId)&&request.CharacterId==42&&request.Town==32&&request.Area==4&&
            request.TargetRevision==1&&request.TtlMs==1000&&request.Version==1,"只导出最小观察目标，不外泄账号owner/raid能力");
        That(gate.ConsumeObservation(request.RequestId,identity) is null,"未观察不可消费");
        That(gate.Observe(request.RequestId,identity)==LukeSceneReceiptResult.Observed&&
            gate.Observe(request.RequestId,identity)==LukeSceneReceiptResult.Duplicate,"首次观察与重复幂等");
        That(gate.ConsumeObservation(request.RequestId,identity)==new LukeSceneObservation(request.RequestId,identity)&&
            gate.ConsumeObservation(request.RequestId,identity) is null,"观察一次消费，不是恢复成功");
        That(gate.Observe(request.RequestId,identity)==LukeSceneReceiptResult.Duplicate,"消费后重复回执不重新开放消费");
        clock.AdvanceMillisecondsWithoutCallbacks(1000);
        That(gate.Observe(request.RequestId,identity)==LukeSceneReceiptResult.Expired,"重复观察不续期，deadline精确到界即过期");
        request=gate.Begin(identity,1)!;clock.AdvanceMillisecondsWithoutCallbacks(1);
        That(gate.ConsumeObservation(request.RequestId,identity) is null,"消费也必须检查期限");
        request=gate.Begin(identity,30000)!;
        That(gate.Observe(request.RequestId,identity)==LukeSceneReceiptResult.Observed,"最大TTL合法");
        clock.AdvanceMillisecondsWithoutCallbacks(30000);
        That(gate.ConsumeObservation(request.RequestId,identity) is null,"已Observed仍不能越期继续");
        foreach(var changed in new[]{identity with{Account="other"},identity with{ConnectionId=2},identity with{Character=43},identity with{OwnerGeneration=2},
            identity with{SelectionEpoch=2},identity with{RaidInstance=Guid.NewGuid()},identity with{Phase=1},
            identity with{PhaseGeneration=1},identity with{Town=33},identity with{Area=5},identity with{TargetRevision=2}})
        {
            request=gate.Begin(identity,1000)!;
            That(gate.Observe(request.RequestId,changed)==LukeSceneReceiptResult.StaleIdentity&&
                gate.Observe(request.RequestId,identity)==LukeSceneReceiptResult.NoPending,"身份或目标变化撤销旧槽，不能改回后复活");
            request=gate.Begin(identity,1000)!;gate.Observe(request.RequestId,identity);
            That(gate.ConsumeObservation(request.RequestId,changed) is null&&gate.ConsumeObservation(request.RequestId,identity) is null,
                "观察后变化在消费时再次失效");
        }
        var old=gate.Begin(identity,1000)!;var replacement=gate.Begin(identity with{TargetRevision=2},1000)!;
        That(old.RequestId!=replacement.RequestId,"替代请求产生新随机ID");
        gate.Cancel(old.RequestId);
        That(gate.Observe(old.RequestId,identity)==LukeSceneReceiptResult.UnknownRequest&&gate.ConsumeObservation(old.RequestId,identity) is null,
            "旧请求携带旧捕获上下文也不清替代槽，只能由匹配ID进入复验");
        That(gate.Observe(old.RequestId,identity with{TargetRevision=2})==LukeSceneReceiptResult.UnknownRequest&&
            gate.Observe(replacement.RequestId,identity with{TargetRevision=2})==LukeSceneReceiptResult.Observed,
            "旧ID回执/取消不毁掉新槽，不替换新目标");
        request=gate.Begin(identity,1000)!;gate.SetCapability(false);
        That(gate.Observe(request.RequestId,identity)==LukeSceneReceiptResult.Unavailable,"能力撤销使旧观察失效");
        gate.SetCapability(true);
        That(gate.Observe(request.RequestId,identity)==LukeSceneReceiptResult.NoPending,"重新启用不复活旧请求");
        old=gate.Begin(identity,1000)!;replacement=gate.Begin(identity with{TargetRevision=2},1000)!;
        gate.Observe(replacement.RequestId,identity with{TargetRevision=2});gate.SetCapability(false);gate.SetCapability(true);
        That(gate.ConsumeObservation(replacement.RequestId,identity with{TargetRevision=2}) is null&&
            gate.Observe(replacement.RequestId,identity with{TargetRevision=2})==LukeSceneReceiptResult.NoPending,
            "独立当前能力撤销事件立即清除Observed，重新开启不复活；旧回执不携能力参数");
        var rollback=new LukeSceneReceipt(true,clock);rollback.SetCapability(true);var rollbackRequest=rollback.Begin(identity,1000)!;
        clock.AdvanceMillisecondsWithoutCallbacks(100);rollback.Observe(rollbackRequest.RequestId,identity);
        clock.AdvanceMillisecondsWithoutCallbacks(-1);
        That(rollback.Observe(rollbackRequest.RequestId,identity)==LukeSceneReceiptResult.Expired&&rollback.Begin(identity,1000) is null,
            "即使仍晚于issued时间，已见单调源回退也锁定旧槽不可重新开始");
        request=gate.Begin(identity,1000)!;gate.Observe(request.RequestId,identity);gate.Disconnect();
        That(gate.ConsumeObservation(request.RequestId,identity) is null&&gate.Begin(identity,1000) is null,
            "断线撤销且旧连接对象不可重新开始");
        var concurrent=new LukeSceneReceipt(true,clock);concurrent.SetCapability(true);request=concurrent.Begin(identity,1000)!;
        var results=Enumerable.Range(0,8).Select(_=>Task.Run(()=>concurrent.Observe(request.RequestId,identity))).ToArray();Task.WaitAll(results);
        That(results.Count(r=>r.Result==LukeSceneReceiptResult.Observed)==1&&results.Count(r=>r.Result==LukeSceneReceiptResult.Duplicate)==7,
            "并发回执最多一次Pending到Observed");
        var consumed=Enumerable.Range(0,8).Select(_=>Task.Run(()=>concurrent.ConsumeObservation(request.RequestId,identity))).ToArray();Task.WaitAll(consumed);
        That(consumed.Count(r=>r.Result is not null)==1,"并发继续校验只消费一次观察");
        Console.WriteLine($"PASS {Count-before} 卢克scene receipt纯领域断言（无native/transport/恢复授权）");
    }
    // JSON仅共享测试表示，不是传输解析器；数值文本只核共享表示/边界；真实typed字段/Begin另有领域测试，ID使用真实validator。
    static void ContractVectors()
    {
        using var stream=typeof(LukeSceneReceiptTests).Assembly.GetManifestResourceStream("DfoServer.SelfTests.LukeSceneContractVectors")!;
        using var json=System.Text.Json.JsonDocument.Parse(stream);
        var root=json.RootElement;
        That(root.GetProperty("contract").GetString()==LukeSceneReceiptRequest.Contract,"双方共享合同版本");
        var baseline=root.GetProperty("baseRequest").EnumerateObject().ToDictionary(p=>p.Name,p=>p.Value.GetString()!);
        foreach(var row in root.GetProperty("requests").EnumerateArray())
        {
            var fields=new Dictionary<string,string>(baseline);
            foreach(var change in row.GetProperty("change").EnumerateObject())fields[change.Name]=change.Value.GetString()!;
            bool valid=fields["version"]=="1"&&LukeSceneReceipt.ValidRequestId(fields["requestId"])&&
                Decimal(fields["characterId"],ushort.MaxValue,out _)&&Decimal(fields["town"],byte.MaxValue,out _)&&
                Decimal(fields["area"],byte.MaxValue,out _)&&Decimal(fields["targetRevision"],long.MaxValue,out long revision)&&revision>0&&
                Decimal(fields["ttlMs"],30000,out long ttl)&&ttl>0;
            That(valid==row.GetProperty("valid").GetBoolean(),"共同request向量："+row.GetProperty("name").GetString());
        }
        foreach(var row in root.GetProperty("replies").EnumerateArray())
            That((row.GetProperty("version").GetString()=="1"&&LukeSceneReceipt.ValidRequestId(row.GetProperty("requestId").GetString()))==
                row.GetProperty("valid").GetBoolean(),"共同reply向量："+row.GetProperty("name").GetString());
    }
    static bool Decimal(string value,long maximum,out long parsed)
    {
        parsed=0;
        return value.Length>0&&(value.Length==1||value[0]!='0')&&value.All(c=>c is >= '0' and <= '9')&&
            long.TryParse(value,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out parsed)&&parsed<=maximum;
    }

}
