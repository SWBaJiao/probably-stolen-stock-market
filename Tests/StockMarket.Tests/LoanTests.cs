using ProbablyStolenStockMarket.Market;
using System.Text.Json.Nodes;

static class LoanTests
{
    private static MarketEngine Engine(int serviceDue = 100)
    {
        var config = TestConfig.Create(); config.DailyLimit = 0m;
        var engine = new MarketEngine(config); engine.ApplyStorageAccess(false);
        engine.AdvanceDay(0); engine.RecordServiceSettlement(0, serviceDue, true);
        engine.AdvanceDay(1); engine.MarkLoanContactMet(); engine.AdvanceDay(2);
        return engine;
    }
    private static void Day(MarketEngine engine, Wallet wallet, int day) { engine.AdvanceDay(day); var result = engine.ProcessLoanDay(day, wallet, () => { }); if (!result.Success) throw new Exception(result.Message); }
    private static LoanQuote Borrow(MarketEngine engine, Wallet wallet, int amount = 100, int duration = 7, LoanKind kind = LoanKind.Unsecured, Dictionary<string, int>? collateral = null)
    {
        var quote = engine.QuoteLoan(kind, amount, duration, collateral);
        var result = engine.TakeLoan(quote, wallet, () => { }); if (!result.Success) throw new Exception(result.Message);
        return quote;
    }

    private static MarketEngine StartingEngine(int firstBusinessDay = 0)
    {
        var config = TestConfig.Create(); config.DailyLimit = 0m;
        var engine = new MarketEngine(config); engine.ApplyStorageAccess(false); engine.AdvanceDay(firstBusinessDay);
        return engine;
    }

