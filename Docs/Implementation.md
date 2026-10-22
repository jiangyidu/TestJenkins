# Integrated 2.0.0 实现说明

适配目标：Unity 2018.4.36f1、C# 6、UGUI、编辑器主线程。本文对应 2026-10-22 的融合实现；使用入口见 [README](../README.zh-CN.md)，融合取舍见 [IntegrationChanges.zh-CN.md](IntegrationChanges.zh-CN.md)，实际测试证据见 [TestReport.zh-CN.md](TestReport.zh-CN.md)。

## 1. 模块与调用关系

```text
Window / CLI
  → ExportOptions 配置快照
  → 单源 Exporter / 共享 BatchService
  → 源对象元数据 + 参考路径解析
  → CaptureSession 隔离副本布局、完整画面及逐 Graphic 渲染
  → Naming / 文件夹与辅助标记
  → Reference 严格身份匹配、完全相同像素复用、安全资源
  → Writer 写临时 PSD
  → OutputVerifier 从文件读回与期望文档核对
  → 原子提交 PSD
  → JSON 单独提交
```

| 模块 | 职责 |
| --- | --- |
| `PrefabPsdOptions` | 四种预设标识、所有导出选项、Clone、导出统计 |
| `PrefabPsdWorkflow` | 只读预检、预设应用、栅格安全命名、文件名清洗 |
| `PrefabPsdBatchService` | 同一配置批量执行、身份去重、稳定输出名、聚合错误与取消 |
| `PrefabPsdExporter` | 整树临时副本、布局、渲染、层序、内存估算与写入衔接 |
| `PrefabPsdMetadata` | 源节点、文字、资源、变换、原插件来源及参考结果 JSON |
| `PrefabPsdReference` | 路径解析、保守匹配、精确像素复用、安全资源与 ID 冲突处理 |
| `PsdDocument / PsdRaster` | 纯托管文档/图层模型、裁剪、翻行和测试用合成 |
| `PsdWriter` | PSD v1 大端编码、Raw/RLE、资源、Unicode、原子写入 |
| `PsdReferenceReader` | 严格、有限额、按需解码的 RGB8 PSD 读取 |
| `PsdSafeResources` | 资源白名单、结构和 sRGB ICC 内容验证 |
| `PsdOutputVerifier` | 提交前核对真实文件的记录、通道及 merged 编码 |

Core 模块不引用 UnityEngine，可独立运行。Unity 层与 CLI 的测试需要真实 Unity 程序集；托管策略测试不创建原生 GameObject，也不伪装 GPU 运行。

## 2. 配置是一份数据，而不是多个入口的默认值

`ExportOptions` 是可序列化的字段集合；当前设置包含尺寸、层级、JSON、压缩、内存、验证、参考路径及参考策略。`Progress` 不序列化。`Clone()` 复制值类型和不可变字符串，并保留进度委托供调用方包装。

`PrefabPsdPresets.Apply` 只在调用方明确选择预设时运行，不在 Export 内部强制重置。预设不改用户尺寸、压缩和内存预算；离开 SourceReference 会清除显式参考路径。Window 保存设置并为一次操作生成快照，BatchService 再按项目复制同一快照，CLI 也使用同样的 exporter 和 batch service。

这修复了 B 中“窗口显示 Source Fidelity，但批量入口重新创建默认 Options”的断层。文件名规划和参数解析被拆成可托管方法，以便直接验证配置、去重与冲突处理。

## 3. 预检纯读取，导出才在副本中重建布局

预检使用现有 Transform、RectTransform 边界、组件类型、活动状态和 Canvas 状态估算节点树。它不 ForceRebuildLayout、不 ForceUpdateCanvases、不实例化源对象，也不访问会创建材质实例的 Graphic.material。

预检的边界和内存是估算，不承诺等于导出后布局。明确输出尺寸、ContentSizeFitter、LayoutGroup 等可能使临时副本重新布局。预检过滤和点击定位只帮助检查源节点，不更改导出包含规则。

CaptureSession 创建 PreviewScene、停用的 host Canvas 和专用 Camera；`camera.scene` 指向该预览场景。源对象实例化到 host 下，业务 MonoBehaviour、动画、其他相机和非 UGUI Renderer 在副本中停用。根旋转/缩放归一，尺寸设为设计画布；子节点的变换保留。

Canvas 改为 WorldSpace。停用 CanvasScaler 前记录各 Canvas 的 referencePixelsPerUnit，之后恢复该值，避免 OnDisable 重置为100引起九宫格边框变化。scaleFactor 使用设计画布约定1。

