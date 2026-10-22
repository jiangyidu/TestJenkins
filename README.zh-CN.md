# Prefab to PSD Integrated 2.0.1

面向 **Unity 2018.4.36f1 / UGUI** 的编辑器工具。以此前实现 A 的 Unity 实际渲染、透明度恢复、PSD 写入和文件保护为基础，吸收 B 的预检、导出预设、命名、批量和原稿参考能力。

输出为 **RGB 8 位分层 PSD**，并可生成保存文字、布局、资源和参考来源信息的 JSON。文字、遮罩、九宫格和 UI 效果烘焙到像素层；不生成 Photoshop 原生可编辑文字、蒙版或智能对象，也不提供完整的 PSD 无损往返承诺。

更新日期：2026-10-22（Asia/Shanghai）。实际验证结果与仍待实机完成的项目见 [测试报告](Docs/TestReport.zh-CN.md)。

v2.0.1 针对“测试红色取样预期255但为0，实际导出的PSD也为空”的反馈调整捕获路径，并加入写入前的已知 UI 图案校准和全透明拒绝。实现与复测步骤见 [空白捕获修复说明](Docs/BlankCaptureFix.zh-CN.md)。本环境未执行 Windows / Direct3D11 GPU 自测，仍需在发生问题的机器验证。

## 安装与升级

1. 使用 Unity **2018.4.36f1**，Player Settings 的 Scripting Runtime Version 采用 `.NET 4.x Equivalent`，建议 API Compatibility Level 为 `.NET 4.x`。
2. 新安装：导入本版 `.unitypackage`，或把源码包中的 `Assets/PrefabToPsd` 整体复制到项目的 `Assets` 下。
3. 从此前 A 或融合版升级：用本版完整的 `Assets/PrefabToPsd` 内容替换旧插件目录，保留自己的导出文件和业务资产。若旧插件在 `Assets/ThirdParty/PrefabToPsd`，覆盖该原目录，不再额外安装一份 `Assets/PrefabToPsd`。不要把 A、B 与融合版的脚本并行叠加，也不要复制旧的编译 DLL 代替本版源码。
4. 等待 Console 编译完成，打开 `Tools > Prefab to PSD > Export Window`。

工具代码全部位于 `Editor` 目录，没有游戏运行时组件。正常导出不依赖 Python、Photoshop、第三方 PSD DLL 或原 `PsdTOUgui`。安装原插件时，可读取其来源元数据；未安装时仍可独立导出。需要可正常渲染且支持浮点纹理渲染、Alpha混合及读回的 Unity 编辑器和图形设备。

## 第一次导出

1. 将 Project 中的 UGUI Prefab，或 Hierarchy 中包含完整上下文的 UI 根节点拖入窗口。根节点必须有 `RectTransform`。
2. 选择预设，确认自动画布尺寸，或填写明确的宽、高。
3. 点击“刷新预检”，查看节点、隐藏状态、名称和估算内存。预检仅读取已有布局，不重建源对象布局；“定位”可以选中对应节点。
4. 保持“提交前校验 PSD”开启，点击“导出当前对象…”。
5. 检查窗口/Console 的结果、警告，以及同目录的 `.prefab.json`。

升级后先运行 `Tools > Prefab to PSD > Run Self Tests`，通过后再导出业务UI；也可用 `Create Demo Prefab` 创建 640×480 示例。自测报告和输出默认写入本 Unity 工程的 `Temp/PrefabToPsdSelfTests`，临时测试资产会在结束时清理。v2.0.1 会输出显卡／格式混合能力和捕获控制结果；第一张合成图成功生成后，还会写出 `composite-rle.diagnostics.txt`、`composite-rle.preview.png` 和 PSD。失败复测文件的收集方式见 [修复说明](Docs/BlankCaptureFix.zh-CN.md)。

## 四种预设

| 预设 / API 枚举 | 适用任务 | 主要行为 |
| --- | --- | --- |
| 当前外观 / `CurrentAppearance` | 导出当前可见 UGUI | 默认保留当前对象名和层级，使用 Unity 渲染；不隐式查找参考 PSD |
| 美术交付 / `ArtistDelivery` | 交付便于阅读的图层 | 清理已知工程后缀和布局标签，保留业务下划线名称，不添加九宫格回导标签 |
| 回导结构 / `RoundTripStructure` | 保留可用的文件夹和控件语义 | 保留空分组，添加隐藏 `#Canvas`；使用 `.group/.btn/.tg/.sld/.inp/.sv` 等容器后缀；文字像素层使用 `.img` |
| 原稿参考 / `SourceReference` | 参考最初 PSD 的身份与安全资源 | 保留当前层序，严格匹配来源；仅完全相同的像素可复用；可恢复匹配像素层 ID 和安全名称，继承允许的资源 |