    public static void Run(Action<bool, string> check)
    {
        check(!LoanServicePolicy.AllPaidServicesUnaffordable(new[] { 10, 100 }, 50), "仍有廉价可开通服务付得起时不触发贷款引荐");
        check(!LoanServicePolicy.AllPaidServicesUnaffordable(new[] { 50, 100 }, 50), "余额等于最便宜服务价格时仍可支付不触发引荐");
        check(!LoanServicePolicy.AllPaidServicesUnaffordable(Array.Empty<int>(), 0) && !LoanServicePolicy.AllPaidServicesUnaffordable(new[] { 0, -1 }, 0), "没有可开通付费服务时不因空集或免费项触发引荐");
        check(LoanServicePolicy.AllPaidServicesUnaffordable(new[] { 50, 100 }, 49) && LoanServicePolicy.AllPaidServicesUnaffordable(new[] { 0, 50, 100 }, 49), "所有可开通付费服务都高于余额时触发且忽略免费项");
        var engine = Engine(); var wallet = new Wallet { Cash = 1000 };
        check(engine.State.Loan.FirstFailureDay == 0 && engine.State.Loan.VisitDay == 1 && engine.State.Loan.ContactUnlocked, "贷款首次资金不足判定排次日来访并在见面后解锁联系人");
        var stable = engine.Serialize();
        check(!engine.RecordServiceSettlement(0, 100, true) && engine.Serialize() == stable, "相同服务结算重复观察不重复排来访或写流水");
        engine.RecordServiceSettlement(1, 200, false); engine.RecordServiceSettlement(2, 300, false);
        check(engine.GetLoanServiceBaseline() == 200m && engine.QuoteLoan(LoanKind.Unsecured, 500, 7).MaximumPrincipal == 500, "前三个实录服务应付均值决定额度且信用额度有硬上限");
        engine.State.Loan.CreditReduced = true;
        check(engine.QuoteLoan(LoanKind.Unsecured, 375, 7).MaximumPrincipal == 375, "信用折扣作用于封顶后的实际额度不能被高服务支出抵消");
        engine.State.Loan.CreditReduced = false;
        foreach (var serviceDue in new[] { 120, 200 })
        {
            var reduced = Engine(serviceDue); reduced.State.Loan.CreditReduced = true;
            check(reduced.QuoteLoan(LoanKind.Unsecured, 375, 7).MaximumPrincipal == 375, $"服务基准 {serviceDue} 的信用额度先封顶五百再降为三百七十五");
        }
        foreach (var row in new[] { (7, .02m), (8, .0225m), (14, .0225m), (15, .025m), (21, .025m), (22, .0275m), (28, .0275m) })
            check(engine.QuoteLoan(LoanKind.Unsecured, 100, row.Item1).DailyRate == row.Item2, $"无抵押贷款 {row.Item1} 日使用正确利率档位");
        check(!engine.QuoteLoan(LoanKind.Unsecured, 100, 0).Success && !engine.QuoteLoan(LoanKind.Unsecured, 100, 29).Success && !engine.QuoteLoan(LoanKind.Unsecured, int.MaxValue, int.MaxValue).Success, "期限严格限制为一至二十八日且无效输入不溢出");
        var quote = Borrow(engine, wallet, 500, 28);
        check(quote.ExpectedInterest == 385m && quote.ExpectedRepayment == 885 && wallet.Cash == 1500 && engine.State.Loan.Contract!.AccruedInterest == 0, "二十八日合同预览与放款一致且放款日不计息");
        check(!engine.TakeLoan(quote, wallet, () => { }).Success && wallet.Cash == 1500, "重复放款和另一种产品都不能绕过单合同限制");
        Day(engine, wallet, 3);
        check(engine.State.Loan.Contract!.AccruedInterest == 13.75m, "次日起按签约日利率精确累计小数利息");
        engine.Config.Loans.UnsecuredDailyRates[3] = .5m;
        Day(engine, wallet, 4);
        check(engine.State.Loan.Contract!.AccruedInterest == 27.50m && engine.State.Loan.Contract.DailyRate == .0275m, "改配置不会改变已签约利率快照");
        var repaid = engine.RepayLoan(100, wallet, () => { });
        check(repaid.Success && engine.State.Loan.Contract!.RemainingPrincipal == 427.50m && engine.State.Loan.Contract.AccruedInterest == 0m, "部分还款先利息后本金并保留小数本金");
        Day(engine, wallet, 5);
        check(engine.State.Loan.Contract!.AccruedInterest == 11.75625m, "部分还本后的利息仅按剩余本金计算");
        var due = engine.State.Loan.Contract.AmountToClose;
        check(engine.RepayLoan(due, wallet, () => { }).Success && engine.State.Loan.Contract == null && engine.State.Loan.CooldownUntilDay == 8, "提前结清统一取整且不收未来利息并开始三日等待");
        check(!engine.QuoteLoan(LoanKind.Unsecured, 100, 7).Success, "结清后等待期内两类贷款均不可办理");

        engine = Engine(); wallet = new Wallet { Cash = 1000 }; Borrow(engine, wallet);
        Day(engine, wallet, 9);
        check(engine.State.Loan.Contract!.AccruedInterest == 14m && engine.State.Loan.Contract.DueDay == 9, "到期当天恰好计入期限内全部日息");
        Day(engine, wallet, 10);
        check(engine.State.Loan.Contract == null && wallet.Cash == 986, "原版日结后自动还款按到期账单扣款不多收次日日息");

        engine = Engine(); wallet = new Wallet { Cash = 0 }; Borrow(engine, wallet, 100, 1); wallet.Cash = 0;
        Day(engine, wallet, 5);
        check(!engine.State.Loan.Contract!.HasDefaulted && engine.State.Loan.Contract.AccruedPenalty == 0m, "到期后两日宽限期不记违约也不收罚息");
        Day(engine, wallet, 6);
        check(engine.State.Loan.Contract!.HasDefaulted && engine.State.Loan.Contract.AccruedPenalty == .5m && engine.State.Loan.DefaultCount == 1 && engine.State.Loan.CreditReduced, "逾期第三日开始罚息并仅记录一次违约");
        wallet.Cash = 1;
        check(engine.RepayLoan(1, wallet, () => { }).Success && engine.State.Loan.Contract!.AccruedPenalty == 0m && engine.State.Loan.Contract.AccruedInterest == 7.5m, "部分还款先罚息再利息且不重置期限");
        Day(engine, wallet, 10);
        var beforeEnding = engine.Serialize();
        check(!engine.State.Loan.EndingPending && engine.State.Loan.Contract!.EndingDay == 11, "逾期第七日仍保留正常还款机会");
        Day(engine, wallet, 11);
        check(engine.State.Loan.EndingPending && !engine.RepayLoan(100, new Wallet { Cash = 1000 }, () => { }).Success, "逾期第八日标记债务清算且部分还款不能延长最后期限");
        var once = engine.Serialize(); engine.ProcessLoanDay(11, wallet, () => { });
        check(engine.Serialize() == once, "贷款每日结算和结束标记在重复日期回调中幂等");
        var restored = new MarketEngine(engine.Config, MarketEngine.Deserialize(beforeEnding, engine.Config));
        Day(restored, wallet, 11);
        check(restored.State.Loan.EndingPending && restored.State.Loan.DefaultCount == 1, "重读最后还款日前存档继续原始倒计时且不重复违约记录");
        engine = Engine(); wallet = new Wallet { Cash = 1000 }; Borrow(engine, wallet, 100, 1);
        Day(engine, wallet, 11);
        check(engine.State.Loan.EndingPending && wallet.Cash == 1100, "跨过整个逾期窗口时不能用当前现金伪造过去已经自动还清");
        engine.AdvanceDay(12); var pendingEnding = engine.Serialize(); engine.ProcessLoanDay(12, wallet, () => { });
        check(engine.Serialize() == pendingEnding, "清算待执行后不重复计息或重新写入结束流水");

        engine = Engine(); wallet = new Wallet(); Borrow(engine, wallet, 100, 1); wallet.Cash = 0;
        Day(engine, wallet, 6); wallet.Cash = 500;
        check(engine.RepayLoan(int.MaxValue, wallet, () => { }).Success && engine.State.Loan.CreditReduced, "违约后结清仍保留下一笔无抵押信用额度折扣");
        Day(engine, wallet, 8);
        check(!engine.QuoteLoan(LoanKind.Unsecured, 100, 7).Success, "违约后结清也必须完成三日等待期");
        Day(engine, wallet, 9);
        check(engine.QuoteLoan(LoanKind.Unsecured, 100, 7).MaximumPrincipal == 375, "违约后的下一笔信用额度按百分之七十五核定");
        Borrow(engine, wallet, 100, 7);
        check(engine.RepayLoan(100, wallet, () => { }).Success && !engine.State.Loan.CreditReduced, "正常结清受折扣的无抵押合同后恢复信用额度");

        engine = Engine(); engine.Config.Loans.GraceDays = 0; engine.Config.Loans.EndingOverdueDays = 30;
        wallet = new Wallet(); Borrow(engine, wallet, 100, 1); wallet.Cash = 0;
        Day(engine, wallet, 31);
        check(engine.State.Loan.Contract!.TotalPenaltyCharged == 10m && engine.State.Loan.Contract.AccruedPenalty == 10m, "长期欠款的累计罚息达到原始本金百分之十后停止增加");

        engine = Engine(); wallet = new Wallet(); Borrow(engine, wallet, 1, 7);
        Day(engine, wallet, 3);
        check(engine.RepayLoan(1, wallet, () => { }).Success && engine.State.Loan.Contract!.RemainingPrincipal == .02m, "整数部分还款不会抹掉剩余小数本金");
        Day(engine, wallet, 4); wallet.Cash = 1;
        check(engine.State.Loan.Contract!.AccruedInterest == .0004m && engine.RepayLoan(1, wallet, () => { }).Success && engine.State.Loan.Contract == null, "小额余额精确计息并仅在最终付款时统一取整");

        engine = Engine(); wallet = new Wallet();
        engine.State.Positions["water"].Quantity = 10; engine.State.Positions["water"].Cost = 1000m;
        var collateral = new Dictionary<string, int> { ["water"] = 10 };
        check(engine.GetCollateralUnitValue("water") == 100m && engine.QuoteLoan(LoanKind.Secured, 500, 7, collateral).MaximumPrincipal == 500, "抵押额度采用完整前收与当前价低值的一半");
        Borrow(engine, wallet, 400, 1, LoanKind.Secured, collateral); wallet.Cash = 0;
        check(engine.GetAvailableShares("water") == 0 && !engine.QuoteOrder("water", OrderSide.Sell, 1, 0).Success, "统一卖出入口拒绝出售已抵押股份");
        Day(engine, wallet, 6);
        check(engine.State.Loan.Contract == null && engine.State.Positions["water"].Quantity == 0 && wallet.Cash == 579 && engine.State.Loan.DefaultCount == 1, "抵押逾期出售股票扣手续费后偿债并返还多余现金");

        engine = Engine(); wallet = new Wallet(); engine.State.Positions["water"].Quantity = 10; engine.State.Positions["water"].Cost = 1000m;
        Borrow(engine, wallet, 400, 1, LoanKind.Secured, collateral); wallet.Cash = 0;
        engine.State.Companies["water"].Price = .01m;
        Day(engine, wallet, 6);
        check(engine.State.Loan.Contract is { CollateralDisposed: true, EndingDay: 11 } && engine.State.Positions["water"].Quantity == 0 && engine.State.Loan.Contract.RemainingPrincipal == 400m, "抵押物净值低于手续费时按零回收处理并保留欠款与原结束日期");

        engine = Engine(); wallet = new Wallet(); engine.State.Positions["water"].Quantity = 10; engine.State.Positions["water"].Cost = 1000m;
        engine.State.Companies["water"].Price = 90m;
        quote = engine.QuoteLoan(LoanKind.Secured, 400, 7, collateral);
        check(quote.CollateralValue == 900m && quote.MaximumPrincipal == 450, "抵押股票当前价低于前收时按较低市价估值");
        engine.State.Companies["water"].Price = 120m;
        check(!engine.TakeLoan(quote, wallet, () => { }).Success, "抵押报价后价格改变必须重新确认合同");
        engine.State.Companies["water"].Candles.ForEach(c => c.IsPartial = true);
        check(engine.GetCollateralUnitValue("water") == 0m && !engine.QuoteLoan(LoanKind.Secured, 1, 7, collateral).Success, "缺少完整历史收盘价的旧档股票暂不接受抵押");

        engine = Engine(); wallet = new Wallet(); engine.State.Positions["water"].Quantity = 10; engine.State.Positions["water"].Cost = 1000m;
        Borrow(engine, wallet, 400, 1, LoanKind.Secured, collateral); wallet.Cash = 0;
        Day(engine, wallet, 5); engine.AdvanceDay(6);
        var beforeLiquidation = engine.Serialize(); var liquidationWrites = 0;
        var failedLiquidation = engine.ProcessLoanDay(6, wallet, () => { if (liquidationWrites++ == 0) throw new IOException("模拟清算持久化失败"); });
        check(!failedLiquidation.Success && engine.Serialize() == beforeLiquidation && wallet.Cash == 0 && engine.State.Positions["water"].Quantity == 10, "清算保存失败完整恢复抵押股票、欠款、违约状态与返还现金");
        check(engine.ProcessLoanDay(6, wallet, () => { }).Success && wallet.Cash == 579 && engine.State.Loan.Contract == null, "清算事务失败后同日可安全重试且仅处置一次");

        engine = Engine(); wallet = new Wallet { Cash = 1000 };
        quote = engine.QuoteLoan(LoanKind.Unsecured, 100, 7); var before = engine.Serialize(); var writes = 0;
        var failure = engine.TakeLoan(quote, wallet, () => { if (writes++ == 0) throw new IOException("模拟放款持久化失败"); });
        check(!failure.Success && wallet.Cash == 1000 && engine.Serialize() == before && !engine.State.TradingFrozen, "放款保存失败同时撤销本金到账与合同");
        quote = engine.QuoteLoan(LoanKind.Unsecured, 100, 7); engine.RecordServiceSettlement(2, 20, false);
        check(!engine.TakeLoan(quote, wallet, () => { }).Success && engine.State.Loan.Contract == null, "报价后服务额度变化须重新确认不能按旧报价放款");
        var old = JsonNode.Parse(engine.Serialize())!; old.AsObject().Remove("Loan");
        var legacy = new MarketEngine(engine.Config, MarketEngine.Deserialize(old.ToJsonString(), engine.Config));
        check(legacy.State.Loan.Contract == null && legacy.State.Loan.FirstFailureDay == -1 && legacy.State.Loan.ServiceDays.Count == 0, "旧存档默认无贷款无历史账单且不追溯触发NPC");

        engine = Engine(); wallet = new Wallet { Cash = int.MaxValue };
        check(!engine.TakeLoan(engine.QuoteLoan(LoanKind.Unsecured, 100, 7), wallet, () => { }).Success, "放款前校验原版整数现金上限");
        engine.State.TradingFrozen = true; var frozen = engine.Serialize();
        check(!engine.ProcessLoanDay(2, wallet, () => { }).Success && engine.Serialize() == frozen, "资金技术冻结时不推进贷款处罚或结束倒计时");

        ContractDocuments(check);
        StartingDebt(check);
    }

