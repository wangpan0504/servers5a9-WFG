using System.Buffers.Binary;
using System.Net;
using System.Text;
using DfoServer.Game.Chat;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 真实分派/owner/日志与抓包sink；无scene模块、能力或发送器，不把保留语法当观察成功。
internal static class LukeSceneNamespaceTests
{
    const string Id = "00112233445566778899aabbccddeeff";
    const string Receipt = "#cp-luke-scene/1 " + Id;
    internal static async Task RunCore()
    {
        int before=Count;
        That(Encoding.ASCII.GetByteCount(Receipt)==49,"预留receipt精确49ASCII字节");
        var good=Body(Receipt);
        That(good.Length==60&&LukeSceneReservedRequests.TryRead(good,out var status,out var id)&&
            status==LukeSceneReservedStatus.ReservedRejected&&id==Id,"严格语法仅ReservedRejected，不是Observed");
        var reserved=new List<byte[]>();
        foreach(string text in new[]{"#cp-luke-scene","#cp-luke-scene/2 "+Id,"#cp-luke-scene/1",Receipt+" ",
            Receipt+"\0","#cp-luke-scene/1 "+Id.ToUpperInvariant(),"#cp-luke-scene/1 "+new string('0',32),
            "#cp-luke-scene\0private","#cp-luke-scene\u007fprivate","#cp-luke-scene\tprivate",
            "#cp-luke-scene/1 中文", "#cp-luke-scene/1 "+new string('a',1000)})reserved.Add(Body(text));
        foreach(byte kind in new byte[]{1,3,6,11,13,36,41,46,255})reserved.Add(Body(Receipt,kind));
        var extra=good.Concat(new byte[]{0x42}).ToArray();reserved.Add(extra);
        var zeroLength=(byte[])good.Clone();BinaryPrimitives.WriteUInt32LittleEndian(zeroLength.AsSpan(7),0);reserved.Add(zeroLength);
        var hugeLength=(byte[])good.Clone();BinaryPrimitives.WriteUInt32LittleEndian(hugeLength.AsSpan(7),uint.MaxValue);reserved.Add(hugeLength);
        var arg=(byte[])good.Clone();arg[3]=1;reserved.Add(arg);
        var invalidByte=(byte[])good.Clone();invalidByte[^1]=0xff;reserved.Add(invalidByte);
        // 真实具名尾布局仍先按原文前缀隔离；不依赖named服务是否存在。
        var whisper=Body(Receipt,36).Concat(new byte[]{1,0,0,0,(byte)'X',0}).ToArray();reserved.Add(whisper);
        foreach(var bytes in reserved)
            That(LukeSceneReservedRequests.TryRead(bytes,out _,out _),"所有保留畸形/频道/目标必须截获，不回退聊天");
        foreach(string text in new[]{"hello","#cp-luke","#cp-luke-scenex","#cp-luke-sceneX/1"," #cp-luke-scene/1","#CP-luke-scene/1","#cp-score/1 "+Id})
            That(!LukeSceneReservedRequests.TryRead(Body(text),out _,out _),"非保留近似普通文字不劫持");
        for(int length=0;length<25;length++)
            That(!LukeSceneReservedRequests.TryRead(new byte[length],out _,out _),"短包分类不越界或猜前缀");
        var wrongOffset=Encoding.ASCII.GetBytes(Receipt+new string('x',11));
        That(!LukeSceneReservedRequests.IsReserved(wrongOffset),"只在CMD17正文固定头之后识别，不扫描任意偏移");
        await Routing(reserved.Append(good).ToArray());
        Console.WriteLine($"PASS {Count-before} scene namespace隔离断言（无scene发送/能力/恢复授权）");
    }

