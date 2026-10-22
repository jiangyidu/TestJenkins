# PsdTOUgui.zip 源码深度分析与逆向设计依据

分析对象：本次上传的 `PsdTOUgui.zip`；以下结论来自实际代码检查，压缩包内注释和 README 仅作待核验材料。这里讨论的“逆向”是借鉴结构、坐标与资源规则后实现反向转换，不是恢复已经丢失的 Photoshop 原始工程。

## 1. 压缩包的实际内容

ZIP 含 125 条目（含目录），106 个文件：42 个 C# 文件、62 个 Unity `.meta`、1 个 `.asset` 设置资源、1 个 Markdown 文档；C# 共约 11,406 行。实际有两套功能：

| 目录 | 职责 | 主要文件 |
|---|---|---|
| `Editor/Readers` | PSD/PSB 大端二进制解析、通道解码、文字元数据读取 | `ManagedPsdDocument`、`ManagedPsdLayerDecoder`、`ManagedPsdTextMetadata` |
| `Editor/Core` | 文档/节点模型、命名规则、标识、几何后处理 | `PsdModels`、`PsdNamingParser`、`PsdModelPostprocessor` |
| `Editor/Resources` | PNG 导出、像素去重、Sprite/Texture 导入设置、清单 | `PsdResourceExporter`、`PsdGeneratedManifest` |
| `Editor/Generation` | UGUI 构建、增量更新、字体映射、业务绑定 | `PsdUguiBuilder`、`PsdPrefabPatcher`、`PsdTextStyler` |
| `Editor/Pipeline`、`UI`、`Settings` | 入口、窗口、资源菜单、配置 | `PsdConverterPipeline.FullConvert` |
| `Runtime` | 来源元数据、UI 绑定、透明交互区域 | `PsdGeneratedElement`、`PsdGeneratedScreen`、`PsdViewBinder`、`PsdUIBlock` |
| `OtherTool/Editor` | 独立的 Prefab 空间邻近重组工具 | `PrefabAssemblyPipeline`、`PrefabMatchers` |

原包没有 Prefab→PSD writer，也没有随包的真实 `.psd`、`.prefab`、贴图、Unity 项目版本文件或自动化测试程序。`OtherTool/README.md` 提到的 `tests/test_assembler.py` 与两个示例 JSON 没有实际包含在 ZIP 中；因此不能把 README 的自测宣称视为已验证结果。

## 2. 原管线如何工作

`Editor/Pipeline/PsdConverterPipeline.cs:46` 的 `FullConvert` 先 `ReadSource`，再 `PsdManifestStore.LoadOrCreate`，随后 `PsdResourceExporter.Export`、`PsdPrefabPatcher.PatchOrBuild`，最后按设置生成 Lua 绑定模板并保存资源。

数据以 `PsdDocumentModel` 和 `PsdNodeModel` 分层，避免二进制解析直接操作 Unity 层级。节点包含图层名、源 ID、源矩形、可见性、不透明度、UI 类型、切片、文字样式和孩子列表。读取后 `PsdModelPostprocessor` 解析 `#Canvas`、分配路径/稳定 ID、补齐容器尺寸。资源层使用像素散列与清单复用贴图；构建层负责 UI 语义。

适合反向借鉴的是相同的分层方法：**Unity 快照/渲染 → 中间文档/图层模型 → PSD writer → 诊断与伴随元数据**。不可把 `PsdConverterPipeline` 的步骤简单倒过来：PNG、Unity 文本、组件行为与 Photoshop 描述符并不是互逆表示。

## 3. PSD 二进制与图层顺序：最容易写反的部分

`ManagedPsdDocument.Read`（同名文件约 273–403 行）检查 `8BPS`，版本 1/2，支持 8/16 位 Grayscale/RGB/CMYK，并跳过颜色数据、图像资源后读取 Layer and Mask 区。每个 layer record 存矩形、通道 ID 与长度、`8BIM` 混合信息、不透明度、flags、额外信息。随后读取所有通道数据偏移。

