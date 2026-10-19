using System;
using System.Linq;

namespace MelonModTemplate.Market;

public sealed partial class MarketEngine
{
    private static void ValidateStockState(MarketState state)
    {
        if (state.Companies.Values.Any(c => c.EventShockDay < -1 || c.EventShockDay > state.LastDay || c.EventShockRatio <= 0m || c.EventShockRatio > 1000000000000m || Math.Abs(c.EventShockNetSignal) > 1000000m)) throw new ArgumentException("主冲击每日预算存档无效");
        var calendar = state.StockEvents;
        if (calendar == null || calendar.Version != 1 || calendar.Sequence < 0 || calendar.PositiveCount < 0 || calendar.NegativeCount < 0 || calendar.CompanyUseCounts == null || calendar.TemplateLastShockDay == null || calendar.Occurrences == null || calendar.SaveIdentity == null || calendar.SaveToken == null) throw new ArgumentException("股市专用事件排程存档无效");
        if (calendar.Initialized && (calendar.RandomState == 0 || calendar.NextShockDay < 0 || calendar.SaveIdentity != state.SaveIdentity || calendar.SaveToken.Length == 0) || !calendar.Initialized && calendar.Occurrences.Count > 0 || calendar.CompanyUseCounts.Values.Any(c => c < 0)) throw new ArgumentException("股市排程随机状态或存档身份无效");
        foreach (var pair in calendar.Occurrences)
        {
            var e = pair.Value;
            if (e == null || e.Id != pair.Key || string.IsNullOrWhiteSpace(e.Id) || string.IsNullOrWhiteSpace(e.TemplateId) || !e.TemplateId.StartsWith("stock:", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(e.PrimaryCompanyId) || string.IsNullOrWhiteSpace(e.Title) || string.IsNullOrWhiteSpace(e.Body) || e.Sectors == null || e.Impacts == null || e.ActualImpacts == null || e.Impacts.Count == 0 || !e.Impacts.ContainsKey(e.PrimaryCompanyId) || e.Polarity != Math.Sign(e.Impacts[e.PrimaryCompanyId]) || e.Impacts.Any(p => !state.Companies.ContainsKey(p.Key) || p.Value == 0 || Math.Abs(p.Value) > .5m) || e.ActualImpacts.Any(p => !state.Companies.ContainsKey(p.Key) || p.Value <= -1m) || e.StartDay < 0 || e.ShockDay < e.StartDay || e.EndDayExclusive <= e.ShockDay || e.EndDayExclusive - e.StartDay > 30 || e.PreheatSeed == 0 || e.PreheatChance < 0 || e.PreheatChance > 1 || e.RetentionRatio <= 0 || e.RetentionRatio >= 1 || e.Intensity <= 0 || e.Intensity > 2 || e.Published && (e.PublicDay < e.ShockDay || e.PublicDay > state.LastDay || e.Cancelled) || !e.Published && e.PublicDay != -1) throw new ArgumentException("股市专用事件实例快照无效");
            var key = EventKey(e.TemplateId, e.Id);
            if (e.Activated && !e.Completed && !state.EventLifecycles.TryGetValue(key, out _) || state.EventLifecycles.TryGetValue(key, out var life) && !life.StockOwned) throw new ArgumentException("股市专用事件与生命周期归属不一致");
        }
        if (state.EventLifecycles.Values.Any(l => l.StockOwned && (!calendar.Occurrences.TryGetValue(l.OccurrenceKey, out var e) || e.TemplateId != l.EventId))) throw new ArgumentException("专用生命周期缺少实例快照");
    }

    private static void MigrateShockBudget(MarketState state)
    {
        if (state.LastDay < 0) return;
        foreach (var company in state.Companies)
        {
            var quote = company.Value;
            if (quote.EventShockDay >= 0) continue;
            var prior = state.EventLifecycles.Values.Where(l => l.ShockApplied && l.ShockDay == state.LastDay && l.Companies.ContainsKey(company.Key)).ToArray();
            if (prior.Length == 0) continue;
            quote.EventShockDay = state.LastDay;
            foreach (var life in prior)
            {
                quote.EventShockRatio *= 1m + life.Companies[company.Key].AppliedInitialImpact;
                quote.EventShockNetSignal += life.Companies[company.Key].ReferenceInitialImpact;
            }
        }
    }
}
