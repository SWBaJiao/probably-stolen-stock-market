using System;
using System.Collections.Generic;
using System.Linq;

namespace MelonModTemplate.Market;

public sealed partial class MarketEngine
{
    // 空列表代表权威确认没有事件；快照不可用时应只调用 AdvanceDay，不能把它伪装成空列表。
    public bool SyncActiveEvents(IReadOnlyList<ActiveMarketEvent> events, int observedDay, bool initialLoad = false)
    {
        if (observedDay < 0 || observedDay < State.LastDay) return false;
        if (events == null || events.Any(e => e == null || string.IsNullOrWhiteSpace(e.EventId) || string.IsNullOrWhiteSpace(e.OccurrenceKey))) return AdvanceDay(observedDay);
        var active = events.GroupBy(e => EventKey(e.EventId, e.OccurrenceKey)).ToDictionary(g => g.Key, g => g.First());
        var changed = false;
        // 先结束缺项，避免推进新日时多续一天；新事件必须在推进日期以后才施加首次冲击。
        foreach (var pair in State.EventLifecycles.ToArray())
        {
            if (active.TryGetValue(pair.Key, out var native)) changed |= ConfirmActive(pair.Value, native, observedDay);
            else
            {
                var lastActive = observedDay - 1;
                if (!pair.Value.IsPermanent && pair.Value.ExpectedEndDayExclusive.HasValue && pair.Value.ExpectedEndDayExclusive.Value <= observedDay)
                    lastActive = Math.Min(lastActive, Math.Max(pair.Value.ExpectedEndDayExclusive.Value - 1, pair.Value.ActiveConfirmedThroughDay));
                changed |= EndEvent(pair.Value.EventId, pair.Value.OccurrenceKey, lastActive);
            }
        }
        changed |= AdvanceDay(observedDay);
        foreach (var pair in active.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!State.ProcessedEvents.Contains(pair.Key)) changed |= ActivateEvent(pair.Value.EventId, pair.Value.OccurrenceKey, pair.Value.StartDay, pair.Value.IsPublic, pair.Value.ExpectedEndDayExclusive, pair.Value.IsPermanent);
            else if (!State.EventLifecycles.ContainsKey(pair.Key))
                changed |= RegisterEventLifecycle(pair.Value, observedDay, null, true);
        }
        return changed;
    }

    // lastActiveDay 为最后生效日：日末 Deactivate(D) 后，最早于 D+1 轻微恢复。
    public bool EndEvent(string eventId, string occurrenceKey, int lastActiveDay)
    {
        if (!State.EventLifecycles.TryGetValue(EventKey(eventId, occurrenceKey), out var life) || !life.NativeActive) return false;
        life.NativeActive = false;
        life.LastActiveDay = Math.Max(life.FirstObservedDay, lastActiveDay);
        life.RecoveryTargetFrozen = false;
        if (life.LastActiveDay <= State.LastDay) FreezeRecoveryTarget(life);
        return true;
    }

    private static void FreezeRecoveryTarget(EventLifecycleState life)
    {
        foreach (var effect in life.Companies.Values)
        {
            effect.RetainedTargetFactor = 1m + (effect.CurrentFactor - 1m) * life.RetentionRatio;
            effect.RecoveryCarry = 0m; effect.RecoveryComplete = false;
        }
        life.RecoveryTargetFrozen = true;
    }

    private bool RegisterEventLifecycle(ActiveMarketEvent native, int observedDay, Dictionary<string, decimal>? appliedFactors, bool legacy)
    {
        var key = EventKey(native.EventId, native.OccurrenceKey);
        if (State.CompletedEventLifecycles.Contains(key)) return false;
        if (State.EventLifecycles.TryGetValue(key, out var existing)) return ConfirmActive(existing, native, observedDay);
        var rule = Config.Events.FirstOrDefault(e => e.EventId == native.EventId);
        if (rule == null || !rule.Impacts.Any(p => p.Value != 0 && State.Companies.ContainsKey(p.Key))) return false;
        if (!State.EventRetentions.TryGetValue(key, out var retention))
        {
            retention = Config.EventRetentionMin + NextRandom() * (Config.EventRetentionMax - Config.EventRetentionMin);
            State.EventRetentions[key] = retention;
        }
        var life = new EventLifecycleState
        {
            EventId = native.EventId, OccurrenceKey = native.OccurrenceKey, StartDay = native.StartDay,
            FirstObservedDay = observedDay, LastAdvanceDay = observedDay, ActiveConfirmedThroughDay = observedDay,
            ExpectedEndDayExclusive = native.ExpectedEndDayExclusive, IsPermanent = native.IsPermanent,
            IsPublic = native.IsPublic, RetentionRatio = retention, LegacyAdopted = legacy
        };
        foreach (var impact in rule.Impacts.Where(p => p.Value != 0 && State.Companies.ContainsKey(p.Key)))
        {
            var factor = appliedFactors?.GetValueOrDefault(impact.Key, 1m) ?? 1m;
            // 旧档不知道旧首冲的真实金额，只跟踪接入以后新增的延续影响，不假造可回撤历史。
            life.Companies[impact.Key] = new EventCompanyEffect { ReferenceInitialImpact = impact.Value, AppliedInitialImpact = factor - 1m, CurrentFactor = factor, RetainedTargetFactor = 1m + (factor - 1m) * retention };
        }
        State.EventLifecycles.Add(key, life);
        return true;
    }

    private static bool ConfirmActive(EventLifecycleState life, ActiveMarketEvent native, int day)
    {
        var changed = !life.NativeActive || life.ActiveConfirmedThroughDay < day || life.ExpectedEndDayExclusive != native.ExpectedEndDayExclusive || life.IsPermanent != native.IsPermanent || life.IsPublic != native.IsPublic;
        life.NativeActive = true; life.LastActiveDay = -1;
        life.RecoveryTargetFrozen = false;
        life.ActiveConfirmedThroughDay = Math.Max(life.ActiveConfirmedThroughDay, day);
        life.ExpectedEndDayExclusive = native.ExpectedEndDayExclusive;
        life.IsPermanent = native.IsPermanent; life.IsPublic = native.IsPublic;
        return changed;
    }

    public decimal GetEventAdjustedAnchor(string companyId)
    {
        var factor = State.Companies[companyId].PersistentEventFactor;
        foreach (var life in State.EventLifecycles.OrderBy(p => p.Key, StringComparer.Ordinal))
            if (life.Value.Companies.TryGetValue(companyId, out var effect)) factor = FactorProduct(factor, effect.CurrentFactor);
        return Math.Clamp(Config.InitialPrice * factor, .01m, 10000000m);
    }

    private void AdvanceEventLifecycles(int day)
    {
        var advancing = State.EventLifecycles.OrderBy(p => p.Key, StringComparer.Ordinal).Where(p => p.Value.LastAdvanceDay < day).ToArray();
        foreach (var pair in advancing)
        {
            var life = pair.Value;
            if (life.NativeActive && !life.IsPermanent && life.ExpectedEndDayExclusive.HasValue && day >= life.ExpectedEndDayExclusive.Value && day > life.ActiveConfirmedThroughDay)
                EndEvent(life.EventId, life.OccurrenceKey, Math.Max(life.ExpectedEndDayExclusive.Value - 1, life.ActiveConfirmedThroughDay));
            if (!life.NativeActive && day > life.LastActiveDay && !life.RecoveryTargetFrozen) FreezeRecoveryTarget(life);
            life.LastAdvanceDay = day;
        }
        foreach (var company in Config.Companies)
        {
            var basePrice = State.Companies[company.Id].Price;
            var simulatedPrice = basePrice;
            var runningRatio = 1m;
            var continued = false; var recovered = false; var hidden = false;
            foreach (var pair in advancing)
            {
                var life = pair.Value;
                if (day <= life.FirstObservedDay || !life.Companies.TryGetValue(company.Id, out var effect)) continue;
                var activeForDay = life.NativeActive || day <= life.LastActiveDay;
                if (!activeForDay && effect.RecoveryComplete) continue;
                var current = effect.CurrentFactor;
                decimal candidate;
                if (activeForDay)
                {
                    var remaining = Math.Max(0m, Math.Abs(effect.ReferenceInitialImpact) * Config.EventContinuationBudget - effect.ContinuationUsed);
                    var amount = Math.Min(remaining, Math.Min(Math.Abs(effect.ReferenceInitialImpact) * Config.EventContinuationScale, current * Config.EventContinuationLimit));
                    candidate = current + Math.Sign(effect.ReferenceInitialImpact) * amount;
                }
                else
                {
                    if (day <= life.LastActiveDay) continue;
                    var remaining = effect.RetainedTargetFactor - current;
                    // 已经位于目标最近的分币报价时，以真实现有因子归档，不伪造不足半分的回撤。
                    if (simulatedPrice * Math.Abs(remaining / current) <= .005000000001m)
                    {
                        effect.RecoveryComplete = true;
                        continue;
                    }
                    effect.RecoveryCarry = Math.Min(Math.Abs(remaining), effect.RecoveryCarry + Math.Abs(remaining) * Config.EventRecoveryRate);
                    candidate = current + Math.Sign(remaining) * effect.RecoveryCarry;
                }
                var ratio = Math.Clamp(candidate / current, (1m - Config.EventDailyDriftLimit) / runningRatio, (1m + Config.EventDailyDriftLimit) / runningRatio);
                var wantedPrice = simulatedPrice * ratio;
                // 持续影响向当前价量化避免超用预算；修复允许到目标最近报价的半分误差。
                var nextPrice = activeForDay ? wantedPrice >= simulatedPrice ? decimal.Floor(wantedPrice * 100m) / 100m : decimal.Ceiling(wantedPrice * 100m) / 100m : decimal.Round(wantedPrice, 2, MidpointRounding.AwayFromZero);
                if (!activeForDay)
                {
                    var nearestTarget = decimal.Round(simulatedPrice * effect.RetainedTargetFactor / current, 2, MidpointRounding.AwayFromZero);
                    nextPrice = Math.Clamp(nextPrice, Math.Min(simulatedPrice, nearestTarget), Math.Max(simulatedPrice, nearestTarget));
                }
                var lower = Math.Max(.01m, decimal.Ceiling(basePrice * (1m - Config.EventDailyDriftLimit) * 100m) / 100m);
                var upper = Math.Min(10000000m, decimal.Floor(basePrice * (1m + Config.EventDailyDriftLimit) * 100m) / 100m);
                nextPrice = lower <= upper ? Math.Clamp(nextPrice, lower, upper) : simulatedPrice;
                var actualRatio = nextPrice / simulatedPrice;
                effect.CurrentFactor = current * actualRatio;
                if (actualRatio != 1m)
                {
                    continued |= activeForDay;
                    recovered |= !activeForDay;
                    hidden |= !life.IsPublic;
                }
                if (activeForDay) effect.ContinuationUsed += Math.Abs(effect.CurrentFactor - current);
                else effect.RecoveryCarry = Math.Max(0m, effect.RecoveryCarry - Math.Abs(effect.CurrentFactor - current));
                simulatedPrice = nextPrice;
                runningRatio = simulatedPrice / basePrice;
            }
            if (runningRatio != 1m)
            {
                var phase = continued && recovered ? "持续影响与余波修复" : recovered ? "余波修复" : "持续影响";
                var reason = (hidden ? "市场异动：" : "事件") + phase + $" {runningRatio - 1m:+0.00%;-0.00%;0.00%}";
                MovePrice(company.Id, runningRatio - 1m, reason, Config.EventDailyDriftLimit);
            }
        }
        foreach (var pair in advancing)
        {
            var life = pair.Value;
            if (life.NativeActive || day <= life.LastActiveDay || !life.RecoveryTargetFrozen || life.Companies.Values.Any(e => !e.RecoveryComplete)) continue;
            foreach (var effect in life.Companies) State.Companies[effect.Key].PersistentEventFactor = FactorProduct(State.Companies[effect.Key].PersistentEventFactor, effect.Value.CurrentFactor);
            State.CompletedEventLifecycles.Add(pair.Key);
            State.EventLifecycles.Remove(pair.Key);
        }
    }

    private static decimal FactorProduct(decimal first, decimal second)
    {
        const decimal max = 1000000000000m;
        const decimal min = .000000000001m;
        return first > max / second ? max : Math.Clamp(first * second, min, max);
    }

    private static void ValidateEventLifecycles(MarketState state)
    {
        if (state.EventLifecycles == null || state.EventRetentions == null || state.CompletedEventLifecycles == null || state.Companies.Values.Any(c => c.PersistentEventFactor <= 0 || c.PersistentEventFactor > 1000000000000m) || state.EventRetentions.Values.Any(r => r <= 0 || r >= 1)) throw new ArgumentException("事件因子或随机保留记录无效");
        foreach (var pair in state.EventLifecycles)
        {
            var life = pair.Value;
            if (life == null || string.IsNullOrWhiteSpace(life.EventId) || string.IsNullOrWhiteSpace(life.OccurrenceKey) || pair.Key != EventKey(life.EventId, life.OccurrenceKey) || life.FirstObservedDay < 0 || life.LastAdvanceDay < life.FirstObservedDay || life.ActiveConfirmedThroughDay < -1 || life.RetentionRatio <= 0 || life.RetentionRatio >= 1 || !state.EventRetentions.TryGetValue(pair.Key, out var retained) || retained != life.RetentionRatio || life.Companies == null || life.Companies.Count == 0 || state.CompletedEventLifecycles.Contains(pair.Key)) throw new ArgumentException("事件生命周期存档无效");
            if (life.Companies.Any(p => !state.Companies.ContainsKey(p.Key) || p.Value == null || p.Value.CurrentFactor < .000000000001m || p.Value.CurrentFactor > 1000000000000m || p.Value.RetainedTargetFactor < .000000000001m || p.Value.RetainedTargetFactor > 1000000000000m || Math.Abs(p.Value.ReferenceInitialImpact) > .5m || p.Value.ContinuationUsed < 0 || p.Value.RecoveryCarry < 0)) throw new ArgumentException("事件公司影响存档无效");
        }
    }
}
