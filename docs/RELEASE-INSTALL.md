# Probably Stolen 股市 Mod 安装说明

适用于 Probably Stolen 的 MelonLoader IL2CPP 版本。本项目当前适配 Probably Stolen Demo 0.46D、MelonLoader 0.7.3；其他版本需要自行验证。安装和更新前，请完全退出游戏，并备份游戏存档及已有股市配置。

## 安装或更新

1. 先在游戏所在的 CrossOver 容器中正确安装 MelonLoader。发布包不包含游戏文件、MelonLoader 或其依赖。
2. 打开含有游戏 `.exe`、`MelonLoader` 和 `Mods` 的游戏安装目录。CrossOver 用户可通过容器的“打开 C: 盘”进入 Steam 游戏安装目录。
3. 若曾安装旧模板版本，将 `Mods/MelonModTemplate.dll` 及同名 `.pdb` 移出 `Mods`，备份到其他目录。不要同时保留旧 DLL 和新 DLL，以免重复加载。
4. 将压缩包中的 `Mods` 与 `UserData` 两个文件夹合并复制到游戏安装目录。允许覆盖同名 Mod DLL 和随包资源；保留其他 Mod 文件。
5. 游戏目录中应存在 `Mods/ProbablyStolenStockMarket.dll` 和 `UserData/ProbablyStolenStockMarket/Assets/`。不要额外套一层压缩包名称目录。
6. 启动游戏，在 MelonLoader 日志中确认 `Probably Stolen Stock Market` 已加载。游戏内功能与存档恢复请实际验收。

发布包仅包含 Mod DLL、版本控制中的配套资源、安装说明和校验清单。不包含个人存档、本地游戏路径、用户运行配置、游戏程序集、原始人物参考照片或构建调试符号。

## 游戏语言

Mod 界面、公司介绍、事件消息、内幕对话、纸条、档案与日历内容支持中文、日文、英文和韩文，并跟随游戏当前语言，无需单独安装语言包。翻译资源内置于 Mod DLL。自行修改配置中的文字不会自动机器翻译，请根据需要自行编写相应内容。

## 保留配置和旧存档

新配置路径为 `UserData/ProbablyStolenStockMarket/market-config.json`。发布包中的 `Assets/market-config.default.json` 是默认配置资源，请不要用它覆盖自己的运行配置。

如果新路径还没有运行配置，Mod 会验证并复制旧路径 `UserData/MelonModTemplate/market-config.json`，旧文件予以保留。新配置已存在时优先使用新文件。也可以在退出游戏后手动备份旧 `UserData/MelonModTemplate`，将其重命名为 `ProbablyStolenStockMarket`；目标目录已存在时请先比较内容，避免覆盖自己的新配置。

股票持仓、内幕、信誉、来访计划和行情仍使用原有存档标识。项目文件夹与程序集改名不要求新建游戏存档。升级前的存档备份请保留至实机验证结束。

## 校验下载文件

Release 同时提供 `.zip.sha256` 校验文件。macOS 可在下载目录运行：

```sh
shasum -a 256 -c ProbablyStolenStockMarket-v0.13.0.zip.sha256
```

后续版本请将命令中的文件名替换为实际下载名称。ZIP 内的 `SHA256SUMS` 记录每个安装文件和本说明的 SHA-256，不包含其自身。解压后也可在解压根目录执行 `shasum -a 256 -c SHA256SUMS`。

## 从源码重新打包

在仓库根目录完成 Release 编译后执行：

```sh
python3 ProbablyStolenStockMarket/tools/package_release.py
```

脚本默认从 `ModInfo.cs` 读取版本，将 ZIP 和外部 SHA-256 校验文件写入 `artifacts/`。可用 `--version 0.13.0` 明确校验版本，或使用 `--output-directory /目标目录` 修改输出位置。打包会直接读取 DLL 中的 `ModInfo.Version` 常量，版本与源码不一致时拒绝发布，避免将更新前的 DLL 打入新版本压缩包。脚本同时检查资源清单、ZIP 内容及 CRC，不会自动编译或修改游戏安装目录。
