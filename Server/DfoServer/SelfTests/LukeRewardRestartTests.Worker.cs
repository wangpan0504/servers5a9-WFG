using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Persistence;
using DfoServer.Testing;

namespace DfoServer;

internal static partial class LukeRewardRestartTests
{
    const string ControlPrefix="S5A9_LUKE_RESTART_CONTROL ";
    const string WorkerVariable="DFO_SELFTEST_LUKE_RESTART_MANIFEST";
    sealed record Manifest(string Nonce,string Account,string Name,ushort Character,string Instance,int Phase,byte Kind,byte Card,string Event,long Anchor,string Pvf,bool Replay,bool BeforeCommit=false,bool Recover=false);
    sealed record Signal(string Nonce,int Pid,string Stage,string? List=null,string? Inspect=null,string? Claim=null,string? Again=null,LukeRewardPrecommitObserver.Proof? Proof=null);

    // 仅--selftest-core测试入口读取；不装到Runtime/游戏协议，不给原客户端执行任何内容。
    internal static async Task<bool> TryRunWorkerAsync()
    {
        string? file=Environment.GetEnvironmentVariable(WorkerVariable);
        if(file is null)return false;
        string root=Path.GetDirectoryName(Path.GetFullPath(file))!;
        if(Path.GetFileName(file)!="worker.json"||!Path.GetFileName(root).StartsWith("S5A9-luke-restart-",StringComparison.Ordinal)||
            File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint)||File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("重启测试只允许专属临时夹具目录");
        var input=JsonSerializer.Deserialize<Manifest>(File.ReadAllText(file))??throw new InvalidDataException("空测试manifest");
        if(!Guid.TryParseExact(input.Nonce,"N",out _)||File.ReadAllText(Path.Combine(root,"nonce"))!=input.Nonce)throw new InvalidDataException("重启测试身份不符");
        string db=Path.Combine(root,"game.db");
        if(File.GetAttributes(db).HasFlag(FileAttributes.ReparsePoint))throw new InvalidDataException("测试库不能是链接");
        var repository=new CharacterRepository(db);var owners=new AccountOwners();
        string args=$"{input.Name} {input.Character} {input.Instance} {input.Phase} {input.Kind} {input.Card}";
        string claim=$"luke-reward-claim {args} {input.Event} {input.Anchor}";
        string Answer(string command,DevelopmentCatalog? catalog=null)=>DevelopmentGm.Answer(command,input.Account,repository,owners,_=>null,catalog:catalog);
        if(input.Replay)
        {
            string list=Answer($"luke-reward-list {input.Name} {input.Character}");
            string inspect=Answer("luke-reward-inspect "+args);
            using var recoveryResources=input.Recover?new TcpTestResources(new ServerOptions{PvfPath=input.Pvf}):null;
            var recoveryCatalog=recoveryResources is null?null:new DevelopmentCatalog(recoveryResources.Archive,recoveryResources.ItemResources,null);
            string replay=Answer(claim,recoveryCatalog),again=Answer(claim);
            await Console.Out.WriteLineAsync(ControlPrefix+JsonSerializer.Serialize(new Signal(input.Nonce,Environment.ProcessId,"REPLAY",list,inspect,replay,again)));
            await Console.Out.FlushAsync();
            return true;
        }
        using var resources=new TcpTestResources(new ServerOptions{PvfPath=input.Pvf});
        var catalog=new DevelopmentCatalog(resources.Archive,resources.ItemResources,null);
        using var observer=input.BeforeCommit?new LukeRewardPrecommitObserver(db,
            proof=>{Console.WriteLine(ControlPrefix+JsonSerializer.Serialize(new Signal(input.Nonce,Environment.ProcessId,"PRECOMMIT",Proof:proof)));Console.Out.Flush();},
            error=>{Console.WriteLine(ControlPrefix+JsonSerializer.Serialize(new Signal(input.Nonce,Environment.ProcessId,"FAILED",Claim:error)));Console.Out.Flush();}):null;
        observer?.VerifyScopeAndArm();
        // 专属child初始化仓储时已建池；注册原生观察器后释放旧连接，领奖必须由已安装回调的新连接执行。
        if(observer is not null)SqliteDatabaseBootstrap.ReleaseRuntimePool();
        string response=Answer(claim,catalog);
        if(input.BeforeCommit)throw new InvalidOperationException("真实领取返回但未停在提交前观察点，不能算crash通过："+response);
        if(!response.StartsWith("OK ",StringComparison.Ordinal))throw new InvalidDataException("真实GM领取失败："+response);
        using(var result=JsonDocument.Parse(response[3..]))
            if(result.RootElement.GetProperty("duplicate").GetBoolean())throw new InvalidDataException("首领不能重放旧回执");
        // Answer返回证明真实仓储Commit完成；控制信号不是业务ACK，业务response不写入输出流。
        await Console.Out.WriteLineAsync(ControlPrefix+JsonSerializer.Serialize(new Signal(input.Nonce,Environment.ProcessId,"COMMITTED")));
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return true;
    }
}
