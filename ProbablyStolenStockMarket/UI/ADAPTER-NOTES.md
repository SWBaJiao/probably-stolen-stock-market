# 0.1.1 IMGUI 代理适配历史记录

0.2.0 已移除此面板实现，当前使用游戏原生控件与素材，见 [原生界面说明](NATIVE-NOTES.md)。以下仅保留旧调用链的诊断依据。

本记录依据本机 `MelonLoader/Il2CppAssemblies` 中 Unity 2021.3.45f2 的生成代理静态反编译。已绑定原生方法表示调用链中存在 `il2cpp_runtime_invoke` 或 `ResolveICall`，不代表已在游戏中实际验收。

已移除的确定异常路径：

- `GUI.BeginScrollView` 的完整重载及 `GUI.EndScrollView(bool)` 直接抛出 `NotSupportedException("Method unstripping failed")`。
- `GUI.DrawTexture` 的简单重载最终进入直接抛错的完整重载。
- `GUI.TextField` 经 `GUI.DoTextField`、`GUIUtility.GetStateObject` 进入直接抛错的 `GUIStateObjects.GetStateObject`。
- `Font.CreateDynamicFontFromOSFont(string, int)` 直接抛错；数组重载指向当前代理未声明的数组构造函数，因此也不作为适配入口。

面板使用的替代链：

- `GUI.BeginGroup` 经 `GUIClip.Push`、`Internal_Push` 到已解析的 `Internal_Push_Injected` 原生调用；`GUI.EndGroup` 到已绑定的 `GUIClip.Internal_Pop`。外层 Group 限制视口，内层 Group 平移内容，滚轮和上下按钮维护滚动位置。
- `GUI.Label`、`GUI.Box` 使用已绑定原生重载；按钮、数字输入和价格柱使用已绑定的 `GUIStyle.Draw(Rect, GUIContent, bool, bool, bool, bool)`。按钮仅处理同一控件的鼠标按下及释放，尊重禁用状态和视口边界。
- 数字输入只接受最多 9 位 ASCII 数字，支持点击后替换、退格、删除及 Ctrl/Cmd+A；Enter/Tab 退出输入，Escape 关闭面板。避免使用 `TextEditor` 状态对象。
- 动态字体使用已绑定默认 `Font()` 构造后调用 `Font.Internal_CreateDynamicFont`；本地字体文件使用已绑定 `Font.Internal_CreateFontFromPath`。`Font.HasCharacter` 已绑定原生，继续检查中文字形。
- 样式构造、字体及字号属性、样式背景纹理、`GUIUtility.GetControlID`、`Event`、`Cursor` 和 `Screen` 在当前代理均为绑定方法或无抛错桩的转发链。

全部行情、交易、持仓、顾客内幕与流水内容保留。游戏内仍需验证窗口实际显示、中文字体、长列表滚动、数字输入、关闭恢复光标与面板遮挡原版操作。
