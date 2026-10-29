using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProbablyStolenStockMarket.Market;

public sealed partial class MarketConfig
{
    public bool EnableStockEvents { get; set; } = true;
    public int StockScheduleDays { get; set; } = 7;
    public int StockEventIntervalMinDays { get; set; } = 2;
    public int StockEventIntervalMaxDays { get; set; } = 4;
    public int StockTemplateCooldownDays { get; set; } = 14;
    public int StockDurationMinDays { get; set; } = 2;
    public int StockDurationMaxDays { get; set; } = 4;
    public decimal StockIntensityMin { get; set; } = .80m;
    public decimal StockIntensityMax { get; set; } = 1.20m;
    public decimal EventShockDailyLimit { get; set; } = .15m;
    public List<StockEventTemplate> StockEventTemplates { get; set; } = CreateStockEventTemplates();
    public bool RequireSaveReloadVerification { get; set; }
    public decimal InitialPrice { get; set; } = 100m;
    public decimal DailyLimit { get; set; } = .03m;
    public decimal TradeDailyLimit { get; set; } = .02m;
    public int EventShockWindowDays { get; set; } = 5;
    public decimal EventPreheatChance { get; set; } = .65m;
    public decimal EventPreheatScale { get; set; } = .05m;
    public decimal EventPreheatLimit { get; set; } = .005m;
    public decimal EventContinuationScale { get; set; } = .10m;
    public decimal EventContinuationLimit { get; set; } = .0075m;
    public decimal EventContinuationBudget { get; set; } = .25m;
    public decimal EventRecoveryRate { get; set; } = .25m;
    public decimal EventDailyDriftLimit { get; set; } = .01m;
    public decimal EventRetentionMin { get; set; } = .20m;
    public decimal EventRetentionMax { get; set; } = .50m;
    // 保留旧配置字段以兼容存档与本地配置；成交影响现由 TradeDailyLimit 在次日统一结算。
    public decimal CustomerLimit { get; set; } = .02m;
    public decimal RetailDailyLimit { get; set; } = .08m;
    public decimal SignalScale { get; set; } = 1000m;
    public decimal FeeRate { get; set; } = .005m;
    public int MinimumFee { get; set; } = 1;
    public int RumorPrice { get; set; } = 50;
    public decimal JianqingReverseProbability { get; set; } = .70m;
    public int RumorHorizon { get; set; } = 3;
    public int BrokerCooldownDays { get; set; } = 3;
    public int HistoryLimit { get; set; } = 240;
    public int LedgerLimit { get; set; } = 300;
    public bool EnableStructuredEventFallback { get; set; } = true;
    public decimal StructuredEventScale { get; set; } = .5m;
    public decimal StructuredEventLimit { get; set; } = .15m;
    public List<CompanyDefinition> Companies { get; set; } = new();
    public List<SectorDefinition> Sectors { get; set; } = new();
    public List<EventRule> Events { get; set; } = new();
    public List<SellerRule> Sellers { get; set; } = new();

