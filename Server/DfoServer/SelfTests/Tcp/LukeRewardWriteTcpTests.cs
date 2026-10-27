using System.Net;
using System.Net.Sockets;
using DfoServer.Game.Accounts;
using DfoServer.Network;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 真实TCP上的生产发送泵故障合同；资产重放另由Runtime资源奖励场景核验，不伪称完整断线重登。
internal static class LukeRewardWriteTcpTests
{
    internal static async Task RunAsync()
    {
        await ScenarioAsync(false);
        await ScenarioAsync(true);
    }

    static async Task ScenarioAsync(bool queueReject)
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);
        listener.Start();
        using var client=new TcpClient();
        var accepted=listener.AcceptTcpClientAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server=await accepted;
        var owners=new AccountOwners(); using var owner=owners.Acquire("result-wire",()=>{});
        using var peer=owners.Acquire("result-wire-peer",()=>{});
        using var stop=new CancellationTokenSource();
        using var output=new FaultStream(server.GetStream(),queueReject?0:3);
        await using var sender=new GameConnectionSender(owner,output,stop,0,(_,_,_,_,_)=>{});
        bool phaseWritten=false,resultWritten=false; int confirmed=0;
        byte[] body=Network.Builders.LukeRewardPackets.Result(new(0,0x1234,1,new(1,10308804,1,0,0x89abcdef,0)));
        sender.Post(0,587,[3,0],"phase",written:()=>phaseWritten=true);
        var rejected=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sender.TryPost(0,600,body,"result",()=>phaseWritten,()=>
        {
            resultWritten=true;
            if(!queueReject)return;
            // 泵仍在本次Written内，确定填满256项，不依赖调度/慢机器时间。
            for(int i=0;i<256;i++) sender.TryPost(0,2,[0],"fill-bounded-queue");
            rejected.TrySetResult(!sender.TryPost(1,660,[1],"ack-rejected",()=>resultWritten,afterWritten:()=>confirmed++));
        });
        if(queueReject) That(await rejected.Task.WaitAsync(TimeSpan.FromSeconds(3)),"600已写成功但ACK队列拒收不提前确认");
        else sender.TryPost(1,660,[1],"ack-fails",()=>resultWritten,afterWritten:()=>confirmed++);
        bool failed=false;
        try { await sender.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch(IOException){failed=true;}
        await sender.DisposeAsync();
        That(failed&&phaseWritten&&resultWritten&&confirmed==0&&sender.PendingBytes==0,
            "真实TCP结果写出后ACK写失败，不确认奖励且释放队列预算");
        async Task Read(byte[] expected)
        {
            byte[] actual=new byte[expected.Length];
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.GetStream().ReadExactlyAsync(actual,deadline.Token);
            Bytes(actual,expected,"真实TCP字节严格按生产帧编码和顺序");
        }
        await Read(GameFrame.Build(0,587,0,[3,0]));
        await Read(GameFrame.Build(0,600,1,body));
        // 新的有界泵重用已经written的结果凭据，只重试ACK；不是重跑资产事务。
        using var retryStop=new CancellationTokenSource();
        using var retryOutput=new FaultStream(server.GetStream(),0);
        await using var retry=new GameConnectionSender(owner,retryOutput,retryStop,2,(_,_,_,_,_)=>{});
        retry.TryPost(0,600,body,"duplicate-result",()=>!resultWritten);
        retry.TryPost(1,660,[1],"ack-retry",()=>phaseWritten&&resultWritten,afterWritten:()=>
            peer.Execute(()=>{confirmed++;return true;}));
        await retry.DrainAsync().WaitAsync(TimeSpan.FromSeconds(3));
        await Read(GameFrame.Build(1,660,2,[1]));
        That(confirmed==1&&retry.PendingBytes==0,"真实ACK重试成功后才锁外确认，没有第二份600");
    }

    sealed class FaultStream(Stream inner,int failAt) : Stream
    {
        int writes;
        public override bool CanRead=>false; public override bool CanSeek=>false; public override bool CanWrite=>true;
        public override long Length=>throw new NotSupportedException();
        public override long Position {get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush()=>inner.Flush();
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default)
        {
            if(++writes==failAt) throw new IOException("显式测试ACK写失败");
            await inner.WriteAsync(buffer,cancellationToken);
        }
    }
}
