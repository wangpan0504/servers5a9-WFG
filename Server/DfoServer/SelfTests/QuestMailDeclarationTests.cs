using System.Collections.Immutable;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class QuestMailDeclarationTests
{
    public static void RunResources(QuestCatalog quests)
    {
        var declarations = quests.All.Where(QuestMailPlan.HasMail).OrderBy(row => row.Id)
            .Select(row => (Quest:row, Mail:QuestMailPlan.Read(row))).ToArray();
        That(declarations.Select(row=>row.Quest.Id).SequenceEqual(new[]{2670,20552,20553,20554,20555,20556}),
            "完整注册表仅六条任务有邮件声明，逐条保留而非按任务名匹配");
        That(declarations[0].Mail is {Time:0,Title:"爱莎的问候",Enclosures.Length:0} &&
            declarations.Skip(1).All(row=>row.Mail is {Time:1,Title:"勇士变身活动",Enclosures.Length:1}),
            "真实PVF的资格信与完成奖励信分开，不混淆触发时机");
    }

    public static void Run()
    {
        var source = QuestEligibilityTests.Quest(100) with { Definition = new([
            new("mail time",[new(0,1,null)],[]),new("mail title",[new(6,0,"标题")],[]),
            new("mail contents",[new(6,0,"正文")],[]),new("mail enclose",[new(0,2058,null),new(0,2,null)],[])]) };
        var read=QuestMailPlan.Read(source);
        That(read.Time==1 && read.Title=="标题" && read.Contents=="正文" && read.Enclosures is [{ItemId:2058,Count:2}],
            "邮件原声明保留时机、标题、正文和附件，不代替任务奖励");
        That(QuestMailPlan.CompletionDeclaration(source),"已接入完成事务的邮件声明可识别");
        foreach(var field in new QuestField[] {
            new("mail time",[new(0,2,null)],[]),new("mail time",[],[]),
            new("mail title",[new(6,0,"空\0字符")],[]),
            new("mail contents",[new(6,0,new string('中',256))],[]),
            new("mail contents",[new(6,0,"😀")],[]),new("mail enclose",[new(0,2058,null)],[]),
            new("mail enclose",[new(0,2058,null),new(0,0,null)],[]) })
        {
            var invalid=source with {Definition=source.Definition with {Fields=source.Definition.Fields.Select(row=>row.Name==field.Name?field:row).ToImmutableArray()}};
            Reject<NotSupportedException>(()=>QuestMailPlan.Read(invalid),"未知时机、编码、长度或附件形状拒绝");
            That(!QuestMailPlan.CompletionDeclaration(invalid),"非法邮件不能进入已支持声明");
        }
        var duplicate=source with {Definition=source.Definition with {Fields=source.Definition.Fields.Add(source.Definition.Fields[0])}};
        Reject<NotSupportedException>(()=>QuestMailPlan.Read(duplicate),"重复时机段拒绝而非只取首段");
    }
}