    private static readonly (string Id, string Name, string PreviousDescription, string Description)[] CompanyProfiles = new[]
    {
        ("harvest", "丰禾供给", "食品、农产品与供应", "丰禾供给最早靠一辆往返种植区的小货车起家，如今替街角商店和食堂收购营养果、农产品与耐储食品。它会向清泉循环采购灌装用水，也借晨星物流的货舱把易腐货物赶在变质前送达。丰收时仓库忙于周转，缺水或运输受阻时，采购员则得为下一批货重新讨价还价。"),
        ("water", "清泉循环", "饮水、净化与循环", "清泉循环接管过一座年久失修的净水站，靠检修管线、回收旧滤芯和分装饮用水慢慢站稳脚跟。如今它既供应日常用水，也经营过滤器和净水物资，处理药剂常要向聚合材料询价，设备故障时则离不开星锻工业的零件。对这家公司而言，水质、耗材和机器开工率总是同一本账上的事。"),
        ("medical", "白塔医疗", "药品与医疗设备", "白塔医疗从一间替诊所分装药品的小作坊发展而来，柜台上既有常用药和医疗耗材，也有需要定期维修的诊疗设备。药剂师会向聚合材料采购配药与消毒原料，技术人员则寻找远星科技提供的电子模块。原料短缺、设备停摆或诊所集中补货，都会让仓库的出货安排随之改变。"),
        ("industrial", "星锻工业", "机械、工具与矿产", "星锻工业的招牌挂在老矿区旁，车间里既收矿石和金属，也修工具、组装机械，出售能让旧设备继续运转的零件。霆光能源的电池与供电设备支撑着生产线，安盾防务的装备维修又带来另一类订单。矿料进价、机器停工和客户是否补充库存，决定了工头每天先开哪一张工单。"),
        ("technology", "远星科技", "电子、模块与科研", "远星科技靠拆解旧终端和重新配制电子模块攒下第一批客户，后来才把业务伸向科研装置与设备控制系统。它为霆光能源的供电设备寻找控制部件，也向安盾防务提供装备所需的电子组件。看似不起眼的模块断货，往往会拖慢整台机器交付，仓库因此始终留着一排等待配件的空箱。"),
        ("commerce", "环宇商贸", "综合贸易与奢侈品", "环宇商贸没有统一风格的货架：食品、医疗用品、电子设备与防护装备旁边，常摆着来自远处的奢侈品。采购员从丰禾供给这样的供应商手里凑齐货单，再借晨星物流把货分送到不同街区。它靠渠道和周转衔接买卖双方，货物涨价、商路中断或顾客口味转变，都会改写下一轮进货计划。"),
        ("energy", "霆光能源", "燃料、电池与供电", "霆光能源起初只替修理铺补充电池，后来逐渐接手燃料、能源电池和供电设备的买卖。星锻工业的机械开工离不开稳定供能，远星科技的电子模块又影响着它的设备维护。业务看起来贯穿整个站区，仓库里却仍要逐件核算能源货物的进价、耗损与运输成本，一次缺货就可能打乱多家客户的排程。"),
        ("logistics", "晨星物流", "运输、仓储与供应链", "晨星物流的员工习惯在别人关店后清点货箱，替各行业经营运输、仓储和补货衔接。丰禾供给的食品要赶在变质前送达，白塔医疗的药品与耗材则需要分清批次妥善存放；机械、能源与奢侈品也挤在同一张调度表上。商路畅通时它忙着周转，通道受阻时便得协调改道、退货和堆积的库存。"),
        ("defense", "安盾防务", "武器、防护与安全", "安盾防务最早替巡逻人员修补防护装备，后来才扩展到武器、安全器材和配套零件的供应。它向星锻工业寻找金属和机械部件，也通过环宇商贸接触分散在各处的买家。新订单会带动车间备料，运输限制和部件短缺则可能拖延交付；柜台后的采购单，往往比门前的招牌更早反映生意冷暖。"),
        ("materials", "聚合材料", "化学品与工业材料", "聚合材料把一间旧仓库改成分装车间，经营化学品、工业原料和供下游加工使用的材料。白塔医疗需要配药与消毒原料，清泉循环需要水处理药剂，维修和电子行业也会来找适用的耗材。同一批原料有时会被几类客户同时询价，采购员必须在供应、存放条件和不同订单之间反复安排。"),
    };

