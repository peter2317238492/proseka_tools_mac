# ProsekaTools WinUI3 -> Avalonia (macOS) 迁移计划

本文基于对 `proseka_tools/ProsekaToolsApp` 的代码与 README 的检查。

## 现状概览（功能 + 架构）

### 功能模块
1) 数据抓取（Tab1 / GrabData）
- 使用 `HttpListener` 监听 `http://+:8000/`，支持 `/status`、`/upload`、`/upload.js`。
- 上传文件按 `X-Original-Url` 分类并落盘到 `%APPDATA%\ProsekaTools\captures`。
- WinUI 页面：`Pages/GrabDataPage.xaml(.cs)`。

2) suite 工具（Tab2）
- 选择/拖拽 `.bin` 文件上传到远端服务（`go.mikuware.top` warm-up + `101.34.19.31:5225/uploadTwSuite`）。
- WinUI 页面：`Pages/Tab2Page.xaml(.cs)`。

3) Mysekai 工具（Tab3）
- 通过 `Services/sssekai.exe apidecrypt` 解密 mysekai 数据。(注意：这里的sssekai.exe在macOS上可以被一个python库https://pypi.org/project/sssekai/替代)
- 解析地图/掉落并在 Canvas 上绘制，加载本地 xray 资源；唱片/曲目元数据来自远端 GitHub，封面从 `storage.sekai.best` 拉取 WebP 并用 `BitmapDecoder` 解码。
- WinUI 页面：`Pages/Tab3Page.xaml(.cs)`。

4) 已拥有的卡片（Tab4）
- 解析 suite JSON 中 `userCards`，拉取卡面/边框/属性图标并缓存。
- 图像缓存使用 `CardImageCacheService`（`Windows.Graphics.Imaging` + `BitmapImage`）。
- WinUI 页面：`Pages/OwnedCardsPage.xaml(.cs)`。

5) 组卡器（Tab5）
- 调用 `Services/Calc/deck_recommend_runner.py`，内部使用 `sekai_deck_recommend_cpp`（C++/pybind）计算。
- 依赖内置 Windows 嵌入式 Python（`python/python-3.12.10-embed-amd64`）。
- WinUI 页面：`Pages/DeckRecommendPage.xaml(.cs)`。

### 架构特征
- 典型 WinUI 3 应用：`App.xaml` + `MainWindow` + `NavigationView` + 多个 Page。
- 代码主要在 Page 的 code-behind 中（非 MVVM）。
- 关键服务：
  - `Services/AppPaths.cs`：统一 AppData 目录。
  - `Services/ThemeService.cs`：读写主题设置（依赖 `Windows.Storage`）。
  - `Services/CardImageCacheService.cs`：图像缓存、解码、编码（依赖 `Windows.Graphics.Imaging`）。
- 平台绑定点：WinRT API (`Windows.*`)、WinUI `Microsoft.UI.Xaml`、`FullTrustProcessLauncher`、`FileOpenPicker`、`BitmapDecoder` 等。

## 迁移目标
- 生成可在 macOS 运行的 Avalonia App（建议同时保留 Windows 版本）。
- 保持核心功能一致：抓包、suite 上传、Mysekai 解析与渲染、已拥有卡片展示、组卡器。

## 关键迁移挑战与替代方案

1) UI 框架
- WinUI 3 -> Avalonia XAML。
- `NavigationView` 替换为 Avalonia `TabControl` 或 `NavigationView` 类似自定义侧栏。

2) Windows API 依赖替换
- `Windows.Storage.Pickers.FileOpenPicker` -> Avalonia `StorageProvider.OpenFilePickerAsync`。
- `Windows.ApplicationModel.FullTrustProcessLauncher` -> 直接 `Process`（macOS 允许）+ 适配权限提示。
- `Windows.Graphics.Imaging.BitmapDecoder` / `SoftwareBitmap` -> Avalonia `Bitmap` + SkiaSharp 处理 WebP/PNG。
- `Windows.Storage.Streams.IRandomAccessStream` -> .NET Stream。

