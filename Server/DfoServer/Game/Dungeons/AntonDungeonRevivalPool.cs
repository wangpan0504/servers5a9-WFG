namespace DfoServer.Game.Dungeons;

// 正式安徒恩DGN的[coin info]缺省0，客户端用本Run总数比较[coin limit]。
// 池跟随PartyDungeon实例；不跨副本或阶段累加，不套卢克阶段额度。
internal sealed class AntonDungeonRevivalPool
{
    readonly object gate = new();
    readonly Dictionary<ushort, long> committedDeaths = [];
    readonly HashSet<Reservation> pending = [];
    Guid? run;
    int? limit;
    int used;
    internal int Used { get { lock (gate) return used; } }

    internal Reservation? Reserve(DungeonRunIdentity identity, long death, int? maximum)
    {
        lock (gate)
        {
            if (death <= 0 || maximum < 0 || run is { } existing &&
                (existing != identity.InstanceId || limit != maximum) ||
                death <= committedDeaths.GetValueOrDefault(identity.CharacterId) ||
                pending.Any(row => row.Identity.CharacterId == identity.CharacterId) ||
                maximum is { } cap && used + pending.Count >= cap) return null;
            run ??= identity.InstanceId;
            limit = maximum;
            var reservation = new Reservation(this, identity, death);
            pending.Add(reservation);
            return reservation;
        }
    }

    internal sealed class Reservation : IDisposable
    {
        readonly AntonDungeonRevivalPool pool;
        readonly long death;
        internal DungeonRunIdentity Identity { get; }
        internal Reservation(AntonDungeonRevivalPool pool, DungeonRunIdentity identity, long death)
        { this.pool = pool; Identity = identity; this.death = death; }

        // 库存事务已经成功；本人owner保证原Run/死亡状态不变，不在提交后追加动态拒绝。
        internal void Commit()
        {
            lock (pool.gate)
            {
                if (!pool.pending.Remove(this)) return;
                pool.committedDeaths[Identity.CharacterId] = death;
                pool.used++;
            }
        }

        public void Dispose()
        {
            lock (pool.gate) pool.pending.Remove(this);
        }
    }
}
