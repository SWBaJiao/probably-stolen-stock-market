using System;
using System.Collections.Generic;
using System.Linq;

namespace ProbablyStolenStockMarket.Market;

public sealed partial class MarketEngine
{
    public OperationResult InitializeStartingDebt(int firstBusinessDay, IWallet wallet, Action persist, string borrowerName = "")
    {
        var loan = State.Loan;
        if (loan.StartingDebtStartDay >= 0) return OperationResult.Ok("负债店主开局已经初始化。");
        if (firstBusinessDay < 0 || firstBusinessDay != State.LastDay || firstBusinessDay > int.MaxValue - 28)
            return OperationResult.Fail("负债店主的首个营业日与当前存档不一致。");
        if (State.TradingFrozen) return OperationResult.Fail(State.FreezeReason);
        if (loan.Contract != null || loan.FirstFailureDay >= 0 || loan.CompletedLoans != 0 || loan.EndingPending || loan.EndingExecuted)
            return OperationResult.Fail("已有贷款记录的存档不能重新初始化开局债务。");
        // 这是开局前已经形成的固定旧债，初始化只记录合同，不再次放款。
        return Transact(wallet, 0, persist, () =>
        {
            loan.StartingDebtStartDay = firstBusinessDay;
            loan.ContactUnlocked = true; loan.HasMetLender = true;
            loan.Contract = new LoanContract
            {
                Id = Guid.NewGuid().ToString("N"), BorrowerName = LoanContractDocument.NormalizeBorrowerName(borrowerName),
                Source = LoanSource.StartingDebt, Kind = LoanKind.Unsecured,
                OriginalPrincipal = 5000, RemainingPrincipal = 5000, StartDay = firstBusinessDay,
                DueDay = firstBusinessDay + 27, EndingDay = firstBusinessDay + 28, LastInterestDay = firstBusinessDay
            };
            loan.LastSettledDay = firstBusinessDay;
            Log("loan.starting", $"负债店主开局：固定旧债 5000，第 {loan.Contract.DueDay} 个游戏日结束时必须结清。");
        });
    }

    // 原版完成最后营业日的服务扣费后调用，保留完整的第二十八个营业日。
    public OperationResult CompleteStartingDebtDay(int completedDay, IWallet wallet, Action persist)
    {
        var loan = State.Loan;
        var contract = loan.Contract;
        if (contract?.Source != LoanSource.StartingDebt || loan.EndingPending || loan.EndingExecuted)
            return OperationResult.Ok("当前没有待结算的开局债务。");
        if (completedDay < contract.StartDay || completedDay > State.LastDay)
            return OperationResult.Fail("开局债务的日结日期与当前存档不一致。");
        if (completedDay < contract.DueDay) return OperationResult.Ok("开局债务尚未到最后还款日。");
        var delta = 0;
        return Transact(wallet, () => delta, persist, () =>
        {
            SettleStartingDebt(contract, wallet, ref delta, completedDay == contract.DueDay && State.LastDay <= contract.EndingDay);
            loan.LastSettledDay = State.LastDay;
        });
    }

    private void SettleStartingDebt(LoanContract contract, IWallet wallet, ref int delta, bool allowPayment)
    {
        contract.LastInterestDay = State.LastDay;
        contract.LastAutoRepayDay = State.LastDay;
        var debit = contract.AmountToClose;
        if (allowPayment && (long)wallet.Cash + delta >= debit)
        {
            delta = checked(delta - debit);
            ApplyLoanPayment(contract, debit);
            Log("loan.auto", $"每日服务扣费后自动结清开局旧债 {debit}。", cashDelta: -debit);
            CloseLoan(State.LastDay);
            return;
        }
        contract.HasDefaulted = true;
        State.Loan.CreditReduced = true;
        State.Loan.DefaultCount++;
        State.Loan.EndingPending = true;
        Log("loan.ending", "负债店主的二十八个营业日已结束，旧债仍未结清，债务清算待执行。");
    }