副本先更新 Canvas/Layout，再保存 RectTransform 快照，停用布局控制器并恢复几何。逐层 Graphic 隔离不会再因 preferred size 改变而重新排列界面。整个会话通过 IDisposable 清理 RT、读回纹理和预览场景，读回期间也在 finally 中恢复 RenderTexture.active。

这不是业务脚本沙箱：实例化、OnEnable/OnDisable 等编辑器生命周期回调仍可能触发项目脚本自身的全局副作用。

## 4. 像素来自 Unity，而不是重新实现 Image 规则

Image、RawImage、Text、材质以及 Shadow/Outline 等都由原 Graphic 的 Unity 渲染链绘制。工具不手写径向 Filled、UV wrap、九宫格缩放、镜像或旋转采样规则，也不要求源纹理开启 Read/Write。

每个目标 Graphic 单独捕获时保留其必要祖先 Mask Graphic，以保持模板写入，但关闭祖先的可见底图颜色。目标自身为可见 Mask 时保留其显示设置。RectMask2D 继续参与原层级中的裁剪。这样不会把祖先背景反复烘焙到所有孩子。

CanvasGroup 的结果已经进入渲染像素。PSD 文件夹采用 pass-through、opacity255，避免透明度同时进入文件夹和孩子而重复衰减。隐藏/禁用 Graphic 不输出像素，但原状态留在 JSON；空文件夹可按选项保留。整个源无可导出 Graphic 时拒绝输出，不伪造启用状态下的图像。

完整画面先渲染一次，此后记录 CanvasRenderer.absoluteDepth，再执行逐层隔离。通常层序来自父 Graphic 在底、孩子按 sibling 绘制的规则；覆盖排序的子 Canvas 会触发扁平化，并使用 sorting layer/order 与已渲染深度排序。跨 Canvas 的三维距离排序不在保证范围。

## 5. Alpha、颜色和合成预览是不同的数据约定

标准 source-over 的 RGB 可以写为 `foreground × alpha + background × (1-alpha)`。对相同目标分别渲染黑底 B、白底 W：

```text
alpha = 1 - mean(W.rgb - B.rgb)
straight.rgb = B.rgb / alpha
```

捕获使用浮点 RT 和读回格式，避免先量化为8位再求覆盖率。Linear 项目在线性数据上计算后转为 sRGB，Gamma 项目按其当前数值空间计算；读回行从底部优先翻为 PSD 顶部优先。

图层通道保存 straight RGB + Alpha。带 merged transparency 的 PSD 合成预览则将 RGB 按白底预合成存储，Alpha 独立写入。Writer 只对 merged RGB 应用白底编码，不把该处理误用到层通道。OutputVerifier 按这个规则核对原始存储字节，独立 psd-tools 测试另核对 reader 的解读和从层重新合成。

方法仅适用于可表达为普通 RGBA 的混合。加法、目标依赖混合、HDR 值等会提示，但无法完整变成普通 normal PSD 层。Unity Linear 与 Photoshop 默认 sRGB 数值混合也不同；同一分层文档重合成时需要匹配混合空间。

## 6. 命名服务描述的是当前栅格结果

CurrentAppearance 保留当前名称；ArtistDelivery 只清理已知点号后缀和布局标签，不以任意下划线截断业务名。RoundTripStructure 为组和控件容器合成有限语义，为内容像素层生成 `.img/.rimg/.bg`，不会给已栅格文字合成 `.txt` 或给已烘焙九宫格补 `@slice`。

SourceReference 也必须避免当前危险名称遗留 `.txt`、`#/$` 或 `@slice` 等再解释语义；普通安全名称保留，原始来源名称另存 JSON。恢复参考名称不会以“旧稿有该名称”覆盖用户改名。

PSD 模型为面板顶部优先顺序：GroupStart、孩子、GroupEnd。Writer 内部反转成物理 bottom-to-top 记录，因此组末标记在前、组头在后。父对象自身有 Graphic 且含子层时，其自身像素放在文件夹底部。JSON 图层名在全部命名/参考处理后按当前 ID 同步。

空组记录本身不能表达完整 RectTransform；其原尺寸、锚点、pivot 及变换依靠 JSON。可选隐藏 `#Canvas` 标记覆盖整个画布，但不计为内容 Graphic 数，也不改变可见 merged 图。

