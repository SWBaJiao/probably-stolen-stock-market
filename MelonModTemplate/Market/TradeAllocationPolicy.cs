using System;
using System.Collections.Generic;
using System.Linq;

namespace MelonModTemplate.Market;

public static class TradeAllocationPolicy
{
    public static bool TryAllocate(int cashDelta, IReadOnlyDictionary<int, decimal> amounts,
        int? expectedWholesaleCost, out Dictionary<int, decimal> signedAmounts)
    {
        signedAmounts = new Dictionary<int, decimal>();
        if (cashDelta == 0 || amounts == null || amounts.Count == 0) return false;
        if (expectedWholesaleCost.HasValue && (expectedWholesaleCost.Value <= 0 || cashDelta >= 0
            || (decimal)cashDelta != -(decimal)expectedWholesaleCost.Value)) return false;
        try
        {
            var items = amounts.OrderBy(p => p.Key).ToArray();
            if (items.Any(p => p.Value <= 0)) return false;
            var sum = items.Sum(p => p.Value);
            var total = Math.Abs((decimal)cashDelta);
            var exact = sum == total;
            // 只接受已由批发入口确认的实际整单扣款，容差按每个已捕获商品一次舍入计算。
            if (!exact && !(cashDelta < 0 && expectedWholesaleCost.HasValue && expectedWholesaleCost.Value > 0
                && (decimal)cashDelta == -(decimal)expectedWholesaleCost.Value && Math.Abs(sum - total) <= items.Length)) return false;

            decimal allocated = 0;
            for (var i = 0; i < items.Length; i++)
            {
                var value = i == items.Length - 1 ? cashDelta - allocated
                    : exact ? items[i].Value * Math.Sign(cashDelta) : cashDelta * items[i].Value / sum;
                signedAmounts.Add(items[i].Key, value);
                allocated += value;
            }
            return true;
        }
        catch (OverflowException)
        {
            // 异常金额拒绝计入行情，不能留下部分分配。
            signedAmounts.Clear();
            return false;
        }
    }
}