    public bool RecordServiceSettlement(int day, int totalDue, bool paymentFailed)
    {
        if (!Config.Loans.Enabled || day < 0 || day > State.LastDay || totalDue < 0) return false;
        var loan = State.Loan;
        var changed = false;
        if (!loan.ServiceDays.TryGetValue(day, out var record))
        {
            record = new LoanServiceDay(); loan.ServiceDays[day] = record; changed = true;
        }
        // 同日重复观察只补充应付总额及资金不足事实，不清除已发生的引荐条件。
        if (record.TotalDue < totalDue) { record.TotalDue = totalDue; changed = true; }
        if (paymentFailed && !record.PaymentFailed) { record.PaymentFailed = true; changed = true; }
        if (paymentFailed && loan.FirstFailureDay < 0 && !loan.ContactUnlocked)
        {
            loan.FirstFailureDay = day; loan.VisitDay = checked(day + 1); changed = true;
            Log("loan.contact", $"所有可开通付费服务均超过余额，贷款专员将在第 {loan.VisitDay} 日起到访。");
        }
        foreach (var old in loan.ServiceDays.Keys.OrderByDescending(d => d).Skip(7).ToArray()) loan.ServiceDays.Remove(old);
        return changed;
    }

    public bool MarkLoanContactMet()
    {
        var loan = State.Loan;
        if (loan.StartingDebtStartDay >= 0) return true;
        if (loan.FirstFailureDay < 0 || State.LastDay < loan.VisitDay) return false;
        loan.HasMetLender = true; loan.ContactUnlocked = true;
        return true;
    }

    public decimal GetLoanServiceBaseline() => State.Loan.ServiceDays.OrderByDescending(d => d.Key).Take(3).Select(d => (decimal)d.Value.TotalDue).DefaultIfEmpty(0m).Average();

    public int GetAvailableShares(string companyId)
    {
        if (!State.Positions.TryGetValue(companyId, out var position)) return 0;
        var contract = State.Loan.Contract;
        var locked = contract is { CollateralDisposed: false } ? contract.Collateral.GetValueOrDefault(companyId) : 0;
        return Math.Max(0, position.Quantity - locked);
    }

    public decimal GetCollateralUnitValue(string companyId)
    {
        if (!State.Companies.TryGetValue(companyId, out var company)) return 0m;
        var close = company.Candles.LastOrDefault(c => c.Day < State.LastDay && !c.IsPartial);
        return close == null ? 0m : Math.Min(company.Price, close.Close);
    }

