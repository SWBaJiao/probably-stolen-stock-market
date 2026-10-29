using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ProbablyStolenStockMarket.Market;

public enum LoanKind { Unsecured, Secured }
public enum LoanSource { Regular, StartingDebt }

public sealed class LoanServiceDay
{
    public int TotalDue { get; set; }
    public bool PaymentFailed { get; set; }
}

public sealed class LoanState
{
    public int SchemaVersion { get; set; } = 1;
    public bool ContactUnlocked { get; set; }
    public bool HasMetLender { get; set; }
    public int FirstFailureDay { get; set; } = -1;
    public int VisitDay { get; set; } = -1;
    public bool CardDelivered { get; set; }
    public int CardItemId { get; set; } = -1;
    public bool CardUncertain { get; set; }
    public int LastSettledDay { get; set; } = -1;
    public Dictionary<int, LoanServiceDay> ServiceDays { get; set; } = new();
    public LoanContract? Contract { get; set; }
    public LoanContract? LastClosedContract { get; set; }
    public bool EndingPending { get; set; }
    public bool EndingExecuted { get; set; }
    public bool CreditReduced { get; set; }
    public int CooldownUntilDay { get; set; } = -1;
    public int LastCollectionDay { get; set; } = -1;
    public int LastReminderDay { get; set; } = -1;
    public int LastClosedDay { get; set; } = -1;
    public int CompletedLoans { get; set; }
    public int DefaultCount { get; set; }
    public int StartingDebtStartDay { get; set; } = -1;
    public bool StartingDebtCompleted { get; set; }
    public bool StartingDebtCompletionNotified { get; set; }
}

public sealed class LoanContract
{
    public string Id { get; set; } = "";
    public string BorrowerName { get; set; } = LoanContractDocument.DefaultBorrowerName;
    public int ClosedDay { get; set; } = -1;
    public LoanKind Kind { get; set; }
    public LoanSource Source { get; set; }
    public int OriginalPrincipal { get; set; }
    public decimal RemainingPrincipal { get; set; }
    public decimal AccruedInterest { get; set; }
    public decimal AccruedPenalty { get; set; }
    public decimal TotalPenaltyCharged { get; set; }
    public decimal DailyRate { get; set; }
    public decimal PenaltyDailyRate { get; set; }
    public decimal PenaltyCapRate { get; set; }
    public int GraceDays { get; set; }
    public int CooldownDays { get; set; }
    public int StartDay { get; set; }
    public int DueDay { get; set; }
    public int EndingDay { get; set; }
    public int LastInterestDay { get; set; }
    public int LastAutoRepayDay { get; set; } = -1;
    public bool CollateralDisposed { get; set; }
    public bool HasDefaulted { get; set; }
    public bool WasCreditReducedLoan { get; set; }
    public decimal LiquidationFeeRate { get; set; }
    public int LiquidationMinimumFee { get; set; }
    public Dictionary<string, int> Collateral { get; set; } = new();
    [JsonIgnore] public decimal TotalDebt => RemainingPrincipal + AccruedInterest + AccruedPenalty;
    [JsonIgnore] public int AmountToClose => checked((int)decimal.Ceiling(TotalDebt));
}

public sealed class LoanQuote
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public LoanKind Kind { get; set; }
    public int Principal { get; set; }
    public int DurationDays { get; set; }
    public int Day { get; set; }
    public decimal DailyRate { get; set; }
    public int MaximumPrincipal { get; set; }
    public decimal ServiceBaseline { get; set; }
    public decimal CollateralValue { get; set; }
    public int DueDay { get; set; }
    public int EndingDay { get; set; }
    public decimal ExpectedInterest { get; set; }
    public int ExpectedRepayment { get; set; }
    public Dictionary<string, int> Collateral { get; set; } = new();
}
