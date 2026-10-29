using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ProbablyStolenStockMarket.Market;

// 合同条款只读取签约快照，动态欠款另列，避免改配置或还款后改写原有约定。
public static class LoanContractDocument
{
    public const string DefaultBorrowerName = "店主";

    public static string NormalizeBorrowerName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultBorrowerName;
        var cleaned = new StringBuilder();
        bool space = false;
        foreach (char ch in value!)
        {
            if (char.IsWhiteSpace(ch)) { space = cleaned.Length > 0; continue; }
            if (char.IsControl(ch) || char.GetUnicodeCategory(ch) == UnicodeCategory.Format || ch == '<' || ch == '>') continue;
            if (space) { cleaned.Append(' '); space = false; }
            cleaned.Append(ch);
        }
        var elements = StringInfo.GetTextElementEnumerator(cleaned.ToString());
        var result = new StringBuilder();
        for (int count = 0; count < 40 && elements.MoveNext(); count++) result.Append(elements.GetTextElement());
        return result.Length == 0 ? DefaultBorrowerName : result.ToString();
    }

    public static string Number(LoanContract contract) => "SH-" + contract.Id.ToUpperInvariant();

    public static string Terms(LoanContract contract, Func<string, string>? translate = null, Func<string, string>? companyName = null)
    {
        string T(string source) => translate?.Invoke(source) ?? source;
        string F(string source, params object[] values) => string.Format(CultureInfo.InvariantCulture, T(source), values);
        var text = new StringBuilder();
        text.AppendLine(T("出借人：沈衡 · 周转贷款专员"));
        text.AppendLine(F("合同编号：{0}", Number(contract)));
        var borrower = NormalizeBorrowerName(contract.BorrowerName);
        text.AppendLine(F("借款人：{0}", borrower == DefaultBorrowerName ? T(DefaultBorrowerName) : borrower));
        if (contract.Source == LoanSource.StartingDebt)
        {
            text.AppendLine(T("一、旧债确认"));
            text.AppendLine(F("本凭证确认既有欠款 {0} 游戏币，不再次发放本金。", contract.OriginalPrincipal));
            text.AppendLine(F("自第 {0} 日起，共 {1} 个营业日可还款；最后还款日为第 {2} 日营业结束。", contract.StartDay, contract.DueDay - contract.StartDay + 1, contract.DueDay));
            text.AppendLine(T("二、清偿约定"));
            text.AppendLine(T("零利息、零罚息，无宽限期。可随时部分还款或提前结清，部分还款不延长期限。"));
            text.AppendLine(T("最后营业日经营费用结算后，现金足够时自动结清；仍有欠款则触发“抵债停业”结局。"));
        }
        else
        {
            text.AppendLine(T("一、放款与期限"));
            text.AppendLine(T(contract.Kind == LoanKind.Secured ? "贷款类型：股票抵押贷款" : "贷款类型：无抵押贷款"));
            text.AppendLine(F("签约并放款：第 {0} 日；到账本金：{1} 游戏币。", contract.StartDay, contract.OriginalPrincipal));
            text.AppendLine(F("贷款期限：{0} 天；到期日：第 {1} 日；最后还款日：第 {2} 日。", contract.DueDay - contract.StartDay, contract.DueDay, contract.EndingDay - 1));
            text.AppendLine(T("二、利息与还款"));
            text.AppendLine(F("签约日利率：{0}。放款当天不计息，之后按剩余本金逐日计算单利，直至结清。", Percent(contract.DailyRate)));
            text.AppendLine(F("按原本金持有至到期，预计利息 {0}，到期预计应还 {1} 游戏币。", Money(contract.OriginalPrincipal * contract.DailyRate * (contract.DueDay - contract.StartDay)), decimal.Ceiling(contract.OriginalPrincipal * (1 + contract.DailyRate * (contract.DueDay - contract.StartDay)))));
            text.AppendLine(T("提前还款仅按实际计息天数收息，无提前还款手续费。还款先抵扣罚息，再抵扣利息和本金；部分还款不延长期限。"));
            text.AppendLine(T("到期后的次日，经营费用结算后现金足够时尝试自动结清；现金不足仍须主动还款。"));
            text.AppendLine(T("三、逾期约定"));
            text.AppendLine(F("到期后宽限 {0} 天；第 {1} 天起每日按剩余本金的 {2} 收取罚息，累计不超过原始本金的 {3}。罚息不计复利。", contract.GraceDays, contract.GraceDays + 1, Percent(contract.PenaltyDailyRate), Percent(contract.PenaltyCapRate)));
            text.AppendLine(F("第 {0} 日开始时仍有欠款，将触发“抵债停业”结局。", contract.EndingDay));
            if (contract.Kind == LoanKind.Secured)
            {
                text.AppendLine(T("四、抵押股份"));
                foreach (var pair in contract.Collateral.OrderBy(p => p.Key, StringComparer.Ordinal))
                    text.AppendLine(F("{0}：{1} 股。", companyName?.Invoke(pair.Key) ?? pair.Key, pair.Value));
                text.AppendLine(T("抵押股份在全部结清前锁定，不能卖出；宽限期结束后按处置时市价出售偿债。"));
                text.AppendLine(F("每笔处置手续费为成交额的 {0}，最低 {1} 游戏币；多余资金返还，不足部分继续欠款，原期限不变。", Percent(contract.LiquidationFeeRate), contract.LiquidationMinimumFee));
            }
            text.AppendLine(T("同一时间只持有一笔贷款，不接受展期或追加本金。"));
            text.AppendLine(F("结清后等待 {0} 天，可重新申请贷款。", contract.CooldownDays));
        }
        text.Append(T("本凭证随存档保留。签约条款以本凭证为准，当前欠款另见账单。"));
        return text.ToString();
    }

    public static string Account(LoanContract contract, int currentDay, Func<string, string>? translate = null)
    {
        string T(string source) => translate?.Invoke(source) ?? source;
        string F(string source, params object[] values) => string.Format(CultureInfo.InvariantCulture, T(source), values);
        if (contract.ClosedDay >= 0)
            return F("已于第 {0} 日结清，当前欠款为 0 游戏币。", contract.ClosedDay);
        var text = new StringBuilder();
        text.AppendLine(F("第 {0} 日账单 · 当前结清金额 {1} 游戏币。", currentDay, contract.AmountToClose));
        text.AppendLine(F("剩余本金 {0}；累计利息 {1}；累计罚息 {2}。", Money(contract.RemainingPrincipal), Money(contract.AccruedInterest), Money(contract.AccruedPenalty)));
        if (contract.Kind == LoanKind.Secured) text.AppendLine(T(contract.CollateralDisposed ? "抵押股份已处置。" : "抵押股份仍处于锁定状态。"));
        if (currentDay > contract.DueDay && contract.Source != LoanSource.StartingDebt) text.AppendLine(F("已逾期 {0} 天，部分还款不延长期限。", currentDay - contract.DueDay));
        text.Append(T("查询或还款请拨打 731207。"));
        return text.ToString();
    }

    private static string Money(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Percent(decimal value) => value.ToString("0.##%", CultureInfo.InvariantCulture);
}
