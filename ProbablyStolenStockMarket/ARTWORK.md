# 股市面板道具图片

## 股市情报名片 — 2026-10-29

`Assets/Textures/Items/intel_business_card.png` 为 1586×992、RGBA 透明 PNG，使用内置 image_gen 生成。旧象牙纸搭配深蓝侧栏、青绿色行情图和黄铜电话图标，图像只包含号码 731206；姓名与预约说明通过原生 handnote 正文本地化。完整提示词保存在同目录 `intel_business_card.prompt.txt`。未修改生成图像的像素；已检查号码和透明通道，实际主物品栏与拖拽显示仍需游戏内验收。

贷款和情报名片使用各自独立的图集、纹理和保存恢复记录，通过 `BusinessCardVisual` 共用原生渲染适配；保存时恢复 handnote 原生图集字段，避免卸载模组后依赖自定义资源。

## 贷款合同素材 — 2026-10-29

`Assets/Textures/UI/loan_contract_paper.png` 为 1024×1536 的无字旧象牙纸底，带酒红双线框、黄铜角饰和低对比纸纹；`loan_contract_seal.png` 为 1254×1254 RGBA 透明圆章，采用朱红双环、天平图标与轻微缺墨。两者由内置 image_gen 生成，未使用本地 API 密钥，原始提示词保存为同目录 `.prompt.txt`。

原生贷款窗口采用 Point 过滤加载图片，正文使用游戏当前语言字体、15 号深褐文字与分节条款，标题采用酒红色。印章在左侧专员落款区，借款人签名独立放在右下角，以斜体和自动字号适配昵称；签名及条款均为动态纯文本，不烘焙进底图。签约条款与即时账单分区，较长合同通过原生滚动条查阅。印章完全无文字，可在所有语言下共用；合同文字支持四语。素材尺寸与透明通道已检查，实际游戏显示仍需验收。

## 0.14.0 贷款美术

`Assets/Textures/Characters/shenheng.png` 为沈衡的独立粗像素立绘，869×1810、RGBA 透明 PNG。人物采用灰鬓、眼镜、橄榄色卷袖衬衫、深色马甲、酒红领带及账本，手持名片。使用内置 image_gen 生成，沿用原版人物尺寸、锚点和最近邻适配；不覆盖原版人物资源。

`Assets/Textures/Items/loan_business_card.png` 为 1659×948 的独立像素名片图像，采用旧象牙纸、酒红侧栏、黄铜天平与电话听筒图案。最终图仅包含数字电话号码 731207，不含姓名或其他语言文字，兼容不同语言；联系人姓名、号码和条款由游戏文字层本地化。名片仍保存为原生 handnote，在柜台与背包中使用专用运行时图像，保存前恢复原版资源字段。

两张图的完整生成及最终修改提示词保存在同目录 `.prompt.txt` 中，使用内置生成工具，未使用本地 API 密钥。透明度和外观已检查，实际柜台比例、背包与拖拽显示需在游戏内验收。

## 0.11.0 人物图片

`Assets/Textures/Characters/jianqing.png` 与 `wenzhou.png` 均为 887 × 1774 的 RGBA 透明 PNG，使用内置 image_gen 生成并人工检查。剑清酱参考用户提供的照片，保留短黑发、金属框眼镜、圆脸、深蓝上衣和黑斜挎包；闻舟采用兜帽与围巾遮脸，脸部不露五官。两张图均无背景、四角透明；用户原始照片不随项目分发。

剑清酱的面部按后续反馈降低细节，采用约二十个逻辑像素宽的大色块、少量肤色和简化眼镜五官，避免面部过于精细。最终 PNG 与提示词已同步为此版本。

完整生成提示词分别保存在同目录的 `jianqing.prompt.txt` 和 `wenzhou.prompt.txt`。运行时以原版男性黑市人物精灵为尺寸、锚点及 PPU 参照，使用最近邻缩放和 Point 过滤注册独立人物键，不覆盖原版图像。生成图的外观与 alpha 已检查，实际人物摆位和柜台遮挡仍需游戏验收。

## 终端道具

成品为 `Assets/Textures/Items/stock_market_terminal.png`，1024 × 1536、RGBA、透明背景。由内置 imagegen 生成，运行时使用 Point 过滤等比缩放至原版便笺精灵尺寸，并保留透明边距。

提示词如下。旧 LearnerMod 的 health_pack.png 仅用于低分辨率像素风格参考；未作为股市道具发布。

> Use case: stylized-concept. Asset type: a single inventory item sprite for Probably Stolen, a low resolution pixel-art sci-fi trading game. Generate a compact handheld stock market terminal, upright rectangular rounded dark gunmetal and muted purple casing, small green monochrome screen showing a clear rising-and-falling financial line chart and 3 tiny candle bars, 3 chunky amber physical buttons beneath screen. Match the supplied reference's crisp low-resolution pixel-art outline, subtle worn machinery, limited muted palette, top-left highlight and side shading; the reference is STYLE ONLY, do not draw its medical subject. A single centered object, front view with very slight top/side depth, entire object visible with thin transparent padding, recognizable silhouette at inventory size. Large intentionally chunky pixel clusters as if hand-drawn on a 32 by 48 pixel grid, enlarged with nearest-neighbor appearance; no smoothing, no blur, no photorealism, no surroundings, no frame, no labels or text, no watermark, no drop shadow outside the object. Genuine transparent RGBA background, not checkerboard. This is final game sprite art, not a UI mockup.
