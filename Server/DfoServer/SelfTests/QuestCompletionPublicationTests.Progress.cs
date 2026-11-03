using System.Buffers.Binary;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Projection;
using DfoServer.Testing;

namespace DfoServer;

internal static partial class QuestCompletionPublicationTests
{
    internal static Task RunProgressAsync(TcpTestResources resources) =>
        RunProgressAsync(resources.Towns,resources.Characters,
            new QuestCompletionRepositoryTests.Resources(resources.Archive,resources.ItemResources));

    static async Task RunProgressAsync(TownCatalog towns,CharacterCatalog characters,
        QuestCompletionRepositoryTests.Resources resources)
    {
        int checks = TestCheck.Count;
        using var fixture = new QuestCompletionRepositoryTests.Fixture(towns,characters,resources);
        var selected = fixture.Session.Selected!;
        var stored = fixture.Read();
        using var stop = new CancellationTokenSource();
        using var stream = new ControlledOutput();
        await using var sender = new GameConnectionSender(fixture.Owner,stream,stop,0,(_,_,_,_,_) => { });
        var context = Context(fixture,stream,sender,stop);
        bool allowed = true;
        bool Guard() => allowed && ReferenceEquals(fixture.Session.Selected,selected);
        // 以下仅给发布器合成已提交后的投影输入，不保存任务或伪造真实猎杀；TCP另覆盖自然进度。
        ActiveMainQuest[] initial =
        [
            new(0,3472,0x01020304) { AcceptanceId = Guid.Parse("11111111-1111-1111-1111-111111111111") },
            new(1,3458,2) { AcceptanceId = Guid.Parse("22222222-2222-2222-2222-222222222222") },
            new(2,3473,0) { AcceptanceId = Guid.Parse("33333333-3333-3333-3333-333333333333") }
        ];
        var projection = selected with { ActiveMainQuests = initial,CompletedQuests = [6171],AccountCompletedQuests = [100] };

        await QuestProjection.ProgressAsync(context,initial,projection,Guard,"quest_progress_unchanged");
        Check(stream.Frames.Count == 0,"相同接取与条件不发送33、574或历史刷新");
        var packed = initial.Select(row => row.QuestId == 3472 ? row with { ConditionState = 0x01020303 } : row).ToArray();
        await QuestProjection.ProgressAsync(context,initial,projection with { ActiveMainQuests = packed },Guard,"quest_progress_packed");
        Check(stream.Frames.Count == 1,"多通道猎杀只发布真正变化的一项，未变化的父任务及材料兄弟不通知");
        CheckCondition(stream.Frames[0],"01900D03030201","猎杀四字节打包计数完整保留高通道，不截成ushort或Boolean");

        stream.Frames.Clear();
        var completed = packed.Select(row => row.QuestId == 3472 ? row with { ConditionState = 0 } :
            row.QuestId == 3473 ? row with { ConditionState = 1 } : row).ToArray();
        await QuestProjection.ProgressAsync(context,packed,projection with { ActiveMainQuests = completed },Guard,"quest_progress_zero_and_regression");
        Check(stream.Frames.Count == 2,"条件清零与材料不足回退各发布一次33，不附加574/356替换活动对象");
        CheckCondition(stream.Frames[0],"01900D00000000","猎杀完成立即原位发布完整状态0");
        CheckCondition(stream.Frames[1],"01910D01000000","材料从满足0回退不足1也按权威状态原位更新");
        stream.Frames.Clear();
        await QuestProjection.ProgressAsync(context,completed,
            projection with { ActiveMainQuests = completed.Reverse().ToArray() },Guard,"quest_progress_reordered");
        Check(stream.Frames.Count == 0,"同槽同接取只调整数组顺序不产生伪进度通知");

        var replacement = initial.Select(row => row.QuestId == 3472 ? row with
            { AcceptanceId = Guid.Parse("44444444-4444-4444-4444-444444444444") } : row).ToArray();
        await Structural(initial,replacement,"03000000900D04030201820D02000000910D00000000",
            "同槽同编号重新接取也必须按新身份完整恢复结构");
        await Structural(initial,[..initial,new(3,100,1) { AcceptanceId = Guid.Parse("55555555-5555-5555-5555-555555555555") }],
            "04000000900D04030201820D02000000910D00000000640001000000","新增接取槽发布完整活动结构");
        await Structural(initial,[],"00000000","全部槽删除后空574仍须随后恢复完成历史");
        await Structural(initial,initial.Select(row => row.QuestId == 3472 ? row with { Slot = 4 } : row).ToArray(),
            "03000000820D02000000910D00000000900D04030201","同任务接取迁移槽位也走结构恢复，按实际槽序投影");
        await Structural(initial,initial.Select(row => row.QuestId == 3472 ? row with { QuestId = 3474 } : row).ToArray(),
            "03000000920D04030201820D02000000910D00000000","槽位及接取标识相同而任务编号变化时仍完整恢复结构");
        stream.Frames.Clear();
        await QuestProjection.ProgressAsync(context,initial,projection with { ActiveMainQuests = [] },Guard,
            "quest_progress_history_owned_by_caller",restoreCompleted: false);
        Check(stream.Frames.Count == 1 && stream.Frames[0][0] == 0 && stream.Opcodes.Single() == 574 &&
            stream.Frames[0].AsSpan(16).SequenceEqual(new byte[4]),
            "调用方另有统一完成历史发布时只发一次空结构，不重复356");

        stream.Frames.Clear();
        allowed = false;
        await QuestProjection.ProgressAsync(context,initial,projection with { ActiveMainQuests = packed },Guard,"quest_progress_stale_scalar");
        await QuestProjection.ProgressAsync(context,initial,projection with { ActiveMainQuests = [] },Guard,"quest_progress_stale_structure");
        Check(stream.Frames.Count == 0 && sender.PendingBytes == 0 && !stop.IsCancellationRequested,
            "失效发布守卫同时拦截条件33及结构574/356，不残留发送或终止健康连接");
        Check(ReferenceEquals(fixture.Session.Selected,selected) && QuestCompletionRepositoryTests.Same(stored,fixture.Read()),
            "发布器测试仅使用投影输入，原Selected和隔离持久角色不被发布过程修改");
        Console.WriteLine($"PASS {TestCheck.Count-checks} 在线任务条件与结构发布断言（合成投影，非实机）");

        static void CheckCondition(byte[] frame,string expected,string name) =>
            Check(frame.Length == 23 && frame[0] == 1 && BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)) == 33 &&
                frame.AsSpan(16).SequenceEqual(Convert.FromHexString(expected)),name);

        async Task Structural(ActiveMainQuest[] previous,ActiveMainQuest[] current,string expected,string name)
        {
            stream.Frames.Clear();
            await QuestProjection.ProgressAsync(context,previous,projection with { ActiveMainQuests = current },Guard,"quest_progress_structure");
            Check(stream.Frames.Count == 2 && stream.Frames.All(frame => frame[0] == 0) &&
                stream.Opcodes.SequenceEqual(new ushort[] {574,356}) &&
                stream.Frames[0].AsSpan(16).SequenceEqual(Convert.FromHexString(expected)),name);
            byte[] history = new byte[30004];
            BinaryPrimitives.WriteUInt32LittleEndian(history,30000);
            history[4+6171] = history[4+100] = 1;
            history[4+13700] = 1; // 既有素喃地图显示兼容位，不是新完成记录。
            Check(stream.Frames[1].AsSpan(16).SequenceEqual(history),
                "结构恢复后紧接同一投影的完整个人及账号完成集合，未丢弃历史或添加完成位");
        }
    }
}