预设在用户明确选择时应用；导出入口不会在执行中重新套用预设、覆盖用户调整的参数。尺寸、压缩和内存预算不随预设重置。切离原稿参考预设时，会清除先前的显式参考路径，避免普通导出意外沿用旧原稿。

“回导结构”保留的是可表达的结构和命名，不等于恢复原 PSD 的文字引擎、交互绑定或完整组件。已经烘焙的文字不会标为 `.txt`，九宫格不会重新附加 `@slice`，避免原导入器把像素再次解释成文字或再次切片。JSON 仍是文字原文和精确 Unity 局部布局的主要记录。

## 原稿参考的规则

选择“原稿参考”预设，或填写显式参考 PSD 路径，才启用参考处理。仅 `AutoDetectReferencePsd=true` 不会让“当前外观”模式自动加载原稿。

自动查找只使用已知 `PsdGeneratedScreen` 来源成员（包括原包的 `SourceAssetPath`）和同目录同名 PSD；多个候选不会猜测，应明确选择。显式文件不存在、无法解析，或原稿参考模式没有找到文件时，导出会在输出提交前失败。**输出文件不能是参考 PSD 自身。**

匹配优先使用唯一来源 ID，其次为原始精确名称及边界，再次为当前精确名称及边界；不进行模糊名字匹配。身份匹配与像素复用是两件事：图层可以恢复来源 ID，同时继续保留用户修改后的当前像素。

像素复用要求：

- 当前和参考的边界、尺寸、可见性及逐字节 RGBA 全部一致。
- 参考像素层和相关祖先满足安全条件；参考 opacity 必须为 255，不把组透明度重新表达第二次。
- 不采用近似颜色阈值、字号容差或“文字样式相似”来覆盖当前渲染。
- 不恢复原稿排序；当前 Unity 导出的层序始终保留。

所以换字体、改对齐、加描边或改变一个可见像素，都不会仅因“样式看起来相近”而被替换成旧稿。严格条件也意味着很多看似相同的层不会复用，这是保留当前修改的取舍。参考 reader 面向 PSD v1、RGB8；像素复用支持 Raw/RLE，其他压缩或不受支持的内容不会伪造成功。

参考名称只有在能确认当前名称未改、像素一致且名称适合已栅格内容时才恢复。`.txt`、`@slice`、`#/$` 等危险语义不作为安全像素层名称恢复，原名记录在 JSON；原稿参考预设会安全化当前危险名称。来源名字、匹配 ID 和是否复用均有逐层 JSON 标记。

允许继承的 Image Resources 仅为：

- `1005`：经校验的 ResolutionInfo。
- `1039`：经矩阵和 TRC 等内容核验的 sRGB ICC；不盲贴未知/Adobe RGB 工作空间。
- `1064`：经校验且比例为 1.0 的 PixelAspectRatio。Unity 设计画布使用方形像素，其他比例会跳过并提示。

ICC 继承不是颜色空间转换。没有可继承的已验证 ICC 时，输出仍按 sRGB 像素解释；Photoshop 对未标记文档请指定 sRGB IEC61966-2.1。

## 批量导出

在 Project 选择多个 Prefab，再点击窗口的“使用当前配置批量导出…”选择目录。批量入口克隆窗口当前配置，保留预设、尺寸、参考路径、压缩、JSON、验证和内存设置，不会另建一份默认配置。

资产输出始终附完整 GUID，场景源使用稳定层级身份后缀；同一源单独或与其他源一起导出时路径一致，避免不同批次的同名输入互相覆盖。文件名处理 Unicode、Windows 保留名和跨平台非法字符。重复选择同一对象会去重。某一项失败会记录错误并继续处理后续项；取消会停止后续任务，已经提交成功的文件保持成功。重复运行相同输入会面向同一批输出路径，由原子写入保护覆盖过程。

资产批量入口要求 Prefab 根资产。同一 Prefab 内部的持久化子对象共享根 GUID，批量服务会明确拒绝它们；需要导出这种子树时使用单对象导出。场景子树可按独立层级身份导出。

显式参考路径会原样传给每个项目；若不同 Prefab 对应不同原稿，应分别导出，或让原稿参考模式按各自来源自动发现。严格匹配不会把不对应的原稿像素强行套入其他 Prefab。

## 输出与校验

