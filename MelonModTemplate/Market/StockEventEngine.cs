using System;
using System.Collections.Generic;
using System.Linq;

namespace MelonModTemplate.Market;

public sealed partial class MarketEngine
{
    // 仅供游戏适配层接入内幕候选；不能将未来排程直接交给公开界面。
    public IReadOnlyList<ScheduledEvent> GetStockScheduledEvents() => State.StockEvents.Occurrences.Values
        .Where(e => !e.Cancelled && !e.Published && e.ShockDay > State.LastDay)
        .OrderBy(e => e.ShockDay).ThenBy(e => e.Id, StringComparer.Ordinal)
        .Select(e => new ScheduledEvent { EventId = e.TemplateId, OccurrenceKey = e.Id, Day = e.ShockDay }).ToList();

    public IReadOnlyList<StockNewsView> GetPublicStockNews(string? companyId = null) => State.StockEvents.Occurrences.Values
        .Where(e => e.Published && e.PublicDay <= State.LastDay && (companyId == null || e.Impacts.ContainsKey(companyId)))
        .OrderByDescending(e => e.PublicDay).ThenBy(e => e.Id, StringComparer.Ordinal)
        .Select(e => new StockNewsView
        {
            Id = e.Id, Title = e.Title, Body = e.Body, PublicDay = e.PublicDay,
            Stage = State.LastDay == e.PublicDay ? "主冲击" : State.LastDay < e.EndDayExclusive ? "延续" : e.Completed ? "已结束" : "余波修复",
            CompanyDirections = e.Impacts.ToDictionary(p => p.Key, p => Math.Sign(p.Value)), ActualImpacts = new(e.ActualImpacts)
        }).ToList();

    public bool CancelStockEvent(string occurrenceId)
    {
        if (_busy || !State.StockEvents.Occurrences.TryGetValue(occurrenceId, out var occurrence) || occurrence.Published || occurrence.Cancelled) return false;
        occurrence.Cancelled = true;
        if (State.EventLifecycles.TryGetValue(EventKey(occurrence.TemplateId, occurrence.Id), out var life)) EndLifecycle(life, State.LastDay);
        foreach (var offer in State.Rumors.Values.Where(r => r.EventId == occurrence.TemplateId && r.OccurrenceKey == occurrence.Id && r.Resolution == RumorResolution.Pending))
        {
            offer.Resolution = RumorResolution.Unverified;
            if (!offer.Purchased) offer.Expired = true;
        }
        return true;
    }

    private StockEventOccurrence? FindStockOccurrence(string eventId, string occurrenceKey) => State.StockEvents.Occurrences.TryGetValue(occurrenceKey, out var stock) && stock.TemplateId == eventId ? stock : null;

    private EventRule? FindScheduledRule(ScheduledEvent scheduled)
    {
        var stock = FindStockOccurrence(scheduled.EventId, scheduled.OccurrenceKey);
        if (stock != null) return stock.Cancelled || stock.Published ? null : new EventRule
        {
            EventId = stock.TemplateId, ImpactSource = "stock-template", Description = stock.Title,
            Sectors = new(stock.Sectors), Impacts = new(stock.Impacts)
        };
        return Config.Events.FirstOrDefault(e => e.EventId == scheduled.EventId);
    }

    private void EnsureStockCalendar(int day)
    {
        if (day < 0 || !Config.EnableStockEvents) return;
        var calendar = State.StockEvents;
        if (!calendar.Initialized)
        {
            unchecked
            {
                var hash = 1469598103934665603UL;
                foreach (var c in State.SaveIdentity) { hash ^= c; hash *= 1099511628211UL; }
                calendar.RandomState = hash == 0 ? 0xD1B54A32D192ED03UL : hash;
                calendar.SaveToken = calendar.RandomState.ToString("x16");
            }
            calendar.SaveIdentity = State.SaveIdentity;
            calendar.Initialized = true;
            calendar.NextShockDay = day + NextStockInt(Config.StockEventIntervalMinDays, Config.StockEventIntervalMaxDays);
        }
        // 重新启用只排未来，已经保存的实例继续使用自己的快照，不补造停用期间的历史。
        if (calendar.NextShockDay <= day) calendar.NextShockDay = day + NextStockInt(Config.StockEventIntervalMinDays, Config.StockEventIntervalMaxDays);
        var horizon = day + Config.StockScheduleDays;
        while (calendar.NextShockDay <= horizon)
        {
            var shockDay = calendar.NextShockDay;
            var eligible = Config.StockEventTemplates.Where(t => !calendar.TemplateLastShockDay.TryGetValue(t.Id, out var last) || shockDay - last >= Config.StockTemplateCooldownDays).ToList();
            if (eligible.Count == 0) { calendar.NextShockDay++; continue; }
            var preferred = calendar.PositiveCount <= calendar.NegativeCount ? 1 : -1;
            var balanced = eligible.Where(t => t.Polarity == preferred).ToList();
            if (balanced.Count == 0) balanced = eligible;
            var least = balanced.Min(t => calendar.CompanyUseCounts.GetValueOrDefault(t.PrimaryCompanyId));
            balanced = balanced.Where(t => calendar.CompanyUseCounts.GetValueOrDefault(t.PrimaryCompanyId) == least).OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
            var template = balanced[NextStockInt(0, balanced.Count - 1)];
            var duration = NextStockInt(Config.StockDurationMinDays, Config.StockDurationMaxDays);
            var offset = NextStockInt(0, Math.Min(duration - 1, shockDay - day - 1));
            var intensity = Config.StockIntensityMin + NextStockRandom() * (Config.StockIntensityMax - Config.StockIntensityMin);
            NextStockRandom(); var seed = calendar.RandomState;
            var retention = Config.EventRetentionMin + NextStockRandom() * (Config.EventRetentionMax - Config.EventRetentionMin);
            var id = "stock-occurrence:" + calendar.SaveToken + ":" + (++calendar.Sequence);
            calendar.Occurrences.Add(id, new StockEventOccurrence
            {
                Id = id, TemplateId = template.Id, PrimaryCompanyId = template.PrimaryCompanyId, Polarity = template.Polarity,
                Title = template.Title, Body = template.Body, Sectors = new(template.Sectors), Impacts = template.Impacts.ToDictionary(p => p.Key, p => p.Value * intensity),
                Intensity = intensity, StartDay = shockDay - offset, ShockDay = shockDay, EndDayExclusive = shockDay - offset + duration,
                PreheatSeed = seed, PreheatChance = Config.EventPreheatChance, RetentionRatio = retention
            });
            calendar.CompanyUseCounts[template.PrimaryCompanyId] = least + 1;
            calendar.TemplateLastShockDay[template.Id] = shockDay;
            if (template.Polarity > 0) calendar.PositiveCount++; else calendar.NegativeCount++;
            calendar.NextShockDay += NextStockInt(Config.StockEventIntervalMinDays, Config.StockEventIntervalMaxDays);
        }
        calendar.PlannedThroughDay = Math.Max(calendar.PlannedThroughDay, horizon);
    }

