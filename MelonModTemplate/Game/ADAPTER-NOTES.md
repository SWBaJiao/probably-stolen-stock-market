# 游戏适配证据与验收

当前编译目标是 Probably Stolen Demo 0.46D / Unity 2021.3.45f2 / MelonLoader 0.7.3 生成的 IL2CPP 代理。检查了代理与 Cpp2IL dummy 元数据；dummy 方法体为空，不能据此声称原生游戏逻辑已经验证。游戏启动和实际游玩由用户完成。

## 已确认的元数据和存档结构

- `PlayerStore.modData` 是带 ES3Serializable 标记的字典。资金入口为 `GetCash` / `ModCash`，修改后核对实际余额。
- `PlayerStore.BuyItem`、`SellItem`、`BuyItemFromTable`、`SellItemFromTable`、`OnItemsSold` 存在；在这些方法和已核实的 `NegociationUIManager.OnWholesaleClick` 范围内，以真实现金变化确认成交方向。嵌套调用只记一次。`PlayerStore.OnItemBought` 和 `StoreClient.OnItemBought/OnItemSold` 输出金额探针；普通成交要求回调与现金一致，批发还核对原生整单报价，单独处理逐件折扣取整差。
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

默认开放股票现货买卖；`RequireSaveReloadVerification=true` 可启用以下严格门控。内幕付款继续要求实际保存重读验证，交易失败冻结及资金回滚规则始终有效。

1. 首次附加存档创建 nonce；严格模式及未验证的内幕付款保持关闭。
2. 正常 `SaveGame` 前同步行情状态与现金快照，调用无异常返回时打印保存完成探针；保存异常不认可本次操作。
3. `LoadGame` 前清理旧引擎并记录旧字典指针；原版载入成功后延迟至游戏对象就绪，读取 envelope。
4. 只有存档身份一致、nonce 存在、保存标记存在、现金快照一致，且读到新的字典对象（或进程首次载入没有旧字典）才开放现金交易。单纯在内存写入后读取不能解锁。

如果当前游戏版本在同一对象中原地恢复字典，严格的指针检查会保持严格模式及内幕付款关闭；日志会显示读回验证 false，应先核对真实保存结构再调整验证方式。不要通过手改 StorageVerified 绕过。默认股票买卖权限由本机配置重新决定，不依赖存档中残留的旧 false。

## 用户实际验收步骤

1. 进入测试存档，确认 `[股市背包探针]` 指向 EmporiumEntry.invElement，`[股市道具]` 记录“主背包直接成员已确认”；确认背包出现道具且双击打开。等待至少 30 秒，不应出现不同编号的重复赠送。旧记录找不到道具时按 Ctrl+F8。
2. 默认股票买卖可直接使用；另以严格模式测试首次不能花费现金。正常保存，返回菜单再载入，检查 nonce、现金一致以及“读回验证=True”。入口编号应保持一致，内幕付款此时也应可用。
3. 先售出一件商品，检查成功回调、商品类型、实际现金变化和分配金额；遣散后确认相关公司涨价。买入同类商品后遣散应跌价。取消交易和无成交遣散不应产生商品影响。
4. 检查批量成交总金额与现金变化一致、嵌套方法没有重复记账。出现“回调金额合计不一致”时保留日志，该交易暂不计行情。
5. 面板打开时确认原生拖拽、商品买卖、遣散、开门、下一位顾客和结束一天不会被点击穿透触发。关闭后恢复。原版更深层对话快捷键仍需实机检查。
6. 测试余额不足、买卖整数股、手续费、连续点击；保存读档后现金、持仓、成本和流水一致。
7. 持有股市面板后，用原版电话手动拨打 731206，预约次日到访；核对购买前只显示来源、商品类别和报价，购后才出现预测。保存读档不刷新报价或真假，事件发生后信誉只更新一次。当前流程见下方 0.6.0 验收，旧版章节保留适配证据。
8. 等待映射事件生效，检查 `[股市事件]`；再次读档不重复跳价。隐藏事件不能在行情历史或流水泄露标识或描述。
9. 背包满、道具丢失、不同存档切换、正常退回菜单均需要检查。入口禁止出售、普通消耗与拖出；普通 postit 保持原版行为。