| 输出 | 内容 |
| --- | --- |
| `界面.psd` | 内容像素层、Unicode 名称、可选分组、合成预览、安全资源及可选隐藏 Canvas 标记 |
| `界面.prefab.json` | schema 2.0；导出器版本、实际文字与字体信息、节点状态、原始变换、资源引用、图层边界、参考匹配与复用统计、警告 |

`LayerCount` 为内容 Graphic 数，不把隐藏 Canvas 辅助标记算成内容层；该数量本身不能证明GPU画出了像素。隐藏 Graphic 不输出像素；选项可以保留空文件夹，JSON 记录所有源节点的状态和原始布局。保留空组针对有可见内容的 Prefab；整个子树没有可导出 Graphic，或源合成图完全透明时会明确报错。

默认的提交顺序为：

```text
源对象元数据与配置快照
  → 隔离副本中计算布局
  → 半透明红色UI正控与隐藏后全透明负控
  → 校准通过的相机路径渲染源内容／全透明检查
  → 命名和严格原稿参考
  → 同目录临时 PSD
  → 读回核对图层/通道/ID/边界/资源/合成预览
  → 原子替换或移动到最终文件
  → 独立提交 JSON
```

捕获校准失败、源合成图全透明、文件校验失败或提交前取消时，不覆盖原有 PSD，并清理临时文件。文件验证是对 writer 承诺的内容做完整性检查，不能替代捕获校准，也不等于 Photoshop 渲染认证。JSON 在 PSD 之后单独提交；若 JSON 写入失败，保留正确 PSD 并报告警告，两者不是联合事务。

## 画布、颜色与容量

自动尺寸依次使用根节点的 `PsdGeneratedScreen.CanvasSize`、合适的根 `CanvasScaler.referenceResolution`、有效 `RectTransform.rect`，最后回退 1920×1080。显式尺寸会影响伸展和布局计算。

导出在预览场景的副本中将根旋转归零、缩放归一，按输出尺寸建立二维设计画布，子节点变换正常参与 Unity 渲染。这是设计画布导出，不是 Game View 截屏。只选子树时，外部祖先的 CanvasGroup、Mask 和布局上下文不会包含。

捕获先尝试普通 Game 相机；只有已知半透明红色UI能正确读回、隐藏控制图后输出全透明，才继续抓取源内容。需要时尝试 SceneView／Preview 与显式UI几何发射路径，每条路径都接受相同校准。`ARGBFloat` 同时满足可渲染和可混合时优先使用；否则尝试具备相同能力的 `ARGBHalf`。这些检查不会放宽业务像素自测的断言。

Linear 项目在线性空间恢复 Alpha 后转为 sRGB；完整 merged 图来自 Unity。Photoshop 默认 sRGB 数值混合与 Unity Linear 混合不同，重新合成半透明层时应使用匹配的混合空间设置。

- 默认内存预算 512 MB，可设 16–4096 MB；它是缓冲估算预算，不是进程峰值内存硬上限。
- 导出画布最多 16,777,216 像素，单边还受 GPU `maxTextureSize` 和 PSD 30000 限制。
- PSD v1 文件最大 2,147,483,647 字节；最多 32767 条记录，分组开始/结束也计数，不输出 PSB。
- 每个 Graphic 双背景全画布渲染，之后按透明边界裁剪。大画布、多图层会增加 GPU、读回和验证时间。
- 取消在进度回调处生效；单次 GPU 渲染及写入不会被强制中断。默认校验入口在提交前仍提供取消点，提交后不再把成功改记为取消。

## 支持范围

支持 UGUI Image、RawImage、Text、标准 Mask/RectMask2D、CanvasGroup、Shadow/Outline、布局组件及子节点变换。图片不要求源 Texture 开启 Read/Write。TMP 和自定义 Graphic 没有硬依赖，可通过实际渲染尝试导出，但复杂子网格或自定义行为未认证。

不导出 3D、SpriteRenderer、粒子、动画播放或交互逻辑。默认针对内置渲染管线，SRP/LWRP 未验证。任意加法、目标依赖混合及 HDR 不能全部由普通 PSD normal RGBA 层表达。复杂跨 Canvas 的三维排序也不保证能重建。

预检不主动修改源布局；实际导出在副本中进行。不过项目的 `ExecuteInEditMode` / `ExecuteAlways` 生命周期回调仍可能运行，工具不能阻止业务脚本自行修改全局状态或资产。

## Editor API

