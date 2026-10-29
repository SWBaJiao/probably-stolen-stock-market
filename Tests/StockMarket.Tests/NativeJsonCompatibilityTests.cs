using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProbablyStolenStockMarket.Market;

static class NativeJsonCompatibilityTests
{
    public static void Run(Action<bool, string> check)
    {
        // 用旧运行时的序列化器生成输入，避免只验证新序列化器自身的往返。
        var legacyOptions = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var config = TestConfig.Create();
        var legacyConfig = System.Text.Json.JsonSerializer.Serialize(config, legacyOptions);
        var restoredConfig = MarketConfig.Deserialize(legacyConfig);
        check(restoredConfig.Companies.Count == config.Companies.Count
            && restoredConfig.StockEventTemplates.Count == config.StockEventTemplates.Count
            && restoredConfig.Loans.UnsecuredDailyRates.SequenceEqual(config.Loans.UnsecuredDailyRates),
            "原生序列化迁移读取旧配置时不重复追加默认公司、事件和利率数组");

        var engine = new MarketEngine(config);
        engine.ApplyStorageAccess(false);
        engine.AdvanceDay(0);
        engine.RecordServiceSettlement(0, 100, true);
        engine.AdvanceDay(1);
        engine.MarkLoanContactMet();
        engine.AdvanceDay(2);
        var wallet = new Wallet();
        var quote = engine.QuoteLoan(LoanKind.Unsecured, 100, 7);
        check(engine.TakeLoan(quote, wallet, () => { }).Success, "序列化兼容场景创建真实有效贷款合同");
        engine.State.Loan.Contract!.RemainingPrincipal = 42.123456789012345678901234567m;
        engine.State.Loan.Contract.AccruedInterest = .1234567890123456789012345678m;
        engine.State.RandomState = ulong.MaxValue;
        engine.State.SaveIdentity = "2026-10-29T00:00:00Z";
        var legacyState = System.Text.Json.JsonSerializer.Serialize(engine.State, legacyOptions);
        var state = MarketEngine.Deserialize(legacyState, restoredConfig);
        check(state.RandomState == ulong.MaxValue
            && state.Loan.Contract!.RemainingPrincipal == engine.State.Loan.Contract.RemainingPrincipal
            && state.Loan.Contract.AccruedInterest == engine.State.Loan.Contract.AccruedInterest
            && state.Loan.Contract.Kind == LoanKind.Unsecured
            && state.SaveIdentity == engine.State.SaveIdentity,
            "旧 JSON 的完整无符号随机种子、贷款小数精度、字符串枚举及日期形标识原样读取");
        var nativeJson = new MarketEngine(restoredConfig, state).Serialize();
        var loan = JObject.Parse(nativeJson)["Loan"]!["Contract"]!;
        check((string?)loan["Kind"] == "Unsecured" && loan["TotalDebt"] == null && loan["AmountToClose"] == null,
            "原生存档继续使用旧枚举名称并排除计算贷款属性");
        var nativeReload = MarketEngine.Deserialize(nativeJson, restoredConfig);
        check(nativeReload.Loan.Contract!.RemainingPrincipal == state.Loan.Contract!.RemainingPrincipal
            && nativeReload.Loan.ServiceDays[0].TotalDue == 100,
            "原生 JSON 再次读档保持贷款精度和整数键日结字典");

        var invalid = JObject.Parse(nativeJson);
        invalid["Loan"]!["Contract"]!["RemainingPrincipal"] = -1;
        var rejected = false;
        try { MarketEngine.Deserialize(invalid.ToString(), restoredConfig); }
        catch (ArgumentException) { rejected = true; }
        check(rejected, "更换序列化器后仍拒绝损坏的负数贷款本金");
    }

    sealed class Wallet : IWallet
    {
        public int Cash { get; private set; } = 1000;
        public void ChangeCash(int delta) => Cash = checked(Cash + delta);
    }
}
