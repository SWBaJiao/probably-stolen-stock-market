using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using ProbablyStolenStockMarket.Market;

namespace ProbablyStolenStockMarket.Configuration;

public static class MarketConfigLoader
{
    public static string ConfigPath => Path.Combine(ModPaths.DataDirectory, "market-config.json");

    public static MarketConfig Load(MelonLogger.Instance logger)
    {
        var path = ConfigPath;
        var legacy = Path.Combine(ModPaths.LegacyDataDirectory, "market-config.json");
        if (!File.Exists(path) && File.Exists(legacy))
        {
            // 先严格验证旧配置，再原样复制；旧文件保留，新文件存在时始终优先使用。
            var text = File.ReadAllText(legacy);
            MarketConfig.Deserialize(text);
            Directory.CreateDirectory(ModPaths.DataDirectory);
            File.Copy(legacy, path, overwrite: false);
            logger.Msg("已将旧版股市配置迁移到：" + path);
        }
        if (File.Exists(path))
        {
            // 用户配置出错时保留原文件并停止初始化，避免悄悄替换经济规则。
            var custom = MarketConfig.Deserialize(File.ReadAllText(path));
            logger.Msg("股市配置已加载：" + path);
            LogStockConfiguration(custom, logger);
            return custom;
        }

        var bundled = ModPaths.AssetPath("market-config.default.json");
        var config = File.Exists(bundled)
            ? MarketConfig.Deserialize(File.ReadAllText(bundled))
            : MarketConfig.CreateDefault();
        config.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, config.Serialize());
        logger.Msg("首次创建股市配置：" + path);
        LogStockConfiguration(config, logger);
        return config;
    }

    private static void LogStockConfiguration(MarketConfig config, MelonLogger.Instance logger)
    {
        logger.Msg($"[股市事件配置] 专用事件{(config.EnableStockEvents ? "启用" : "停用")}；模板 {config.StockEventTemplates.Count} 个；提前排程 {config.StockScheduleDays} 日；间隔 {config.StockEventIntervalMinDays}～{config.StockEventIntervalMaxDays} 日；同公司每日主冲击净限幅 {config.EventShockDailyLimit:P0}。");
    }
}