适配补丁仅由本模组 Harmony ID 安装和解绑。订单及情报费用使用独立货币操作标记，不会纳入商品信号。异常会写明原因并冻结交易，不会将打印成功日志等同于实际验收通过。

## 0.1.1 图像及界面兼容修复

当前生成代理中 Graphics.DrawTextureImpl 直接抛出 Method unstripping failed。图标改为已核实的原生 LoadImage/GetPixels32/SetPixels32/Apply/Sprite.Create 调用，加纯 C# 最近邻采样；不使用 Graphics、GL、RenderTexture 绘制路径。图片失败会记录阶段和完整异常，本次运行不重复分配失败资源。

面板滚动、输入、柱图与字体创建也需要避开代理中的裁剪占位方法；具体替换见 [界面适配说明](../UI/ADAPTER-NOTES.md)。补领热键使用已核实的 Event.current/control/keyCode 绑定，只补领物品，不改变通过背包双击打开的主交互。

## 0.2.0 真实图像绑定与原生窗口

0.1.1 实机日志确认 PNG 解码和 32×32 缓存创建成功，但用户截图仍显示便笺，因此“资产已载入”不等于背包图像已绑定。对当前 GameAssembly.dll 的静态反汇编显示：SetSprite（RVA 0x79BDB0）仅修改路径与 spriteChanged；ValidatePartialSprite（RVA 0x78E510）和 ApplyAnimationFrame（RVA 0x788A40）才更新真实及拖拽 Image。

新实现会显式调用图像刷新、设置实际 overrideSprite，并比较两个 Image.sprite/overrideSprite 的指针。只有读回与生成精灵相同时才打印 stage=renderer readback；该日志仍不代替视觉截图。专用 ResolveSpriteByName 补丁只处理当前股市道具的独立路径，保存及切档时停用，关闭时移除。

双击界面改用本版本实际提供的 CustomUIManager/CustomUIBuilder 与游戏预制件，详见 [原生界面说明](../UI/NATIVE-NOTES.md)。需要用户重启后确认图标、窗口素材、中文、页面切换、数量输入、原版资金门控和关闭恢复输入。

## 0.5.0 掮客、纸条与日历

以下为当前 Demo 的元数据和 GameAssembly 静态调用链证据，尚未代替实际游戏验收：

- `StoreClientListDict.storeClientDict` 提供独立工厂。专属 `probably_stolen_stock.intel_broker` 创建新 StoreClient，使用原版黑市人物素材和 INFORMATION_DEALER 意图；不复用原版情报商内容生成器。
- `StoreClientManager.AddClient`（RVA 0x680980）追加队尾，`AddNextClient`（0x680A40）插入队首；此处仅使用前者。`TrackClient`（0x689990）写原版访问统计，因此仅为专属 ID 跳过。来访计划保存在 modData，不写原版未来顾客 ID 队列。
- 原版情报介绍末句调用 `OnIntroduced` 并打开 `NegociationUIManager.OpenUI`。专属介绍使用相同流程，原版 UI 初始化后替换股市措辞、来源、类别、价格与按钮状态。
- 原版 `OnOfferAcceptClick`（0x723B50）的情报分支会直接扣钱，专属 NPC 的 prefix 始终阻止原方法，统一调用股市事务；失败也不能回落原版扣款。`CompleteBusiness`（0x68ADB0）仅更新完成状态，不能当成交付回调。
- 原版情报生成回调会创建纸条并调用 `QueueFuturEvent` 安排真实事件；本 Mod 不调用这部分。购买后只根据已经生成的已购记录播放预测，再由末句、遣散和恢复轮询调用同一幂等交付入口。
- 原生 `handnote` 使用 `HANDNOTE_TITLE/BODY/SIGN`，保留默认字体，经 `HandnoteUIManager.OpenPanel` 阅读。`GameItem.GetTagReadonly` 可读回标签。原版 `AddDirectSellingItemToTable`（0x586710）在 0x586932 访问 `EmporiumEntry.frontInvinvElement`；交付前检查柜台绑定和空间，交付后检查真实父容器与直接成员。
- 纸条插入前保存精确编号与 Uncertain 状态，成功后保存 Delivered。确认未插入且临时物品已销毁才允许专用复位；结果不确定或保存失败时锁定再次造物。纸条仅含原版资源和文档标签。
- 日历仅在 `CalendarDayElement.SetElement` 的列表副本追加 Mod 显示用 StoreEvent，点击专属实例打开原生纸张查看已购安全内容。不进入 `activeEvents/futurEvents`，不更改真实事件的 IsKnown、日期或价格影响。

