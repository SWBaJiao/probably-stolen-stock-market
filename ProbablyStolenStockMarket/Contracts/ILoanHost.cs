using System.Collections.Generic;
using ProbablyStolenStockMarket.Market;

namespace ProbablyStolenStockMarket.Contracts;

// 贷款界面只提交业务命令，资金、存档和游戏对象由适配层统一管理。
public interface ILoanHost
{
    MarketEngine? Engine { get; }
    bool CanUseLoans { get; }
    string LoanSaveIdentity { get; }
    int LoanCash { get; }
    string LoanStatus { get; }
    bool LoanContactUnlocked { get; }
    string TakeLoan(LoanKind kind, int amount, int days, Dictionary<string, int> collateral);
    string RepayLoan(int amount);
    void LoanWindowChanged(bool visible);
}
