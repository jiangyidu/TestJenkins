# Prefab to PSD

将 Unity UGUI Prefab 导出为分层 PSD 的编辑器脚本系统，融合版 **v2.0.1**，目标版本为 **Unity 2018.4.36f1**。

支持离屏渲染、层级分组、透明度与遮罩处理、四种导出预设、只读预检、批量导出、命令行导出、严格匹配的源 PSD 参考，以及提交文件前的输出校验。

## 安装和使用

1. 从 [Releases/v2.0.1](Releases/v2.0.1) 下载 `.unitypackage` 并导入 Unity；也可以复制本仓库的 `Assets/PrefabToPsd` 到项目 `Assets` 目录。已有插件在 `Assets/ThirdParty/PrefabToPsd` 时，从源码包覆盖该原目录，避免同时留下两套脚本。
2. 等待编译，在目标 Unity 环境中运行 **Tools → Prefab to PSD → Run Self Tests**。
3. 自测通过后，打开 **Tools → Prefab to PSD → Export Window**，选择 UGUI Prefab 并设置输出路径。

完整源码、Unity 导入包、说明、测试报告和样例也打包在 [完整交付 ZIP](Releases/v2.0.1/PrefabToPsd_Integrated_v2.0.1_Unity2018.4.36f1_Delivery.zip) 中。

v2.0.1 针对“PSD 有结构但画面为空”的反馈增加了 UGUI 捕获正负校准、相机路径回退、浮点纹理混合能力检查及完整像素诊断；校准失败或源合成图全透明时，在写入前停止并保留旧 PSD。[空白捕获修复与复测说明](Docs/BlankCaptureFix.zh-CN.md) 说明了机制、升级方式和仍需目标机验证的范围。

## 文档和验证

- [详细使用说明](README.zh-CN.md)
- [融合变更说明](Docs/IntegrationChanges.zh-CN.md)
- [实现说明](Docs/Implementation.md)
- [测试报告与验证边界](Docs/TestReport.zh-CN.md)
- [空白捕获修复与复测](Docs/BlankCaptureFix.zh-CN.md)
- [测试记录](Tests/Results/all-checks-summary.json)
- [来源说明](Docs/Provenance.txt)

v2.0.1 已完成统一复跑：真实 Unity API 引用编译、703 项托管断言、130 项原导入器读取检查，以及 7 个独立 PSD／2 个 ICC profile 验证全部通过。新增的 112 项断言直接测试实际自测 PSD reader 与捕获控制判定；详细记录见 [测试报告](Docs/TestReport.zh-CN.md) 和 [测试摘要](Tests/Results/all-checks-summary.json)。

当前环境未完成 Unity GPU 渲染、Unity 导入及 Photoshop GUI 实测，尚不能声明 Windows / Direct3D11 上的空白问题已实机解决；请先运行目标环境自测。

## 复现托管检查

在 Linux 环境安装 Unity 2018.4.36f1 Editor、Python 3 和测试依赖后运行：

```sh
python -m pip install -r Tests/Standalone/requirements.txt
python Tests/run_all_checks.py /path/to/Unity/Editor
```

可选的原导入器兼容性检查还需要用户自行提供原插件源码，并添加 `--original-importer /path/to/PsdTOUgui`。本仓库不分发原插件或 Unity 安装文件。
