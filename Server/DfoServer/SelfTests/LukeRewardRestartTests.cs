using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Raids;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 真子进程GM持久权益验证；不是原客户端ACK、active Battle恢复或系统掉电。
internal static partial class LukeRewardRestartTests
{
    internal static async Task RunAsync(TcpTestResources resources,string source,string account,Character role,RaidPhaseEligibility eligibility,string pvf)
    {
        int before=Count;
        foreach(bool beforeCommit in new[]{false,true})
        foreach(bool full in new[]{false,true})
        {
            string root=Path.Combine(Path.GetTempPath(),"S5A9-luke-restart-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);string db=Path.Combine(root,"game.db");bool passed=false;
            try
            {
                using(var original=Open(source,true))using(var copy=Open(db,false))original.BackupDatabase(copy);
                var repository=new CharacterRepository(db);var owners=new AccountOwners();
                var offer=eligibility.Offers.First();
                using(var owner=owners.Acquire(account,()=>{}))
                {
                    role=repository.LoadCharacter(owner,role.Slot);
                    if(full)
                    {
                        var template=resources.ItemResources.Items.Get(offer.Grants[0].ItemId)!;
                        var range=ItemSlotBoundService.Range(template.Kind,role.Inventory);var list=ItemSlotBoundService.List(template.Kind);
                        var packed=role.Inventory.Items.Where(item=>item.Position.List!=list||item.Position.Index<range.Start||item.Position.Index>range.End).ToImmutableArray();
                        for(short slot=range.Start;slot<=range.End;slot++)packed=packed.Add(new(0,new(list,slot),InventoryRewardGrantService.Create(template,template.StackLimit)));
                        role=InventoryService.Commit(repository,owner,role,new(true,0,new InventoryPlan(role.Inventory,role.Inventory with{Items=packed},"fixture_full",new(role))));
                    }
                }
                string initial=Snapshot(db);
                var manifest=new Manifest(Guid.NewGuid().ToString("N"),account,role.Name,role.Id,eligibility.Instance.ToString("N"),eligibility.Phase,
                    offer.Kind,offer.Card,eligibility.Event.ToString("N"),eligibility.Anchor,Path.GetFullPath(pvf),false,BeforeCommit:beforeCommit);
                string committed="";string mails="";
                void ValidateCommitted()
                {
                    // 在终止前由父进程独立SQL读确认，不以信号或sleep替代持久提交证据。
                    using var connection=Open(db,true);using var command=connection.CreateCommand();
                    command.CommandText="SELECT plan_json,mail_ids FROM character_raid_reward_receipts WHERE character_id=$id AND instance_id=$instance AND phase=$phase AND kind=$kind";
                    command.Parameters.AddWithValue("$id",role.Id);command.Parameters.AddWithValue("$instance",manifest.Instance);
                    command.Parameters.AddWithValue("$phase",eligibility.Phase);command.Parameters.AddWithValue("$kind",offer.Kind);
                    using(var row=command.ExecuteReader())
                    {
                        That(row.Read(),"kill前独立SQL可见唯一原权益receipt");
                        var prepared=JsonSerializer.Deserialize<PreparedRaidReward>(row.GetString(0))!;mails=row.GetString(1);
                        That(prepared.Instance==eligibility.Instance&&prepared.Character==role.Id&&prepared.Phase==eligibility.Phase&&
                            prepared.Kind==offer.Kind&&prepared.Card==offer.Card&&prepared.Grants.SequenceEqual(offer.Grants),"kill前receipt冻结原身份与完整奖励数量");
                        That(!row.Read(),"同kind无重复receipt");
                    }
                    var mailIds=JsonSerializer.Deserialize<uint[]>(mails)!;
                    That(full?mailIds.Length>0:mailIds.Length==0,"入包和满包邮件使用真实资产分支");
                    using(var owner=owners.Acquire(account,()=>{}))
                    {
                        var saved=repository.LoadCharacter(owner,role.Slot);
                        That(saved.Revision==role.Revision+1,"kill前父进程重读角色资产版本已提交一次");
                        var delivered=full?repository.LoadMailbox(owner,role.Id,DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Entries
                            .Where(mail=>mailIds.Contains(mail.Id)).SelectMany(mail=>mail.Attachments).Select(attachment=>attachment.Item).ToArray():saved.Inventory.Items.ToArray();
                        foreach(var grant in offer.Grants.GroupBy(grant=>grant.ItemId))
                        {
                            long previous=full?0:role.Inventory.Items.Where(item=>item.Core.ItemId==grant.Key).Sum(item=>(long)item.Core.Count);
                            That(delivered.Where(item=>item.Core.ItemId==grant.Key).Sum(item=>(long)item.Core.Count)-previous==grant.Sum(item=>(long)item.Count),
                                "kill前父进程读取真实资产数量，非仅receipt自证");
                        }
                        That(delivered.All(item=>item.Uid>0),"kill前资产均有真实持久UID");
                    }
                    committed=Snapshot(db);That(committed!=initial,"kill前完整持久事实已变化");
                }
                await WorkerAsync(root,manifest,signal=>
                {
                    if(beforeCommit)
                    {
                        That(signal.Stage=="PRECOMMIT"&&signal.Proof is {Inserts:1,ReceiptRowId:>0,Connection:not 0} proof&&
                            Path.GetFullPath(proof.Database)==Path.GetFullPath(db)&&proof.Version>0&&proof.Source.Length>0,
                            "真实原生同连接receipt INSERT后commit callback内阻塞，未先放行COMMIT");
                        That(Snapshot(db)==initial,"kill前独立SQL仍看到完整旧资产/无新receipt，仅作未提交旁证");
                    }
                    else
                    {
                        That(signal.Stage=="COMMITTED","独立控制信号只在真实GM事务成功后产生");
                        ValidateCommitted();
                    }
                },kill:true);
                if(beforeCommit)
                {
                    That(await SnapshotAfterCrashAsync(db)==initial,"提交前突然终止后新连接看到全部写入回滚，原资格保留");
                    await WorkerAsync(root,manifest with{Nonce=Guid.NewGuid().ToString("N"),Replay=true,Recover=true,BeforeCommit=false},signal=>
                    {
                        That(signal.Stage=="REPLAY","新进程真实GM恢复原未领权益");
                        foreach(string answer in new[]{signal.List!,signal.Inspect!,signal.Claim!,signal.Again!})That(answer.StartsWith("OK ",StringComparison.Ordinal),"提交前崩溃后发现/检查/领取成功");
                        using var inspected=JsonDocument.Parse(signal.Inspect![3..]);
                        using var first=JsonDocument.Parse(signal.Claim![3..]);using var again=JsonDocument.Parse(signal.Again![3..]);
                        That(!inspected.RootElement.GetProperty("Claimed").GetBoolean()&&!first.RootElement.GetProperty("duplicate").GetBoolean()&&
                            again.RootElement.GetProperty("duplicate").GetBoolean(),"原资格仍未领，新进程首次实际授奖后立即重放不再增发");
                        That(first.RootElement.GetProperty("MailIds").GetRawText()==again.RootElement.GetProperty("MailIds").GetRawText(),"重试恢复首领后mailIDs稳定");
                    },kill:false);
                    ValidateCommitted();
                }
                That(Snapshot(db)==committed,"提交事实在进程边界保留，或未提交回滚后重试仅提交一次");
                await WorkerAsync(root,manifest with{Nonce=Guid.NewGuid().ToString("N"),Replay=true,BeforeCommit=false},signal=>
                {
                    That(signal.Stage=="REPLAY","新进程重建GM/Repository/owner读取原库");
                    foreach(string answer in new[]{signal.List!,signal.Inspect!,signal.Claim!,signal.Again!})That(answer.StartsWith("OK ",StringComparison.Ordinal),"重启后真实GM发现/检查/领取均成功");
                    using var listed=JsonDocument.Parse(signal.List![3..]);
                    That(listed.RootElement.GetProperty("Entries").EnumerateArray().Any(entry=>
                        entry.GetProperty("Eligibility").GetProperty("Instance").GetGuid()==eligibility.Instance&&
                        entry.GetProperty("Eligibility").GetProperty("Phase").GetInt32()==eligibility.Phase),"新进程发现原实例阶段，不仅得到空成功页");
                    using var inspected=JsonDocument.Parse(signal.Inspect![3..]);
                    That(inspected.RootElement.GetProperty("Claimed").GetBoolean(),"重启检查识别原已领权益");
                    foreach(string answer in new[]{signal.Claim!,signal.Again!})
                    {
                        using var parsed=JsonDocument.Parse(answer[3..]);
                        That(parsed.RootElement.GetProperty("duplicate").GetBoolean()&&parsed.RootElement.GetProperty("MailIds").GetRawText()==mails,
                            "两次新进程重放只返回原receipt/mailIDs，不加载PVF重新规划");
                    }
                },kill:false);
                That(Snapshot(db)==committed,"重启发现及重复领取保持所有表完整内容不变，含UID/数量/位置/钱包/邮件/资格");
                passed=true;
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if(passed)Directory.Delete(root,true);else Console.WriteLine("Luke重启失败现场保留："+root);
            }
        }
        Console.WriteLine($"PASS {Count-before} 卢克真实子进程提交前/后未应答终止与权益幂等断言（GM非原客户端）");
    }

    static async Task WorkerAsync(string root,Manifest input,Action<Signal> inspect,bool kill)
    {
        string file=Path.Combine(root,"worker.json");File.WriteAllText(file,JsonSerializer.Serialize(input));File.WriteAllText(Path.Combine(root,"nonce"),input.Nonce);
        string host=Environment.ProcessPath??throw new InvalidOperationException("测试进程host未知");
        var start=new ProcessStartInfo(host){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(Path.GetFileNameWithoutExtension(host).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(LukeRewardRestartTests).Assembly.GetName().Name + ".dll"));
        // 单文件发行入口仍需宿主转交标记；开发DLL入口不接受该标记。
        if(Environment.GetCommandLineArgs().Skip(1).Contains("--server",StringComparer.Ordinal))start.ArgumentList.Add("--server");
        start.ArgumentList.Add("--selftest-core");start.Environment[WorkerVariable]=file;
        using var child=Process.Start(start)??throw new IOException("测试child启动失败");child.StandardInput.Close();
        string label=input.Recover?"recover":input.Replay?"replay":input.BeforeCommit?"precommit":"committed";
        var received=new TaskCompletionSource<Signal>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout=CaptureControlAsync(child.StandardOutput,Path.Combine(root,label+".stdout"),received);
        var stderr=TcpTestFixture.CopyOutputAsync(child.StandardError,Path.Combine(root,label+".stderr"));
        try
        {
            var signal=await received.Task.WaitAsync(TimeSpan.FromSeconds(120));
            That(signal.Nonce==input.Nonce&&signal.Pid==child.Id&&(!kill||!child.HasExited),"控制nonce/PID精确匹配本次创建且尚存活的child");
            File.WriteAllText(Path.Combine(root,label+".control.json"),JsonSerializer.Serialize(signal));
            inspect(signal);
            if(kill)child.Kill(entireProcessTree:true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            That(kill?child.ExitCode!=0:child.ExitCode==0,"仅自有child被非正常终止或新进程正常结束");
        }
        finally
        {
            if(!child.HasExited){child.Kill(entireProcessTree:true);await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));}
            await Task.WhenAll(stdout,stderr);
        }
    }

    // RedirectStandardOutput由Process创建匿名管道，不依赖沙箱不开放的AF_UNIX命名socket。
    // 只识别明确测试前缀，生产GM业务response没有写到此管道；完整日志仍保留。
    static async Task CaptureControlAsync(StreamReader input,string file,TaskCompletionSource<Signal> received)
    {
        try
        {
            await using var output=new StreamWriter(file);
            while(await input.ReadLineAsync() is { } line)
            {
                if(line.Length>262144)throw new InvalidDataException("测试child输出超过单行上限");
                await output.WriteLineAsync(line);await output.FlushAsync();
                if(line.StartsWith(ControlPrefix,StringComparison.Ordinal))
                {
                    var signal=JsonSerializer.Deserialize<Signal>(line[ControlPrefix.Length..])??throw new InvalidDataException("空控制信号");
                    if(!received.TrySetResult(signal))throw new InvalidDataException("测试child重复控制信号");
                }
            }
            received.TrySetException(new IOException("测试child退出前没有提交控制信号"));
        }
        catch(Exception error){received.TrySetException(error);throw;}
    }

    static SqliteConnection Open(string path,bool readOnly)
    {
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=readOnly?SqliteOpenMode.ReadOnly:SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString());
        connection.Open();return connection;
    }
    static async Task<string> SnapshotAfterCrashAsync(string path)
    {
        var elapsed=Stopwatch.StartNew();
        for(int attempt=0;;attempt++)
        {
            try
            {
                string result=Snapshot(path);
                if(attempt>0)Console.WriteLine($"Luke restart WAL truncation wait: attempts={attempt}, elapsed_ms={elapsed.ElapsedMilliseconds}");
                return result;
            }
            catch(IOException error) when(OperatingSystem.IsWindows()&&elapsed.Elapsed<TimeSpan.FromSeconds(5)&&
                error.InnerException is SqliteException{SqliteExtendedErrorCode:1546})
            {
                // 原child已精确终止并回收；只等Windows释放WAL截断资源，每轮仍用新只读连接完整取证。
                await Task.Delay(25);
            }
        }
    }
    static string Snapshot(string path,[System.Runtime.CompilerServices.CallerLineNumber]int caller=0)
    {
        string reading="sqlite_master";
        try
        {
        using var db=Open(path,true);using var transaction=db.BeginRead();
        using var command=db.CreateCommand();command.Transaction=transaction;
        command.CommandText="SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var tables=new List<string>();using(var reader=command.ExecuteReader())while(reader.Read())tables.Add(reader.GetString(0));
        var result=new List<object>();
        foreach(string table in tables)
        {
            reading=table;
            string escaped="\""+table.Replace("\"","\"\"",StringComparison.Ordinal)+"\"";
            command.CommandText="SELECT * FROM "+escaped;
            using var reader=command.ExecuteReader();var rows=new List<string>();
            while(reader.Read())
            {
                var values=new object[reader.FieldCount];reader.GetValues(values);rows.Add(JsonSerializer.Serialize(values));
            }
            rows.Sort(StringComparer.Ordinal);result.Add(new{Table=table,Rows=rows});
        }
        return JsonSerializer.Serialize(result);
        }
        catch(SqliteException error)
        {throw new IOException($"重启快照失败：调用行{caller}，表{reading}，SQLite扩展码{error.SqliteExtendedErrorCode}",error);}
    }
}