```csharp
using System.IO;
using PrefabToPsd;
using UnityEditor;
using UnityEngine;

public static class ExportUiExample
{
    [MenuItem("Tools/Export UI Example")]
    public static void Run()
    {
        GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/UI/Main.prefab");
        ExportOptions options = new ExportOptions();
        PrefabPsdPresets.Apply(options, ExportPreset.SourceReference);
        options.ReferencePsdPath = Path.GetFullPath("Art/Main.psd");
        options.Width = 1920;
        options.Height = 1080;
        string directory = Path.GetFullPath("UIExports");
        Directory.CreateDirectory(directory);
        ExportReport report = PrefabPsdExporter.Export(source, Path.Combine(directory, "Main.psd"), options);
        Debug.Log(report.PsdPath + "; verified=" + report.VerificationPassed);
    }
}
```

所有 Unity API 调用在编辑器主线程执行。`ExportOptions.Clone()` 生成配置快照，进度回调可抛出 `OperationCanceledException`。`PrefabPsdBatchService.Export` 与单对象导出使用同样的配置及 exporter。

## 命令行

需要有效 Unity 授权与可工作图形会话，**不要添加 `-nographics`**。以下为 Bash 示例，路径替换为本地绝对路径。

```bash
/path/Unity/Editor/Unity \
  -batchmode -projectPath /absolute/project \
  -executeMethod PrefabToPsd.PrefabPsdBatch.Export \
  -prefabToPsdInput Assets/UI/Main.prefab \
  -prefabToPsdOutput /absolute/output/Main.psd \
  -prefabToPsdPreset ArtistDelivery \
  -prefabToPsdWidth 1920 -prefabToPsdHeight 1080 \
  -logFile /absolute/output/export.log
```

批量输入文件为 UTF-8 纯文本，每行一个 `Assets/... .prefab` 路径，空行忽略：

```bash
/path/Unity/Editor/Unity \
  -batchmode -projectPath /absolute/project \
  -executeMethod PrefabToPsd.PrefabPsdBatch.Export \
  -prefabToPsdInputsFile /absolute/inputs.txt \
  -prefabToPsdOutputDirectory /absolute/output \
  -prefabToPsdPreset SourceReference \
  -prefabToPsdMemoryMB 1024 \
  -logFile /absolute/output/batch.log
```

输入参数二选一：`-prefabToPsdInput` / `-prefabToPsdInputsFile`；输出参数二选一：`-prefabToPsdOutput` / `-prefabToPsdOutputDirectory`。多个输入必须使用输出目录。显式尺寸必须成对给出。`-prefabToPsdReference` 提供参考路径；可选 `-prefabToPsdNoVerify`、`-prefabToPsdFlat`、`-prefabToPsdNoMetadata`。成功退出0；异常、批量失败或取消退出1。参数/资产加载失败在开始批量前报错；批量过程中各项导出错误汇总处理。

GPU 自测入口：

```bash
/path/Unity/Editor/Unity \
  -batchmode -projectPath /absolute/project \
  -executeMethod PrefabToPsd.PrefabPsdSelfTests.RunBatch \
  -prefabPsdTestOutput /absolute/output/self-tests \
  -logFile /absolute/output/self-tests.log
```

## 开发检查与文档

真实程序集编译与纯托管测试不需要启动 Unity GUI；Python 只用于开发验证，不是插件运行依赖。具体实测计数以 [TestReport.zh-CN.md](Docs/TestReport.zh-CN.md) 为准。本环境未完成有授权 Unity GPU/Photoshop GUI 验收，不能把编译或托管测试通过写成全部实机场景通过。

```bash
python Tests/compile_unity.py /path/Unity/Editor
UNITY_EDITOR_PATH=/path/Unity/Editor bash Tests/IntegratedCore/run-integrated-core-tests.sh
python Tests/Reference/run_reference_tests.py /path/Unity/Editor
python Tests/Workflow/run-workflow-tests.py /path/Unity/Editor
python Tests/Probe/run_probe_tests.py /path/Unity/Editor
```

先在自己的 Python 环境安装 `Tests/Standalone/requirements.txt`；如使用自定义 Python，可设置脚本支持的 `PYTHON`/`PYTHONPATH`。上述路径布局主要按本次 Linux Unity 工具链提供。

- [融合变更与取舍](Docs/IntegrationChanges.zh-CN.md)
- [实现说明](Docs/Implementation.md)
- [原 PsdTOUgui 上传包分析](Docs/OriginalPackageAnalysis.md)
- [测试报告](Docs/TestReport.zh-CN.md)
- [空白捕获修复与目标机复测](Docs/BlankCaptureFix.zh-CN.md)