    public static MarketConfig CreateDefault()
    {
        var c = new MarketConfig();
        c.Companies = CompanyProfiles.Select(p => new CompanyDefinition { Id = p.Id, Name = p.Name, Description = p.Description }).ToList();
        void Sector(string id, string name, string[] aliases, params (string id, decimal weight)[] weights) => c.Sectors.Add(new SectorDefinition { Id = id, Name = name, ItemTypes = aliases.ToList(), Weights = weights.ToDictionary(x => x.id, x => x.weight) });
        // 别名仅精确匹配；游戏适配层应以运行时记录补充实际类型，禁止通过物品显示名猜测。
        Sector("food", "食品、农产品", new[] { "food", "agriculture" }, ("harvest", .65m), ("commerce", .20m), ("logistics", .15m));
        Sector("water", "饮用水、水处理用品", new[] { "water", "WATER_PURIFICATION_SUPPLY", "FILTER" }, ("water", .65m), ("harvest", .20m), ("logistics", .15m));
        Sector("medical", "药品、医疗耗材", new[] { "medical", "medicine" }, ("medical", .70m), ("commerce", .20m), ("logistics", .10m));
        Sector("tools", "工具、机械", new[] { "tool", "tools", "machine" }, ("industrial", .60m), ("materials", .20m), ("defense", .10m), ("logistics", .10m));
        Sector("minerals", "矿石、金属、工业材料", new[] { "mineral", "ore", "metal", "MATERIAL", "BASIC_MATERIAL" }, ("industrial", .55m), ("energy", .20m), ("defense", .15m), ("logistics", .10m));
        Sector("electronics", "电子、模块", new[] { "electronic", "electronics", "module" }, ("technology", .50m), ("energy", .20m), ("industrial", .20m), ("commerce", .10m));
        Sector("energy", "燃料、电池", new[] { "fuel", "battery", "energy", "ENERGY_CELL" }, ("energy", .65m), ("industrial", .20m), ("logistics", .15m));
        Sector("materials", "化学品", new[] { "chemical", "substance", "CHEMICAL_SUPPLIES" }, ("materials", .50m), ("medical", .20m), ("technology", .20m), ("water", .10m));
        Sector("weapons", "武器、防护装备", new[] { "weapon", "armor", "protection" }, ("defense", .65m), ("commerce", .20m), ("technology", .15m));
        Sector("luxury", "奢侈品", new[] { "luxury", "LUXURY_ITEM" }, ("commerce", .75m), ("logistics", .15m), ("technology", .10m));
        // 事件标识来自当前存档；涨跌幅为本模组的玩法配置。
        c.Events.Add(new EventRule { ImpactSource = EventImpactResolver.BuiltinPriceSource, DirectionVersion = 1, EventId = "badNutrifruitHarvest", Description = "营养果歉收，食品供应承压", TrueHint = "食品供应可能收紧，相关商家的进货压力正在增加", FalseHint = "食品供应可能增加，相关商家的进货压力正在缓解", Sectors = new() { "food" }, Impacts = new() { ["harvest"] = -.12m, ["commerce"] = -.05m, ["logistics"] = -.05m } });
        c.Events.Add(new EventRule { ImpactSource = EventImpactResolver.BuiltinPriceSource, DirectionVersion = 1, EventId = "bm_illegal_weapon_shipment", Description = "非法武器货物到港，防务市场出现波动", TrueHint = "一批防务物资可能流入市场，供应渠道正在扩张", FalseHint = "防务物资流通可能收紧，供应渠道正在收缩", Sectors = new() { "weapons", "electronics" }, Impacts = new() { ["defense"] = .10m, ["commerce"] = .06m, ["technology"] = .05m } });
        c.Events.Add(new EventRule { ImpactSource = EventImpactResolver.BuiltinPriceSource, DirectionVersion = EventImpactResolver.CurrentDirectionVersion, EventId = "rev_mobilization_secret", Description = "武器需求增加", TrueHint = "有组织正在筹集装备，相关采购规模可能扩大", FalseHint = "有组织正在缩减装备采购，相关订单规模可能回落", Sectors = new() { "weapons", "minerals", "electronics" }, Impacts = new() { ["defense"] = -.12m, ["industrial"] = -.07m, ["technology"] = -.05m } });
        void Seller(string id, string identity, decimal probability, params string[] sectors) => c.Sellers.Add(new SellerRule { Identifier = id, Identity = identity, BaseReliability = probability, Sectors = sectors.ToList() });
        Seller("shadyCustomerInformation", "情报商", .65m);
        Seller("pharmacist", "药剂师", .75m, "medical", "materials");
        Seller("hospitalAdmin", "医院管理者", .75m, "medical");
        Seller("clinicNurse", "诊所护士", .75m, "medical");
        Seller("lowerLevelChemist", "化学从业者", .75m, "materials");
        Seller("farmerMinor", "农业从业者", .75m, "food");
        Seller("miner", "矿业从业者", .75m, "minerals", "energy");
        Seller("minerMinor", "矿业从业者", .75m, "minerals");
        Seller("iceMiner", "冰矿从业者", .75m, "water", "minerals");
        Seller("maintenanceWorker", "维修从业者", .75m, "tools", "electronics", "energy");
        Seller("retired_water_merchant", "退休水商", .75m, "water");
        Seller("patrolOfficer", "巡逻警员", .75m, "weapons");
        Seller("securityInspector", "安保检查员", .75m, "weapons");
        Seller("thief", "可疑来源", .30m);
        return c;
    }

