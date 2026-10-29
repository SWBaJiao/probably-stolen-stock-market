using System.IO;
using ProbablyStolenStockMarket.Market;

namespace ProbablyStolenStockMarket.Configuration;

public static class MarketConfigLoader
{
    private const string ConfigFile = "market-config.json";
    public static string ConfigPath => Path.Combine(ModPaths.DataDirectory, ConfigFile);

    public static MarketConfig Load(ModLogger logger)
    {
        var custom = ModHelper.ReadModDataFileText(ModInfo.Id, ConfigFile);
        if (custom != null)
        {
            // 配置错误时保留原文并停止初始化，避免悄悄替换经济规则。
            var config = MarketConfig.Deserialize(custom);
            logger.Msg("股市配置已加载：" + ConfigPath);
            LogStockConfiguration(config, logger);
            return config;
        }
        var bundled = ModPaths.ReadAssetText("market-config.default.json");
        var defaults = MarketConfig.Deserialize(bundled);
        defaults.Validate();
        ModHelper.WriteModDataFileText(ModInfo.Id, ConfigFile, defaults.Serialize());
        logger.Msg("首次创建股市配置：" + ConfigPath);
        LogStockConfiguration(defaults, logger);
        return defaults;
    }

    private static void LogStockConfiguration(MarketConfig config, ModLogger logger)
    {
        logger.Msg($"[股市事件配置] 专用事件{(config.EnableStockEvents ? "启用" : "停用")}；模板 {config.StockEventTemplates.Count} 个；提前排程 {config.StockScheduleDays} 日；间隔 {config.StockEventIntervalMinDays}～{config.StockEventIntervalMaxDays} 日；同公司每日主冲击净限幅 {config.EventShockDailyLimit:P0}。");
    }
}
