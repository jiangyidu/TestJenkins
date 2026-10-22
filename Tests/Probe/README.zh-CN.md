# GPU 自测 PSD 读取器的托管回归

运行：

```sh
python Tests/Probe/run_probe_tests.py /path/to/Unity/Editor
```

使用 Unity 2018.4 的真实程序集及 C# 6 编译全部插件源码，通过反射调用实际的私有 `PrefabPsdSelfTests.PsdProbe.Read`，不复制解析器，也不调用 Unity 原生图形接口。

真实 `PsdWriter` 分别生成 Raw/RLE 文件；独立指定的预期数据覆盖截图中的红色取样点、蓝色、半透明重叠、透明区、1/2/63/127/128/192/254/255 透明度、Unicode 分组、准确图层边界与原始通道、非对称位置和上下/左右镜像防错。

同时直接测试生产 `PrefabPsdExporter.CheckCaptureControl`：正常半透明红、1×1/小画布、透明空白、错误不透明度、重复绘制、颜色或通道错误、九个取样位置被裁剪、无效尺寸/缓冲区，以及容差内外边界。校验过程不得修改输入像素。

此测试用于排查 PSD 解析、白底还原、压缩和坐标解释问题，不能证明 Unity GPU 渲染正确。结果默认写入当前目录下的 `Results/`；全套流程可用 `--output` 指定其他证据目录。