    public string Serialize() => JsonConvert.SerializeObject(this, MarketEngine.JsonOptions);
    public static MarketConfig Deserialize(string json)
    {
        var c = JsonConvert.DeserializeObject<MarketConfig>(json, MarketEngine.JsonOptions) ?? throw new ArgumentException("行情配置为空");
        var document = JObject.Parse(json);
        // 旧自定义公司与行业列表只获得完整可用的新模板，不补改用户的业务定义。
        if (document.Property(nameof(StockEventTemplates), StringComparison.Ordinal) == null && c.Companies != null && c.Sectors != null)
        {
            c.StockEventTemplates = c.StockEventTemplates.Where(t => t.Impacts.Keys.All(id => c.Companies.Any(x => x?.Id == id)) && t.Sectors.All(id => c.Sectors.Any(x => x?.Id == id))).ToList();
            if (c.StockEventTemplates.Count == 0) c.EnableStockEvents = false;
        }
        c.UpgradeLegacyDefaultEventRules(); c.UpgradeLegacyCompanyDescriptions(); c.Validate(); return c;
    }
    // 只替换原有短介绍；公司数值、行业映射及玩家自写的故事均不参与本次升级。
    public bool UpgradeLegacyCompanyDescriptions()
    {
        if (Companies == null) return false;
        var changed = false;
        foreach (var company in Companies)
        {
            if (company == null) continue;
            foreach (var profile in CompanyProfiles)
            {
                if (company.Id != profile.Id || company.Description != profile.PreviousDescription) continue;
                company.Description = profile.Description; changed = true;
                break;
            }
        }
        return changed;
    }

    // 仅完全匹配旧默认值时升级；用户编辑过的幅度、行业或文案始终作为显式配置保留。
    public bool UpgradeLegacyDefaultEventRules()
    {
        var old = Events?.FirstOrDefault(e => e?.EventId == "rev_mobilization_secret");
        if (old == null || old.ImpactSource != "" || old.DirectionVersion != 0 || old.Impacts == null || old.Sectors == null) return false;
        var updated = CreateDefault().Events.First(e => e.EventId == "rev_mobilization_secret");
        if (old.Description != updated.Description || old.TrueHint != updated.TrueHint || old.FalseHint != updated.FalseHint || !old.Sectors.SequenceEqual(updated.Sectors) || old.Impacts.Count != updated.Impacts.Count || old.Impacts.Any(p => !updated.Impacts.TryGetValue(p.Key, out var current) || p.Value != -current)) return false;
        old.Impacts = new Dictionary<string, decimal>(updated.Impacts);
        old.ImpactSource = updated.ImpactSource; old.DirectionVersion = updated.DirectionVersion;
        return true;
    }

