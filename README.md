# Probably Stolen 股市 Mod

基于 MelonLoader 的投资玩法 Mod：10 家虚构公司、商品交叉关联、顾客成交驱动行情、原版事件跳价、现货持仓与付费真假内幕。

<img src="MelonModTemplate/Assets/Textures/Items/stock_market_terminal.png" height="220" alt="股市面板道具图标">

进入存档后自动赠送“股市面板”，双击打开。第一次需要正常保存并重新载入存档，确认投资状态能够随原版存档恢复后才开放资金交易。背包满时延后赠送，道具缺失时自动补领。

## 当前状态

开发目标为 Probably Stolen Demo 0.46D、Unity 2021.3.45f2、MelonLoader 0.7.3、Windows .NET 6 IL2CPP 环境。使用 CrossOver 安装时，构建路径填写容器在 macOS 上的实际磁盘路径。

已完成源码、独立逻辑测试、构建与部署配置。游戏中的回调顺序、背包入口、保存恢复与画面显示需要实际游玩验收；测试和编译通过不能代替游戏内验证。详见 [开发与安装说明](MelonModTemplate/README.md) 和 [适配证据与验收步骤](MelonModTemplate/Game/ADAPTER-NOTES.md)。

## 玩法

- 商品成交按分类权重分配到多家公司，多分类取平均；玩家卖出利好，买入利空，遣散顾客后统一结算。
- 每日小幅波动与估值回归；事件可让不同公司出现相反走势。每次顾客影响及单日累计影响设有上限。
- 使用原版资金买卖整数股，显示持仓、成本、盈亏、手续费和流水。
- 来访顾客出售真假内幕，行业身份与历史记录影响可信度。购买前隐藏内容，按事件事实验证，取消的事件不损害信誉。
- 新增独立透明像素风终端图标；保存时仍使用原版便笺资源标识。

## 开发

1. 安装 MelonLoader 并运行游戏一次，生成对应版本的 `MelonLoader/Il2CppAssemblies`。
2. 复制 `MelonModTemplate/Game.local.props.example` 为 `Game.local.props`，填写 `GameDirectory`。
3. 执行 `dotnet build MelonModTemplate/MelonModTemplate.csproj -c Release`。构建成功后自动部署 DLL 和资源；只编译使用 `-p:DeployOnBuild=false`。

模组目标框架为 .NET 6；独立测试使用 .NET 10 SDK，命令为 `dotnet run --project Tests/StockMarket.Tests`。测试不加载游戏，也不需要游戏 DLL。

运行配置首次生成在 `<游戏>/UserData/MelonModTemplate/market-config.json`。修改后重启游戏生效；非法配置会停止股市初始化并保留原文件。已有配置不会被构建覆盖。

## 来源

开发流程参考 [dragonpan2/probably-stolen-modding](https://github.com/dragonpan2/probably-stolen-modding) 的 LearnerMod。当前游戏的原生 ModLoader 处于受限状态，因此实现使用 MelonLoader 与 Harmony；不依赖较新原生 ModHelper API。加载器、游戏及生成代理程序集不包含在仓库中。
