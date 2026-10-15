using System;
using System.Collections.Generic;
using System.Linq;

namespace MelonModTemplate.Market;

public sealed class CommodityPriceModifier
{
    public string ItemType { get; set; } = "";
    public int Percent { get; set; }
}

// 已配置事件优先；其余仅使用游戏真实提供的商品价格修正，不根据事件名字猜测。
public static class EventImpactResolver
{
    public static EventRule? EnsureRule(MarketEngine engine, string eventId,
        IEnumerable<CommodityPriceModifier> modifiers)
    {
        var configured = engine.Config.Events.FirstOrDefault(e => e.EventId == eventId);
        if (configured != null) return configured;
        if (!engine.Config.EnableStructuredEventFallback || string.IsNullOrWhiteSpace(eventId)) return null;

        var impacts = new Dictionary<string, decimal>();
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in modifiers.Where(m => !string.IsNullOrWhiteSpace(m.ItemType))
                     .GroupBy(m => m.ItemType, StringComparer.OrdinalIgnoreCase))
        {
            var percent = Math.Clamp(group.Sum(m => (decimal)m.Percent), -100m, 100m) / 100m;
            if (percent == 0) continue;
            var weights = engine.GetWeights(new[] { group.Key });
            if (weights.Count == 0) continue;
            types.Add(group.Key);
            foreach (var weight in weights)
                impacts[weight.Key] = impacts.GetValueOrDefault(weight.Key)
                    + percent * weight.Value * engine.Config.StructuredEventScale;
        }

        var limit = engine.Config.StructuredEventLimit;
        impacts = impacts.Select(p => new KeyValuePair<string, decimal>(p.Key,
                Math.Clamp(p.Value, -limit, limit)))
            .Where(p => p.Value != 0).ToDictionary(p => p.Key, p => p.Value);
        if (impacts.Count == 0) return null;
        var sectors = engine.Config.Sectors.Where(s => s.ItemTypes.Any(types.Contains)).ToArray();
        var rule = new EventRule
        {
            EventId = eventId,
            Description = string.Join("、", sectors.Select(s => s.Name)) + "供需变化",
            Sectors = sectors.Select(s => s.Id).ToList(),
            Impacts = impacts
        };
        engine.Config.Events.Add(rule);
        return rule;
    }
}
