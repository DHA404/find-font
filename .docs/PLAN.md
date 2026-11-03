# 🚀 find-font 项目开发计划书

## 一、 项目概述
   项目名称：`find-font`
   项目定位：一款专为 Windows 打造的超轻量、极速本地字体预览与管理工具。
   目标用户：设计师、字体爱好者、需要快速查找系统字体的办公人员。
   核心差异化：极致顺滑的滚动体验（无惧上千字体）、极低的常驻内存占用、深度字体管理功能（静默安装卸载坏字体清理）。

## 二、 技术选型与架构决策
基于前期评估，最终确定以下技术路线：

   开发语言：C# 12 (.NET 8)
   UI 框架：WPF (MVVM 模式)
   MVVM 框架：`CommunityToolkit.Mvvm`（源码生成器，极简、高性能）
   打包策略：框架依赖发布（Framework-dependent）。单文件体积控制在 5-10MB 左右，首次运行检测用户环境，若无 .NET 8 Desktop Runtime 则引导下载（接受 4C 妥协，放弃 10MB 执念）。
   字体扫描方案：混合方案（2C）。启动时读取注册表（毫秒级出数据），后台异步调用 DirectWrite PInvoke 补全缺失的本地化名称与变体信息。
   字体渲染与性能策略：彻底绕过 WPF 的 `FontFamily` 资源管理机制（规避 3AB 内存泄漏）。使用 `FormattedText` + `RenderTargetBitmap` 离屏渲染出位图（`BitmapSource`），UI 层仅使用 `Image` 控件展示（结合 5B 位图缓存）。
   坏字体容错：轻量规则推断（6A）。基于字体名称推断语言支持，不解析真实二进制字符表，对异常字体静默捕获异常并展示占位符。

## 三、 核心痛点与解决方案（排雷指南）

 痛点  风险  解决方案 
---  ---  ---
 WPF 内存泄漏  大量 `FontFamily` 实例导致非托管内存飙升、程序崩溃。  坚决不在 UI 层绑定 `FontFamily`。使用 `RenderTargetBitmap` 渲染为图片并 `Freeze()` 后交给 UI。 
 滚动卡顿  上千个复杂字体同时渲染导致 UI 线程阻塞。  1. 启用 `VirtualizingStackPanel` 并开启 `Recycling` 回收模式。br2. 建立内存级 `ConcurrentDictionary` 位图缓存池。br3. 搜索时使用 300ms 防抖。 
 一键安装卸载  主程序提权（UAC）会导致拖拽等功能失效。  主程序保持普通权限；安装卸载时通过 `Process.Start` 启动带 `runas` 参数的独立微进程（或自身传递启动参数）执行文件操作。 
 缺失 DirectWrite 名字  注册表读出的名字是 `msyh.ttc`，缺少“微软雅黑”展示名。  启动时 UI 先绑定注册表粗略数据，后台 `Task.Run` 异步使用 `IDWriteFactory` 提取 `LocalizedStrings`，完成后通过 `ObservableCollection` 更新 UI。 

## 四、 项目工程结构 (建议目录)

```text
find-font
├── FindFont.sln
├── README.md
├── src
│   └── FindFont.App
│       ├── FindFont.App.csproj
│       ├── App.xaml
│       ├── App.xaml.cs
│       ├── Models                  # 数据模型
│       │   └── FontItem.cs
│       ├── ViewModels              # 视图模型
│       │   ├── MainViewModel.cs
│       │   └── CompareViewModel.cs  # 对比弹窗的VM
│       ├── Views                   # 视图 (WPF 窗口用户控件)
│       │   ├── MainWindow.xaml
│       │   └── CompareWindow.xaml
│       ├── Services                # 核心服务
│       │   ├── FontScanner.cs       # 注册表读取 + DirectWrite 补全
│       │   ├── FontRenderCache.cs   # 位图渲染与缓存引擎
│       │   └── FontInstaller.cs     # 提权安装卸载逻辑
│       ├── Converters              # 转换器
│       │   └── FontToImageConverter.cs
│       └── Helpers                 # 工具类
│           └── NativeMethods.cs     # PInvoke 声明 (DirectWrite, User32)
└── docs                            # 文档
    └── PLAN.md                      # 本计划书
```

## 五、 迭代路线图 (Roadmap)

### 阶段一：POC 验证 (当前进度 100%)
   [x] 成功读取系统注册表获取字体列表。
   [x] 实现离屏位图渲染核心类 (`FontRenderCache`)。
   [x] 搭建基础 WPF 虚拟化列表 (`ListBox` + `VirtualizingStackPanel`)。
   [x] 验证滚动不卡顿、内存不暴涨。

### 阶段二：MVP (最小可行性产品) - 目标：核心体验闭环  ✅ 已完成
   [x] 实时搜索与防抖：完善 `MainViewModel`，实现输入框输入后 300ms 更新列表。
   [x] 实时预览文字更新：将顶部输入框的文本通过 `MultiBinding` 传递给 `FontToImageConverter`，实现预览文字的全局替换。
   [x] UI 美化：完善卡片阴影、悬停效果、加载动画（骨架屏）。
   [x] 异步补全名字：引入 `Task.Run` 解析字体文件头（或 DirectWrite API），替换注册表中的生硬文件名。
   [x] 错误处理：当 `FontFamily` 找不到或渲染失败时，UI 显示“Unknown Font”。

### 阶段三：Alpha (高级功能) - 目标：构建壁垒  ✅ 已完成
   [x] 多字体同屏对比：实现 2A 弹窗逻辑。选中列表中多个字体，点击“对比”，弹出一个新窗口（固定字号的 TextBox 大文本框）。
   [x] 一键安装卸载：实现 `FontInstaller.cs`，处理 UAC 提权，支持将外部字体安装至系统并刷新列表。
   [x] 坏字体清理：扫描系统注册表中指向不存在的路径，或无法渲染的字体，打上红色标记，提供清理入口。
   [x] 筛选器：侧边栏加入筛选功能（中文、英文、字重、风格）。

### 阶段四：Beta 与发布 - 目标：交付用户  ✅ 已完成
   [x] 环境检测脚本：编写一个极小的启动引导器（或使用 C# 编写的启动器），检测用户是否安装了 .NET 8。
   [x] 性能压测：准备一个包含 10000 个外部字体的文件夹，测试内存极限和搜索极限。
   [x] 打包发布：发布为单文件（Single File），采用 `PublishSingleFile=true`，生成绿色版压缩包。

## 六、 AI 协作开发规约
为了确保 AI 生成代码的质量，后续对话请严格遵守以下规约：
1.  禁止在 XAML 中直接绑定 `FontFamily`。所有字体显示必须走 `BitmapSource` 渲染路径。
2.  长耗时操作必异步。任何涉及 IO（读文件、读注册表）或复杂计算（DirectWrite 解析）的代码，必须包裹在 `Task.Run` 或 `asyncawait` 中。
3.  UI 线程安全。修改 `ObservableCollection` 必须在 UI 线程进行（利用 `Dispatcher.Invoke` 或 `CommunityToolkit.Mvvm` 的跨线程支持）。
4.  代码结构清晰。单文件代码不超过 300 行，视图逻辑必须解耦到 ViewModel。