    public LoanQuote QuoteLoan(LoanKind kind, int principal, int durationDays, Dictionary<string, int>? collateral = null)
    {
        var cfg = Config.Loans;
        var quote = new LoanQuote { Kind = kind, Principal = principal, DurationDays = durationDays, Day = State.LastDay, ServiceBaseline = GetLoanServiceBaseline() };
        if (kind != LoanKind.Unsecured && kind != LoanKind.Secured) { quote.Message = "贷款类型无效。"; return quote; }
        if (durationDays >= 1 && durationDays <= cfg.MaxDurationDays && State.LastDay >= 0 && State.LastDay <= int.MaxValue - durationDays - cfg.EndingOverdueDays)
        {
            quote.DailyRate = (kind == LoanKind.Unsecured ? cfg.UnsecuredDailyRates : cfg.SecuredDailyRates)[(durationDays - 1) / 7];
            quote.DueDay = State.LastDay + durationDays;
            quote.EndingDay = quote.DueDay + cfg.EndingOverdueDays;
        }
        var invalidCollateral = false;
        if (kind == LoanKind.Secured && collateral != null)
            foreach (var share in collateral)
            {
                if (share.Value < 0 || !State.Positions.ContainsKey(share.Key)) { invalidCollateral = true; continue; }
                if (share.Value == 0) continue;
                quote.Collateral[share.Key] = share.Value;
                var value = GetCollateralUnitValue(share.Key);
                if (share.Value > GetAvailableShares(share.Key) || value <= 0) invalidCollateral = true;
                else quote.CollateralValue += value * share.Value;
            }
        var unsecuredLimit = Math.Min(cfg.UnsecuredMaximum, quote.ServiceBaseline > 0m ? quote.ServiceBaseline * cfg.UnsecuredServiceMultiple : cfg.UnsecuredBaseAmount);
        if (State.Loan.CreditReduced) unsecuredLimit *= cfg.CreditReducedRatio;
        quote.MaximumPrincipal = (int)decimal.Floor(kind == LoanKind.Unsecured
            ? Math.Min(cfg.UnsecuredMaximum, unsecuredLimit)
            : Math.Min(cfg.SecuredMaximum, Math.Min(quote.ServiceBaseline * cfg.SecuredServiceMultiple, quote.CollateralValue * cfg.CollateralRatio)));
        if (principal > 0 && principal <= 1000000 && quote.DailyRate > 0)
        {
            quote.ExpectedInterest = principal * quote.DailyRate * durationDays;
            quote.ExpectedRepayment = checked((int)decimal.Ceiling(principal + quote.ExpectedInterest));
        }
        if (!cfg.Enabled) quote.Message = "贷款业务已停用。";
        else if (!State.StorageVerified || State.TradingFrozen) quote.Message = State.TradingFrozen ? State.FreezeReason : "存档暂未就绪。";
        else if (State.Loan.EndingPending || State.Loan.EndingExecuted) quote.Message = "债务清算已经启动，无法办理贷款。";
        else if (!State.Loan.ContactUnlocked) quote.Message = "请先接待贷款专员，获得联系方式。";
        else if (State.Loan.Contract != null) quote.Message = "你当前还有一笔贷款未结清，暂时无法办理新的贷款。";
        else if (State.LastDay < State.Loan.CooldownUntilDay) quote.Message = $"贷款业务将在第 {State.Loan.CooldownUntilDay} 日恢复。";
        else if (durationDays < 1 || durationDays > cfg.MaxDurationDays || quote.DailyRate == 0) quote.Message = $"贷款期限须为 1～{cfg.MaxDurationDays} 个游戏日。";
        else if (kind == LoanKind.Secured && (invalidCollateral || quote.Collateral.Count == 0)) quote.Message = "请选择有可靠历史收盘价且数量有效的股票作为抵押物。";
        else if (principal < 1 || principal > quote.MaximumPrincipal) quote.Message = $"本次可借本金为 1～{quote.MaximumPrincipal} 游戏币。";
        else { quote.Success = true; quote.Message = "贷款报价有效。"; }
        return quote;
    }

    public OperationResult TakeLoan(LoanQuote quote, IWallet wallet, Action persist, string borrowerName = "")
    {
        if (quote == null) return OperationResult.Fail("贷款报价为空。");
        var current = QuoteLoan(quote.Kind, quote.Principal, quote.DurationDays, quote.Collateral);
        if (!current.Success) return OperationResult.Fail(current.Message);
        if (!quote.Success || quote.Day != current.Day || quote.DailyRate != current.DailyRate || quote.DueDay != current.DueDay || quote.EndingDay != current.EndingDay
            || quote.MaximumPrincipal != current.MaximumPrincipal || quote.CollateralValue != current.CollateralValue || quote.ServiceBaseline != current.ServiceBaseline || quote.ExpectedRepayment != current.ExpectedRepayment)
            return OperationResult.Fail("贷款报价已变化，请重新确认合同。");
        if ((long)wallet.Cash + current.Principal > int.MaxValue) return OperationResult.Fail("放款将超出游戏资金上限。");
        var result = Transact(wallet, current.Principal, persist, () =>
        {
            var cfg = Config.Loans;
            State.Loan.Contract = new LoanContract
            {
                Id = Guid.NewGuid().ToString("N"), BorrowerName = LoanContractDocument.NormalizeBorrowerName(borrowerName),
                Kind = current.Kind, OriginalPrincipal = current.Principal, RemainingPrincipal = current.Principal,
                DailyRate = current.DailyRate, StartDay = State.LastDay, DueDay = current.DueDay, EndingDay = current.EndingDay, LastInterestDay = State.LastDay,
                PenaltyDailyRate = cfg.PenaltyDailyRate, PenaltyCapRate = cfg.PenaltyCapRate, GraceDays = cfg.GraceDays, CooldownDays = cfg.CooldownDays,
                WasCreditReducedLoan = current.Kind == LoanKind.Unsecured && State.Loan.CreditReduced,
                Collateral = new Dictionary<string, int>(current.Collateral), LiquidationFeeRate = Config.FeeRate, LiquidationMinimumFee = Config.MinimumFee
            };
            State.Loan.LastSettledDay = State.LastDay;
            Log("loan.borrow", $"办理{(current.Kind == LoanKind.Secured ? "抵押" : "无抵押")}贷款，本金 {current.Principal}，期限 {current.DurationDays} 日，日利率 {current.DailyRate:P2}，第 {current.DueDay} 日到期。", cashDelta: current.Principal);
        });
        if (result.Success) result.Message = $"贷款已到账 {current.Principal}，第 {current.DueDay} 日到期。";
        return result;
    }

