using MelonModTemplate.Market;

static class TradeAllocationTests
{
    public static void Run(Action<bool, string> check)
    {
        var items = new Dictionary<int, decimal> { [3] = 11m, [1] = 9m, [2] = 10m };
        check(TradeAllocationPolicy.TryAllocate(-30, items, null, out var exactBuy) && exactBuy[1] == -9m && exactBuy[2] == -10m && exactBuy[3] == -11m && exactBuy.Values.Sum() == -30m, "常规买入按实际负现金精确分配成交金额");
        check(TradeAllocationPolicy.TryAllocate(30, items, null, out var exactSell) && exactSell[1] == 9m && exactSell[2] == 10m && exactSell[3] == 11m && exactSell.Values.Sum() == 30m, "常规卖出按实际正现金精确分配成交金额");
        // 原价 11、12、13，九折后逐件向下取整为 9、10、11，整单取整为 32。
        check(TradeAllocationPolicy.TryAllocate(-32, items, 32, out var wholesale) && wholesale[1] == -9.6m && wholesale[2] == -320m / 30m && wholesale[3] == -32m - wholesale[1] - wholesale[2] && wholesale.Values.Sum() == -32m, "批发整单与逐件折扣舍入不同按实际扣款比例分配并由尾项补足");
        check(items[1] == 9m && items[2] == 10m && items[3] == 11m, "金额分配不修改原始成交回调数据");
        check(!TradeAllocationPolicy.TryAllocate(-32, items, null, out var unsupported) && unsupported.Count == 0, "普通交易不能使用批发舍入容差");
        check(!TradeAllocationPolicy.TryAllocate(-32, items, 31, out var wrongDebit) && wrongDebit.Count == 0, "实扣金额与批发入口报价不符时拒绝分配");
        check(!TradeAllocationPolicy.TryAllocate(-30, items, 31, out var exactWrongQuote) && exactWrongQuote.Count == 0, "即使回调合计吻合实扣也拒绝不符的已知批发报价");
        check(!TradeAllocationPolicy.TryAllocate(-30, items, 0, out _) && !TradeAllocationPolicy.TryAllocate(-30, items, -30, out _), "回调精确吻合也不能绕过批发报价必须为正的限制");
        check(!TradeAllocationPolicy.TryAllocate(30, items, 30, out var exactPositiveWholesale) && exactPositiveWholesale.Count == 0, "回调精确吻合也拒绝批发入口的正向资金变化");
        check(!TradeAllocationPolicy.TryAllocate(32, items, 32, out var positiveMismatch) && positiveMismatch.Count == 0, "卖出收益或奖励差额不能套用批发容差");
        check(!TradeAllocationPolicy.TryAllocate(-32, items, 0, out _) && !TradeAllocationPolicy.TryAllocate(-32, items, -32, out _), "非正批发报价不能授权金额差异");
        check(!TradeAllocationPolicy.TryAllocate(-34, items, 34, out var tooLarge) && tooLarge.Count == 0, "超过每件一次舍入上限的大额差异拒绝计量");
        check(TradeAllocationPolicy.TryAllocate(-33, items, 33, out var inclusive) && inclusive.Values.Sum() == -33m, "批发舍入容差上限包含每件一元且总额守恒");
        check(TradeAllocationPolicy.TryAllocate(-28, items, 28, out var lowerWhole) && lowerWhole.Values.Sum() == -28m && lowerWhole.Values.All(v => v < 0), "整单扣款少于逐件合计时也按比例分配实际负金额");
        check(!TradeAllocationPolicy.TryAllocate(0, items, 32, out _) && !TradeAllocationPolicy.TryAllocate(-32, new Dictionary<int, decimal>(), 32, out _), "没有实际资金变化或没有回调时不创建成交信号");
        check(!TradeAllocationPolicy.TryAllocate(-30, new Dictionary<int, decimal> { [1] = 30, [2] = 0 }, 30, out _) && !TradeAllocationPolicy.TryAllocate(-30, new Dictionary<int, decimal> { [1] = 31, [2] = -1 }, 30, out _), "零金额及正负混合回调不能抵消后伪装成正常成交");
        check(TradeAllocationPolicy.TryAllocate(int.MinValue, new Dictionary<int, decimal> { [1] = 2147483648m }, null, out var minimum) && minimum[1] == int.MinValue, "最小整数现金差转十进制绝对值不溢出");
        check(!TradeAllocationPolicy.TryAllocate(-1, new Dictionary<int, decimal> { [1] = decimal.MaxValue, [2] = decimal.MaxValue }, 1, out var overflow) && overflow.Count == 0, "回调合计溢出安全拒绝且返回空分配");
        var thirds = new Dictionary<int, decimal> { [30] = 1, [10] = 1, [20] = 1 };
        check(TradeAllocationPolicy.TryAllocate(-4, thirds, 4, out var fractional) && fractional.Values.Sum() == -4m && fractional[30] == -4m - fractional[10] - fractional[20], "无法整除的比例分配尾项吸收小数余量保证总额精确");
    }
}