`ManagedPsdDocument.cs:397` 执行 `rawLayers.Reverse()`；`ToModel`（约 154–230 行）再按反转后的顺序组织层级：`lsct/lsdk` 的 1/2 为组开始，3 为组结束。`PsdUguiBuilder.cs:63` 与 `:178` 又逆序遍历 roots/children，以适配 Unity sibling 后画覆盖先画的规则。

因此，在使用正常层级绘制且无 Canvas sorting 特例时，一个 Unity 组包含 A（底）、B（顶），新 PSD 文件中的原始 record 顺序应为：

```
组结束占位记录 lsct=3
A 的像素记录
B 的像素记录
组头记录 lsct=1
```

这使原 reader 反转后得到“组头、B、A、组尾”，构建器再逆序还原 Unity 顺序。writer 的模型必须明确其列表表示底→顶还是顶→底，不能混用图层面板顺序与文件顺序。父节点自己有 Graphic 且有子节点时，其 Graphic 需成为组内最底的独立像素层；否则转换组后会丢掉父节点的图像。

`ReadTaggedBlock`（约 543 行起）识别 `luni`（UTF-16BE 图层名称）、`lyid`（ID）、`lsct/lsdk`（组）、`TySh`（文字）、`SoCo`（纯色）、`vmsk/vsms`（矢量蒙版标志）、`lfx2/lrFX`（效果标志）、`PlLd/SoLd/SoLE`（智能对象标志）。新 writer 至少应正确写 `luni`，不能只用受 255 字节和编码限制的 Pascal 名称。组记录、通道长度、额外块 padding 与 Layer Info 偶数字节对齐都需要由独立 reader 验证。

原 reader 主要读取分层数据，不读取/验证合并预览图；故“原项目成功解析新 PSD”不能单独证明 Photoshop 兼容，必须再用独立 PSD 工具核对合并图和透明通道。

## 4. 坐标、画布和几何

`PsdRectTransformUtility.Apply` 把 PSD 左上原点、Y 向下的矩形映射到 Unity RectTransform 的锚点、pivot、offset。未拉伸轴按父尺寸、边距和 anchor 计算 `anchoredPosition`；拉伸轴写入 `offsetMin/offsetMax`。原转换会把 scale 重置为 1、rotation 重置为单位值，因此它本身没有保留任意 PSD 旋转/仿射布局。

`PsdModelGeometryResolver.CalculateChildrenBounds` 用有效子节点的 sourceRect 并集计算空组边界，过滤忽略和默认不导出的隐藏节点；`PsdModelPostprocessor.ResolveCanvasMarker` 允许 `#Canvas` 改写实际画布矩形。

反向不能只用 `anchoredPosition + sizeDelta`：伸展锚点、非中心 pivot、LayoutGroup、ContentSizeFitter、父缩放/旋转都会令该做法错误。应先在指定输出尺寸下完成布局，再使用真实渲染结果或 `GetWorldCorners`→根/相机坐标转换。`OtherTool/Editor/PrefabMatchers.GetCanvasBounds`（约 86 行）已经示范 `GetWorldCorners` 后 `canvasRoot.InverseTransformPoint` 的方法，值得参考，但轴对齐包围盒只能用于裁剪范围，无法替代旋转后的像素渲染。

画布分辨率不是普通 Prefab 的必然固有属性。原生成根节点常只有 RectTransform，没有 Canvas；`PsdGeneratedScreen.CanvasSize` 可提供原设计尺寸。反向应支持显式宽高，或读取该元数据/根 rect 推导，且把选择写入报告。修改导出尺寸可能触发不同的 stretch 和布局结果，必须让用户知晓。

## 5. 图像、Alpha、蒙版与视觉保真

