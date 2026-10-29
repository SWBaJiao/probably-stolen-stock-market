using System;
using System.Linq;

namespace ProbablyStolenStockMarket.Market;

public sealed class LoanConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxDurationDays { get; set; } = 28;
    public int UnsecuredMaximum { get; set; } = 500;
    public int SecuredMaximum { get; set; } = 2000;
    public int UnsecuredBaseAmount { get; set; } = 100;
    public decimal UnsecuredServiceMultiple { get; set; } = 5m;
    public decimal SecuredServiceMultiple { get; set; } = 20m;
    public decimal CollateralRatio { get; set; } = .50m;
    public decimal CreditReducedRatio { get; set; } = .75m;
    public decimal[] UnsecuredDailyRates { get; set; } = new[] { .02m, .0225m, .025m, .0275m };
    public decimal[] SecuredDailyRates { get; set; } = new[] { .01m, .0125m, .015m, .0175m };
    public int GraceDays { get; set; } = 2;
    public int EndingOverdueDays { get; set; } = 8;
    public int CooldownDays { get; set; } = 3;
    public decimal PenaltyDailyRate { get; set; } = .005m;
    public decimal PenaltyCapRate { get; set; } = .10m;

    public void Validate()
    {
        if (MaxDurationDays < 1 || MaxDurationDays > 28 || UnsecuredMaximum < 1 || UnsecuredMaximum > 1000000 || SecuredMaximum < 1 || SecuredMaximum > 1000000 || UnsecuredBaseAmount < 0 || UnsecuredBaseAmount > UnsecuredMaximum
            || UnsecuredServiceMultiple <= 0 || UnsecuredServiceMultiple > 1000 || SecuredServiceMultiple <= 0 || SecuredServiceMultiple > 1000 || CollateralRatio <= 0 || CollateralRatio > 1 || CreditReducedRatio <= 0 || CreditReducedRatio > 1
            || GraceDays < 0 || GraceDays > 7 || EndingOverdueDays <= GraceDays + 1 || EndingOverdueDays > 30 || CooldownDays < 0 || CooldownDays > 30 || PenaltyDailyRate < 0 || PenaltyDailyRate > 1 || PenaltyCapRate < 0 || PenaltyCapRate > 1)
            throw new ArgumentException("贷款额度、期限或逾期配置无效");
        foreach (var rates in new[] { UnsecuredDailyRates, SecuredDailyRates })
            if (rates == null || rates.Length != 4 || rates.Any(r => r <= 0 || r > 1) || !rates.SequenceEqual(rates.OrderBy(r => r))) throw new ArgumentException("贷款日利率须包含四个递增或相同的有效档位");
    }
}

public sealed partial class MarketConfig
{
    public LoanConfig Loans { get; set; } = new();
}