    private static void ContractDocuments(Action<bool, string> check)
    {
        check(LoanContractDocument.NormalizeBorrowerName(null) == "店主"
            && LoanContractDocument.NormalizeBorrowerName("\n\t\u202e<>") == "店主", "Steam 昵称缺失或只有控制字符时使用默认店主签名");
        check(LoanContractDocument.NormalizeBorrowerName("  小店\n\t 店主\u202e<> ") == "小店 店主", "Steam 昵称过滤换行、双向控制字符和富文本括号且合并空白");
        var emojiName = string.Concat(Enumerable.Repeat("😀", 50));
        check(LoanContractDocument.NormalizeBorrowerName(emojiName) == string.Concat(Enumerable.Repeat("😀", 40)), "长 Steam 昵称按完整 Unicode 字符截断避免截坏表情");

        var engine = Engine(); var wallet = new Wallet { Cash = 1000 };
        var quote = engine.QuoteLoan(LoanKind.Unsecured, 100, 7);
        check(engine.TakeLoan(quote, wallet, () => { }, "  Steam Owner  ").Success
            && engine.State.Loan.Contract!.BorrowerName == "Steam Owner", "放款事务保存签约时 Steam 昵称快照");
        var contract = engine.State.Loan.Contract!;
        var terms = LoanContractDocument.Terms(contract);
        var number = LoanContractDocument.Number(contract);
        engine.Config.Loans.PenaltyDailyRate = .99m; engine.Config.Loans.GraceDays = 0; engine.Config.FeeRate = .4m;
        Day(engine, wallet, 3); engine.RepayLoan(20, wallet, () => { });
        check(LoanContractDocument.Terms(engine.State.Loan.Contract!) == terms
            && LoanContractDocument.Account(engine.State.Loan.Contract!, 3).Contains("剩余本金 82"), "改配置与部分还款不改签约条款且动态账单反映当前欠款");
        var snapshot = engine.Serialize(); var writes = 0;
        check(!engine.RepayLoan(int.MaxValue, wallet, () => { if (writes++ == 0) throw new IOException("模拟结清合同保存失败"); }).Success
            && engine.Serialize() == snapshot && engine.State.Loan.LastClosedContract == null
            && engine.State.Loan.Contract!.ClosedDay == -1, "结清保存失败不能留下已结清合同或结清日期");
        check(engine.RepayLoan(int.MaxValue, wallet, () => { }).Success && engine.State.Loan.Contract == null
            && engine.State.Loan.LastClosedContract is { ClosedDay: 3, BorrowerName: "Steam Owner", TotalDebt: 0 }, "全部结清保留最近一份合同、签名和结清日期");
        var archived = engine.State.Loan.LastClosedContract!;
        check(LoanContractDocument.Terms(archived) == terms && LoanContractDocument.Number(archived) == number
            && LoanContractDocument.Account(archived, 99).Contains("已于第 3 日结清"), "结清凭证继续显示原条款和固定合同编号，账单显示结清状态");
        var restored = new MarketEngine(engine.Config, MarketEngine.Deserialize(engine.Serialize(), engine.Config));
        check(restored.State.Loan.LastClosedContract!.BorrowerName == "Steam Owner"
            && LoanContractDocument.Number(restored.State.Loan.LastClosedContract) == number, "读档恢复结清合同与原 Steam 签名");
        Day(engine, wallet, 6); Borrow(engine, wallet);
        check(engine.State.Loan.LastClosedContract!.Id == archived.Id, "新贷款放款不会提前删除上一份结清凭证");
        engine.RepayLoan(int.MaxValue, wallet, () => { });
        check(engine.State.Loan.LastClosedContract!.Id != archived.Id && engine.State.Loan.CompletedLoans == 2, "再次结清只替换最近凭证避免合同历史无限增长");

        engine = Engine(); wallet = new Wallet { Cash = 1000 }; quote = engine.QuoteLoan(LoanKind.Unsecured, 100, 7);
        writes = 0;
        check(!engine.TakeLoan(quote, wallet, () => { if (writes++ == 0) throw new IOException("模拟签名保存失败"); }, "New Owner").Success
            && engine.State.Loan.Contract == null && engine.State.Loan.LastClosedContract == null, "放款保存失败同时撤销合同签名和凭证");
        Borrow(engine, wallet);
        var legacy = JsonNode.Parse(engine.Serialize())!;
        legacy["Loan"]!["Contract"]!.AsObject().Remove("BorrowerName");
        legacy["Loan"]!["Contract"]!.AsObject().Remove("ClosedDay");
        restored = new MarketEngine(engine.Config, MarketEngine.Deserialize(legacy.ToJsonString(), engine.Config));
        check(restored.State.Loan.Contract is { BorrowerName: "店主", ClosedDay: -1 }, "旧合同缺少签名和结清日期字段时兼容默认名称与未结清状态");

        engine = StartingEngine(5); wallet = new Wallet { Cash = 5000 };
        engine.InitializeStartingDebt(5, wallet, () => { }, "Old Debt Owner");
        terms = LoanContractDocument.Terms(engine.State.Loan.Contract!);
        check(terms.Contains("不再次发放本金") && terms.Contains("共 28 个营业日") && terms.Contains("第 32 日营业结束")
            && terms.Contains("零利息、零罚息，无宽限期") && !terms.Contains("预计利息"), "开局旧债合同完整显示二十八营业日和免息约定，不冒充新发放贷款");
        engine.RepayLoan(int.MaxValue, wallet, () => { });
        restored = new MarketEngine(engine.Config, MarketEngine.Deserialize(engine.Serialize(), engine.Config));
        check(restored.State.Loan.LastClosedContract is { Source: LoanSource.StartingDebt, BorrowerName: "Old Debt Owner", ClosedDay: 5 }
            && LoanContractDocument.Terms(restored.State.Loan.LastClosedContract) == terms, "开局旧债结清后保留独立条款和签名且可读档");
        var translated = LoanContractDocument.Terms(restored.State.Loan.LastClosedContract!, source => source == "借款人：{0}" ? "Borrower: {0}" : source);
        check(translated.Contains("Borrower: Old Debt Owner"), "合同先翻译模板再插入签名避免翻译用户昵称");
    }

