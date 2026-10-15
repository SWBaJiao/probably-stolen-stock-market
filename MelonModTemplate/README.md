# 股市 Mod 开发与使用

程序集保持 `MelonModTemplate.dll`，模组显示名称为 `Probably Stolen Stock Market`，版本 `0.1.1`。覆盖旧模板 DLL 后只会加载一个入口；无需同时保留旧模板。

## 安装与构建

目标环境为本机已生成代理的 Demo 0.46D / Unity 2021.3.45f2 / MelonLoader 0.7.3 / Windows x64 .NET 6.0.36。其他版本需要重新核对代理接口。

复制 `Game.local.props.example` 为 `Game.local.props` 并填写游戏目录。CrossOver 下填写 macOS 可访问的容器目录，不填写 Windows 的 C 盘路径。本地配置、生成 DLL、缓存和游戏代理均不提交到 Git。

```sh
dotnet build MelonModTemplate/MelonModTemplate.csproj -c Release
```

默认启用 `UseGameInterop=true`，引用游戏生成的代理以及加载器提供的 Harmony/Il2CppInterop，引用不随模组复制。输出自动部署为：

```text
<游戏>/Mods/MelonModTemplate.dll
<游戏>/UserData/MelonModTemplate/Assets/market-config.default.json
<游戏>/UserData/MelonModTemplate/Assets/Textures/Items/stock_market_terminal.png
```

只构建加 `-p:DeployOnBuild=false`；复制调试符号加 `-p:DeployDebugSymbols=true`。构建不会启动游戏。

## 第一次使用

1. 进入存档，等待背包出现“股市面板”；背包满时腾出空间，系统每 10 秒重试。
2. 双击道具打开面板。旧版记录了道具却找不到时，在游戏中按 `Ctrl+F8` 显式补领；会优先采用已有道具，不删除便笺。初次只允许查看，按原版流程保存，再返回菜单重新载入。
3. 日志出现 `[股市探针] ... 读回验证=True` 后，才能买卖股票或购买内幕。保存状态、现金快照和存档身份不一致时保持锁定。
4. 卖出或买入商品，然后遣散顾客，观察对应公司。股票操作和情报费不会计为商品交易。

窗口关闭可点击“关闭”或按 Escape。`Ctrl+F8` 只请求补领道具，不直接打开交易窗口。面板显示期间阻断主要原版买卖、拖拽及顾客操作，关闭后恢复鼠标状态。最终输入穿透情况仍需游戏内验证。

面板采用原生 Group 裁剪、自绘按钮、数字键输入和样式柱图，避开本版本中未还原的 ScrollView、TextField 和 DrawTexture 方法。点击股数字段后输入数字，支持退格及全选；滚动使用鼠标滚轮或右侧上下按钮。

中文字体优先选择微软雅黑、思源黑体、宋体等系统字体；当前 CrossOver 容器已有这些字体。也可放置 `Assets/Fonts/StockMarket.ttf` 作为独立字体。找不到中文字体时日志会提示具体路径。

## 公司和商品

默认 10 家公司：丰禾供给、清泉循环、白塔医疗、星锻工业、远星科技、环宇商贸、霆光能源、晨星物流、安盾防务、聚合材料。

商品只按真实类型映射，不通过显示名称猜测。已核实类型包括 FOOD、WATER、MEDICAL、MACHINE、MATERIAL、BASIC_MATERIAL、MODULE、ENERGY_CELL、CHEMICAL_SUPPLIES、LUXURY_ITEM、WATER_PURIFICATION_SUPPLY、FILTER、WEAPON。空瓶如果没有 WATER 类型，不会自动算作饮用水。

同一分类内多个标签只算一次；命中多个分类时平均各行权重，总影响不随标签数量增加。成交以真实资金变化及调用范围确认，嵌套回调去重；无法确认的成交记录探针并跳过。

默认日基础变化上限为 3%，单次顾客对单家公司最多影响 2%，每日零售累计绝对影响上限 8%。股票初始价 100，手续费 0.5%，最低 1 游戏币；买入支出向上取整，卖出净收入向下取整。

## 事件与内幕

显式事件规则优先。默认已核实并配置 `badNutrifruitHarvest`、`bm_illegal_weapon_shipment`、`rev_mobilization_secret`。未知事件如果带真实 `negociationDatas.itemType/value`，会按商品价格冲击与公司交叉权重生成影响：价格修正乘 `StructuredEventScale`，单家公司限制在 `StructuredEventLimit` 内。它是商品售价冲击对公司行情的玩法近似，具体企业的受益或受损关系应使用显式规则覆盖。没有结构化价格数据的未知事件不猜测影响。

内幕只从未来 3 个游戏日内有规则的关联事件生成。当前没有可确认稳定的个人身份，因此信誉按角色类型和阵营累计，并在界面标注。行业从业者、情报商、普通顾客与可疑来源具有不同初始可信度，历史验证记录会更新信誉。

真假内容使用相同句式和相反的供需线索，不直接暴露内部事件标识或实际发生日期。一条情报可关联多家公司，只更新一次来源信誉。事件取消或到期未发生记为“未能验证”，不扣信誉。报价与随机状态随存档保存，重复交谈或读档不重抽。

## 配置与保存

首次启动从打包的默认配置创建 `<游戏>/UserData/MelonModTemplate/market-config.json`。可以修改公司、类型别名、交叉权重、事件影响、身份规则、手续费与上限。公司 ID 需要稳定，不要删除仍有持仓的公司；非法配置会停止初始化，保留原文件。

全部投资状态和原版资金共同保存在 `PlayerStore.modData["probably_stolen.stock_market.v1"]`。不使用独立持仓文件。交易失败会补偿现金和持仓；补偿或重写旧状态失败时冻结交易。

股市入口使用原生 postit 和存档内唯一编号识别，普通便笺不受影响。发放入口为 `EmporiumEntry.invElement`，需要确认主背包窗口实际绑定，并在插入后核对父容器和直接成员。成功后锁定本次会话，空查询不再触发自动造物；旧编号缺失时使用 `Ctrl+F8` 显式恢复。独立 PNG 通过原生解码、CPU 最近邻缩放和原生纹理上传注册图集，保留透明度及原版占位。保存前还原原版资源路径，保存后重新应用图标。缺图时回退便笺图像。

## 验证与代码结构

```sh
dotnet run --project Tests/StockMarket.Tests
dotnet run --project Tests/TerminalGraphics.Tests
dotnet run --project Tests/TerminalGrant.Tests
python3 MelonModTemplate/tools/verify_deployment.py
```

独立测试使用 .NET 10 SDK，包含 60 项断言，覆盖交叉分配、行情去重、资金回滚、真假内幕、信誉、隐藏信息、存档隔离和事件推导。另有 10 个图像采样场景和 33 项道具发放断言，覆盖透明缩放、满背包、重复查询和不确定插入。部署验证使用临时目录，不写入实际游戏。

`Market/` 为不依赖游戏的领域逻辑；`Game/` 负责原版接口、Harmony 补丁和存档；`UI/` 为 IMGUI 面板；`Contracts/` 限定界面命令入口。`ModEntry` 成对订阅与解绑，并只清理本模组拥有的补丁和图像资源。

进一步的静态证据、适配限制和实机步骤见 [ADAPTER-NOTES.md](Game/ADAPTER-NOTES.md)。游戏启动和实际游玩由使用者验证；编译成功不代表原生调用顺序和存档持久化已验收。