`ManagedPsdLayerDecoder.TryDecode/DecodeChannel` 支持 Raw、RLE/PackBits、ZIP、ZIP Prediction；统一输出 `rgbaTopDown`。`CombineChannels`（约 428 行起）把透明度通道 -1 与栅格蒙版 -2 相乘，蒙版外使用 defaultColor。16 位转换为 8 位、CMYK 通过固定公式转 RGB，未做完整 ICC 色彩管理。

`PsdResourceExporter` 写 PNG，设置 `alphaIsTransparency`、Clamp、Bilinear、Sprite/Texture、border；构建器 `ConfigureImage/ConfigureRawImage` 再把图层 opacity 写入 UI 颜色 alpha。`Image.Type.Sliced`、Sprite atlas UV、`RawImage.uvRect`、Text 字体采样、Shadow/Outline、Mask/RectMask2D、CanvasGroup、材质等都可能使**最终画面不等于直接读取源纹理**。

因此新工具若面向“导出当前 Prefab 的外观”，应优先让 Unity 绘制每个可见单元再读取 RGBA；将当前缩放、裁切、颜色、九宫格、文字效果烘焙到像素中。导出器必须专门处理透明 RT 上标准 UGUI shader 的 Alpha 累积，不能假定一次渲染读回的 RGB/Alpha 已经是 PSD 需要的 straight alpha。可通过受控的黑/白背景双次渲染还原覆盖率与非预乘 RGB，并设置相应容差测试。

蒙版是相互依赖的绘制状态：孤立某个目标层时，如果禁用祖先 Mask 的 Graphic，会丢 stencil；如果不隐藏祖先可见图像，又会在多个图层里重复烘焙祖先背景。新工具需明确定义隔离策略，并在自测中验证嵌套 Mask 与 `showMaskGraphic`。跨 Canvas 排序、自定义混合 shader、粒子/3D 内容无法仅以 Transform sibling 层级安全表达，应提供明确诊断和整张合成导出的退路。

## 6. 文本：可恢复内容与不可恢复格式

`ManagedPsdDocument.ReadTypeTool`（约 637 行）读取 `TySh` 仿射矩阵与 descriptor 中的 `Txt `，从 `EngineData` 获取样式，并用矩阵 Y 轴长度缩放字号。`ManagedPsdTextMetadata.Apply` 以正则从 EngineData 提取首个字体索引、字号、颜色、对齐和 FauxBold/FauxItalic。它不是完整的 Photoshop 文字排版引擎，未保留每个 style run、逐字样式、字距、段落/变形全部信息。

`PsdTextStyler.CreateOrStyle` 最终创建标准 `UnityEngine.UI.Text`，映射/回退字体，字号取整、设置全体统一样式与 Overflow。即使枚举含 `TmpText`，此实现仍创建 `Text`。因此不能从最终 Text 重构最初完整 TySh/EngineData。

稳健的反向结果应默认把文字作为独立栅格图层，并在 JSON 中保留文字内容、字体名称、字号、颜色、对齐及来源路径，明确“可移动/隐藏的像素图层”与“Photoshop 可编辑文字层”的区别。若要额外生成 Photoshop 原生文字，应作为单独功能，需要合法 TySh、EngineData、字体映射与 Photoshop 实机验证；不应仅写一段文字 descriptor 就宣称支持。

## 7. 名称与来源追踪

`PsdNamingParser.Parse` 支持 `#` 忽略、`$` 提升孩子、`.`/`_` 类型后缀（img、rimg、txt、btn、tg、sld、inp、sv、msk 等），以及 `@top/@stretchx/@fullscreen/@width/@height/@slice/@image` 等参数。最终 GameObject 名称可能经 `ResolveNodeObjectName` 改成类型索引或业务前缀，不能假设当前名字就是原 PSD 名。

