using System.Reflection;
using System.Runtime.ExceptionServices;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing;

// 只拦截指定父任务的 CommitQuestCompletion。子任务提交照常落库。
// Busy 为真时抛 SQLITE_BUSY，真实连接上的忙锁重试会按同一冻结计划再跑一次。
internal class QuestCommitFaultProxy : DispatchProxy
{
    internal ICharacterRepository? Target { get; set; }
    internal int ParentQuestId { get; set; }
    internal int FailuresLeft { get; set; }
    internal bool AfterCommit { get; set; }
    internal bool Busy { get; set; }
    internal int ParentCalls { get; private set; }
    internal bool SamePlan { get; private set; }
    internal bool? FirstDuplicate { get; private set; }
    internal bool LastDuplicate { get; private set; }
    QuestCompletionPlan? firstPlan;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null || Target is null)
            throw new InvalidOperationException("领奖故障代理尚未接上仓储");
        try
        {
            if (targetMethod.Name == nameof(ICharacterRepository.CommitQuestCompletion) &&
                args is [_, QuestCompletionPlan plan] && plan.QuestId == ParentQuestId)
            {
                ParentCalls++;
                if (firstPlan is null) firstPlan = plan;
                else SamePlan = ReferenceEquals(firstPlan, plan);
                if (FailuresLeft > 0)
                {
                    FailuresLeft--;
                    if (!AfterCommit)
                        throw Fault();
                    var committed = targetMethod.Invoke(Target, args);
                    if (committed is QuestCompletionCommit commit) Remember(commit);
                    throw Fault();
                }
            }
            var result = targetMethod.Invoke(Target, args);
            if (targetMethod.Name == nameof(ICharacterRepository.CommitQuestCompletion) &&
                args is [_, QuestCompletionPlan donePlan] && donePlan.QuestId == ParentQuestId &&
                result is QuestCompletionCommit done)
                Remember(done);
            return result;
        }
        catch (TargetInvocationException error)
        {
            ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw();
            throw;
        }
    }

    void Remember(QuestCompletionCommit commit)
    {
        FirstDuplicate ??= commit.Duplicate;
        LastDuplicate = commit.Duplicate;
    }

    Exception Fault() => Busy
        ? new SqliteException(AfterCommit ? "范围父任务已提交但返回丢失" : "范围父任务提交前失败", 5)
        : new IOException(AfterCommit ? "范围父任务已提交但返回丢失" : "范围父任务提交前失败");

    internal static QuestCommitFaultProxy Arm(GameSession session, int parentQuestId, bool afterCommit, bool busy)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var field = typeof(GameSession).GetField("<repository>P", flags)
            ?? throw new InvalidOperationException("会话仓储字段不存在");
        var inner = (ICharacterRepository)field.GetValue(session)!;
        var proxy = DispatchProxy.Create<ICharacterRepository, QuestCommitFaultProxy>();
        var fault = (QuestCommitFaultProxy)(object)proxy;
        fault.Target = inner;
        fault.ParentQuestId = parentQuestId;
        fault.AfterCommit = afterCommit;
        fault.Busy = busy;
        fault.FailuresLeft = 1;
        field.SetValue(session, proxy);
        return fault;
    }
}
