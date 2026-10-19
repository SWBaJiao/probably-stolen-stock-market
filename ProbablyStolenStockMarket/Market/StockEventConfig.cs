using System;
using System.Collections.Generic;
using System.Linq;

namespace ProbablyStolenStockMarket.Market;

public sealed partial class MarketConfig
{
    private static List<StockEventTemplate> CreateStockEventTemplates()
    {
        var result = new List<StockEventTemplate>();
        void Add(string id, string primary, string sector, string title, string body, params (string Company, decimal Impact)[] impacts) => result.Add(new StockEventTemplate
        {
            Id = "stock:" + id, PrimaryCompanyId = primary, Polarity = Math.Sign(impacts.First(p => p.Company == primary).Impact),
            Title = title, Body = body, Sectors = new() { sector }, Impacts = impacts.ToDictionary(p => p.Company, p => p.Impact)
        });
        Add("harvest_supply_contract", "harvest", "food", "丰禾签下长期供货合同", "丰禾供给公布新的长期食品供货合同，稳定的采购安排有望改善仓库周转。环宇商贸参与分销，晨星物流承接配套运输，因此也受到订单扩大的带动。", ("harvest", .08m), ("commerce", .03m), ("logistics", .02m));
        Add("harvest_batch_recall", "harvest", "food", "丰禾食品批次召回", "丰禾供给宣布召回一批食品并开展质量复查，退换与整改成本压低了市场预期。环宇商贸需要调整分销计划，晨星物流的原定货运安排也面临缩减。", ("harvest", -.08m), ("commerce", -.03m), ("logistics", -.02m));
        Add("water_new_plant", "water", "water", "清泉新净水设备投产", "清泉循环的新净水设备进入投产阶段，市场期待供应能力与处理效率改善。星锻工业获得设备配套需求，聚合材料的水处理耗材也迎来补货机会。", ("water", .08m), ("industrial", .03m), ("materials", .02m));
        Add("water_filter_quality", "water", "water", "清泉滤芯质量事故", "清泉循环披露滤芯质量事故，需要承担赔偿和整改成本。不过替代滤材与设备抢修需求增加，聚合材料和星锻工业因此可能获得额外订单，相关公司预期出现分化。", ("water", -.10m), ("materials", .03m), ("industrial", .02m));
        Add("medical_clinic_order", "medical", "medical", "白塔获得诊所联合采购订单", "多家诊所的联合采购订单落到白塔医疗，药品和医疗耗材的出货预期提升。聚合材料为生产提供原料，远星科技参与设备模块配套，也有望分享到新增需求。", ("medical", .08m), ("materials", .03m), ("technology", .02m));
        Add("medical_approval_delay", "medical", "medical", "白塔新药审批受阻", "白塔医疗的新药审批进程受阻，相关产品投产与销售计划被推迟。聚合材料的原料订单预期随之减少，环宇商贸原定的渠道铺货安排也受到影响。", ("medical", -.08m), ("materials", -.02m), ("commerce", -.02m));
        Add("industrial_export_order", "industrial", "tools", "星锻获得机械出口订单", "星锻工业获得一批机械出口订单，生产线和零件采购安排转趋繁忙。晨星物流受益于设备运输需求，霆光能源也可能获得更多生产供能与能源配套订单。", ("industrial", .08m), ("logistics", .03m), ("energy", .02m));
        Add("industrial_line_accident", "industrial", "tools", "星锻生产线事故", "星锻工业的生产线发生事故，检修停工令交货预期承压。安盾防务依赖的机械部件供应受到牵连，霆光能源面向该生产线的供能需求也可能下降。", ("industrial", -.10m), ("defense", -.03m), ("energy", -.02m));
        Add("technology_control_breakthrough", "technology", "electronics", "远星控制模块取得突破", "远星科技公布控制模块技术突破，市场期待新模块拓展应用。霆光能源的供电控制设备和白塔医疗的电子诊疗设备都与这类模块有关，配套业务预期一同改善。", ("technology", .10m), ("energy", .03m), ("medical", .02m));
        Add("technology_module_recall", "technology", "electronics", "远星关键模块召回", "远星科技宣布召回关键模块，维修与替换安排令交付预期承压。霆光能源和白塔医疗的相关配套设备也需要重新检查，市场因而下调这些业务的近期预期。", ("technology", -.10m), ("energy", -.03m), ("medical", -.02m));
        Add("commerce_district_rights", "commerce", "luxury", "环宇取得商业区独家经销权", "环宇商贸取得一处商业区的独家经销权，新增渠道有望扩大货物流通。晨星物流参与配送安排，丰禾供给的食品也进入新的销售网络，相关业务预期受益。", ("commerce", .08m), ("logistics", .03m), ("harvest", .02m));
        Add("commerce_channel_closure", "commerce", "luxury", "环宇违规渠道遭查封", "环宇商贸的一条违规渠道遭到查封，渠道收入和晨星物流的相关配送预期受到打击。安盾防务可能接手部分合规供货需求，因此在这次消息中反而受到利好带动。", ("commerce", -.10m), ("logistics", -.03m), ("defense", .02m));
        Add("energy_storage_order", "energy", "energy", "霆光获得储能电池大单", "霆光能源获得储能电池大单，能源设备出货预期上升。远星科技提供控制模块，星锻工业参与机械与设备配套，订单扩大有望带动两家公司的相关业务。", ("energy", .10m), ("technology", .03m), ("industrial", .02m));
        Add("energy_fuel_contract_loss", "energy", "energy", "霆光燃料采购合同破裂", "霆光能源的燃料采购合同破裂，采购与交付计划面临重新安排。星锻工业的生产供能预期承压，晨星物流原定的能源货运需求也可能减少。", ("energy", -.10m), ("industrial", -.03m), ("logistics", -.02m));
        Add("logistics_route_approval", "logistics", "food", "晨星新货运航线获批", "晨星物流的新货运航线获批，运输能力与配送覆盖有望扩大。环宇商贸可以拓展货物流通，丰禾供给也多了一条食品运输通道，供应链伙伴的预期随之改善。", ("logistics", .08m), ("commerce", .03m), ("harvest", .02m));
        Add("logistics_equipment_failure", "logistics", "tools", "晨星货运设备大面积故障", "晨星物流的货运设备大面积故障，运输与环宇商贸的渠道周转预期承压。设备维修需求同时增加，星锻工业可能接到更多检修与替换部件订单，形成不同方向的影响。", ("logistics", -.10m), ("commerce", -.03m), ("industrial", .02m));
        Add("defense_security_contract", "defense", "weapons", "安盾获得长期安保合同", "安盾防务获得长期安保合同，装备和安全器材需求预期增长。星锻工业的机械部件与远星科技的电子组件参与配套，市场也上调了两家供应商的相关业务预期。", ("defense", .08m), ("industrial", .03m), ("technology", .02m));
        Add("defense_armor_recall", "defense", "weapons", "安盾防护装备召回", "安盾防务宣布召回一批防护装备，质保成本与交付延误令市场预期下降。星锻工业和远星科技面向该批装备的部件与电子组件订单也受到牵连。", ("defense", -.08m), ("industrial", -.03m), ("technology", -.02m));
        Add("materials_treatment_certified", "materials", "materials", "聚合净水药剂通过认证", "聚合材料的净水药剂通过认证，市场期待水处理原料业务拓展。清泉循环可将其用于净水配套，白塔医疗相关卫生耗材的原料供应预期也得到改善。", ("materials", .08m), ("water", .03m), ("medical", .02m));
        Add("materials_pollution_shutdown", "materials", "materials", "聚合工厂因污染停产", "聚合材料的工厂因污染停产，整改和停工成本令经营预期承压。清泉循环依赖的水处理原料与白塔医疗的相关原料供应也可能受到影响，预期同步转弱。", ("materials", -.10m), ("water", -.03m), ("medical", -.02m));
        return result;
    }

