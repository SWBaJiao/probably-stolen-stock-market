# 原生股市界面适配

窗口使用当前游戏代理中真实存在的 `CustomUIManager`、`CustomUIBuilder`、`CustomUIWindow` 与 `CustomUIElement`。这些方法均是 `il2cpp_runtime_invoke` 的游戏原生调用，不依赖旧界面的 Unity IMGUI 路径。原版 `UIHelper` 只有提示位置计算接口，本实现未使用它创建控件。

- 入口为 `NativeStockMarketPanel(IMarketHost)`，提供 `Show/Hide/Tick/Dispose` 和 `IsVisible`。宿主每帧调用 `Tick`；可见时每 0.5 秒更新既有控件文本和按钮状态，不重建窗口、滚动控件或数量输入框。
- 独立窗口 ID 为 `MelonModTemplate.stock_market.native`，使用管理器提供的 overlay 画布和窗口、按钮、标签、输入、滚动预制件。沿用原生边框、TMP 字体及关闭/拖动组件，在本窗口内部增加自有实色层。没有复制交易按钮、背包窗口或任何原版业务组件。
- 当前代理没有指南中部分较新的快捷接口，例如 `BeginTabs/AddFlexibleSpace/GetWindow/OnClose` 构建器方法。本实现使用四个独立原生列容器切换分页，窗口关闭字段 `onClose` 绑定具名回调。
- 按钮和输入事件通过 `DelegateSupport.ConvertDelegate` 转换到 `UnityAction`、`UnityAction<string>`，保存原生委托和托管入口；关闭时以相同实例 `RemoveListener`，然后销毁自己的窗口。输入使用 `TMP_InputField` 的整数限制。
- 行情显示较昨收、最新变化、当日分时走势及 7/14/30 日 K 线、商品交叉权重、共同关联公司及已公开事件。情报只读取 `IMarketHost` 安全报价和 `GetRumors()` 的已购投影，不显示未购买未来事件。
- 委托和内幕购买仍受 `CanTrade` 门控。确认委托时重新读取资金并调用 `QuoteOrder`，随后才通过 `PlaceOrder` 执行；界面不直接改资金和存档。
- 情报购买提交上一次实际展示的 `OfferId`，由适配层拒绝已经更换顾客的报价，避免半秒刷新间隔内购买到其他顾客的情报。
- 所有控件回调与定时刷新均校验打开时的行情引擎对象和存档标识；切档以及同档重新载入都关闭并解绑旧控件。关闭与异常路径恢复此前光标状态。窗口创建失败只记录本阶段一次完整异常，不触碰玩家背包。
- 长文本使用原生 TMP `GetPreferredValues` 按所在列实际宽度计算高度；文字或列宽变化时重新测量。固定项使用相同的最小与首选宽度，填充项首选宽度为零、伸缩权重为一，避免回退到文本固有宽度。页面及滚动容器按标题和页脚以外的剩余高度伸缩，横向 ContentSizeFitter 关闭，保留纵向内容适配。
- `NativeUiBootstrap.Resolve()` 负责原生管理器和模板就绪：优先使用已就绪的 SDK 管理器和通用模板；缺失时用原生 UGUI 组件构造本 Mod 自有的通用模板，并引用游戏已加载的 sprite、材质和 TMP 字体。没有使用猜测的资源路径，也没有导出游戏素材。面板只通过其返回的 SDK 对象构建界面，卸载时先关闭窗口并解绑，再释放本 Mod 拥有的模板资源。

构建只验证当前代理签名与类型。`CustomUIManager.Instance`、prefab 就绪、窗口布局、原生中文字体覆盖、滚动/焦点保持，以及与游戏 Escape/输入阻断的配合仍须由用户启动游戏验收。若原生标题字体缺少中文，本实现记录明确告警并保留游戏字体素材。

## 管理器引导与素材合同

当前原生代码不会为缺失的 SDK prefab 自动构造完整控件。`CustomUIManager.Awake`（RVA `0x6D6970`）只登记实例、构建精灵字典和 Overlay Canvas；`Start`（`0x6DAE00`）只处理测试窗口。`CreateWindow` 检测到空 `windowPrefab` 时建立裸容器；`CustomUIBuilder.Spawn`（`0x6B2FE0`）遇到空控件 prefab 则记录错误并返回空组件包装。因此，不能将 `AddComponent<CustomUIManager>` 成功视为完整界面已经就绪。

`NativeUiBootstrap` 依次复用已有完整管理器、从 `Resources.FindObjectsOfTypeAll(Type)` 的管理器组件借用通用 prefab 引用，再为仍然缺少的字段构造专用模板。不会覆盖原管理器已有的非空字段。窗口素材只取当前 `EmporiumEntry.invWindow.background.image`，或已加载 `TreeNodeRender.window.background.image`；按钮皮肤优先取已加载原生 `Button` 的 Image，未找到时沿用原生窗口皮肤。字体与共享材质取游戏已加载的 TMP 文本组件，优先当前活动对象。日志分别记录 `stage=borrow skin` 的原生节点、sprite、texture、material 名称，以及 `stage=borrow font` 的字体来源；不读取商品图标充当窗口背景，也不创建默认白色贴图。