## 7. 原稿参考不再使用“近似等价覆盖”

仅 SourceReference 或显式路径启用参考。路径发现限于已知来源成员和同目录同名文件；多个候选或缺失的强制参考明确失败。参考不是输出目标，不写回原稿。

仅像素层参与恢复：唯一 sourceLayerId → 精确来源名及边界 → 精确当前名及边界。不进行模糊名匹配；重复/克隆 ID 不作为唯一身份猜测。身份匹配成功可恢复来源 ID，但是否替换像素仍由下面的独立条件决定。

像素必须边界、尺寸、可见性、opacity及逐字节 RGBA一致，且参考层/祖先没有不安全的遮罩、效果、混合或透明语义。复用前按需解码，不根据文字内容、字体名近似、字号容差或颜色距离作判定。SourceReference 不重排当前层序。

这一方案以“不覆盖用户视觉修改”为优先。它不会最大化原稿复用计数：即使肉眼近似，只要一个字节不同就保留当前捕获；裁剪边界或 alpha 表达不同的原稿层也可能不能复用。它不是完整的原 PSD 重建器。

分配参考 ID 后，再对全树包括组和辅助层进行唯一性处理，避免参考 ID 与新合成组冲突；同步 JSON 当前导出 ID、参考 ID、名字和复用标志。

## 8. Reader 和资源策略

ReferenceReader 面向 PSD v1、RGB8。解析阶段检查 section 长度、Unicode、ID、分组结构、坐标和数量，像素按需读取并限制解码预算；Raw 和 PackBits RLE 均严格检查行长度与输出长度。通过持有只读共享文件句柄和 Dispose 管理读取生命周期。

Reader 记录 raster/vector mask、effects、smart object、clipping、混合与祖先状态，供 Reference 判定复用资格。它不会把 unsupported 内容粗略渲染后假称等价。输出校验另可读取 merged 原始四通道，检查其白底编码。

SafeResources 仅允许1005、1039、1064。Writer 在创建临时文件前验证资源，不允许任意资源字节注入。sRGB 判定检查 profile 结构、矩阵/白点/TRC 等内容，不只看 profile 名称；不明 profile 即使名字叫 sRGB，也不会无条件接受。

Unity 参考桥接对1064更严格：即使该资源格式合法，也只有比例1.0才复制，因为当前设计画布是方形像素。非 sRGB ICC 和非方形像素比例会跳过并给出提示。白名单复制不是 ICC 转换，也不会复制 XMP、路径、文字引擎或智能对象工程数据。

## 9. 写入、验证、取消和失败语义

Writer 保留 PSD v1 RGB8、Unicode luni、lyid、lsct、Raw/RLE、大端、长度回填及 padding。层通道逐个写入，避免先保留所有图层的压缩副本。校验涵盖像素数组、名字代理对、重复ID、平衡分组、坐标溢出和格式上限。

路径 Write 使用同目录临时文件。新的回调重载允许在临时文件关闭后、最终提交前读取验证；回调返回前必须关闭句柄。任何写入/校验/取消异常都清除临时文件并保留旧目标；文件系统不支持安全 Replace 时失败，不先删旧文件。

默认 OutputVerifier 核对：头部尺寸/位深/通道、层序/类型/名称/实际ID、可见性/opacity、像素层边界及每个 RGBA 字节、继承资源及 merged 白底 RGB/Alpha 原始字节。它验证存储完整性，不模拟 Photoshop 字体或混合渲染。

最后一个可取消进度点在提交前验证阶段。提交后不再调用会取消的进度回调，避免完成的文件被报告为失败。BatchService 在每项开始前和进行中传递取消，取消停止后续项，不撤销先前成功。JSON 单独提交，失败只增加警告并清空 report.MetadataPath，正确 PSD 保留。

## 10. 验证层级与限制

融合版扩展了原核心回归、严格 reader/验证、资源/ICC、原稿策略、命名/批量规划/CLI参数，以及编辑器完整工作流测试。实际运行计数、工具链、日志和失败注入结果由 [TestReport.zh-CN.md](TestReport.zh-CN.md) 记录，不从测试方法个数推导通过结论。

编辑器自测提供只读预检、源快照不变、空组/Canvas、原稿复用与修改保护、批量配置与重名、部分失败、取消保护等断言。当前环境没有完成 Unity GPU 和 Photoshop GUI验收；纯托管方法和真实程序集编译通过，不能代替真实项目材质、字体、GPU和原插件回导的本机测试。
