using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

// 只排除调用方独立指定且已核验的一个每日；其它所有槽仍参与原有精确集合断言。
internal sealed class QuestDailyCoexistence
{
    readonly TcpTestFixture fixture;
    readonly ushort character;
    readonly int quest;
    readonly uint initial;
    readonly bool progress;
    readonly string identity;
    readonly long slot;
    uint previous;

    internal QuestDailyCoexistence(TcpTestFixture fixture, ushort character, int quest, uint initial, bool progress = false)
    {
        this.fixture=fixture; this.character=character; this.quest=quest; this.initial=initial; this.progress=progress;
        var rows=Read();
        That(rows is [[long,long,string]],"预期地区每日恰好一条且含独立接取身份");
        slot=(long)rows[0][0]; identity=(string)rows[0][2]; previous=initial;
        That((long)rows[0][1]==initial && Guid.TryParseExact(identity,"N",out var id) && id!=Guid.Empty,
            "地区每日初值与独立PVF常量一致，接取身份有效");
    }

    internal void Check()
    {
        var rows=Read();
        That(rows is [[long,long,string]] && (long)rows[0][0]==slot && (string)rows[0][2]==identity,
            "地区每日原槽和UUID保持，不通过筛掉每日掩盖丢失或重建");
        uint value=checked((uint)(long)rows[0][1]);
        That(progress ? value>>18==0 && (value&511)<=(previous&511) && (value>>9)<=(previous>>9) : value==initial,
            "每日只允许各自计数递减；非战斗路径不改变每日进度");
        previous=value;
    }

    internal object[][] OtherRows(string columns, string order="")
    {
        Check();
        return fixture.Query("SELECT "+columns+" FROM character_active_main_quests WHERE character_id=$p0 AND quest_id<>$p1 "+order,character,quest);
    }

    object[][] Read()=>fixture.Query("SELECT slot,condition_state,acceptance_id FROM character_active_main_quests WHERE character_id=$p0 AND quest_id=$p1",character,quest);
}
