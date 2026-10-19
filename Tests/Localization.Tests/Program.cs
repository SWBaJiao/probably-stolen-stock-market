using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProbablyStolenStockMarket.Localization;
using ProbablyStolenStockMarket.Game;

int count = 0;
void Check(bool passed, string message)
{
    if (!passed) throw new Exception(message);
    count++;
}
foreach (var (code, expected) in new (string?, ModLanguage)[]
{
    ("zh-Hans", ModLanguage.Chinese), ("zh-Hant", ModLanguage.Chinese), ("ZH_cn", ModLanguage.Chinese),
    ("ja-JP", ModLanguage.Japanese), ("ko-KR", ModLanguage.Korean), ("en-GB", ModLanguage.English),
    ("fr", ModLanguage.English), (null, ModLanguage.English), ("", ModLanguage.English)
}) Check(Localizer.ResolveLanguage(code) == expected, "游戏语言代码映射错误：" + code);

var samples = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var part in new[] { "market", "game", "ui" })
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProbablyStolenStockMarket.Localization." + part + ".json");
    Check(stream != null, "缺少内置词库：" + part);
    using var reader = new StreamReader(stream!);
    var catalog = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(reader.ReadToEnd())!;
    Check(catalog.Count > 100, "词库条目异常：" + part);
    foreach (var item in catalog)
    {
        foreach (var language in new[] { "en", "ja", "ko" })
            Check(item.Value.TryGetValue(language, out var text) && !string.IsNullOrWhiteSpace(text), "翻译缺失：" + part + "/" + item.Key + "/" + language);
        Check(!Regex.IsMatch(item.Value["en"], "[\u3400-\u9fff]"), "英文条目残留中文：" + item.Key);
        samples.TryAdd(item.Key, item.Value["en"]);
    }
}

// 对每条完整原文测试最长匹配，防止短词抢先切碎公司故事或消息正文。
Localizer.Language = ModLanguage.English;
foreach (var item in samples)
    Check(Localizer.Text(item.Key) == item.Value, "最长匹配不正确：" + item.Key);

string original = "第 42 日 · 丰禾供给\n买入 10 股；可用资金 500\n消息来源：闻舟\n记录状态：未核实";
string serialized = JsonSerializer.Serialize(new { Content = original, EventId = "stock_event_001", Day = 42, Cash = 500 });
foreach (var language in new[] { ModLanguage.English, ModLanguage.Japanese, ModLanguage.Korean, ModLanguage.Chinese, ModLanguage.English })
{
    Localizer.Language = language;
    var translated = Localizer.Text(original);
    Check(translated.Contains("42") && translated.Contains("500") && translated.Contains("10"), "切换语言修改数值");
    Check(Localizer.Text("probably_stolen.stock_market.v1|stock_event_001") == "probably_stolen.stock_market.v1|stock_event_001", "稳定标识被翻译");
    Check(Localizer.Text("Custom user-authored event") == "Custom user-authored event", "自定义文案被改写");
    Check(serialized == JsonSerializer.Serialize(new { Content = original, EventId = "stock_event_001", Day = 42, Cash = 500 }), "显示语言污染保存原文");
    Check(language == ModLanguage.Chinese ? translated == original : translated != original, "语言切换未刷新显示");
}
var english = Localizer.Text("丰禾供给");
Check(Localizer.Text("第42日") == "Day 42", "英文日期单位重复");
Check(Localizer.Text("第42～44日") == "Day 42～44", "英文日期范围不正确");
var revision = Localizer.Revision;
Localizer.Language = ModLanguage.English;
Check(Localizer.Revision == revision, "相同语言无须重建窗口");
Localizer.Language = ModLanguage.Korean;
Check(Localizer.Revision == revision + 1 && Localizer.Text("丰禾供给") != english, "语言切换后缓存未清理");
Localizer.Language = (ModLanguage)99;
Check(Localizer.Language == ModLanguage.English, "未知语言未回退英语");
Check(Localizer.Text("<b>丰禾供给</b> #e3c46a") == "<b>Harvest Supply</b> #e3c46a", "格式标记被破坏");
foreach (var (language, prefix) in new[]
{
    (ModLanguage.Chinese, "档案号："), (ModLanguage.English, "Archive no.: "),
    (ModLanguage.Japanese, "記録番号："), (ModLanguage.Korean, "기록 번호: ")
})
{
    Localizer.Language = language;
    Check(Localizer.Text("\n档案号：A17B29C30D44") == "\n" + prefix + "A17B29C30D44", "跨语言纸条标记发生改变");
    var marker = Localizer.Text(IntelPaperMarker.Canonical("stock-offer-42"));
    Check(IntelPaperMarker.Matches("Wen Zhou" + marker, "stock-offer-42"), "无法识别已购纸条的当前语言标记");
    Check(!IntelPaperMarker.Matches(marker, "different-offer"), "错误报价哈希被认领");
    Check(!IntelPaperMarker.Matches(marker + "X", "stock-offer-42"), "带多余后缀的纸条被认领");
}
Check(!IntelPaperMarker.Matches(null, "stock-offer-42"), "空签名被认领");
Check(!IntelPaperMarker.Matches(IntelPaperMarker.Canonical("stock-offer-42"), ""), "空报价被认领");
Check(!IntelPaperMarker.Matches(IntelPaperMarker.Canonical("stock-offer-42").Split('：')[1], "stock-offer-42"), "裸哈希被认领");
Console.WriteLine($"本地化验证通过：{count} 项，{samples.Count} 个不同原文片段。");