    private void ValidateStockConfiguration()
    {
        if (Companies == null || Sectors == null) throw new ArgumentException("公司或行业配置为空");
        if (StockScheduleDays < 1 || StockScheduleDays > 30 || StockEventIntervalMinDays < 1 || StockEventIntervalMaxDays < StockEventIntervalMinDays || StockEventIntervalMaxDays > 30 || StockTemplateCooldownDays < 0 || StockTemplateCooldownDays > 365 || StockDurationMinDays < 1 || StockDurationMaxDays < StockDurationMinDays || StockDurationMaxDays > 30 || StockIntensityMin <= 0 || StockIntensityMax < StockIntensityMin || StockIntensityMax > 2m || EventShockDailyLimit < 0 || EventShockDailyLimit > .15m) throw new ArgumentException("股市专用事件排程参数无效");
        if (StockEventTemplates == null || EnableStockEvents && StockEventTemplates.Count == 0) throw new ArgumentException("股市专用事件模板为空");
        if (StockEventTemplates!.Any(t => t == null || string.IsNullOrWhiteSpace(t.Id) || !t.Id.StartsWith("stock:", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(t.Title) || string.IsNullOrWhiteSpace(t.Body) || string.IsNullOrWhiteSpace(t.PrimaryCompanyId) || t.Sectors == null || t.Impacts == null || t.Polarity != 1 && t.Polarity != -1 || !t.Impacts.TryGetValue(t.PrimaryCompanyId, out var impact) || Math.Sign(impact) != t.Polarity || t.Impacts.Any(p => p.Value == 0 || Math.Abs(p.Value) * StockIntensityMax > .5m || EnableStockEvents && !Companies.Any(c => c?.Id == p.Key)) || EnableStockEvents && t.Sectors.Any(s => !Sectors.Any(c => c?.Id == s))) || StockEventTemplates.Select(t => t.Id).Distinct().Count() != StockEventTemplates.Count) throw new ArgumentException("股市专用事件模板或公司方向无效");
    }
}
