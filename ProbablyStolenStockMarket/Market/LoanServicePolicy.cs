using System.Collections.Generic;

namespace ProbablyStolenStockMarket.Market;

public static class LoanServicePolicy
{
    // 调用方只传入已解锁且未冷却的服务；免费服务不参与贷款引荐条件。
    public static bool AllPaidServicesUnaffordable(IEnumerable<int> eligibleCosts, int cash)
    {
        if (eligibleCosts == null) return false;
        var hasPaidService = false;
        foreach (var cost in eligibleCosts)
        {
            if (cost <= 0) continue;
            hasPaidService = true;
            if (cost <= cash) return false;
        }
        return hasPaidService;
    }
}
