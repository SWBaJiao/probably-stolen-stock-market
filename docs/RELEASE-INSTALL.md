# Probably Stolen 股市 Mod 安装说明

0.16.0 适用于 Probably Stolen 正式版的 Windows x64 Mono 版本，使用游戏内置模组加载器。接口迁移依据为本机 Steam Build 26284217。安装或更新前退出游戏，保留现有存档与股市配置备份。

## 安装或更新

1. 在 Steam 库右键正式版游戏，选择“管理 → 浏览本地文件”。CrossOver 用户进入对应容器的 Steam 游戏目录，目标文件夹为 `Probably Stolen`。
2. 将发布包中的 `Mods` 文件夹合并复制到游戏安装目录，允许覆盖本 Mod 的同名 DLL、清单和配套资源。
3. 确认存在 `Mods/ProbablyStolenStockMarket/manifest.xml`、`Mods/ProbablyStolenStockMarket/ProbablyStolenStockMarket.dll` 和该目录下的 `Assets/`，不要额外套一层压缩包名称目录。
4. 启动游戏，在“模组”菜单选择 `Probably Stolen Stock Market` 并启用。游戏会按清单扫描 DLL；如果显示能力确认，核对模组来源后继续启用。
5. 重启游戏后进入存档。在 F8 控制台查看以模组名和“股市”开头的日志，验证道具、交易、内幕和贷款流程。

原生版本不需要 MelonLoader、Windows .NET 6 安装包或 IL2CPP 代理。Demo 中旧版 Mod 的安装目录独立保留；不要将这个发布包复制到 `Probably Stolen Demo`，也不要将旧 Melon DLL 合并进正式版原生模组目录。

发布包仅包含本 Mod DLL、原生清单、配套资源、安装说明和校验清单。不包含个人存档、本地游戏路径、用户运行配置、游戏程序集、框架依赖、图片生成提示或构建调试符号。首次正式版加载、回调时序、UI 与保存恢复仍需实际游玩验收。

0.16.0 新增“负债店主”开局，在创建新存档的职业／开局选择页中选择。初始现金 1500 元、固定欠款 5000 元，跳过部分基础教程，有完整 28 个游戏日还清。电话 731207 开局解锁；到期日结在普通服务付费后自动结清剩余旧债，不足时抵债停业。已有存档不会自动增加该债务。

## 游戏语言

股市面板在第 3 天营业时由证券开户专员林澈来访办理，支付 50 元开户后发放，可选择三天后再来。面板按背包、桌面、窗外的顺序交付；窗外的面板请在保存或结束营业前收好，该区域不随存档保存且会在睡眠时清空。已开户后丢失可按 `Ctrl+F8` 免费补领，补领前扫描现有面板；旧存档已有面板时保留开户资格。

股市情报名片会在持有股市面板后自动放入主物品栏，背包满时延后，已有存档也只补发一次。双击可查看预约电话 731206 与来访说明。

界面、公司介绍、事件消息、内幕对话、纸条、档案、日历与贷款内容支持中文、日文、英文和韩文，跟随游戏当前语言。翻译资源内置于 Mod DLL。自行修改配置中的文字保留原文。

## 保留配置和旧存档

运行配置使用游戏持久化目录下的 `ModData/probably_stolen_stock_market/market-config.json`，启动日志会显示完整路径。包中的 `Assets/market-config.default.json` 是默认资源，不会覆盖已有运行配置。需要沿用 Demo 的自定义配置时，退出游戏后将旧 `UserData/ProbablyStolenStockMarket/market-config.json`（更早版本为 `UserData/MelonModTemplate/market-config.json`）复制到新配置路径，覆盖前保留原文件。

源码仓库另提供配置迁移工具，明确填写来源和目标文件。以下命令在源码仓库根目录执行。先加 `--dry-run` 预检查，确认路径后移除该选项执行；工具只做 JSON 基本结构检查和原样复制，已有不同配置时拒绝覆盖，不扫描用户目录。

```sh
python3 ProbablyStolenStockMarket/tools/migrate_config.py \
  --source "/旧游戏目录/UserData/ProbablyStolenStockMarket/market-config.json" \
  --destination "/正式版持久化目录/ModData/probably_stolen_stock_market/market-config.json" \
  --dry-run
```

股票持仓、内幕、信誉、来访计划、贷款和行情继续使用原有存档键 `probably_stolen.stock_market.v1`。原生迁移保留该键和保存结构；加载器变化不要求清空投资状态。游戏本身对 Demo 存档的兼容性与 Mod 状态恢复需要在正式版分别验证。

## 校验下载文件

Release 同时提供 `.zip.sha256` 校验文件。macOS 可在下载目录运行：

```sh
shasum -a 256 -c ProbablyStolenStockMarket-v0.16.0.zip.sha256
```

ZIP 内的 `SHA256SUMS` 记录安装文件和本说明的 SHA-256，不包含其自身。解压后可在解压根目录执行 `shasum -a 256 -c SHA256SUMS`。

## 从源码重新打包

在仓库根目录完成 Release 编译后执行：

```sh
python3 ProbablyStolenStockMarket/tools/package_release.py
```

脚本从 `ModInfo.cs` 读取版本，读取 `bin/Release/netstandard2.1/` 中的 DLL，将 ZIP 和 SHA-256 写入 `artifacts/`。可使用 `--version 0.16.0` 校验指定版本，或用 `--output-directory /目标目录` 修改输出位置。脚本校验实际 DLL 常量、清单版本、必需资源、ZIP 内容、CRC 与 SHA-256，不自动编译或修改游戏。

`Mods/ProbablyStolenStockMarket/` 就是创意工坊的内容根目录，包含本 Mod 所需完整文件；本次迁移未执行工坊上传。上传和后续更新使用 SteamCMD，保留返回的 `publishedfileid`。订阅者下载后仍需在游戏“模组”菜单启用并重启。开发时同 ID 的本地目录优先于工坊副本，验收订阅版本时需先移走本地副本。