    public OperationResult RepayLoan(int amount, IWallet wallet, Action persist)
    {
        var contract = State.Loan.Contract;
        if (contract == null) return OperationResult.Fail("当前没有未结清贷款。");
        if (contract.Source != LoanSource.StartingDebt && (!State.StorageVerified || State.TradingFrozen)) return OperationResult.Fail(State.TradingFrozen ? State.FreezeReason : "存档暂未就绪。");
        if (State.Loan.EndingPending || State.Loan.EndingExecuted) return OperationResult.Fail("债务清算已经启动，已超过最后还款期限。");
        if (contract.LastInterestDay != State.LastDay) return OperationResult.Fail("贷款账单正在更新，请稍后再试。");
        if (amount <= 0) return OperationResult.Fail("还款金额须大于零。");
        var debit = Math.Min(amount, contract.AmountToClose);
        if (wallet.Cash < debit) return OperationResult.Fail("余额不足，未扣款，也不因此新增违约记录。");
        var result = Transact(wallet, -debit, persist, () =>
        {
            ApplyLoanPayment(contract, debit);
            Log("loan.repay", $"主动偿还贷款 {debit}。", cashDelta: -debit);
            if (contract.TotalDebt == 0m) CloseLoan(State.LastDay);
        });
        if (result.Success) result.Message = State.Loan.Contract == null
            ? contract.Source == LoanSource.StartingDebt ? "旧债已清，负债店主挑战完成，可以继续正常经营。" : "贷款已全部结清，抵押股份已解除锁定。"
            : $"还款成功，剩余欠款 {State.Loan.Contract.AmountToClose}。";
        return result;
    }