    private static void StartingDebt(Action<bool, string> check)
    {
        var engine = StartingEngine(); var wallet = new Wallet { Cash = 1500 };
        check(engine.InitializeStartingDebt(0, wallet, () => { }).Success && wallet.Cash == 1500
            && engine.State.Loan.Contract is { Source: LoanSource.StartingDebt, OriginalPrincipal: 5000, RemainingPrincipal: 5000, DueDay: 27, EndingDay: 28 },
            "负债店主只建立五千元旧债且保留一千五百元现金，首日到第二十八营业日为完整期限");
        check(engine.State.Loan.ContactUnlocked && engine.State.Loan.HasMetLender && engine.State.Loan.FirstFailureDay == -1
            && engine.State.Loan.VisitDay == -1 && !engine.QuoteLoan(LoanKind.Unsecured, 100, 7).Success,
            "负债店主无需服务付费失败即可认识债权人且旧债期间不能新借款");
        var initial = engine.Serialize(); var writes = 0;
        check(engine.InitializeStartingDebt(1, wallet, () => writes++).Success && writes == 0 && engine.Serialize() == initial && wallet.Cash == 1500,
            "重复开局初始化不重建合同、不修改现金、不延长期限");
        engine.RecordServiceSettlement(0, 2000, true);
        check(engine.State.Loan.FirstFailureDay == -1 && engine.State.Loan.VisitDay == -1,
            "开局已认识债权人后资金不足不再排首次引荐来访");
        Day(engine, wallet, 13);
        check(engine.State.Loan.Contract is { RemainingPrincipal: 5000, AccruedInterest: 0, AccruedPenalty: 0, TotalPenaltyCharged: 0, LastInterestDay: 13 }
            && !engine.State.Loan.EndingPending,
            "开局旧债经过两周仍为固定五千元且不计利息罚息");
        check(engine.RepayLoan(1000, wallet, () => { }).Success && wallet.Cash == 500 && engine.State.Loan.Contract!.RemainingPrincipal == 4000,
            "开局债务部分还款直接减少本金并扣除准确现金");
        var saved = engine.Serialize();
        engine = new MarketEngine(engine.Config, MarketEngine.Deserialize(saved, engine.Config));
        check(engine.State.Loan.Contract is { Source: LoanSource.StartingDebt, RemainingPrincipal: 4000, StartDay: 0, DueDay: 27 }
            && engine.State.Loan.ContactUnlocked && !engine.State.Loan.StartingDebtCompleted,
            "开局合同读档保留剩余本金、联系人与原始期限");
        Day(engine, wallet, 27);
        check(!engine.State.Loan.EndingPending && engine.State.Loan.Contract!.AmountToClose == 4000,
            "第二十八营业日开始时仍允许经营和主动还款");
        wallet.Cash = 4100;
        check(engine.CompleteStartingDebtDay(27, wallet, () => { }).Success && wallet.Cash == 100
            && engine.State.Loan.Contract == null && engine.State.Loan.StartingDebtCompleted && !engine.State.Loan.EndingPending
            && engine.State.Loan.CooldownUntilDay == 27 && engine.QuoteLoan(LoanKind.Unsecured, 100, 7).Success,
            "最后营业日服务扣费后可自动结清剩余旧债并立即恢复普通贷款");
        var cleared = engine.Serialize();
        check(engine.InitializeStartingDebt(27, wallet, () => { }).Success && engine.Serialize() == cleared
            && engine.CompleteStartingDebtDay(27, wallet, () => { }).Success && wallet.Cash == 100,
            "开局清债后重复初始化和重复日结不会恢复欠款或重复扣钱");
        var reloaded = new MarketEngine(engine.Config, MarketEngine.Deserialize(cleared, engine.Config));
        check(reloaded.State.Loan.StartingDebtCompleted && reloaded.State.Loan.Contract == null && reloaded.State.Loan.ContactUnlocked,
            "清债成功后读档保留挑战完成与贷款联系人");

        engine = StartingEngine(5); wallet = new Wallet { Cash = 1500 };
        engine.InitializeStartingDebt(5, wallet, () => { }); Day(engine, wallet, 32); wallet.Cash = 4999;
        check(engine.State.Loan.Contract is { DueDay: 32, EndingDay: 33 } && !engine.State.Loan.EndingPending,
            "首个正式营业日不为零时仍保留完整二十八日经营窗口");
        check(engine.CompleteStartingDebtDay(32, wallet, () => { }).Success && engine.State.Loan.EndingPending
            && wallet.Cash == 4999 && engine.State.Loan.Contract is { RemainingPrincipal: 5000, AccruedInterest: 0, AccruedPenalty: 0 }
            && engine.State.Loan.DefaultCount == 1,
            "最终日结后差一元即进入失败且不先扣走不足以结清的现金");
        var ended = engine.Serialize();
        engine.CompleteStartingDebtDay(32, wallet, () => { });
        check(engine.Serialize() == ended && !engine.RepayLoan(5000, new Wallet { Cash = 5000 }, () => { }).Success,
            "截止后的重复日结不重复违约且不能通过事后还款延长挑战");

        engine = StartingEngine(); wallet = new Wallet { Cash = 5000 }; engine.InitializeStartingDebt(0, wallet, () => { });
        Day(engine, wallet, 28);
        check(engine.State.Loan.Contract == null && engine.State.Loan.StartingDebtCompleted && wallet.Cash == 0,
            "最后一晚完成回调漏观测时次日入口仍会按原期限结清旧债");
        engine = StartingEngine(); wallet = new Wallet { Cash = 5000 }; engine.InitializeStartingDebt(0, wallet, () => { });
        Day(engine, wallet, 29);
        check(engine.State.Loan.EndingPending && !engine.State.Loan.StartingDebtCompleted && wallet.Cash == 5000,
            "跨过最后期限的日期跳转不能用后来的现金倒补开局还款");

        engine = StartingEngine(); wallet = new Wallet { Cash = 1500 }; engine.Config.Loans.Enabled = false;
        check(engine.InitializeStartingDebt(0, wallet, () => { }).Success, "关闭普通贷款业务不妨碍创建固定开局债务");
        Day(engine, wallet, 27); engine.CompleteStartingDebtDay(27, wallet, () => { });
        check(engine.State.Loan.EndingPending, "关闭普通贷款配置不能免除开局旧债或取消最终期限");

        engine = StartingEngine(); wallet = new Wallet { Cash = 1500 }; var before = engine.Serialize(); writes = 0;
        check(!engine.InitializeStartingDebt(0, wallet, () => { if (writes++ == 0) throw new IOException("模拟开局债务保存失败"); }).Success
            && engine.Serialize() == before && wallet.Cash == 1500 && !engine.State.TradingFrozen,
            "初始化保存失败完整恢复合同、联系人和一次性标记且不修改现金");
        check(engine.InitializeStartingDebt(0, wallet, () => { }).Success, "初始化失败回滚后可安全重试");
        Day(engine, wallet, 27); wallet.Cash = 5000; before = engine.Serialize(); writes = 0;
        check(!engine.CompleteStartingDebtDay(27, wallet, () => { if (writes++ == 0) throw new IOException("模拟到期还款保存失败"); }).Success
            && engine.Serialize() == before && wallet.Cash == 5000 && !engine.State.TradingFrozen,
            "最后日自动还款保存失败回滚本金、完成标记和现金");
        check(engine.CompleteStartingDebtDay(27, wallet, () => { }).Success && wallet.Cash == 0 && engine.State.Loan.StartingDebtCompleted,
            "最后日自动还款失败后同日重试只扣一次");

        engine = StartingEngine(); wallet = new Wallet { Cash = 5000 }; engine.InitializeStartingDebt(0, wallet, () => { });
        check(engine.RepayLoan(int.MaxValue, wallet, () => { }).Success && wallet.Cash == 0 && engine.State.Loan.StartingDebtCompleted,
            "首日提前清偿只支付五千元且立即完成挑战");
        var ordinary = Engine(); wallet = new Wallet(); Borrow(ordinary, wallet);
        before = ordinary.Serialize();
        check(!ordinary.InitializeStartingDebt(ordinary.State.LastDay, wallet, () => { }).Success && ordinary.Serialize() == before,
            "已有普通贷款的存档不能被开局初始化覆盖");
        var oldContract = JsonNode.Parse(before)!; oldContract["Loan"]!["Contract"]!.AsObject().Remove("Source");
        reloaded = new MarketEngine(ordinary.Config, MarketEngine.Deserialize(oldContract.ToJsonString(), ordinary.Config));
        check(reloaded.State.Loan.Contract!.Source == LoanSource.Regular && reloaded.State.Loan.StartingDebtStartDay == -1,
            "缺少来源字段的旧贷款按普通合同兼容而不获得开局免息");
        engine = StartingEngine(); wallet = new Wallet { Cash = 1500 }; engine.InitializeStartingDebt(0, wallet, () => { });
        var corrupt = JsonNode.Parse(engine.Serialize())!; corrupt["Loan"]!["Contract"]!["DailyRate"] = .01m;
        bool invalid = false;
        try { MarketEngine.Deserialize(corrupt.ToJsonString(), engine.Config); } catch (ArgumentException) { invalid = true; }
        check(invalid, "开局债务存档被改为计息规则时拒绝损坏合同");

        static void RestrictMarket(MarketEngine target)
        {
            target.Config.RequireSaveReloadVerification = true;
            target.ApplyStorageAccess(false);
            target.State.TradingFrozen = true;
            target.State.FreezeReason = "模拟股市资金异常冻结";
        }
        static bool RestrictionsRemain(MarketEngine target) => !target.State.StorageVerified && target.State.TradingFrozen
            && target.State.FreezeReason == "模拟股市资金异常冻结";

        engine = StartingEngine(); wallet = new Wallet { Cash = 1500 }; engine.InitializeStartingDebt(0, wallet, () => { }); RestrictMarket(engine);
        Day(engine, wallet, 1);
        check(engine.RepayLoan(500, wallet, () => { }).Success && wallet.Cash == 1000 && engine.State.Loan.Contract!.RemainingPrincipal == 4500
            && RestrictionsRemain(engine), "股市未验证且冻结时仍可主动偿还开局债务，不解除原有冻结原因");
        wallet.Cash = 5000;
        check(engine.RepayLoan(int.MaxValue, wallet, () => { }).Success && wallet.Cash == 500 && engine.State.Loan.StartingDebtCompleted
            && RestrictionsRemain(engine) && !engine.QuoteLoan(LoanKind.Unsecured, 100, 7).Success,
            "冻结状态下提前清债保留股市门控且普通贷款仍不可办理");
        engine.State.Loan.StartingDebtCompletionNotified = true;
        reloaded = new MarketEngine(engine.Config, MarketEngine.Deserialize(engine.Serialize(), engine.Config));
        check(reloaded.State.Loan.StartingDebtCompletionNotified && reloaded.State.Loan.StartingDebtCompleted && RestrictionsRemain(reloaded),
            "开局清债完成通知标记随存档保存且不会解除股市冻结");

        engine = StartingEngine(); wallet = new Wallet { Cash = 5000 }; engine.InitializeStartingDebt(0, wallet, () => { }); RestrictMarket(engine);
        Day(engine, wallet, 27);
        check(engine.CompleteStartingDebtDay(27, wallet, () => { }).Success && wallet.Cash == 0 && engine.State.Loan.StartingDebtCompleted
            && RestrictionsRemain(engine), "严格验证未完成且股市冻结时最后一晚仍自动结清开局债务");
        engine = StartingEngine(); wallet = new Wallet { Cash = 4999 }; engine.InitializeStartingDebt(0, wallet, () => { }); RestrictMarket(engine);
        Day(engine, wallet, 27);
        check(engine.CompleteStartingDebtDay(27, wallet, () => { }).Success && wallet.Cash == 4999 && engine.State.Loan.EndingPending
            && RestrictionsRemain(engine), "严格验证和股市冻结不能绕过最后一晚欠款不足的失败期限");
        engine = StartingEngine(); wallet = new Wallet { Cash = 5000 }; engine.InitializeStartingDebt(0, wallet, () => { }); RestrictMarket(engine);
        Day(engine, wallet, 28);
        check(wallet.Cash == 0 && engine.State.Loan.StartingDebtCompleted && RestrictionsRemain(engine),
            "股市门控未开放时次日兜底仍正常结清旧债并保留冻结");
        engine = StartingEngine(); wallet = new Wallet { Cash = 4999 }; engine.InitializeStartingDebt(0, wallet, () => { }); RestrictMarket(engine);
        Day(engine, wallet, 28);
        check(wallet.Cash == 4999 && engine.State.Loan.EndingPending && RestrictionsRemain(engine),
            "股市门控未开放时次日兜底仍触发欠款失败而不延长期限");

        engine = StartingEngine(); wallet = new Wallet { Cash = 1500 }; engine.InitializeStartingDebt(0, wallet, () => { }); RestrictMarket(engine);
        before = engine.Serialize(); writes = 0;
        check(!engine.RepayLoan(500, wallet, () => { if (writes++ == 0) throw new IOException("模拟冻结期间还债保存失败"); }).Success
            && engine.Serialize() == before && wallet.Cash == 1500 && RestrictionsRemain(engine),
            "冻结期间开局还款仍执行事务快照和保存失败现金回滚");
        corrupt = JsonNode.Parse(engine.Serialize())!; corrupt["Loan"]!["StartingDebtCompletionNotified"] = true; invalid = false;
        try { MarketEngine.Deserialize(corrupt.ToJsonString(), engine.Config); } catch (ArgumentException) { invalid = true; }
        check(invalid, "未清债的存档不能提前记录挑战完成通知");
    }
}
