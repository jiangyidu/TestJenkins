# Prefab to PSD

将 Unity UGUI Prefab 导出为分层 PSD 的编辑器脚本系统，融合版 **v2.0.0**，目标版本为 **Unity 2018.4.36f1**。

支持离屏渲染、层级分组、透明度与遮罩处理、四种导出预设、只读预检、批量导出、命令行导出、严格匹配的源 PSD 参考，以及提交文件前的输出校验。

## 安装和使用

1. 从 [Releases/v2.0.0](Releases/v2.0.0) 下载 `.unitypackage` 并导入 Unity；也可以复制本仓库的 `Assets/PrefabToPsd` 到项目 `Assets` 目录。
2. 打开 **Tools → Prefab to PSD → Export Window**，选择 UGUI Prefab 并设置输出路径。
3. 在目标 Unity 环境中运行 **Tools → Prefab to PSD → Run Self Tests**。

完整源码、Unity 导入包、说明、测试报告和样例也打包在 [完整交付 ZIP](Releases/v2.0.0/PrefabToPsd_Integrated_v2.0.0_Unity2018.4.36f1_Delivery.zip) 中。

## 文档和验证

- [详细使用说明](README.zh-CN.md)
- [融合变更说明](Docs/IntegrationChanges.zh-CN.md)
- [实现说明](Docs/Implementation.md)
- [测试报告与验证边界](Docs/TestReport.zh-CN.md)
- [测试记录](Tests/Results/all-checks-summary.json)
- [来源说明](Docs/Provenance.txt)

已通过真实 Unity API 引用编译、591 项托管断言、130 项原导入器读取检查，以及 PSD 独立读取与封装校验。

当前环境未完成 Unity GPU 渲染、Unity 导入及 Photoshop GUI 实测；测试报告说明了验证范围和目标环境中的自测方法。

## 复现托管检查

在 Linux 环境安装 Unity 2018.4.36f1 Editor、Python 3 和测试依赖后运行：

```sh
python -m pip install -r Tests/Standalone/requirements.txt
python Tests/run_all_checks.py /path/to/Unity/Editor
```

可选的原导入器兼容性检查还需要用户自行提供原插件源码，并添加 `--original-importer /path/to/PsdTOUgui`。本仓库不分发原插件或 Unity 安装文件。