    public void Validate()
    {
        if (Loans == null) throw new ArgumentException("贷款配置为空");
        Loans.Validate();
        ValidateStockConfiguration();
        if (TradeDailyLimit < 0 || TradeDailyLimit > .2m) throw new ArgumentException("每日成交影响幅度无效");
        if (EventShockWindowDays < 1 || EventShockWindowDays > 3650 || EventPreheatChance < 0 || EventPreheatChance > 1 || EventPreheatScale < 0 || EventPreheatScale > 1 || EventPreheatLimit < 0 || EventPreheatLimit > .1m) throw new ArgumentException("事件冲击窗口与预热参数无效");
        if (EventContinuationScale < 0 || EventContinuationScale > 1 || EventContinuationLimit < 0 || EventContinuationLimit > .1m || EventContinuationBudget < 0 || EventContinuationBudget > 1 || EventRecoveryRate <= 0 || EventRecoveryRate > 1 || EventDailyDriftLimit < 0 || EventDailyDriftLimit > .1m || EventRetentionMin <= 0 || EventRetentionMax >= 1 || EventRetentionMin > EventRetentionMax) throw new ArgumentException("事件持续与保留参数无效");
        if (JianqingReverseProbability < 0m || JianqingReverseProbability > 1m) throw new ArgumentException("剑清酱的反向消息概率须在零到一之间");
        if (BrokerCooldownDays < 1 || BrokerCooldownDays > 100) throw new ArgumentException("情报商到访间隔无效");
        if (InitialPrice < .01m || InitialPrice > 10000000m || DailyLimit < 0 || DailyLimit > .2m || CustomerLimit < 0 || CustomerLimit > .2m || RetailDailyLimit < 0 || RetailDailyLimit > .5m || SignalScale <= 0 || FeeRate < 0 || FeeRate > .5m || MinimumFee < 0 || RumorPrice < 0 || RumorHorizon < 1 || HistoryLimit < 2 || LedgerLimit < 1 || StructuredEventScale < 0 || StructuredEventScale > 1 || StructuredEventLimit < 0 || StructuredEventLimit > .5m) throw new ArgumentException("行情参数超出允许范围");
        if (Companies == null || Sectors == null || Events == null || Sellers == null || Companies.Count == 0 || Companies.Any(x => x == null || string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Name)) || Companies.Select(x => x.Id).Distinct().Count() != Companies.Count) throw new ArgumentException("公司配置不完整或标识重复");
        var ids = Companies.Select(x => x.Id).ToHashSet();
        if (Sectors.Any(s => s == null || string.IsNullOrWhiteSpace(s.Id) || s.ItemTypes == null || s.Weights == null || s.Weights.Count == 0 || s.Weights.Any(w => !ids.Contains(w.Key) || w.Value <= 0) || s.Weights.Values.Sum() != 1m) || Sectors.Select(x => x.Id).Distinct().Count() != Sectors.Count) throw new ArgumentException("行业权重必须为正且合计为 100%");
        if (Events.Any(e => e == null || e.ImpactSource == null || e.DirectionVersion < 0 || e.DirectionVersion > EventImpactResolver.CurrentDirectionVersion || string.IsNullOrWhiteSpace(e.EventId) || e.Impacts == null || e.Sectors == null || e.Impacts.Any(w => !ids.Contains(w.Key) || Math.Abs(w.Value) > .5m) || e.Sectors.Any(s => !Sectors.Any(x => x.Id == s))) || Events.Select(x => x.EventId).Distinct().Count() != Events.Count) throw new ArgumentException("事件配置无效或重复");
        if (Sellers.Any(s => s == null || string.IsNullOrWhiteSpace(s.Identifier) && string.IsNullOrWhiteSpace(s.Faction) || s.BaseReliability < .2m || s.BaseReliability > .9m || s.Sectors == null || s.Sectors.Any(id => !Sectors.Any(x => x.Id == id)))) throw new ArgumentException("售卖者身份配置无效");
    }
}