实机验收顺序：

1. 用已解锁资金的测试存档，观察有未来关联事件才在队尾出现掮客，日志显示“队尾追加”及“实际到访”；当天保存重载不重复排队，实际来访后的三日内不再安排下一次。确认原版情报商与剧情顾客仍正常出现。
2. 介绍后检查原版购买窗口标题“股市内幕交易”、专属股市措辞和“购买股市情报”按钮。付款前不显示公司名、方向、真实事件标识或日期；未验证存档、余额不足和失效报价均不扣钱。
3. 确认只扣一次情报费，连续点击不能重复成交，原版奖励/事件生成不运行。购后对话依次显示预测和交纸条末句；跳过对话、提前遣散后仍只交一张。
4. 柜台满时保持待交付，腾出空间后自动补交。检查 handnote 标题、正文、来源、可信度、购买日及观察窗口。付款后待交付、接待中和交付后分别保存重载，核对现金、记录、纸条编号和队列。
5. 购买日为 D 时，日历只在 D+1、D+2、D+3 出现相同样式的未核实提示；点击显示已购全文，纸条丢失后仍可查看。取消或核实后重新打开日历应看到最新状态，不暴露未购内容。
6. 真/假消息按关联事件实际影响方向核实，一条多公司记录只更新一次信誉；取消或无法验证不扣信誉。旧档内幕继续可读，不批量发新纸条。复制测试存档移除 Mod 后，原版应能正常读档并阅读原生纸条。

领域断言验证计划、隐藏信息、资金事务和状态机；以上原生回调时序、存档恢复与卸载兼容仍待用户实机确认。

0.5.1 在“内幕档案”增加“邀请内幕掮客”。营业时手动调用同一个队尾追加入口，可以绕过自动冷却；同日复用既定报价，已购、结束或失效后不能刷新。实际入队后读取同一 NPC 指针确认，重复点击扫描当前接待对象和队列，避免重复添加。手动计划标记随 modData 保存，读档后由原来恢复流程接续。验收时请分别检查营业前提示、无可售线索、冷却期间手动邀请、连续点击、排队中保存重载，以及接待中的再次邀请。

## 0.5.3 邀请后营业状态异常排查

用户反馈首次邀请提示需要开店，开店后再邀请出现人数空白、铃铛提示已打烊。当前日志没有工厂或入队记录，不能认定普通顾客已被邀请逻辑清除。原生 `StoreClientCounter.Update`（0x6BC240）在 OPEN 时直接显示 `clientStack.Count`，在 MORNING/CLOSED 时显示空字符串；`StoreBell` 同样按营业状态决定能否呼叫。`AddClient`（0x680980）直接追加队列，`TrackClient`（0x689990）仅写访问统计。

静态调用链确认卷帘 `EndAndClose`（0x6C0970）在调用 `PlayerStore.EndDay` 前已经清柜台、加锁并打断对话。此前只保护 EndDay，无法完整阻止上层副作用。0.5.3 在窗口可见时暂停原版 `InputActionManager.Update` 独立输入分发，并保护 `StoreShutterButton.OnClick/CloseShutter/EndAndClose` 和 `StoreBell.RingBell`。Unity EventSystem 继续处理本 Mod 的原生 UGUI 控件；关闭窗口后恢复原版输入，不修改店铺状态或顾客计数。

