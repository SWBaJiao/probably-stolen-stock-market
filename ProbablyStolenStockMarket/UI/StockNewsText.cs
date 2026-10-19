using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ProbablyStolenStockMarket.Market;

namespace ProbablyStolenStockMarket.UI;

// 新闻文字仅接收已经公开的投影，不读取排程、配置目标或内部事件状态。
internal static class StockNewsText
{
    internal static string Format(MarketEngine engine, IReadOnlyList<StockNewsView> items, int limit)
    {
        var news = items.OrderByDescending(n => n.PublicDay).Take(limit).ToArray();
        if (news.Length == 0) return "暂无已公开的股市专用新闻。";
        var text = new StringBuilder();
        foreach (var item in news)
        {
            text.AppendLine($"第 {item.PublicDay} 日 · {item.Stage} · {item.Title}");
            text.AppendLine(item.Body);
            foreach (var pair in item.CompanyDirections)
            {
                var company = engine.Config.Companies.FirstOrDefault(c => c.Id == pair.Key)?.Name ?? pair.Key;
                var direction = pair.Value > 0 ? "利好" : pair.Value < 0 ? "利空" : "中性";
                var impact = item.ActualImpacts.GetValueOrDefault(pair.Key);
                var percent = (impact > 0 ? "+" : "") + impact.ToString("0.##%", CultureInfo.InvariantCulture);
                text.AppendLine($"{company}：{direction} · 事件累计影响 {percent}");
            }
            text.AppendLine();
        }
        if (items.Count > limit) text.Append($"显示最近 {limit} 条公开新闻。");
        return text.ToString().TrimEnd();
    }
}