    private decimal NextStockRandom()
    {
        var calendar = State.StockEvents; var x = calendar.RandomState;
        x ^= x << 13; x ^= x >> 7; x ^= x << 17;
        calendar.RandomState = x;
        return (x >> 11) / 9007199254740992m;
    }
    private int NextStockInt(int min, int max) => min + (int)decimal.Floor(NextStockRandom() * ((long)max - min + 1));

    private void StartStockEvents(int day)
    {
        foreach (var stock in State.StockEvents.Occurrences.Values.Where(e => !e.Cancelled && !e.Activated && e.StartDay <= day).OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var key = EventKey(stock.TemplateId, stock.Id);
            var life = new EventLifecycleState
            {
                StockOwned = true, EventId = stock.TemplateId, OccurrenceKey = stock.Id, ImpactSource = "stock-template",
                StartDay = stock.StartDay, FirstObservedDay = day, LastAdvanceDay = day - 1, ActiveConfirmedThroughDay = day,
                ShockPlanVersion = 1, ShockDay = stock.ShockDay, PreheatSeed = stock.PreheatSeed, PreheatChance = stock.PreheatChance,
                ExpectedEndDayExclusive = stock.EndDayExclusive, IsPublic = false, PublicDescription = stock.Title, RetentionRatio = stock.RetentionRatio
            };
            foreach (var impact in stock.Impacts) life.Companies[impact.Key] = new EventCompanyEffect { ReferenceInitialImpact = impact.Value };
            State.EventRetentions[key] = stock.RetentionRatio;
            State.EventLifecycles.Add(key, life); State.ProcessedEvents.Add(key); stock.Activated = true;
        }
    }

    private void PublishStockEvent(EventLifecycleState life, int day)
    {
        var stock = FindStockOccurrence(life.EventId, life.OccurrenceKey);
        if (stock == null || stock.Cancelled || stock.Published) return;
        stock.Published = true; stock.PublicDay = day; life.IsPublic = true;
        var key = EventKey(stock.TemplateId, stock.Id);
        State.OccurredEvents.Add(key); State.ObservedEventDirections[key] = stock.Impacts.ToDictionary(p => p.Key, p => Math.Sign(p.Value));
        Log("stock-event", stock.Title + "。" + stock.Body);
    }

    private void CaptureStockProgress()
    {
        foreach (var stock in State.StockEvents.Occurrences.Values.Where(e => e.Activated))
        {
            var key = EventKey(stock.TemplateId, stock.Id);
            if (State.EventLifecycles.TryGetValue(key, out var life)) stock.ActualImpacts = life.Companies.ToDictionary(p => p.Key, p => p.Value.CurrentFactor - 1m);
            else if (State.CompletedEventLifecycles.Contains(key)) stock.Completed = true;
        }
    }

    private string IntelPublicNews(RumorOffer offer)
    {
        if (!offer.Purchased || offer.Resolution == RumorResolution.Pending) return "";
        var stock = FindStockOccurrence(offer.EventId, offer.OccurrenceKey);
        return stock?.Published == true ? stock.Title + "：" + stock.Body : "";
    }

    private string IntelVerificationSummary(RumorOffer offer)
    {
        if (!offer.Purchased || offer.Resolution == RumorResolution.Pending) return "";
        var stock = FindStockOccurrence(offer.EventId, offer.OccurrenceKey);
        if (offer.Resolution == RumorResolution.Unverified || stock != null && !stock.Published) return "原线索未能验证，不计入来源信誉。";
        if (!State.ObservedEventDirections.TryGetValue(EventKey(offer.EventId, offer.OccurrenceKey), out var observed)) return "原线索未能验证，不计入来源信誉。";
        var comparisons = offer.CompanyDirections.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
            (Config.Companies.FirstOrDefault(c => c.Id == p.Key)?.Name ?? "关联公司") + "：原预测" + (p.Value > 0 ? "走强" : "走弱") + "，实际事件作用" + (observed.GetValueOrDefault(p.Key) > 0 ? "利好" : "利空"));
        return (offer.Resolution == RumorResolution.True ? "核验属实。" : "核验失实。") + string.Join("；", comparisons) + "。核验依据为事件事实，不以股价最终涨跌判定。";
    }
}