3) 外部可执行文件
- `Services/sssekai.exe`：需要 macOS 对应版本：https://pypi.org/project/sssekai/。
- `python-3.12.10-embed-amd64`：不可在 macOS 使用，需改用系统 Python 或自带 macOS Python 分发（如 python.org + app bundle）。
- 这两个可以改用自带 macOS Python 分发

4) HttpListener
- .NET 8 的 `HttpListener` 在 macOS 可用性有限；建议用 `Kestrel` + `Minimal API` 替换。

5) 文件/路径
- `%APPDATA%` 在 macOS 映射到 `~/Library/Application Support`，`AppPaths` 需要确认路径逻辑。

## 推荐迁移结构

```
ProsekaTools
├─ src/
│  ├─ ProsekaTools.Core/          # 共享逻辑（抓包/解析/网络/模型）
│  ├─ ProsekaTools.Avalonia/      # macOS Avalonia UI
│  └─ ProsekaTools.WinUI/         # 保留原 WinUI（可选）
└─ tools/
   └─ sssekai/                    # 跨平台可执行文件/脚本
```

- **Core**：抽离跨平台逻辑（HTTP 上传、数据解析、组卡调用、缓存管理）。
- **Avalonia**：负责 UI + 平台文件选择 + 进程执行 + 图像显示。

## 迁移步骤（建议顺序）

### 阶段 1：调研与拆分
- 梳理 WinUI 页面功能与依赖（已完成初步梳理）。
- 在 WinUI 项目中抽象出跨平台服务层（可先在新项目中复制）。
- 输出清单：哪些代码仍依赖 WinRT/WinUI。

### 阶段 2：基础 Avalonia 工程搭建
- 创建 Avalonia .NET 8 项目，搭建主窗口 + 侧边导航。
- 实现基础导航框架（Tab1–Tab5 + 设置）。
- 先做空页面，验证 macOS 运行。

### 阶段 3：功能逐个迁移
1) **数据抓取**
   - 用 Kestrel 替换 `HttpListener`。
   - 适配保存路径与文件命名规则。

2) **suite 上传**
   - 迁移 `HttpClient` 逻辑，替换 FilePicker/拖拽。

3) **Mysekai 工具**
   - `sssekai.exe` 在 macOS 上改为内置 Python 分发 + `sssekai` 库（通过 `python -m sssekai apidecrypt` 运行）。
   - 迁移 JSON 解析与地图绘制逻辑。
   - WebP 解码改为 Avalonia/SkiaSharp。

4) **已拥有卡片**
   - 迁移 JSON 解析与网络拉取逻辑。
   - 用 Avalonia/SkiaSharp 重写 `CardImageCacheService`。

5) **组卡器**
   - 确定 macOS Python 运行时（系统 Python or 内置发行版）。
   - 重新编译 `sekai_deck_recommend_cpp` 生成 macOS wheel。
   - 保持 `deck_recommend_runner.py` 调用接口一致。

### 阶段 4：打包与发布
- macOS App Bundle 打包，配置 `Info.plist` 权限（网络/文件访问）。
- 确认资源文件（Assets、masterdata、json）随包分发。

### 阶段 5：测试与验证
- 每个模块用现有 .bin / json 测试数据对比结果。
- 重点回归：
  - 抓包写入
  - WebP 图像显示
  - 组卡输出 JSON 兼容

## 重点风险与待确认
- `sssekai` 是否有 macOS 版本？如果无，需要替代方案（可能通过 Python/Go/Node 脚本或重建解密逻辑）。
- `sekai_deck_recommend_cpp` 是否支持 macOS 构建？需要 CMake + pybind 编译产物。
- WebP 解码性能（Avalonia 默认支持是否完整）。
- 上传服务 URL 是否长期可用。

## 后续可选优化
- 引入 MVVM（比如 CommunityToolkit.Mvvm）减少 code-behind。
- 将网络/IO/解析逻辑统一抽象成可测试服务。
- 提供 Windows + macOS 双平台统一代码库。
