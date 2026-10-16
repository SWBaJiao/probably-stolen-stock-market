using System;
using System.Collections.Generic;
using System.Linq;

namespace MelonModTemplate.Market;

public sealed partial class MarketEngine
{
    internal void ReconcileEventDirection(EventRule rule) => ReconcileEventDirection(State, rule);

    private static void ReconcileEventDirections(MarketState state, MarketConfig config)
    {
        foreach (var rule in config.Events) ReconcileEventDirection(state, rule);
    }

    private static void ReconcileEventDirection(MarketState state, EventRule rule)
    {
        if (rule.DirectionVersion != EventImpactResolver.CurrentDirectionVersion ||
            rule.ImpactSource != EventImpactResolver.DerivedPriceSource && rule.ImpactSource != EventImpactResolver.BuiltinPriceSource) return;
        foreach (var life in state.EventLifecycles.Values.Where(l => l.EventId == rule.EventId && l.DirectionVersion < rule.DirectionVersion))
        {
            // 尚未首冲只纠正未来方向；已经实际发生的预热、随机计划及预算均保留。
            if (!life.ShockApplied)
            {
                foreach (var effect in life.Companies) effect.Value.ReferenceInitialImpact = rule.Impacts.GetValueOrDefault(effect.Key);
                foreach (var impact in rule.Impacts.Where(p => p.Value != 0 && !life.Companies.ContainsKey(p.Key) && state.Companies.ContainsKey(p.Key)))
                    life.Companies[impact.Key] = new EventCompanyEffect { ReferenceInitialImpact = impact.Value };
            }
            // 已首冲的整个真实历史快照继续原有生命周期，不回放或反向撤销。
            life.ImpactSource = rule.ImpactSource;
            life.DirectionVersion = rule.DirectionVersion;
        }
        foreach (var offer in state.Rumors.Values.Where(r => r.EventId == rule.EventId && r.DirectionVersion < rule.DirectionVersion))
        {
            var conflict = !offer.IsBrokerOffer || offer.CompanyDirections.Count == 0 || offer.CompanyDirections.Any(p =>
                !rule.Impacts.TryGetValue(p.Key, out var impact) || impact == 0 || p.Value != Math.Sign(impact) * (offer.PredictsOccurrence ? 1 : -1));
            if (conflict && offer.Resolution == RumorResolution.Pending)
            {
                // 升级规则不能变成售卖者的错误记录；已购正文与已验证的历史均不重写。
                offer.Resolution = RumorResolution.Unverified;
                if (!offer.Purchased) offer.Expired = true;
            }
            offer.ImpactSource = rule.ImpactSource;
            offer.DirectionVersion = rule.DirectionVersion;
        }
    }
}