    // 由原版日结完成后的适配入口调用；行情推进本身绝不触碰玩家现金。
    public OperationResult ProcessLoanDay(int day, IWallet wallet, Action persist)
    {
        if (day < 0 || day != State.LastDay) return OperationResult.Fail("贷款结算日期与当前存档不一致。");
        if (day <= State.Loan.LastSettledDay || State.Loan.EndingExecuted || State.Loan.EndingPending) return OperationResult.Ok("今日贷款账单已结算或清算正在执行。");
        // 开局债务的期限与偿还独立于股市交易门控，仍通过资金事务核对及回滚。
        if (State.Loan.Contract?.Source != LoanSource.StartingDebt && (!State.StorageVerified || State.TradingFrozen)) return OperationResult.Fail(State.TradingFrozen ? State.FreezeReason : "存档暂未就绪。");
        var delta = 0;
        return Transact(wallet, () => delta, persist, () =>
        {
            var contract = State.Loan.Contract;
            if (contract != null)
            {
                if (contract.Source == LoanSource.StartingDebt)
                {
                    contract.LastInterestDay = day;
                    // 漏掉夜间完成回调时在次日补结算；跨过期限后不能用后来取得的现金倒补。
                    if (day >= contract.EndingDay) SettleStartingDebt(contract, wallet, ref delta, day == contract.EndingDay);
                    State.Loan.LastSettledDay = day;
                    return;
                }
                if ((long)day - contract.LastInterestDay > 10000) throw new InvalidOperationException("贷款结息跨度异常");
                for (var d = contract.LastInterestDay + 1; d <= day; d++)
                {
                    // 次日开始先尝试偿还昨日到期账单，成功时不额外收取次日利息。
                    if (d == day && d == contract.DueDay + 1 && contract.LastAutoRepayDay < d)
                    {
                        contract.LastAutoRepayDay = d;
                        var debit = contract.AmountToClose;
                        if ((long)wallet.Cash + delta >= debit)
                        {
                            delta = checked(delta - debit);
                            ApplyLoanPayment(contract, debit);
                            Log("loan.auto", $"每日服务扣费后自动结清贷款 {debit}。", cashDelta: -debit);
                            CloseLoan(d); break;
                        }
                    }
                    contract.AccruedInterest += contract.RemainingPrincipal * contract.DailyRate;
                    contract.LastInterestDay = d;
                    if (d > contract.DueDay + contract.GraceDays)
                    {
                        if (!contract.HasDefaulted)
                        {
                            contract.HasDefaulted = true; State.Loan.CreditReduced = true; State.Loan.DefaultCount++;
                            Log("loan.default", "贷款已超过宽限期，记录一次违约。");
                        }
                        // 漏观测期间没有可靠的历史现金及成交价，不用当前资金或价格伪造过去的还款。
                        if (d == day && d < contract.EndingDay && contract.Kind == LoanKind.Secured && !contract.CollateralDisposed)
                        {
                            var proceeds = DisposeLoanCollateral(contract);
                            var used = Math.Min(proceeds, contract.AmountToClose);
                            ApplyLoanPayment(contract, used);
                            delta = checked(delta + proceeds - used);
                            Log("loan.liquidate", $"抵押股票已处置，所得 {proceeds}，用于偿债 {used}，返还 {proceeds - used}。", cashDelta: proceeds - used);
                            if (contract.TotalDebt == 0m) { CloseLoan(d); break; }
                        }
                        if (contract.Kind == LoanKind.Unsecured || contract.CollateralDisposed)
                        {
                            var room = Math.Max(0m, contract.OriginalPrincipal * contract.PenaltyCapRate - contract.TotalPenaltyCharged);
                            var penalty = Math.Min(room, contract.RemainingPrincipal * contract.PenaltyDailyRate);
                            contract.AccruedPenalty += penalty; contract.TotalPenaltyCharged += penalty;
                        }
                    }
                    if (d >= contract.EndingDay)
                    {
                        State.Loan.EndingPending = true;
                        Log("loan.ending", "贷款超过最后还款期限，债务清算待执行。");
                        break;
                    }
                }
            }
            State.Loan.LastSettledDay = day;
        });
    }

    private int DisposeLoanCollateral(LoanContract contract)
    {
        var total = 0m;
        foreach (var share in contract.Collateral)
        {
            var position = State.Positions[share.Key];
            if (position.Quantity < share.Value) throw new InvalidOperationException("抵押持仓不足，已停止清算");
            var gross = State.Companies[share.Key].Price * share.Value;
            var fee = Math.Max(contract.LiquidationMinimumFee, gross * contract.LiquidationFeeRate);
            var proceeds = Math.Max(0m, decimal.Floor(gross - fee));
            var releasedCost = position.Cost * share.Value / position.Quantity;
            position.Quantity -= share.Value; position.Cost -= releasedCost; position.RealizedProfit += proceeds - releasedCost;
            total += proceeds;
            Log("loan.collateral", $"抵押处置 {share.Value} 股，净所得 {proceeds}。", share.Key, share.Value);
        }
        contract.CollateralDisposed = true;
        return checked((int)total);
    }

