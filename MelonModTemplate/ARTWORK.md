# 股市面板道具图片

## 0.11.0 人物图片

`Assets/Textures/Characters/jianqing.png` 与 `wenzhou.png` 均为 887 × 1774 的 RGBA 透明 PNG，使用内置 image_gen 生成并人工检查。剑清酱参考用户提供的照片，保留短黑发、金属框眼镜、圆脸、深蓝上衣和黑斜挎包；闻舟采用兜帽与围巾遮脸，脸部不露五官。两张图均无背景、四角透明；用户原始照片不随项目分发。

剑清酱的面部按后续反馈降低细节，采用约二十个逻辑像素宽的大色块、少量肤色和简化眼镜五官，避免面部过于精细。最终 PNG 与提示词已同步为此版本。

完整生成提示词分别保存在同目录的 `jianqing.prompt.txt` 和 `wenzhou.prompt.txt`。运行时以原版男性黑市人物精灵为尺寸、锚点及 PPU 参照，使用最近邻缩放和 Point 过滤注册独立人物键，不覆盖原版图像。生成图的外观与 alpha 已检查，实际人物摆位和柜台遮挡仍需游戏验收。

## 终端道具

成品为 `Assets/Textures/Items/stock_market_terminal.png`，1024 × 1536、RGBA、透明背景。由内置 imagegen 生成，运行时使用 Point 过滤等比缩放至原版便笺精灵尺寸，并保留透明边距。

提示词如下。旧 LearnerMod 的 health_pack.png 仅用于低分辨率像素风格参考；未作为股市道具发布。

> Use case: stylized-concept. Asset type: a single inventory item sprite for Probably Stolen, a low resolution pixel-art sci-fi trading game. Generate a compact handheld stock market terminal, upright rectangular rounded dark gunmetal and muted purple casing, small green monochrome screen showing a clear rising-and-falling financial line chart and 3 tiny candle bars, 3 chunky amber physical buttons beneath screen. Match the supplied reference's crisp low-resolution pixel-art outline, subtle worn machinery, limited muted palette, top-left highlight and side shading; the reference is STYLE ONLY, do not draw its medical subject. A single centered object, front view with very slight top/side depth, entire object visible with thin transparent padding, recognizable silhouette at inventory size. Large intentionally chunky pixel clusters as if hand-drawn on a 32 by 48 pixel grid, enlarged with nearest-neighbor appearance; no smoothing, no blur, no photorealism, no surroundings, no frame, no labels or text, no watermark, no drop shadow outside the object. Genuine transparent RGBA background, not checkerboard. This is final game sprite art, not a UI mockup.
