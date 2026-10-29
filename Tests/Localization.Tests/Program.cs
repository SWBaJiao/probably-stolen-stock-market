using System.Reflection;
using Newtonsoft.Json;
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
foreach (var part in new[] { "market", "game", "ui", "loans" })
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ProbablyStolenStockMarket.Localization." + part + ".json");
    Check(stream != null, "缺少内置词库：" + part);
    using var reader = new StreamReader(stream!);
    var catalog = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(reader.ReadToEnd())!;
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
string serialized = JsonConvert.SerializeObject(new { Content = original, EventId = "stock_event_001", Day = 42, Cash = 500 });
foreach (var language in new[] { ModLanguage.English, ModLanguage.Japanese, ModLanguage.Korean, ModLanguage.Chinese, ModLanguage.English })
{
    Localizer.Language = language;
    var translated = Localizer.Text(original);
    Check(translated.Contains("42") && translated.Contains("500") && translated.Contains("10"), "切换语言修改数值");
    Check(Localizer.Text("probably_stolen.stock_market.v1|stock_event_001") == "probably_stolen.stock_market.v1|stock_event_001", "稳定标识被翻译");
    Check(Localizer.Text("Custom user-authored event") == "Custom user-authored event", "自定义文案被改写");
    Check(serialized == JsonConvert.SerializeObject(new { Content = original, EventId = "stock_event_001", Day = 42, Cash = 500 }), "显示语言污染保存原文");
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

// 动态合同、催收和账单必须保留数字与稳定标记，英文显示不得残留中文片段。
var loanSamples = new[]
{
    "负债店主 · 731207",
    "第 1 日 · 可用现金 1500 · 负债店主",
    "负债店主 · 开局债务\n原始欠款 5000 · 剩余欠款 4100\n最后还款日：第 28 日营业结束\n距最后还款日还有 14 天（含今天仍可继续营业）。\n零利息、零罚息，无宽限期。",
    "负债店主 · 开局债务\n原始欠款 5000 · 剩余欠款 1\n最后还款日：第 28 日营业结束\n今天营业结束后必须结清。",
    "负债店主：炒股亏损后，你只剩 1500 元现金，并背上了固定旧债 5000 元。你有完整 28 个游戏日还清欠款，零利息、零罚息，无宽限期。\n\n距最后还款日还有 27 天（含今天仍可继续营业）。\n\n剩余欠款 5000，最后还款日：第 28 日营业结束。\n可随时部分还款或提前结清，部分还款不延长期限。\n电话 731207 可查询或还款。",
    "负债店主 · 还款提醒\n\n今天是最后还款日。经营费用结算后，现金足够时自动结清；否则进入“抵债停业”结局。\n\n剩余欠款 3500，最后还款日：第 28 日营业结束。\n可随时部分还款或提前结清，部分还款不延长期限。\n电话 731207 可查询或还款。",
    "你曾想靠炒股赚一笔，却在行情暴跌中亏掉积蓄，还背上了 5000 元债务。如今手头只剩 1500 元，你决定重新经营店铺，在 28 个营业日内还清欠款。跳过部分基础教程，直接开始经营。",
    "初始现金 1500 元\n固定旧债 5000 元，无利息或罚息\n28 个营业日内还清，无逾期宽限\n跳过部分基础教程\n挑战开局",
    "到期日结先支付普通服务费用，再用现金自动结清剩余旧债；不足则抵债停业。",
    "今天是旧债的最后还款日。结束营业后仍可在夜间还款；日结先扣普通服务费用，再自动偿还剩余旧债，现金不足将抵债停业。\n\n剩余旧债 3500 · 可用现金 4000\n继续结束营业\n返回经营",
    "旧债已清，负债店主挑战完成，可以继续正常经营。\n继续经营",
    "周转贷款 · 731207",
    "第 42 日 · 可用现金 500 · 周转贷款",
    "丰禾供给\n持有 10 / 可用 6 / 抵押估值 98.75",
    "原始本金 500 · 剩余本金 410.25\n累计利息 157.5 · 累计罚息 2.05\n日利率 2.25% · 当前结清金额 570\n放款日 42 · 到期日 56 · 最终清算日 64",
    "已经逾期 3 天，部分还款不会延长清算期限。\n丰禾供给：4 股（已锁定）\n清泉循环：2 股（已处置）",
    "无抵押贷款报价\n服务费用基准 120 · 最高可借 500\n到账本金 500 · 期限 14 天 · 日利率 2.25%\n预计利息 157.5 · 到期应还 658\n到期日 56 · 最终清算日 64\n贷款报价有效。",
    "抵押贷款报价\n所选股票估值 1600，抵押率最高 50%。\n到账本金 800 · 期限 28 天 · 日利率 1.75%\n预计利息 392 · 到期应还 1192",
    "贷款业务将在第 45 日恢复。\n贷款期限须为 1～28 个游戏日。\n本次可借本金为 1～500 游戏币。",
    "贷款已到账 500，第 56 日到期。\n还款成功，剩余欠款 410。",
    "目前全部结清需要 570 游戏币。最后还款日是第 63 日。",
    "每日服务付款失败，贷款专员将在第 43 日起到访。",
    "所有可开通付费服务均超过余额，贷款专员将在第 43 日起到访。",
    "办理无抵押贷款，本金 500，期限 14 日，日利率 2.25%，第 56 日到期。",
    "办理抵押贷款，本金 800，期限 28 日，日利率 1.75%，第 70 日到期。",
    "主动偿还贷款 100。\n每日服务扣费后自动结清贷款 570。",
    "抵押股票已处置，所得 700，用于偿债 570，返还 130。\n抵押处置 7 股，净所得 700。",
    "贷款已结清，第 45 日起可以再次申请。",
    "沈衡 · 贷款专员\nPS_LOAN_CARD_V1:A17B29C30D44E55F",
    "贷款将在明天到期，请准备还款。\n\n当前结清金额 885，到期日 70，最后还款日 77。\n电话 731207 可查询或还款。",
    "今天是最后还款日，明天仍有欠款将触发抵债停业结局。\n\n当前结清金额 920，到期日 70，最后还款日 77。\n电话 731207 可查询或还款。",
    "贷款已经逾期，这是最后通牒，请在最终期限前结清。\n\n当前结清金额 900，到期日 70，最后还款日 77。\n电话 731207 可查询或还款。",
    "罚息：每日剩余本金的 0.5%，累计不超过原始本金的 10%，罚息不计复利。\n抵押清算：每笔股票处置按成交额的 0.5% 收取手续费，最低 1 游戏币。多余资金返还，不足部分继续欠款，原清算期限不变。",
    "罚息：每日剩余本金的 0.75%，累计不超过原始本金的 12.5%，罚息不计复利。\n抵押清算：每笔股票处置按成交额的 1.25% 收取手续费，最低 3 游戏币。多余资金返还，不足部分继续欠款，原清算期限不变。"
};
foreach (var language in new[] { ModLanguage.English, ModLanguage.Japanese, ModLanguage.Korean, ModLanguage.Chinese })
{
    Localizer.Language = language;
    foreach (var source in loanSamples)
    {
        var translated = Localizer.Text(source);
        Check(language == ModLanguage.Chinese ? translated == source : translated != source, "贷款文案未切换语言：" + source);
        Check(Regex.Matches(source, @"[0-9]+(?:\.[0-9]+)?").Select(m => m.Value)
            .SequenceEqual(Regex.Matches(translated, @"[0-9]+(?:\.[0-9]+)?").Select(m => m.Value)), "贷款翻译修改数字：" + source);
        if (language == ModLanguage.English) Check(!Regex.IsMatch(translated, "[\u3400-\u9fff]"), "贷款英文残留中文：" + translated);
    }
    var marker = "PS_LOAN_CARD_V1:A17B29C30D44E55F";
    Check(Localizer.Text("沈衡 · 贷款专员\n" + marker).EndsWith(marker, StringComparison.Ordinal), "贷款名片稳定标记被改写");
    Check(Localizer.Text("probably_stolen_stock.loan_agent|probably_stolen_loan_default|731207")
        == "probably_stolen_stock.loan_agent|probably_stolen_loan_default|731207", "贷款业务标识或电话被翻译");
}
// 名片文字跟随语言，电话号码和存档认领标记不得被翻译或改写。
var intelCardBody = "预约电话：731206\n\n持有本存档的股市面板后，可在电话簿选择“行情消息专线”，或手动拨打 731206。确认预约后，闻舟或剑清酱会在次日营业时随机来访，每次只安排一人。\n\n情报需要付费购买，消息可能属实，也可能失实。名片仅提供联系方式，不替代股市面板，也不会直接创建预约。";
foreach (var language in new[] { ModLanguage.Chinese, ModLanguage.English, ModLanguage.Japanese, ModLanguage.Korean })
{
    Localizer.Language = language;
    var text = Localizer.Text(intelCardBody);
    Check(language == ModLanguage.Chinese ? text == intelCardBody : text != intelCardBody, "情报名片正文未切换语言");
    Check(Regex.Matches(text, "731206").Count == 2, "情报名片电话被改变");
    Check(Localizer.Text("行情消息专线\nPS_INTEL_CARD_V1:A17B29C30D44E55F").EndsWith("PS_INTEL_CARD_V1:A17B29C30D44E55F", StringComparison.Ordinal), "情报名片标记被翻译");
    Check(language == ModLanguage.Chinese || Localizer.Text("股市情报名片") != "股市情报名片", "情报名片名称未切换语言");
    if (language == ModLanguage.English) Check(!Regex.IsMatch(text, "[\\u3400-\\u9fff]"), "情报名片英文残留中文");
}
// 开户对话与补领说明必须跟随游戏语言，费用和快捷键保持一致。
var accountSamples = new[]
{
    "林澈 · 证券开户专员",
    "证券开户专员尚未到访，请等到约定日期。",
    "已开通证券账户，无需重复付费。",
    "开户成功，已支付 50 元。",
    "开户需要 50 元，当前余额不足。可以选择三天后再来。",
    "请等待证券开户专员来访，支付 50 元开户后领取股市面板。",
    "你好，我叫林澈，是证券开户专员。今天来介绍证券账户，开户费 50 元，办理后就能领取股市面板。暂时不方便的话，我可以三天后再来。",
    "开户费只收一次，共 50 元。你可以现在办理，也可以让我三天后再来。",
    "开户安排已经处理。祝你经营顺利，投资时记得留足日常周转资金。",
    "好的，我三天后再来。到时仍然只需支付 50 元开户。",
    "开通证券账户需要一次性支付 50 元，开户后会发放股市面板。双击面板即可查看行情和买卖股票。优先放入背包，背包满则放桌面，桌面也满则放窗外。丢失后可按 Ctrl+F8 免费补领，无需再次开户。\n\n现在开户，还是三天后再来？",
    "背包已满，股市面板已放在桌面上。",
    "背包和桌面已满，股市面板已放在窗外。请在保存或结束营业前收好。",
    "物品放置区域尚未就绪，稍后重试。",
    "已发放股市面板；丢失后可按 Ctrl+F8 免费补领。",
    "原股市面板仍存在或正在拖动，请先收好物品后再尝试补领。",
    "上次交付位置尚未确认，请保存并重新载入后再按 Ctrl+F8 补领。",
    "已找到现有股市面板，请检查背包、桌面、窗外及其他容器，未重复发放。",
    "背包、桌面和窗外都已满。开户资格已保留，腾出空间后自动发放，也可按 Ctrl+F8 免费补领。",
    "丢失后可按 Ctrl+F8 免费补领，无需再次开户。",
    "支付 50 元开户\n三天后再来",
    "存档已记录面板，自动发放已停止。确认丢失后可按 Ctrl+F8 显式补领。"
};
foreach (var language in new[] { ModLanguage.Chinese, ModLanguage.English, ModLanguage.Japanese, ModLanguage.Korean })
{
    Localizer.Language = language;
    foreach (var source in accountSamples)
    {
        var text = Localizer.Text(source);
        Check(language == ModLanguage.Chinese ? text == source : text != source, "开户文案未切换语言：" + source);
        Check(Regex.Matches(source, @"[0-9]+(?:\.[0-9]+)?").Select(m => m.Value)
            .SequenceEqual(Regex.Matches(text, @"[0-9]+(?:\.[0-9]+)?").Select(m => m.Value)), "开户翻译修改数字：" + source);
        if (source.Contains("Ctrl+F8")) Check(text.Contains("Ctrl+F8"), "开户补领快捷键被改写");
        if (language == ModLanguage.English) Check(!Regex.IsMatch(text, "[\u3400-\u9fff]"), "开户英文残留中文：" + text);
    }
    Check(Localizer.Text("probably_stolen_stock.account_agent|Ctrl+F8") == "probably_stolen_stock.account_agent|Ctrl+F8", "开户稳定标识被改写");
}
Console.WriteLine($"本地化验证通过：{count} 项，{samples.Count} 个不同原文片段。");
