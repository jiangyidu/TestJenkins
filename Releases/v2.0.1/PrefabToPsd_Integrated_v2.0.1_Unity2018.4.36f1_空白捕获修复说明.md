# v2.0.1 空白捕获修复与目标机复测

目标版本：Unity 2018.4.36f1，UGUI。日期：2026-10-22（Asia/Shanghai）。

## 已收到的故障

用户在Windows／Direct3D11／Gamma下执行自测，PSD尺寸、图层数量和透明区检查通过，但红色取样 `(10,10)` 的R通道预期255、实际0。用户随后确认实际业务Prefab导出的PSD内容也为空；因此不能把故障仅归因于测试取样，也不能把早先的“导出成功／结构校验通过”当成视觉成功。

现有图层数统计只证明发现了活动Graphic；writer读回校验只证明PSD保存的数据符合输入文档。两者都可能在上游捕获已经空白时通过。

## 已确认的缺口与补丁机制

固定红蓝像素的托管回归已直接调用自测中的真实私有 `PsdProbe.Read`，正确读取了红点 `(255,0,0,128)`、图层边界与非对称坐标。它排除了这些固定输入下的reader／白底还原错误，尚不能替代发生故障机器的GPU证据。

v2.0.1 做了以下调整：

1. 捕获优先使用Game相机；需要时依次尝试SceneView、SceneView+显式 `EmitWorldGeometryForSceneView`、Preview+显式发射路径。
2. 在同一隔离画布中暂时隐藏源副本，绘制已知的半透明红色UI控制图。候选路径必须读回接近 `(255,0,0,128)` 的RGB与alpha，再在隐藏控制图后读回全透明负控，才用于业务捕获。正控可识别未绘制、错误混合或重复绘制，负控用于发现控制图残留。
3. `ARGBFloat` 除了可渲染，还必须支持alpha混合；不满足时尝试具备这些能力的 `ARGBHalf`。如果两者均不满足，明确报错。
4. 所有候选路径校准失败，或校准后源合成图仍完全透明时，在写入PSD前抛出异常，保留旧PSD。完全透明的内容不再作为成功导出。
5. 保留原有像素预期和容差，增加完整RGBA、取样坐标、图层边界、非透明像素统计、预览PNG、显卡能力与CanvasRenderer状态诊断。

这些调整针对已经确认的能力检查和捕获验收缺口，并提供经过校准的渲染路径选择。**本环境未执行Unity GPU，尚未确认用户Windows／Direct3D11空白问题的全部根因，也不能声明该机器已实测修复。**

## 升级时保持一套脚本

当前项目截图中的插件位于 `Assets/ThirdParty/PrefabToPsd`。建议解压新版源码，取其中 `Assets/PrefabToPsd` 的插件内容覆盖这个原目录，保留自己的业务资源和导出文件。不要同时在 `Assets/PrefabToPsd` 再装第二套同命名空间脚本，也不要用旧DLL覆盖源码。

若项目本来使用默认的 `Assets/PrefabToPsd`，可在原位置更新或导入新版 `.unitypackage`。等待Unity完成编译，确认Console没有重复类型等编译错误。

## 在发生故障的机器复测

1. 执行 `Tools > Prefab to PSD > Run Self Tests`。先检查报告开头的导出器版本应为 `2.0.1`，Unity版本和显卡信息应来自当前机器。
2. 自测通过后，再用 `Export Window` 导出原先出现空白的完整UI根。
3. 打开PSD检查merged预览与各个内容图层；也检查同目录JSON和窗口／Console警告。

默认自测输出目录为Unity工程根目录下的 `Temp/PrefabToPsdSelfTests`。如果仍然失败，请提供：

- `self-test-report.txt`：失败时也应生成，含显卡、格式混合能力、捕获候选路径和异常。
- `composite-rle.diagnostics.txt`：若生成，含完整像素取样、alpha边界、图层边界及渲染器状态。
- `composite-rle.preview.png` 和 `composite-rle.psd`：若生成，可直接检查是空白、错位还是颜色异常。

若在捕获校准阶段已经停止，合成PSD和预览图可能尚未生成；此时先提供报告即可。异常中“未写入／未替换PSD”表示旧文件得到保护，不表示GPU自测已经通过。

## 本次实际验证

v2.0.1统一检查的六项任务退出码均为0：16个正式脚本以C#6、63个真实Unity2018.4程序集编译通过；703项托管断言、130项原导入器读取检查通过，另有7个独立PSD和2个ICC profile验证。新增112项中，61项调用真实私有PSD reader，51项检查生产捕获控制判定。

这些托管检查没有启动Unity GPU或Photoshop桌面程序。完整记录见 [测试报告](TestReport.zh-CN.md) 和 `Tests/Results/all-checks-summary.json`。

新版包位于仓库的 `Releases/v2.0.1/`；完整交付包名为 `PrefabToPsd_Integrated_v2.0.1_Unity2018.4.36f1_Delivery.zip`。
