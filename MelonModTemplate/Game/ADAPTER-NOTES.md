# 游戏适配证据与验收

当前编译目标是 Probably Stolen Demo 0.46D / Unity 2021.3.45f2 / MelonLoader 0.7.3 生成的 IL2CPP 代理。检查了代理与 Cpp2IL dummy 元数据；dummy 方法体为空，不能据此声称原生游戏逻辑已经验证。游戏启动和实际游玩由用户完成。

## 已确认的元数据和存档结构

- `PlayerStore.modData` 是带 ES3Serializable 标记的字典。资金入口为 `GetCash` / `ModCash`，修改后核对实际余额。
- `PlayerStore.BuyItem`、`SellItem`、`BuyItemFromTable`、`SellItemFromTable`、`OnItemsSold` 存在；只在这些方法的调用范围内，以真实现金变化确认成交方向。嵌套调用只记一次。`PlayerStore.OnItemBought` 和 `StoreClient.OnItemBought/OnItemSold` 输出金额探针；顾客回调金额与现金总变化不符时不计行情，避免纳入奖励或退款。
- `DismissCurrentClient` 存在；同步移除立即结算，异步移除则在顾客实例指针实际变化后结算。空顾客和同类顾客连续来访均可切换会话。
- `StoreClientInstance.GetClientBlueprint()` 提供顾客。当前能确认 `identifier`、`clientFaction`、`tag`、`clientBuyingTagList`；没有确证跨存档稳定的个人身份，信誉明确按角色类型与阵营累计。
- `StoreEventManager.GetActiveEvents/ActivateEvent/futurEvents/futurEventsDay` 存在。实际存档显示 `futurEventsDay` 为剩余天数：当前第 41 日时，队列偏移 1/8/2/3 对应事件 startDay 42/49/43/44。适配器优先使用事件 startDay，否则使用当前日加偏移。
- 事件去重键为标识和 startDay；载入后同步已生效事件。隐藏事件可影响行情，公开界面仅显示市场异动。
- 实际存档中的原版 `postit` 保存 uniqueId、名称和标签。入口使用原生 postit，名称为“股市面板”，通过当前存档 envelope 中的唯一编号加原生标识识别，普通便笺不会被拦截。
- 独立道具图片从 `Assets/Textures/Items/stock_market_terminal.png` 载入。当前代理的 `RenderHandler.atlasCache` 确认是图集到 Sprite 字典的缓存，`GameItem.SetSprite` 与 `SetSpriteAndShape` 是不同方法。适配器通过独立图集键注册图片、按原版便笺精灵尺寸缩放并保留原版形状；载入存档后重新注册。图片失败只记录警告，入口功能继续使用便笺图像。
- PNG 在运行时以 Point 过滤等比缩放，剩余位置保持透明。原版 SaveGame 前恢复便笺图集字段，保存返回或异常后再恢复专用图像，使原版物品存档记录 native postit 图集；下次载入或卸载 Mod 时不依赖尚未注册的自定义图集。
- 0.1.0 实机日志确认 `PlayerStore.gridInv` 为空，不能用于玩家背包发放。0.1.1 改用 `EmporiumEntry.invElement`，要求 `invWindow.childElement` 指针一致且尺寸有效。插入必须同时确认 parentInventory 与直接 childItems；成功或结果不确定后锁定本次会话，不因空查询再次创建。完整扫描优先采用已知编号和旧版同名 postit；记录已有道具但查不到时停止自动补发，用户可按 Ctrl+F8 显式恢复。主背包实际显示仍待新版实机确认。

## 原版存档与交易解锁

全部状态写入 `modData["probably_stolen.stock_market.v1"]`，没有独立资金或持仓文件。Envelope 包含 Market JSON、存档身份、顾客会话、道具编号、nonce、原版保存现金及保存标记。

1. 首次附加存档创建 nonce，保持现金交易关闭。
2. 正常 `SaveGame` 前同步行情状态与现金快照，调用无异常返回时打印保存完成探针；保存异常不认可本次操作。
3. `LoadGame` 前清理旧引擎并记录旧字典指针；原版载入成功后延迟至游戏对象就绪，读取 envelope。
4. 只有存档身份一致、nonce 存在、保存标记存在、现金快照一致，且读到新的字典对象（或进程首次载入没有旧字典）才开放现金交易。单纯在内存写入后读取不能解锁。

如果当前游戏版本在同一对象中原地恢复字典，严格的指针检查会保持交易关闭；日志会显示读回验证 false，应先核对真实保存结构再调整验证方式。不要通过手改 StorageVerified 绕过。

## 用户实际验收步骤

1. 进入测试存档，确认 `[股市背包探针]` 指向 EmporiumEntry.invElement，`[股市道具]` 记录“主背包直接成员已确认”；确认背包出现道具且双击打开。等待至少 30 秒，不应出现不同编号的重复赠送。旧记录找不到道具时按 Ctrl+F8。
2. 首次面板应提示保存并读档，按钮不能花费现金。正常保存，返回菜单再载入，检查 nonce、现金一致以及“读回验证=True”。入口编号应保持一致。
3. 先售出一件商品，检查成功回调、商品类型、实际现金变化和分配金额；遣散后确认相关公司涨价。买入同类商品后遣散应跌价。取消交易和无成交遣散不应产生商品影响。
4. 检查批量成交总金额与现金变化一致、嵌套方法没有重复记账。出现“回调金额合计不一致”时保留日志，该交易暂不计行情。
5. 面板打开时确认原生拖拽、商品买卖、遣散、开门、下一位顾客和结束一天不会被点击穿透触发。关闭后恢复。原版更深层对话快捷键仍需实机检查。
6. 测试余额不足、买卖整数股、手续费、连续点击；保存读档后现金、持仓、成本和流水一致。
7. 购买有报价顾客的内幕，核对购买前只显示身份和报价，购后才出现内容；保存读档不刷新报价或真假。事件发生后信誉只更新一次。
8. 等待映射事件生效，检查 `[股市事件]`；再次读档不重复跳价。隐藏事件不能在行情历史或流水泄露标识或描述。
9. 背包满、道具丢失、不同存档切换、正常退回菜单均需要检查。入口禁止出售、普通消耗与拖出；普通 postit 保持原版行为。

适配补丁仅由本模组 Harmony ID 安装和解绑。订单及情报费用使用独立货币操作标记，不会纳入商品信号。异常会写明原因并冻结交易，不会将打印成功日志等同于实际验收通过。

## 0.1.1 图像及界面兼容修复

当前生成代理中 Graphics.DrawTextureImpl 直接抛出 Method unstripping failed。图标改为已核实的原生 LoadImage/GetPixels32/SetPixels32/Apply/Sprite.Create 调用，加纯 C# 最近邻采样；不使用 Graphics、GL、RenderTexture 绘制路径。图片失败会记录阶段和完整异常，本次运行不重复分配失败资源。

面板滚动、输入、柱图与字体创建也需要避开代理中的裁剪占位方法；具体替换见 [界面适配说明](../UI/ADAPTER-NOTES.md)。补领热键使用已核实的 Event.current/control/keyCode 绑定，只补领物品，不改变通过背包双击打开的主交互。
