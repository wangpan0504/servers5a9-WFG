namespace DfoServer.Game.Raids;

// 只由服务端当前会话/领域快照构造，绝不从client回执反序列化；不复制角色资产。
internal sealed record LukeSceneReceiptIdentity(string Account, long ConnectionId, ushort Character, long OwnerGeneration,
    long SelectionEpoch, Guid RaidInstance, int Phase, long PhaseGeneration,
    byte Town, byte Area, long TargetRevision)
{
    internal bool Valid => !string.IsNullOrWhiteSpace(Account) && ConnectionId > 0 && OwnerGeneration > 0 &&
        SelectionEpoch > 0 && RaidInstance != Guid.Empty && Phase is 0 or 1 && PhaseGeneration >= 0 && TargetRevision > 0;
}

// CP_LUKE_SCENE/1逻辑合同，不定义opcode、端序或传输封装；TargetRevision不是native scene generation。
internal sealed record LukeSceneReceiptRequest(string RequestId, ushort CharacterId, byte Town, byte Area,
    long TargetRevision, int TtlMs)
{
    internal const string Contract = "CP_LUKE_SCENE/1";
    internal int Version => 1;
}
internal enum LukeSceneReceiptResult { Unavailable, NoPending, StaleIdentity, Expired, UnknownRequest, Observed, Duplicate }
// 仅表示收到观察，不是Ready/RestoreApproved；后续仍需完整服务端重入门禁。
internal sealed record LukeSceneObservation(string RequestId, LukeSceneReceiptIdentity Identity);

// 每个连接独占一个槽；默认关闭且无Runtime注册。调用方须持当前owner/命令门取得事实并串行使用返回值。
// 本锁只保护本槽；不获取owner/Battle锁、不等待网络或SQL。外部已失效的事实不能凭本对象变回权威。
internal sealed class LukeSceneReceipt(bool enabled = false, TimeProvider? clock = null)
{
    readonly object gate = new();
    readonly TimeProvider time = clock ?? TimeProvider.System;
    sealed record Pending(LukeSceneReceiptRequest Request, LukeSceneReceiptIdentity Identity, long Started)
    {
        internal bool Observed { get; set; }
        internal bool Consumed { get; set; }
    }
    Pending? pending;
    bool capabilityConfirmed;
    bool disconnected;
    bool clockFaulted;
    long? lastTimestamp;

    // 只能由此连接真实owner上下文的当前能力事件调用，不接受延迟回执捕获的能力标志。
    // 本批没有协商/Runtime调用；默认false。重新启用不复活已撤销的槽。
    internal void SetCapability(bool confirmed)
    {
        lock (gate)
        {
            if (disconnected) return;
            capabilityConfirmed = confirmed;
            if (!confirmed) pending = null;
        }
    }

    // 能力来自未来对CP_LUKE_SCENE/1精确版本的独立协商，不来自本条回执自报。
    internal LukeSceneReceiptRequest? Begin(LukeSceneReceiptIdentity? current, int ttlMs)
    {
        lock (gate)
        {
            if (!enabled || disconnected || clockFaulted || !capabilityConfirmed || current is not { Valid: true })
            { pending = null; return null; }
            if (ttlMs is < 1 or > 30000) return null;
            if (!ReadTimestamp(out long started)) return null;
            string id;
            do { id = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)); }
            while (!ValidRequestId(id) || id == pending?.Request.RequestId);
            var request = new LukeSceneReceiptRequest(id, current.Character,
                current.Town, current.Area, current.TargetRevision, ttlMs);
            pending = new(request, current, started);
            return request;
        }
    }

    internal LukeSceneReceiptResult Observe(string? requestId, LukeSceneReceiptIdentity? current)
    {
        lock (gate)
        {
            if (!Available()) return LukeSceneReceiptResult.Unavailable;
            if (pending is not null && requestId != pending.Request.RequestId) return LukeSceneReceiptResult.UnknownRequest;
            var check = Validate(current);
            if (check is not null) return check.Value;
            if (!ValidRequestId(requestId) || requestId != pending!.Request.RequestId) return LukeSceneReceiptResult.UnknownRequest;
            if (pending.Observed) return LukeSceneReceiptResult.Duplicate;
            pending.Observed = true;
            return LukeSceneReceiptResult.Observed;
        }
    }

    internal LukeSceneObservation? ConsumeObservation(string? requestId, LukeSceneReceiptIdentity? current)
    {
        lock (gate)
        {
            if (!Available()) return null;
            if (pending is not null && requestId != pending.Request.RequestId) return null;
            if (Validate(current) is not null || requestId != pending!.Request.RequestId ||
                !pending.Observed || pending.Consumed) return null;
            pending.Consumed = true;
            return new(pending.Request.RequestId, pending.Identity);
        }
    }

    // 只撤销自己的请求；旧排队回调不得清掉替代请求。
    internal void Cancel(string requestId)
    { lock (gate) if (pending?.Request.RequestId == requestId) pending = null; }

    // 连接对象生命周期结束不可复用；新连接必须有新槽，不能让旧连接清理波及新连接。
    internal void Disconnect()
    { lock (gate) { disconnected = true; pending = null; } }

    LukeSceneReceiptResult? Validate(LukeSceneReceiptIdentity? current)
    {
        if (!Available()) return LukeSceneReceiptResult.Unavailable;
        if (pending is null) return LukeSceneReceiptResult.NoPending;
        if (current is not { Valid: true } || pending.Identity != current)
        { pending = null; return LukeSceneReceiptResult.StaleIdentity; }
        if (!ReadTimestamp(out long now)) return LukeSceneReceiptResult.Expired;
        var elapsed = time.GetElapsedTime(pending!.Started, now);
        // 单调源回退同样失效；不能因负elapsed把请求延长。Observed/Consumed也不重置原deadline。
        if (elapsed < TimeSpan.Zero || elapsed >= TimeSpan.FromMilliseconds(pending.Request.TtlMs))
        { pending = null; return LukeSceneReceiptResult.Expired; }
        return null;
    }

    // 已验证能力与回执内容分离；真正撤销事件直接清槽，旧ID不能携旧能力事实影响新槽。
    bool Available()
    {
        if (enabled && !disconnected && !clockFaulted && capabilityConfirmed) return true;
        pending = null;
        return false;
    }

    bool ReadTimestamp(out long stamp)
    {
        stamp = time.GetTimestamp();
        if (lastTimestamp is { } previous && stamp < previous)
        { clockFaulted = true; pending = null; return false; }
        lastTimestamp = stamp;
        return true;
    }

    internal static bool ValidRequestId(string? value) => value is { Length: 32 } &&
        value.Any(c => c != '0') && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
