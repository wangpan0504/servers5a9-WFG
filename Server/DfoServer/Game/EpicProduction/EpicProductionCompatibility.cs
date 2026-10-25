namespace DfoServer.Game.EpicProduction;

// 本服明确选择的独立大成功策略，不声称还原 PVF 的 min/max big chance limit 或 fix range 调度。
internal sealed record EpicProductionCompatibility
{
    public int GreatSuccessPercent { get; init; } = 8;
    public EpicProductionCompatibility Validate()
    {
        if (GreatSuccessPercent is <0 or >100) throw new InvalidDataException("史诗加工独立大成功概率必须在0至100之间");
        return this;
    }
    // 单次规划调用一次并持有返回值；数据库重试必须复用原计划，不重新调用此函数。
    public bool DrawGreatSuccess(Func<int,int> next)
    {
        Validate();
        int sample=next(1000000);
        if (sample is <0 or >=1000000) throw new InvalidDataException("史诗加工随机样本越界");
        return sample<GreatSuccessPercent*10000;
    }
}