    static async Task Routing(byte[][] reserved)
    {
        string root=Path.Combine(Path.GetTempPath(),"luke-scene-namespace-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var fixture=new CharacterScoreNoticeTests.Fixture();
            using var stop=new CancellationTokenSource();using var stream=new MemoryStream();
            await using var sender=new GameConnectionSender(fixture.Owner,stream,stop,0,(_,_,_,_,_)=>{});
            var context=new GameConnectionContext(new(1,IPAddress.Loopback,stream,stop),fixture.Owner,fixture.Session,sender);
            var delivery=new Delivery();
            using(var log=new ServerLog(root))
            using(var capture=new PacketCapture(root,retainRecent:true))
            {
                var commands=new GameCommandRegistry();new ChatHandler(new ChatService(new Store(),delivery),null,log).Register(commands);commands.Seal();
                foreach(var body in reserved)
                {
                    fixture.Clock.Advance(1);
                    string redacted=PacketCapture.ReceivedCommandBody(17,body);
                    log.Write("recv",new{Opcode=17,Length=body.Length,Body=redacted});
                    log.Write("unimplemented",new{Opcode=17,Length=body.Length,Body=redacted});
                    var frame=new byte[13+body.Length];frame[0]=1;BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1),17);body.CopyTo(frame,13);
                    capture.Write(1,false,false,frame);
                    That(await commands.DispatchAsync(context,new(1,17,body))==CommandResult.Handled,"正式handler消费保留包");
                    await sender.DrainAsync();
                    That(delivery.Count==0&&stream.Length==0,"无模块配置也不广播/不发送响应");
                    That(redacted=="[luke-scene-reserved-redacted]","分派之前recv与未处理诊断使用相同脱敏");
                }
                That(capture.Recent().Length==reserved.Length&&capture.Recent().All(row=>row.Redacted&&row.BodyHex.Length==0),
                    "实际在线抓包历史所有保留包脱敏，不只验证文件标志");
                foreach(string text in new[]{"hello","#cp-luke-scenex","#cp-luke"})
                {
                    fixture.Clock.Advance(1);int previous=delivery.Count;
                    That(await commands.DispatchAsync(context,new(1,17,Body(text)))==CommandResult.Handled&&delivery.Count==previous+1,
                        "正常及部分前缀真实广播保留");
                    That(PacketCapture.ReceivedCommandBody(17,Body(text))==Convert.ToHexString(Body(text)),"普通聊天原诊断未扩大脱敏");
                }
                That(PacketCapture.ReceivedCommandBody(18,Body(Receipt))==Convert.ToHexString(Body(Receipt)),"其他opcode同字节不误判聊天偏移");
                int deliveries=delivery.Count;
                fixture.Clock.Advance(10);
                for(int n=0;n<7;n++)await commands.DispatchAsync(context,new(1,17,Body(Receipt)));
                That(delivery.Count==deliveries&&stream.Length==0,"总聊天限流下机器包仍无泄漏或响应");
                fixture.Clock.Advance(10);
                using var replacement=fixture.Owners.Acquire("notice",()=>{});
                bool stale=false;
                try{await commands.DispatchAsync(context,new(1,17,Body(Receipt)));}
                catch(InvalidOperationException error)when(error.Message=="Stale account generation"){stale=true;}
                That(stale&&delivery.Count==deliveries,"旧owner被真实registry拒绝，预留入口不放宽门禁");
            }
            string logs=File.ReadAllText(Path.Combine(root,"server.jsonl"));
            string packets=File.ReadAllText(Path.Combine(root,"packet_log.txt"));
            string hexId=Convert.ToHexString(Encoding.ASCII.GetBytes(Id));
            That(!logs.Contains(Id,StringComparison.Ordinal)&&!logs.Contains(hexId,StringComparison.Ordinal)&&!logs.Contains("private",StringComparison.Ordinal),"真实日志sink无ID/正文");
            That(!packets.Contains(hexId,StringComparison.Ordinal)&&!packets.Contains(BitConverter.ToString(Encoding.ASCII.GetBytes(Id)).Replace('-',' '),StringComparison.Ordinal)&&!packets.Contains("raw:",StringComparison.Ordinal)&&packets.Contains("luke scene reserved payload redacted",StringComparison.Ordinal),"真实抓包sink仅元数据与脱敏标志");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    static byte[] Body(string text,byte kind=3)
    {
        var bytes=ChatText.Encoding.GetBytes(text);var body=new byte[11+bytes.Length];body[0]=kind;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7),(uint)bytes.Length);bytes.CopyTo(body,11);return body;
    }
    sealed class Store:IChatDirectoryStore{public string? AccountOfCharacter(ushort id)=>null;public string[] GuildMemberAccounts(long id)=>[];}
    sealed class Delivery:IChatDelivery
    {
        public int Count;
        public int DeliverAll(Func<ChatRecipient,bool> filter,ChatNotice notice){Count++;return 0;}
        public bool DeliverTo(string account,Func<ChatRecipient,bool> filter,ChatNotice notice){Count++;return false;}
    }
}
