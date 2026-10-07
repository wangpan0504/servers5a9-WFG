using DfoServer.Game.Inventory.Services;

namespace DfoServer.Testing.Inventory;

internal static class BoosterBatchRandomTests
{
    public static void Run(Action<bool, string> check)
    {
        var edges = new FixedRandom(1e-6);
        check(BoosterBatchRandom.Next(0, 1, 4, edges) == 0 && BoosterBatchRandom.Next(100, 0, 4, edges) == 0 &&
            BoosterBatchRandom.Next(100, 4, 4, edges) == 100 && edges.Calls == 0, "empty and certain distributions need no random draws");
        long large = 8190L * int.MaxValue;
        check(BoosterBatchRandom.Next(large, 1, long.MaxValue, edges) == 1 &&
            BoosterBatchRandom.Next(large, long.MaxValue - 1, long.MaxValue, edges) == large - 1,
            "integer complement preserves tiny positive weights instead of rounding to zero or certainty");
        check(BoosterBatchRandom.Next(65, 1, long.MaxValue, new RareRandom()) == 1 &&
            BoosterBatchRandom.Next(65, long.MaxValue - 1, long.MaxValue, new RareRandom()) == 64,
            "a reachable rare event survives even when its entire CDF tail is smaller than double precision near one");

        // 固定种子只用于测试；验证分布及资源量，不要求与旧的逐次抽取使用同一随机序列。
        const int samples = 20000;
        foreach (var (trials, weight, total) in new (long, long, long)[]
        {
            (65, 1, 8), (65, 1, 2), (1024, 1, 64), (int.MaxValue, 1, 4),
            (int.MaxValue, 1, int.MaxValue), (large, 1, 3)
        })
        {
            var random = new Random(310240);
            double p = (double)weight / total, mean = trials * p, variance = trials * p * (1 - p);
            double sum = 0, squares = 0;
            int[] histogram = new int[66];
            for (int i = 0; i < samples; i++)
            {
                long value = BoosterBatchRandom.Next(trials, weight, total, random);
                if (value < 0 || value > trials)
                    throw new InvalidOperationException("Binomial sample outside support");
                double z = (value - mean) / Math.Sqrt(variance);
                sum += z;
                squares += z * z;
                if (trials == 65)
                    histogram[(int)value]++;
            }
            check(Math.Abs(sum / samples) < 0.04 && squares / samples is > 0.9 and < 1.1,
                $"binomial mean and variance remain correct for {trials} trials, weight {weight}/{total}");
            if (trials != 65)
                continue;
            double mass = Math.Pow(1 - p, trials), expected = 0, observed = 0, difference = 0;
            for (int k = 0; k <= trials; k++)
            {
                expected += mass;
                observed += (double)histogram[k] / samples;
                difference = Math.Max(difference, Math.Abs(expected - observed));
                mass *= (trials - k) * p / ((k + 1) * (1 - p));
            }
            check(difference < 0.02, "full binomial CDF matches in both waiting-time and rejection sampling branches");
        }
    }

    sealed class FixedRandom(double value) : Random
    {
        public int Calls { get; private set; }
        public override double NextDouble()
        {
            Calls++;
            return value;
        }
        public override long NextInt64(long maxValue)
        {
            Calls++;
            return Math.Min(maxValue - 1, (long)(value * maxValue));
        }
    }

    sealed class RareRandom : Random
    {
        bool first = true;
        public override long NextInt64(long maxValue)
        {
            long value = first ? 0 : maxValue / 2;
            first = false;
            return value;
        }
    }

    internal sealed class BudgetRandom(int seed) : Random(seed)
    {
        public int Calls { get; private set; }
        void Count()
        {
            if (++Calls > 1024)
                throw new InvalidOperationException("Bulk booster exceeded random-work budget");
        }
        public override double NextDouble()
        {
            Count();
            return base.NextDouble();
        }
        public override long NextInt64(long maxValue)
        {
            Count();
            return base.NextInt64(maxValue);
        }
    }
}
