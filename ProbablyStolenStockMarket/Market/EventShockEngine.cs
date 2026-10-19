using System;
using System.Linq;

namespace ProbablyStolenStockMarket.Market;

public sealed partial class MarketEngine
{
    // 当日冲击先净额合并，再只把真实新增价格变化分配给本批同方向实例。
    private void ApplyEventShocks(EventLifecycleState[] due, int day)
    {
        foreach (var life in due.Where(l => l.StockOwned)) PublishStockEvent(life, day);
        foreach (var company in Config.Companies)
        {
            var contributors = due.Where(l => l.Companies.TryGetValue(company.Id, out var e) && e.ReferenceInitialImpact != 0).ToArray();
            if (contributors.Length == 0) continue;
            var quote = State.Companies[company.Id];
            if (quote.EventShockDay != day) { quote.EventShockDay = day; quote.EventShockNetSignal = 0m; quote.EventShockRatio = 1m; }
            var previous = quote.EventShockRatio;
            var signal = contributors.Sum(l => l.Companies[company.Id].ReferenceInitialImpact);
            quote.EventShockNetSignal += signal;
            // 旧档或日内修改限额不能强行撤回已经真实发生的价格。
            var lowerRatio = Math.Min(1m - Config.EventShockDailyLimit, previous);
            var upperRatio = Math.Max(1m + Config.EventShockDailyLimit, previous);
            var target = signal == 0m ? previous : Math.Clamp(1m + quote.EventShockNetSignal, lowerRatio, upperRatio);
            // 取整误差也不能使本批利好反跌或利空反涨。
            target = signal > 0m ? Math.Max(previous, target) : signal < 0m ? Math.Min(previous, target) : previous;
            var before = quote.Price;
            var lower = lowerRatio == previous ? before : Math.Max(.01m, decimal.Ceiling(before / previous * lowerRatio * 100m) / 100m);
            var upper = upperRatio == previous ? before : Math.Min(10000000m, decimal.Floor(before / previous * upperRatio * 100m) / 100m);
            var next = decimal.Round(before * target / previous, 2, MidpointRounding.AwayFromZero);
            next = lower <= upper ? Math.Clamp(next, lower, upper) : before;
            var actual = next / before;
            var total = previous * actual;
            var direction = Math.Sign(actual - 1m);
            var effective = contributors.Where(l => Math.Sign(l.Companies[company.Id].ReferenceInitialImpact) == direction).ToArray();
            var weight = effective.Sum(l => Math.Abs(l.Companies[company.Id].ReferenceInitialImpact));
            var running = previous;
            for (var i = 0; i < effective.Length; i++)
            {
                var effect = effective[i].Companies[company.Id];
                var end = i == effective.Length - 1 ? total : running + (total - previous) * Math.Abs(effect.ReferenceInitialImpact) / weight;
                var ratio = end / running;
                effect.AppliedInitialImpact = ratio - 1m;
                effect.CurrentFactor *= ratio;
                effect.RetainedTargetFactor = 1m + (effect.CurrentFactor - 1m) * effective[i].RetentionRatio;
                running = end;
            }
            quote.EventShockRatio = total;
            if (actual != 1m)
            {
                var reason = contributors.Any(l => !l.IsPublic) ? "市场异动" : contributors.Length == 1 ? contributors[0].PublicDescription : "多项公开事件合并冲击";
                MovePrice(company.Id, actual - 1m, reason);
            }
        }
        foreach (var life in due) life.ShockApplied = true;
    }
}
