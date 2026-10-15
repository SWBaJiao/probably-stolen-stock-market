using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MelonModTemplate.Market;

public sealed class MarketConfig
{
    public decimal InitialPrice { get; set; } = 100m;
    public decimal DailyLimit { get; set; } = .03m;
    public decimal CustomerLimit { get; set; } = .02m;
    public decimal RetailDailyLimit { get; set; } = .08m;
    public decimal SignalScale { get; set; } = 1000m;
    public decimal FeeRate { get; set; } = .005m;
    public int MinimumFee { get; set; } = 1;
    public int RumorPrice { get; set; } = 50;
    public int RumorHorizon { get; set; } = 3;
    public int HistoryLimit { get; set; } = 240;
    public int LedgerLimit { get; set; } = 300;
    public bool EnableStructuredEventFallback { get; set; } = true;
    public decimal StructuredEventScale { get; set; } = .5m;
    public decimal StructuredEventLimit { get; set; } = .15m;
    public List<CompanyDefinition> Companies { get; set; } = new();
    public List<SectorDefinition> Sectors { get; set; } = new();
    public List<EventRule> Events { get; set; } = new();
    public List<SellerRule> Sellers { get; set; } = new();

    public static MarketConfig CreateDefault()
    {
        var c = new MarketConfig();
        var names = new[] { ("harvest", "丰禾供给", "食品、农产品与供应"), ("water", "清泉循环", "饮水、净化与循环"), ("medical", "白塔医疗", "药品与医疗设备"), ("industrial", "星锻工业", "机械、工具与矿产"), ("technology", "远星科技", "电子、模块与科研"), ("commerce", "环宇商贸", "综合贸易与奢侈品"), ("energy", "霆光能源", "燃料、电池与供电"), ("logistics", "晨星物流", "运输、仓储与供应链"), ("defense", "安盾防务", "武器、防护与安全"), ("materials", "聚合材料", "化学品与工业材料") };
        c.Companies = names.Select(x => new CompanyDefinition { Id = x.Item1, Name = x.Item2, Description = x.Item3 }).ToList();
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
        c.Events.Add(new EventRule { EventId = "badNutrifruitHarvest", Description = "营养果歉收，食品供应承压", TrueHint = "食品供应可能收紧，相关商家的进货压力正在增加", FalseHint = "食品供应可能增加，相关商家的进货压力正在缓解", Sectors = new() { "food" }, Impacts = new() { ["harvest"] = -.12m, ["commerce"] = -.05m, ["logistics"] = -.05m } });
        c.Events.Add(new EventRule { EventId = "bm_illegal_weapon_shipment", Description = "非法武器货物到港，防务市场出现波动", TrueHint = "一批防务物资可能流入市场，供应渠道正在扩张", FalseHint = "防务物资流通可能收紧，供应渠道正在收缩", Sectors = new() { "weapons", "electronics" }, Impacts = new() { ["defense"] = .10m, ["commerce"] = .06m, ["technology"] = .05m } });
        c.Events.Add(new EventRule { EventId = "rev_mobilization_secret", Description = "武器需求增加", TrueHint = "有组织正在筹集装备，相关采购规模可能扩大", FalseHint = "有组织正在缩减装备采购，相关订单规模可能回落", Sectors = new() { "weapons", "minerals", "electronics" }, Impacts = new() { ["defense"] = .12m, ["industrial"] = .07m, ["technology"] = .05m } });
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

    public string Serialize() => JsonSerializer.Serialize(this, MarketEngine.JsonOptions);
    public static MarketConfig Deserialize(string json) { var c = JsonSerializer.Deserialize<MarketConfig>(json, MarketEngine.JsonOptions) ?? throw new ArgumentException("行情配置为空"); c.Validate(); return c; }
    public void Validate()
    {
        if (InitialPrice < .01m || InitialPrice > 10000000m || DailyLimit < 0 || DailyLimit > .2m || CustomerLimit < 0 || CustomerLimit > .2m || RetailDailyLimit < 0 || RetailDailyLimit > .5m || SignalScale <= 0 || FeeRate < 0 || FeeRate > .5m || MinimumFee < 0 || RumorPrice < 0 || RumorHorizon < 1 || HistoryLimit < 2 || LedgerLimit < 1 || StructuredEventScale < 0 || StructuredEventScale > 1 || StructuredEventLimit < 0 || StructuredEventLimit > .5m) throw new ArgumentException("行情参数超出允许范围");
        if (Companies == null || Sectors == null || Events == null || Sellers == null || Companies.Count == 0 || Companies.Any(x => x == null || string.IsNullOrWhiteSpace(x.Id) || string.IsNullOrWhiteSpace(x.Name)) || Companies.Select(x => x.Id).Distinct().Count() != Companies.Count) throw new ArgumentException("公司配置不完整或标识重复");
        var ids = Companies.Select(x => x.Id).ToHashSet();
        if (Sectors.Any(s => s == null || string.IsNullOrWhiteSpace(s.Id) || s.ItemTypes == null || s.Weights == null || s.Weights.Count == 0 || s.Weights.Any(w => !ids.Contains(w.Key) || w.Value <= 0) || s.Weights.Values.Sum() != 1m) || Sectors.Select(x => x.Id).Distinct().Count() != Sectors.Count) throw new ArgumentException("行业权重必须为正且合计为 100%");
        if (Events.Any(e => e == null || string.IsNullOrWhiteSpace(e.EventId) || e.Impacts == null || e.Sectors == null || e.Impacts.Any(w => !ids.Contains(w.Key) || Math.Abs(w.Value) > .5m) || e.Sectors.Any(s => !Sectors.Any(x => x.Id == s))) || Events.Select(x => x.EventId).Distinct().Count() != Events.Count) throw new ArgumentException("事件配置无效或重复");
        if (Sellers.Any(s => s == null || string.IsNullOrWhiteSpace(s.Identifier) && string.IsNullOrWhiteSpace(s.Faction) || s.BaseReliability < .2m || s.BaseReliability > .9m || s.Sectors == null || s.Sectors.Any(id => !Sectors.Any(x => x.Id == id)))) throw new ArgumentException("售卖者身份配置无效");
    }
}
