# Integrated 2.0.1 测试与流程验证

日期：2026-10-22（Asia/Shanghai）。目标：Unity 2018.4.36f1、.NET 4.x Equivalent、UGUI。

## 已实际执行

统一入口 Tests/run_all_checks.py 在 v2.0.1 源码上完成，compile、core、reference、workflow、probe、original-reader 六项任务退出码均为 0：

| 检查 | 结果 | 原始记录（Tests/Results/ 内） |
| --- | --- | --- |
| 全包编译 | 16 个 C#、63 个真实程序集、C# 6，通过 | unity-api-compile.txt |
| PSD 基础回归 | 267 项断言 | integrated-core/baseline/core.log |
| Reference reader / 提交前校验 | 103 项断言 | integrated-core/reader/results.log |
| 资源白名单 / sRGB ICC | 76 项断言 | integrated-core/resources/resource-tests.log |
| 原稿参考策略 | 37 项断言 | reference/reference.log |
| 命名 / 批量规划 / 配置 / CLI | 108 项断言 | workflow/results.log |
| 实际GPU自测中的私有 PSD reader | 61 项托管断言 | probe/results.log |
| 捕获正控像素判定 | 51 项托管断言 | probe/results.log |
| 独立 psd-tools 读取和重合成 | 7 个 PSD 通过 | 基础 interop.log、资源 independent-resources.log |
| 独立 Pillow / LittleCMS ICC 读取 | 2 个 profile 通过 | integrated-core/resources/independent-resources.log |
| 原上传 PsdTOUgui reader/decoder | 130 项检查、5 个 PSD 通过 | original-reader.txt |

算法/策略合计 **703 项托管断言**（446+37+108+112）；原读取器的130项检查单列。新增112项由61项实际私有reader检查和51项捕获控制判定组成。不同测试计数不能与B的“16项自测”直接相除排名。

编译使用 Unity 官方 2018.4.36f1（changeset 6cd387d23174）的 Data/Tools/RoslynNet46/csc.exe、Mono 和真实 UnityEngine/UnityEditor/UGUI 程序集，没有用自制 API stub。最终源码 SHA256 在 Tests/Results/all-checks-summary.json；打包时会核对与成功测试快照一致。

## 验证覆盖

### 文件和提交保护

Raw/RLE、分组、Unicode/emoji、ID、边界、可见性、opacity、逐层RGBA与合成通道均有真实文件校验。合成RGB使用PSD白底预合成，图层仍是straight RGBA；独立工具从图层重合成，避免只验证已有预览。

VerifyOutput默认开启：写同目录临时文件，关闭写流，使用PsdOutputVerifier核对完整图层、ID、名字、坐标、资源和merged通道，然后才原子提交。校验失败或取消清理临时文件并保留旧输出。

### 空白捕获反馈与 v2.0.1 回归

用户在 Unity 2018.4.36f1、Direct3D11、Gamma 下报告红色取样预期255但实际0，并确认业务PSD内容为空。已有“图层数量”和PSD结构校验不能证明GPU产生了可见像素，因此该反馈没有被视为正常导出或仅测试误差。

新增的托管Probe测试使用真实Unity程序集编译全部生产脚本，通过反射调用GPU自测本身的私有 `PsdProbe.Read`。独立指定红蓝半透明、非对称坐标、Raw/RLE、Unicode、图层边界和alpha边界，验证截图中的红点应读取为 `(255,0,0,128)`；固定文件输入下未复现reader错误。这是文件解析证据，不是GPU渲染证据。

新增的捕获控制判定测试覆盖正常半透明红图、1×1与非方形尺寸、全透明、错误RGB、错误alpha、不透明、重复绘制、边角缺失、非法输入及容差边界。它验证生产校准判定函数能拒绝这些错误数组；没有在本环境实际绘制该控制图。

捕获流程现依次尝试 Game、SceneView、SceneView+显式UI几何发射、Preview+显式UI几何发射。每条候选路径均须读回半透明红色正控，并在隐藏控制对象后得到全透明负控；全部失败则在写PSD前抛错。ARGBFloat还必须支持alpha混合，否则尝试具备渲染和混合能力的ARGBHalf。即使校准通过，源合成图完全透明也会拒绝写入或替换旧PSD。业务像素断言没有放宽。