另确认 Unity 日志中有 406 次专用终端纹理不可读的 alphaHitTest 错误。终端纹理改为 `Apply(false,false)` 保留 CPU 像素，满足原版透明像素命中检测；它与营业状态变化的直接因果尚未证实。

新增 `[原生股市邀请]`、`[股市手动邀请]`、`[股市营业状态]`、`[股市输入隔离]` 日志，记录请求前后 state/queue 与关店方法是否被拦截。重启后重新载入测试存档，开店后邀请并关闭面板，确认人数和铃铛正常；实际故障恢复仍需用户实机验证。

## 批发成交补录入口

实机日志出现逐件 `PlayerStore.OnItemBought` 与 `StoreClient.OnItemSold`，但没有对应股市成交。静态扫描确认 `OnWholesaleClick`（RVA 0x724150）在 0x7242FB 先扣整单资金，随后在 0x724427 逐件调用 `OnItemBought`，绕过原有单件买入补丁。现在额外包裹整个批发方法，以调用前后现金差确认成功，沿用同一组逐件回调；不在底层另建第二笔成交。

`GetWholesalePrice`（0x58E810）对总价折扣取整，逐件回调分别折扣取整，两者可能存在最多每件一个单位的差。仅在明确批发范围内、实扣等于原生整单报价且差值在边界内时按比例分配实际现金，尾项承接剩余，保持总额守恒。普通多件买入/卖出仍调用已覆盖的 `BuyItem/SellItem`，不额外重复包裹。


## 0.6.0 电话次日预约

现有股市面板是解锁道具，使用当前存档唯一编号、原生 postit 类型和实际已拥有物品查询核实持有；仅保存过发放标志不够。档案页移除邀请按钮，改为显示预约电话 731206 和预约状态。普通自动来访和当日手动入队已停止。

电话静态证据：`PhoneUIManager.HandleCall(long)`（0x72C490）是手动拨号最终入口；等待铃声后由 Update 直接调用。专号只在此处截流，先检查 `PlayerStore.PhoneClientDict.ContainsKey`，冲突时继续原版流程；其他号码完全沿用原版。`StopCall`（0x72D110）清空号码、通话标志、计时器和声音，随后写 statusDisplay 显示预约结果。Update（0x72D250）不逐帧写 statusDisplay。没有添加原版电话簿联系人，所以需要手动输入号码。

`PlayerStore.PhoneClientDict` 明确标记 ES3Serializable，不能直接注入依赖 Mod 对话函数的联系人。本适配不写它，也不向原版未来顾客队列写专属 ID。预约只保存在股市 modData：当日 D 固定次日 D+1 的来访、来源、报价、真假与外观，线索事件限定 D+2 至 D+1+RumorHorizon；到访日已生效的事件不出售。重复拨号保持同一预约，买完并遣散后可预约次日，失败持久化保留内存计划并允许重拨重试，不重新抽取。

次日正常营业后，`GetDueBrokerPhoneVisit` 才返回已有计划，再通过原版 AddClient 追加队尾。不更改营业状态。错过预约日、基础事件取消时结束计划，不能临时抽替代线索。升级后的旧未到访计划不继续自动发；旧已到访标志却没有实际 NPC/排队对象时，结束该旧记录以免永久堵预约。有真实掮客正在接待时拒绝替换，无论旧标志是否就绪。

验收步骤：持道具拨号且当日人数/营业状态不变；无道具、号码冲突、无可售线索均正确反馈；重复拨号/保存重载不变报价；次日开店仅出现一名掮客，普通顾客顺序不变；买后纸条与日历照常；原版其他号码、挂断和重新拨号正常；过期及升级旧档能再次预约。领域断言和代理编译不能代替这些实机流程。