    private static void ApplyLoanPayment(LoanContract contract, decimal payment)
    {
        var paid = Math.Min(payment, contract.AccruedPenalty); contract.AccruedPenalty -= paid; payment -= paid;
        paid = Math.Min(payment, contract.AccruedInterest); contract.AccruedInterest -= paid; payment -= paid;
        contract.RemainingPrincipal -= Math.Min(payment, contract.RemainingPrincipal);
    }

    private void CloseLoan(int day)
    {
        var loan = State.Loan; var contract = loan.Contract!;
        if (!contract.HasDefaulted && contract.WasCreditReducedLoan) loan.CreditReduced = false;
        loan.LastClosedDay = day; loan.CooldownUntilDay = checked(day + contract.CooldownDays);
        if (contract.Source == LoanSource.StartingDebt) loan.StartingDebtCompleted = true;
        // 只保留最近一份结清合同；与扣款一起保存，失败时由资金事务完整回滚。
        contract.ClosedDay = day; loan.LastClosedContract = contract;
        loan.CompletedLoans++; loan.Contract = null; loan.EndingPending = false;
        Log("loan.closed", $"贷款已结清，第 {loan.CooldownUntilDay} 日起可以再次申请。");
    }

    private static void ValidateLoanState(MarketState state)
    {
        var loan = state.Loan;
        if (loan == null || loan.SchemaVersion != 1 || loan.FirstFailureDay < -1 || loan.VisitDay < -1 || loan.CardItemId < -1 || loan.LastSettledDay < -1 || loan.LastSettledDay > state.LastDay
            || loan.CooldownUntilDay < -1 || loan.LastCollectionDay < -1 || loan.LastReminderDay < -1 || loan.LastReminderDay > state.LastDay || loan.LastClosedDay < -1 || loan.CompletedLoans < 0 || loan.DefaultCount < 0 || loan.ServiceDays == null
            || loan.StartingDebtStartDay < -1 || loan.StartingDebtStartDay > state.LastDay || loan.StartingDebtStartDay > int.MaxValue - 28 || loan.StartingDebtCompleted && loan.StartingDebtStartDay < 0
            || loan.StartingDebtCompletionNotified && !loan.StartingDebtCompleted
            || loan.ServiceDays.Count > 7 || loan.ServiceDays.Any(d => d.Key < 0 || d.Key > state.LastDay || d.Value == null || d.Value.TotalDue < 0)
            || loan.ContactUnlocked != loan.HasMetLender || loan.FirstFailureDay < 0 && (loan.VisitDay >= 0 || loan.HasMetLender && loan.StartingDebtStartDay < 0) || loan.FirstFailureDay >= 0 && (loan.FirstFailureDay > state.LastDay || (long)loan.FirstFailureDay + 1 != loan.VisitDay)
            || loan.StartingDebtStartDay >= 0 && !loan.ContactUnlocked
            || loan.StartingDebtStartDay >= 0 && !loan.StartingDebtCompleted && loan.Contract?.Source != LoanSource.StartingDebt
            || (loan.EndingPending || loan.EndingExecuted) && loan.Contract == null) throw new ArgumentException("贷款联系人或日结存档无效");
        var archive = loan.LastClosedContract;
        if (archive != null && (string.IsNullOrWhiteSpace(archive.Id) || archive.ClosedDay < archive.StartDay || archive.ClosedDay > state.LastDay
            || archive.ClosedDay != loan.LastClosedDay || archive.TotalDebt != 0 || archive.OriginalPrincipal < 1 || archive.OriginalPrincipal > 1000000
            || archive.StartDay < 0 || archive.DueDay <= archive.StartDay || (long)archive.DueDay - archive.StartDay > 28 || archive.EndingDay <= archive.DueDay
            || archive.DailyRate < 0 || archive.DailyRate > 1 || archive.PenaltyDailyRate < 0 || archive.PenaltyDailyRate > 1
            || archive.PenaltyCapRate < 0 || archive.PenaltyCapRate > 1 || archive.GraceDays < 0 || archive.GraceDays > 7
            || archive.LiquidationFeeRate < 0 || archive.LiquidationFeeRate > .5m || archive.LiquidationMinimumFee < 0
            || archive.Collateral == null || archive.Collateral.Any(p => p.Value < 1)
            || (archive.Source != LoanSource.Regular && archive.Source != LoanSource.StartingDebt)
            || (archive.Kind != LoanKind.Unsecured && archive.Kind != LoanKind.Secured))) throw new ArgumentException("已结清贷款合同存档无效");
        var c = loan.Contract;
        if (c == null) return;
        bool startingDebt = c.Source == LoanSource.StartingDebt;
        if (string.IsNullOrWhiteSpace(c.Id) || c.ClosedDay != -1 || (c.Source != LoanSource.Regular && !startingDebt) || (c.Kind != LoanKind.Unsecured && c.Kind != LoanKind.Secured) || c.OriginalPrincipal < 1 || c.OriginalPrincipal > 1000000 || c.RemainingPrincipal < 0 || c.RemainingPrincipal > c.OriginalPrincipal
            || c.AccruedInterest < 0 || c.AccruedInterest > 100000000m || c.AccruedPenalty < 0 || c.AccruedPenalty > c.TotalPenaltyCharged || c.TotalPenaltyCharged < 0 || c.TotalPenaltyCharged > c.OriginalPrincipal * c.PenaltyCapRate
            || (startingDebt ? c.DailyRate != 0 : c.DailyRate <= 0) || c.DailyRate > 1 || c.PenaltyDailyRate < 0 || c.PenaltyDailyRate > 1 || c.PenaltyCapRate < 0 || c.PenaltyCapRate > 1 || c.GraceDays < 0 || c.GraceDays > 7 || c.CooldownDays < 0 || c.CooldownDays > 30
            || c.StartDay < 0 || c.StartDay > state.LastDay || (long)c.DueDay - c.StartDay < 1 || (long)c.DueDay - c.StartDay > 28
            || (!startingDebt && ((long)c.EndingDay - c.DueDay <= c.GraceDays + 1 || (long)c.EndingDay - c.DueDay > 30))
            || c.LastInterestDay < c.StartDay || c.LastInterestDay > state.LastDay || c.LastAutoRepayDay < -1 || c.LastAutoRepayDay > state.LastDay || c.TotalDebt <= 0 || c.TotalDebt > int.MaxValue
            || c.LiquidationFeeRate < 0 || c.LiquidationFeeRate > .5m || c.LiquidationMinimumFee < 0 || c.Collateral == null || c.Kind == LoanKind.Unsecured && c.Collateral.Count > 0 || c.Kind == LoanKind.Secured && c.Collateral.Count == 0
            || c.Collateral.Any(p => p.Value < 1 || !state.Positions.ContainsKey(p.Key) || !c.CollateralDisposed && state.Positions[p.Key].Quantity < p.Value)) throw new ArgumentException("贷款合同、金额或抵押股份存档无效");
        if (startingDebt && (loan.StartingDebtStartDay != c.StartDay || loan.StartingDebtCompleted || c.Kind != LoanKind.Unsecured || c.OriginalPrincipal != 5000
            || c.RemainingPrincipal != decimal.Truncate(c.RemainingPrincipal) || c.AccruedInterest != 0 || c.AccruedPenalty != 0 || c.TotalPenaltyCharged != 0
            || c.PenaltyDailyRate != 0 || c.PenaltyCapRate != 0 || c.GraceDays != 0 || c.CooldownDays != 0 || c.WasCreditReducedLoan || c.CollateralDisposed
            || (long)c.DueDay - c.StartDay != 27 || (long)c.EndingDay - c.StartDay != 28)) throw new ArgumentException("负债店主开局债务规则无效");
    }
}