上述变更已通过真实2018.4 API编译与托管回归，但仍需发生故障的Windows机器复测，尚不能据此宣称空白捕获的具体根因已确认或GPU问题已实机修复。升级和证据收集见 [BlankCaptureFix.zh-CN.md](BlankCaptureFix.zh-CN.md)。

### 原稿参考

真实reader/writer测试覆盖完全相同像素复用、单字节编辑/移动/改名保护、唯一匹配及歧义拒绝、ID碰撞处理、保持当前层序、危险.txt/@slice/#/$名称、原稿缺失和同源目标路径保护、未知RGB profile拒绝、方形/非方形PixelAspect。没有用文字样式近似或像素误差阈值覆写修改。

这些是托管策略和文件测试，不把构造RGBA当成GPU字体截图。

### 工作流和批量

覆盖Clone和四预设、艺术名/安全像素名、相邻布局标签、Unicode/Windows保留名、大小写/规范化碰撞、GUID/场景身份、输入集合变化下稳定文件名，以及CLI单源/列表输入、预设/原稿/尺寸/内存与错误参数。

GUI和CLI调用同一服务，逐项克隆同一配置。资产始终用完整GUID；同一资产单独和放在批次中路径一致。批量资产要求Prefab根，内部持久化子对象明确拒绝，避免共享根GUID互相覆盖。场景子树按层级身份。

## 已提供但未在本环境运行的编辑器测试

Tools / Prefab to PSD / Run Self Tests 已接入工作流测试，并通过编译：

- 预检不改源Prefab哈希、组件或场景状态。
- 文字.img、空分组、隐藏全画布#Canvas实际读回。
- Unity真实导出作为参考后严格复用；改颜色/文字对齐不复用，层序不回退。
- 批量配置、重名、去重、部分失败、取消、持久化内部子对象拒绝。
- 提交前验证阶段取消，旧文件保持且无临时残留。
- 半透明叠色、Mask/RectMask2D逐层隔离、CanvasGroup、非默认PPU九宫格、Raw/RLE一致性。
- 捕获控制正负校准、完整实际RGBA、自测合成图alpha边界／图层边界，以及显卡和CanvasRenderer状态诊断。

自测报告默认保存在工程 `Temp/PrefabToPsdSelfTests/self-test-report.txt`，失败时也会写出。第一张合成PSD成功生成后，额外保存 `composite-rle.diagnostics.txt`、`composite-rle.preview.png` 和PSD；若在捕获校准阶段失败，这些图像文件可能尚未生成。完全无活动Graphic或源合成图全透明的根会拒绝；保留空组指有可导出内容的Prefab内部空组。

本环境无图形启动诊断停留在legacy licensing初始化，出现CDN超时和Error enumerating network interfaces: Operation not permitted，退出1。它没有执行GPU自测，也没有提供或绕过授权。日志见Tests/Results/unity-startup/editor.log。

**没有声明Unity GPU全流程或Photoshop桌面验证通过。** API编译、托管策略和文件互通不能替代这两项。全流程性能、复杂TMP/SRP/任意自定义Shader、接近2GiB文件压力也未实测。

## 复现

先安装Tests/Standalone/requirements.txt，再从源码根目录执行（Linux工具链）：

    python Tests/run_all_checks.py '/path/to/Unity/Editor'

可选加入用户自行解压的原上传插件：

    python Tests/run_all_checks.py '/path/to/Unity/Editor' --original-importer '/path/to/PsdTOUgui'

不会自动下载Unity、获取授权或启动GPU。交付不包含Unity程序集或原上传插件。

目标机建议：覆盖原插件目录（特别是已有 `Assets/ThirdParty/PrefabToPsd` 的项目，避免第二份脚本）→等待编译→Run Self Tests→通过后导出实际完整UI根→Photoshop按sRGB打开并逐层核对。若仍失败，请提供上述报告和已生成的诊断图／PSD。Linear项目的默认Photoshop混合空间仍可能与Unity不同，详见README。