所有模板均位于本 Mod 的 inactive 根节点下，模板自身 `activeSelf=true`，保证原生 SDK 克隆后可以显示。窗口模板填写 `CustomUIWindowFrame.contentRoot/titleText/closeButton/dragHandle`；输入模板填写 `TMP_InputField.textViewport/textComponent/placeholder/targetGraphic`；滚动模板填写 `ScrollRect.viewport/content`，并设置裁切、纵向布局与内容尺寸适配。只有这些引用和模板激活状态逐项检查通过，才打印 `stage=contracts ready`；该日志不代表截图或点击交互已验收。需要自有管理器时先在 inactive 对象上填完 prefab，再激活以执行原版 Awake 和画布初始化。

场景已有 `EventSystem.current` 时直接复用；缺失时才创建本 Mod 自有的 `EventSystem` 与 `StandaloneInputModule`。API 检查已确认使用的 `GameObject.GetComponent(Type)`、`AddComponent(Type)`、`Resources.FindObjectsOfTypeAll(Type)` 为当前代理的原生调用；模板构造不使用此前已发现会失败的 `Graphics.DrawTextureImpl`。

卸载时面板先关闭自己的窗口并解绑。随后，仅当管理器字段仍指向本 Mod 填入的模板时才恢复原值；只销毁自有模板、管理器和事件系统，不销毁游戏 sprite、font 或 material。如果管理器仍有其他 Mod 的存活窗口，则保留正在共享的管理器及模板并记录原因，避免破坏其他窗口。原生素材与控件实际渲染、输入、拖动、裁切和中文覆盖仍需游戏实测。

## 当日分时与日 K 线

当日分时使用独立的 `NativeIntradayChart`，与 `NativeCandlestickChart` 互斥显示。所有当天真实结算样本按顺序连接，横轴表示采样次序，不使用现实时间冒充游戏分钟。开盘或接入基准、顾客成交结算、每日行情及事件变价形成各自的记录；隐藏事件沿用安全的“市场异动”原因，不通过图表公开事件标识或未购内幕。当天采样独立保存，不受 `HistoryLimit` 跨日价格历史裁剪影响。跨日只开始当天的新曲线；旧档恢复到的记录不完整时明确提示，不生成不存在的点。

日 K 线周期固定为 7、14、30 个有记录的游戏日，继续使用保存的每日 OHLC。图表切换不改变行情或随机状态；关闭窗口释放两张图自身的对象，不销毁游戏字体与材质。

## 图表悬停

宿主每帧对当前选中的图调用 `TickHover()`，行情文本继续每半秒刷新。分时根据横轴距离选择最近实际采样，日 K 线按蜡烛槽位选择日期；空数据和绘图区外均无提示。提示只读取已显示行情快照，变价原因沿用安全公开字段，未知 OHLC 保持 `—`，当天标注未收盘。

两图通过 `SetHoverBounds(windowRoot)` 共用窗口下的提示浮层，按拥有者令牌隐藏并计数释放，避免未激活的图清掉当前提示。提示背景与文字不接收射线；指引线挂在当前图内以服从滚动裁切。鼠标命中使用实际画布的 overlay/camera 语义，检查上级滚动裁切和可见性；提示框按窗口边界翻转、夹限。离开、切换、重绘和关闭时隐藏旧提示，字体与材质继续引用游戏已有资源。

`Tests/ChartInteraction.Tests` 独立验证 23 项索引、边界和夹限场景，不加载游戏。实际鼠标命中、滚动裁切及提示位置仍需游戏内验收。


## 0.5.2 高对比主题

颜色集中于 `StockUiTheme`：不透明深蓝背景、浅白正文、浅蓝标题和边框；涨红跌绿，等待或错误说明用黄色。窗口、滚动区与按钮内侧使用本 Mod 子对象绘制纯色，避免原版纹理与色调相乘后近黑。原版边框及字体仍由游戏提供。

装饰层设置 `LayoutElement.ignoreLayout=true`、`Image.raycastTarget=false` 并置于文字之前，随所属窗口销毁。按钮的 ColorTint 只作用于白色自有填充层，分别设置普通、选中、悬停、按下与禁用状态；正文颜色不随背景乘暗。当前代理裁剪了部分 `ColorBlock` 属性 setter，使用已还原的 `m_NormalColor/m_HighlightedColor/m_SelectedColor/m_PressedColor` 等结构字段，经原生 Selectable.colors 写回。

图表背景不透明，坐标使用浅色文字，悬停框为 14 号白字及浅蓝细边框。源码调色板的 27 组正文、涨跌与状态文字组合对比检查最低为 5.05:1；这只是标称实色对比，实际字体、游戏材质、边框及交互状态仍需实机截图验收。原有 23 项图表命中及提示边界检查通过。
