# Probably Stolen 股市 Mod

基于 MelonLoader 的投资玩法 Mod：10 家虚构公司、商品交叉关联、顾客成交驱动行情、原版与企业专用事件、现货持仓与付费真假内幕。

安装包见 [GitHub Releases](https://github.com/SWBaJiao/probably-stolen-stock-market/releases)，解压安装与旧版升级步骤见 [安装说明](docs/RELEASE-INSTALL.md)。

<img src="ProbablyStolenStockMarket/Assets/Textures/Items/stock_market_terminal.png" height="220" alt="股市面板道具图标">

进入存档后自动赠送“股市面板”，双击打开。现货买卖默认开放，资金与投资状态随原版存档保存。背包满时延后赠送，旧版道具编号失效时可按 `Ctrl+F8` 显式恢复，已发放后不会因空查询重复创建。

## 当前状态

开发目标为 Probably Stolen Demo 0.46D、Unity 2021.3.45f2、MelonLoader 0.7.3、Windows .NET 6 IL2CPP 环境。使用 CrossOver 安装时，构建路径填写容器在 macOS 上的实际磁盘路径。

已完成源码、独立逻辑测试、构建与部署配置。游戏中的回调顺序、背包入口、保存恢复与画面显示需要实际游玩验收；测试和编译通过不能代替游戏内验证。详见 [开发与安装说明](ProbablyStolenStockMarket/README.md) 和 [适配证据与验收步骤](ProbablyStolenStockMarket/Game/ADAPTER-NOTES.md)。

第 41～42 日 K 线、批发成交漏记及交易门控的检查结果见 [行情诊断](docs/PRICE-DIAGNOSIS.md)。

0.12.0 的 20 个企业事件、基础冲击与验收步骤见 [股市专用事件](docs/STOCK-EVENTS.md)；0.11.0 原版事件的方向与覆盖审查保存在 [事件影响审查](docs/EVENT-IMPACT-AUDIT.md)。

## 玩法

- 商品成交按分类权重分配到多家公司，多分类取平均；玩家卖出利好，买入利空，当日买卖先抵消，次日统一结算前一天的成交信号。成交和遣散顾客都不即时变价。
- 每日小幅波动与估值回归叠加昨日成交量影响，默认非事件变动合计不超过 ±3%，其中成交影响上限为 ±2%。事件期内随机一天大幅跳价，此前可能小幅预热，之后小幅延续，结束后逐步消退并随机保留 20%～50% 的实际影响；同一事件可让不同公司出现相反走势。
- 未显式配置的事件根据原版商品价格修正反向推导股价：商品涨价对应相关公司利空，降价对应利好，再按商品关联权重分配并限幅。例如供水危机提高水和净水物资售价，将压低相关股票；自定义事件影响表仍优先。
- 新增 20 个股市专用企业事件，覆盖十家公司的利好和利空，包含订单、认证、召回与生产事故。每 2～4 日安排一条，提前排定未来 7 日；同模板冷却 14 日，强度随机为基础幅度的 80%～120%。事件直接按经营因果设置公司影响，可产生涨跌分化，不修改原版商品价格或事件队列。
- 专用事件持续 2～4 日，主冲击日随机且与公开日一致；此前仅有匿名预热。原版与专用事件主冲击共用同公司每日 ±15% 限额，实际份额随档保存，用于后续恢复。
- 使用原版资金买卖整数股，显示持仓、成本、盈亏、手续费和流水。
- 原生界面包含公司列表、7/14/30 日 K 线和委托；公司列表独立滚动，图表使用剩余高度，买卖操作区固定显示。每日开高低收随存档保存，旧档未知部分明确标注。
- 公司介绍以独立弹窗展示主营商品、交叉业务与行业联系的背景故事，替代商品影响百分比明细，不占用 K 线和委托区域。
- 日 K 线支持鼠标悬停，查看当天开高低收。
- “市场新闻”只展示已公开专用事件及其阶段，公司介绍同步显示相关新闻；未公开日程不进入普通界面。
- 持有股市面板后，在原版电话簿点击“行情消息专线”快速拨号，或手动拨打 **731206**；弹窗确认后随机安排闻舟或剑清酱次日营业时到访。两人初次见面分别自我介绍，随后在原版情报购买界面交易。付款前只透露来源和商品类别，购后提供行业型或指定公司型预测、原生纸条及次日起三日的日历提醒。
- 真假内幕按事件实际影响方向验证，行业身份与历史记录影响来源信誉；取消或无法核实的事件不损害信誉。股市面板的“内幕档案”保留已购内容。
- 专用事件内幕的未来三日窗口以主冲击／公开日为准。纸条、档案和日历保留原始预测，核验后追加公开事实及方向对照；已发纸条重新阅读时同步最新结果。
- 电话预约需要有可售线索，取消或挂断确认窗口不会创建新预约。报价、首次见面状态与来访计划随存档保存，重复拨号不重抽；“内幕档案”可查看号码与预约状态。
- 新增“剑清酱”，与闻舟共用 **731206** 电话随机来访，分别保存预约、来访及信誉。她的消息默认 70% 概率逐家公司与真实事件影响反向、30% 概率一致，历史信誉不改变这个比例；两人都有独立像素立绘，闻舟以兜帽和面罩隐藏面部。
- 独立透明像素风终端图标，显式刷新背包与拖拽图像；双击使用游戏原生窗口、按钮、输入框和字体素材。保存时仍使用原版便笺资源标识。

人物资源预览：[剑清酱](ProbablyStolenStockMarket/Assets/Textures/Characters/jianqing.png)、[闻舟](ProbablyStolenStockMarket/Assets/Textures/Characters/wenzhou.png)。剑清酱的面部采用减少细节后的粗像素版本。

## 开发

1. 安装 MelonLoader 并运行游戏一次，生成对应版本的 `MelonLoader/Il2CppAssemblies`。
2. 复制 `ProbablyStolenStockMarket/Game.local.props.example` 为 `Game.local.props`，填写 `GameDirectory`。
3. 执行 `dotnet build ProbablyStolenStockMarket/ProbablyStolenStockMarket.csproj -c Release`。构建成功后自动部署 DLL 和资源；只编译使用 `-p:DeployOnBuild=false`。

模组目标框架为 .NET 6；独立测试使用 .NET 10 SDK，命令为 `dotnet run --project Tests/StockMarket.Tests`。测试不加载游戏，也不需要游戏 DLL。

运行配置首次生成在 `<游戏>/UserData/ProbablyStolenStockMarket/market-config.json`。修改后重启游戏生效；非法配置会停止股市初始化并保留原文件。已有配置不会被构建覆盖。

0.12.1 将项目目录、程序集和资源目录统一命名为 `ProbablyStolenStockMarket`。部署成功后，旧 `MelonModTemplate.dll` 及符号文件移入新 UserData 目录的 `LegacyBackup`，避免同时加载两份模组。首次运行优先使用新配置；仅存在旧配置时，验证后原样复制到新目录，资源也支持从旧目录读取。存档标识、持仓、内幕与 NPC 标识保持稳定。

0.10.0 为自动事件规则增加来源和方向版本。能确认来源的旧事件若尚未大跳，只更新未来影响方向，保留已发生的预热、随机日期及实际价格；已执行大跳的事件继续原有生命周期。方向调整涉及的旧待核实内幕标为“未能验证”，不扣来源信誉。仍与旧版出厂配置完全一致的动员事件规则会在加载时升级，用户修改过的显式规则保留。

## 来源

开发流程参考 [dragonpan2/probably-stolen-modding](https://github.com/dragonpan2/probably-stolen-modding) 的 LearnerMod。当前游戏的原生 ModLoader 处于受限状态，因此实现使用 MelonLoader 与 Harmony；不依赖较新原生 ModHelper API。加载器、游戏及生成代理程序集不包含在仓库中。