`Runtime/PsdGeneratedElement.cs` 保留 `SourceLayerName/SourceLayerPath/SourceLayerId/StableId/GeneratedUiType/GeneratedVisualMode` 与局部矩形快照；`PsdGeneratedScreen` 保留源 PSD 路径、哈希、画布尺寸、manifest 路径。新工具可通过反射读取这些公开属性，实现装了原插件时自动利用、没装时也能独立工作。稳定 ID 应基于源资产与层级/组件身份，不应仅由名字决定，重名、斜杠、中文必须有测试。

需要特别说明命名回导风险：像素图层如果仍命名 `标题.txt`，原 importer 会按 Text 语义构建，而且缺少 TySh 时用显示名当内容；`#` 层会被跳过，`$` 组可能展开。`@slice` 还可能再次加工已烘焙九宫格的像素。保留来源名称有溯源价值，但不等于保证原插件无损往返；可选择纯视觉导出命名，或把原名字仅记录在元数据中。

## 8. 源码检查发现的局限（不把注释当成能力）

| 源码证据 | 实际含义 |
|---|---|
| `sourceBlendMode/sourceHasVectorMask/sourceHasLayerEffects` 只是模型字段；构建器无对应实现 | PSD 混合、矢量蒙版、图层效果不能无损回收，已生成 Prefab 可能与原 PSD 不同 |
| 构建器未创建 CanvasGroup 处理 Group opacity | 原 PSD 组透明度不会成为相等的 Unity 组透明度 |
| `PsdTextStyler` 使用 `style.color`，未乘 `node.opacity` | 原文本文字层 opacity 信息未同图像一样映射；反向只能导出 Unity 实际状态 |
| `ReadTypeTool` 只保留缩放后的字号；`Apply` 重置 rotation/scale | 旋转、复杂变形和字体精度损失不可凭最终节点恢复 |
| `DecodePackBitsRow` 对不完整行截断/填零，未检查 written 达到期望 | reader 对部分坏流较宽容；新 writer 必须用严格 reader 验证 |
| `ManagedPsdSourceBackend.AddNodeFallbackIssues` 的警告称“保留智能对象栅格”，但 `CreateNode` 仍把显示名放进 text | 警告文字与实际路径不完全一致，不能依此承诺栅格保留 |
| `OtherTool/README.md` 列了缺失的测试与 configs | README 的功能/测试记录不等于随包可重现证据 |

这些是原系统边界，不应通过反向工具暗中改写原 Prefab 来“纠正”。导出工具的基准应是当前 Unity 对象在约定画布与时点下的渲染状态。

## 9. Unity 2018.4.36f1 适配与验证建议

原工程 `PsdUnityCompatibility` 常量声明最低 2018.4，实质主要解析版本号和显示兼容带；它不是自动 API 适配层，也不是版本测试结果。使用 `PrefabUtility.LoadPrefabContents/UnloadPrefabContents/SaveAsPrefabAsset` 的代码路线符合 2018.3 后 Prefab 工作流。新工具宜独立放在 `Assets/.../Editor`，使用保守 C# 语法与 .NET 4.x API，避免依赖 UI Toolkit、较新 PrefabStage 命名空间、现代 SRP、第三方 PSD DLL 或必须安装 TMP。

建议验证分为三层，并明确各层的实际执行环境：

1. 纯 C# writer：大端值、RGBA 通道、RLE、负 layer count 的合并透明通道、Unicode、层序/组、边界/尺寸、透明层、文件长度与损坏拒绝。
2. 独立 PSD reader：读取生成文件，验证层树、名字、坐标、通道与合并图；可再用原 reader 做兼容对照，但不能只用自己的 reader。
3. Unity 2018.4.36f1 实机：导入编译；Image/RawImage/Text、stretch/pivot/rotation、九宫格/填充、遮罩、隐藏层、CanvasGroup、同名节点、布局、取消/异常清理与不污染原场景；用独立像素样本比较单层组合与整屏截图。

本报告只对原包做静态分析，不把尚未运行的 Unity 编辑器测试或 Photoshop 实机打开写为通过。
