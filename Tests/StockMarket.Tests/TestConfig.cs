using MelonModTemplate.Market;

static class TestConfig
{
    // 原有场景独立验证既有功能，专用排程由新的完整场景负责。
    public static MarketConfig Create() { var config = MarketConfig.CreateDefault(); config.EnableStockEvents = false; return config; }
}
