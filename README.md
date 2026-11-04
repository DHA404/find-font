# find-font

**把本机装的所有字体，一次看全。**

Windows 自带的字体查看器只会列文件名。想知道「这个字体排版出来长什么样」「这台机器到底装了多少中文字体」「注册表里那条指向已删除文件的僵尸登记怎么清掉」，它都帮不上忙。find-font 把系统里每个字体按真实字形渲染成样本，让你像翻样本册一样浏览、搜索、对比、安装与卸载。

[![build](https://github.com/DHA404/find-font/actions/workflows/build.yml/badge.svg)](https://github.com/DHA404/find-font/actions/workflows/build.yml)
[![release](https://img.shields.io/github/v/release/DHA404/find-font?include_prereleases&label=release)](https://github.com/DHA404/find-font/releases)
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![platform](https://img.shields.io/badge/platform-Windows%2010%201803%2B%20%7C%2011-lightgrey.svg)](#-环境要求)

![find-font 主界面](docs/screenshot.png)

## 目录

- [为什么做这个](#为什么做这个)
- [功能](#功能)
- [快速开始](#快速开始)
- [使用](#使用)
- [界面](#界面)
- [核心设计约束](#核心设计约束)
- [架构](#架构)
- [构建](#构建)
- [项目结构](#项目结构)
- [测试](#测试)
- [性能基线](#性能基线)
- [版本与发布](#版本与发布)
- [已知限制](#已知限制)
- [贡献](#贡献)
- [许可](#许可)

## 为什么做这个

三个具体痛点：

1. **看不见字形。** 字体名是「方正准圆简体」，但它到底圆到什么程度、中英文混排时基线对不对，只有渲染出来才知道。
2. **分不清语言归属。** 系统里同时装着西文、中文、日文、韩文上百套字体，靠名字猜和靠眼睛翻是两回事。
3. **有僵尸登记。** 卸载软件时常留下「注册表说有这个字体、文件却已不存在」的条目，普通界面里看不见，也不容易删。

find-font 对这三件事的处理分别是：全部字体离屏渲染成位图、按名字关键词推断语言并计数、把失效登记单独标出来并支持一键清理（只删注册表项，不动任何字体文件）。

## 功能

| 功能 | 说明 |
| --- | --- |
| **全字体预览** | 本机 1300+ 个字体逐个按真实字形渲染，中英文混排，滚动即见 |
| **实时搜索** | 300 ms 防抖，匹配显示名 / 本地化名 / 中文名 / 家族名 / 文件名 / 样式名，任意大小写 |
| **分类与风格筛选** | 全部 / 中文 / 西文 / 其他，全部 / 常规 / 粗体 / 斜体，计数随筛选实时刷新 |
| **多字体对比** | 按住 Ctrl 选 1–3 个字体同屏对照，改对比文字立刻重渲染 |
| **自定义样本文字** | 顶部输入框改一次，整个列表与对比窗口同步 |
| **安装 / 卸载** | 主程序保持普通权限，仅文件与注册表操作走 UAC 提权微进程；**卸载时只删本工具装过的文件**（进回收站），系统自带字体只清登记 |
| **拒装网页字体** | `.woff/.woff2/.eot` 是浏览器格式，装进系统目录也看不见，直接明说拒绝原因而不是假装成功 |
| **失效登记清理** | 删除注册表里指向缺失文件的条目，**不删除任何字体文件** |
| **单实例** | 重复双击不会开出第二个窗口，而是把已有窗口叫到前台 |
| **记忆上次状态** | 窗口位置 / 大小 / 上次搜索词 / 样本文字 / 字号 / 分类 / 风格都记在 `%LOCALAPPDATA%\find-font\settings.json` |
| **高分屏清晰** | 声明 PerMonitorV2 DPI 感知，位图按屏幕真实 DPI 渲染（100% / 150% / 200% 物理尺寸一致） |
| **出问题有据可查** | 未处理异常自动落盘到 `%LOCALAPPDATA%\find-font\logs`，保留 7 天；「关于」里一键复制环境信息 |
| **键盘优先** | `Ctrl+F` 搜索、`Esc` 逐层退出、`Enter` 直接对比 |

## 快速开始

### 环境要求

- Windows 10 1803 及以上，或 Windows 11
- [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（Desktop Runtime）

### 安装

到 [Releases](../../releases) 下载 `find-font-<version>-win-x64.zip`，解压到任意目录，双击 **`FindFont.exe`**。绿色版，不写系统目录、不需要安装程序。

压缩包里只有两个文件：

| 文件 | 说明 |
| --- | --- |
| `FindFont.exe` | 入口引导器（.NET Framework 4.8，约 7 KB），先检测桌面运行时再启动主程序 |
| `FindFont.App.exe` | 主程序，框架依赖单文件（约 400 KB） |

没装 .NET 8 时双击不会毫无反应——引导器会弹框说明缺什么、并给出下载链接。

## 使用

| 操作 | 怎么做 |
| --- | --- |
| 搜索 | 顶部「搜索」框直接输入，或按 `Ctrl+F` |
| 筛选 | 左栏点分类行；风格是四个互斥方块；「只看失效登记」收窄到僵尸条目 |
| 看细节 | 单击任意一行，该行展开成大样本，右侧「档案」列出家族、本地化名、样式、字重、文件、路径、语言、状态 |
| 对比 | 按住 `Ctrl` 选 1–3 个字体后点「对比选中」，或直接双击某一行 |
| 改样本 | 顶部「样本文字」框，全局生效 |
| 换字号 | 右上角字号下拉（20–88） |
| 安装 | 「从文件安装」选 `.ttf/.otf/.ttc/.otc`，会弹 UAC；取消 UAC 视为放弃，不留半成品。选到 `.woff/.woff2/.eot` 会被直接拒绝并说明原因 |
| 卸载 | 选中后点「卸载字体」，同样走 UAC。本工具装过的文件进回收站；系统自带字体**只清除登记、不删除文件**，确认框会写清是哪一种 |
| 清理 | 「清理失效登记」只删注册表项 |
| 退出搜索 | `Esc` 清空搜索词，再按一次取消选中 |
| 对比窗口 | `Enter`，或双击列表行 |

## 界面

默认 1600×1000（最小 1280×780），三栏：索引栏 272px、样本列表、选中时出现的「档案」面板 380px。

视觉上只有一套灰阶 + 一个强调色：纸灰底 `#ECEDEF`、面板 `#F6F7F8`、墨色 `#101215`、发丝线 `#CDD1D6`，以及唯一用来表达「被选中 / 有焦点 / 已失效」的品红 `#C4005E`。界面字体是 Cambria（标题、样本元数据）与 Segoe UI（控件），全部无圆角，靠 1px 线分层而不是靠卡片阴影。

样本行默认 92px，选中后容器高度动画长到 220px，小样本淡出、大样本延迟 60 ms 淡入——字形始终垂直居中，观感是「行张开」而不是换图。筛选结果逐行错开入场（每行延迟 12 ms、封顶 180 ms），输入框聚焦时底边从中心长出一条 2px 品红标尺。

窗口材质走 `DWMWA_SYSTEMBACKDROP_TYPE`（Win11 22H2+），旧的 `DWMWA_MICA_EFFECT` 与 Win10 的 `SetWindowCompositionAttribute` 逐级回退，全部失败时留在不透明纸色上；材质生效时内容层铺 `#F0ECEDEF` 半透明纸色，保证正文对比度不随壁纸变化。所有动画都读 `SystemParameters.ClientAreaAnimation`——系统关掉动画即全部退化为瞬时切换。

样本文字只用离屏渲染出的位图，XAML 里不会出现 `FontFamily` 绑定（见下节），因此界面字体与字体列表渲染互不影响。

## 核心设计约束

这些约束来自一次踩坑记录，每条都对应一个真实的崩溃、卡顿或内存事故。

| 问题 | 方案 | 代码位置 |
| --- | --- | --- |
| 绑定 `FontFamily` 会让 WPF 加载并常驻上千套字形，内存暴涨 | XAML 里**禁止**出现 `FontFamily` 绑定；改用 `FormattedText` + `RenderTargetBitmap` 在独立 STA 线程离屏出图，缓存为冻结的 `BitmapSource` | `Services/FontRenderCache.cs`、`Converters/FontToImageConverter.cs` |
| 上千个字体逐一解析会卡住界面 | 先同步读注册表立即出列表，再后台异步补全本地化名；补名直读 sfnt 表，约 0.44 ms/文件 | `Services/FontScanner.cs`、`Helpers/FontMetadataReader.cs` |
| 滚动时反复渲染导致掉帧 | `VirtualizingStackPanel` + `VirtualizationMode=Recycling` + `CacheLength=1,1`；**只有被选中的行才请求大样本**，小样本按 96 MB 字节预算做 LRU 淘汰 | `Views/MainWindow.xaml`、`Themes/Templates.xaml` |
| 输入每个字符都过滤一次会卡顿 | 300 ms 防抖 + 单遍扫描同时产出结果与四个分类计数（`ReplaceAll` 只发一次 Reset）+ 预拼小写 `SearchHaystack` | `ViewModels/MainViewModel.cs` |
| 安装/卸载需要管理员权限，但主程序不该整体提权 | 主进程普通权限，`Verb=runas` 拉起同一个 exe 的微进程只做文件与注册表操作 | `Services/ElevationHost.cs` |
| 注册表指向不存在文件的条目会让扫描崩溃 | 缺失文件生成占位项（品红标记、可一键清理），解析失败静默捕获 | `Services/FontScanner.cs` |
| 卸载若直接删文件会误删系统自带字体 | 维护一份「本工具装过什么」的台账（`%ProgramData%\find-font\installed.txt`），台账里没有的文件一律只清登记；删除走回收站，回收站不可用就报错而不是硬删 | `Services/FontInstaller.cs` |
| 用户没装 .NET 8 时双击 exe 毫无反应 | 入口是 .NET Framework 4.8 引导器（Win10 1803+ 自带），缺运行时弹框给链接 | `src/FindFont.Launcher/Program.cs` |
| 动效写在 XAML 里，用户关了系统动画却还在播 | 全部时长从 `Helpers/Motion.cs` 取，读 `SystemParameters.ClientAreaAnimation`，关闭时返回 `TimeSpan.Zero` | `Helpers/Motion.cs` |
| 重复双击会开出两个窗口、各自扫一遍注册表 | 全局命名互斥体 + `EnumWindows` 找已有窗口叫到前台；拿不到互斥体时降级为允许启动，绝不让程序打不开 | `Helpers/SingleInstance.cs` |
| 窗口大小、搜索词每次重启都丢 | `System.Text.Json` 存 `%LOCALAPPDATA%\find-font\settings.json`，变更后 500 ms 合并写；先写 `.tmp` 再原子替换，避免断电留半截 JSON | `Services/AppSettings.cs` |
| 高分屏上样本偏小发虚 | `app.manifest` 声明 PerMonitorV2；位图按 `VisualTreeHelper.GetDpi` 的真实 DPI 渲染，**DPI 进缓存键**，跨屏拖动不会命中旧 DPI 的模糊图 | `app.manifest`、`Services/RenderDpi.cs` |
| 崩溃后用户什么都说不清 | 未处理异常统一落盘到 `%LOCALAPPDATA%\find-font\logs`（不可写则依次退到 `%TEMP%`），保留 7 天；「关于」一键复制环境信息 | `Helpers/Diagnostics.cs` |

更多取舍（为什么直读 sfnt 而不是 `GlyphTypeface`、为什么用 `Motion.Hold` 而不是 `FillBehavior.Stop`）写在 `.docs/PLAN.md`。

## 架构

```
     ┌────────────────┐  注册表（HKLM/HKCU × 32/64）
     │  FontScanner   │──────────────┐
     └────────────────┘              │ 同步：立即出列表
              │ 异步                    ▼
              │                 ┌──────────────────┐
     ┌────────▼────────┐        │  MainViewModel   │
     │ FontMetadata-   │        │  搜索/筛选/多选   │
     │ Reader（sfnt）  │───────▶│  分类计数/档案    │
     └─────────────────┘  补名   └────────┬─────────┘
                                          │ 请求位图
                                  ┌───────▼─────────┐
                                  │ FontRenderCache │
                                  │  STA 渲染线程    │
                                  │  LRU + 字节预算  │
                                  └───────┬─────────┘
                                          │ 冻结 BitmapSource
                                  ┌───────▼─────────┐
                                  │  Image（XAML）   │
                                  └─────────────────┘
```

技术栈：C# 12 / .NET 8 / WPF / MVVM（`CommunityToolkit.Mvvm`）/ 无第三方 UI 库。

写入路径（安装、卸载、清理）不走上述链路，而是 `FontInstaller` → `ElevationHost` → `runas` 起同一个 exe 带 `--install-font` 之类参数运行 → 主程序在 `App.OnStartup` 里识别到操作请求就执行并立即退出。这样提权面被压缩到一个只做文件与注册表操作的分支。

## 构建

> 本机 `%TEMP%\MSBuildTemp`、`%USERPROFILE%\.templateengine`、NuGet v3-cache 均不可写，`build.cmd` 会把 `TMP`/`TEMP`/`NUGET_PACKAGES`/`DOTNET_CLI_HOME` 全部重定向到仓库内的 `.build`、`.nuget` 目录。

```bat
build.cmd          :: 编译
build.cmd test     :: 跑冒烟测试（扫描真实系统字体并实际渲染位图）
build.cmd run      :: 直接启动
build.cmd publish  :: 发布到 publish\ 并打包 dist\find-font-<version>-win-x64.zip
```

手动构建等价于：

```bat
set TMP=%~dp0.build\tmp
set NUGET_PACKAGES=%~dp0.nuget\packages
set DOTNET_CLI_HOME=%~dp0.build\home
dotnet build FindFont.slnx -c Debug
```

`build.cmd` 本身必须是纯 ASCII：`cmd.exe` 用 OEM 代码页解析批处理，UTF-8 中文注释会被拆成乱码命令，进而让 `%TEMP%` 重定向失效。

## 项目结构

```
assets/find-font.ico             应用图标（16–256 七档，两个 exe 共用）
src/FindFont.Launcher/           .NET Framework 4.8 引导器（运行时检测 + 启动 FindFont.App.exe）
src/FindFont.App/
  app.manifest                     PerMonitorV2 DPI 感知、longPathAware、最高支持的 OS
  Models/FontItem.cs               字体条目（BestName 回退链、SearchHaystack、小/大样本两个位图槽）
  Models/FacetItem.cs              左栏分类索引行（名称固定、计数就地更新）
  Collections/RangeObservableCollection.cs   整批替换只发一次 Reset
  Helpers/NativeMethods.cs         AddFontResourceEx / RemoveFontResourceEx / WM_FONTCHANGE 广播
  Helpers/FontMetadataReader.cs   字体元数据入口（sfnt 优先，GlyphTypeface 回退）
  Helpers/SfntReader.cs           sfnt/ttcf 容器、表目录、big-endian tag
  Helpers/NameTableParser.cs      name 表记录与本地化名评分
  Helpers/LanguageInference.cs    按名字关键词推断 CJK 归属，不解析字符表
  Helpers/Motion.cs               三档时长 + 遵循系统动画开关
  Helpers/WindowBackdrop.cs       Mica / Acrylic / 降级，三级回退
  Helpers/SingleInstance.cs       命名互斥体 + 唤起已有窗口
  Helpers/Diagnostics.cs          崩溃日志（可写目录回退链、保留 7 天）
  Services/FontScanner.cs         注册表扫描 + 缺失文件占位 + 异步补全
  Services/FontRegistryReader.cs  读 HKLM/HKCU × 32/64 的四种字体注册表视图
  Services/FontDeduplicator.cs    同文件同家族的合并规则
  Services/FontRenderCache.cs     STA 渲染线程 + 字节预算 LRU + 并发去重 + 过期请求丢弃
  Services/FontRenderCache.Rendering.cs   FormattedText / RenderTargetBitmap / Typeface 缓存
  Services/FontInstaller.cs       安装 / 卸载 / 清理失效登记（仅提权进程执行）
  Services/FontInstaller.Registry.cs     注册表值名与读写
  Services/ElevationHost.cs       --install-font 等参数协议的微进程宿主
  Services/AppSettings.cs         settings.json 读写、窗口几何、屏幕可见性校验
  Services/RenderDpi.cs           当前监视器 DPI 的来源
  Converters/                     FontToImage（MultiBinding → BitmapSource）、可见性转换器
  Themes/Tokens.xaml              色板、字体、尺寸常量（唯一配色来源）
  Themes/Controls.xaml            聚合器：Inputs / Buttons / Rows 三份
  Themes/Templates.xaml           样本行模板（行高动画 + 双图交叉淡化）
  Themes/Scrollbars.xaml          4px 细滚动条
  ViewModels/MainViewModel.cs     状态字段、集合、计算属性
  ViewModels/MainViewModel.Filtering.cs   扫描加载、防抖搜索、筛选、分类计数
  ViewModels/MainViewModel.Commands.cs    选择、安装、卸载、清理、打开目录
  ViewModels/MainViewModel.Settings.cs    设置往返、DPI 刷新、关于/日志命令
  ViewModels/CompareViewModel.cs  最多三款字体同屏对照
  Views/MainWindow.xaml(.cs)      主窗口
  Views/CompareWindow.xaml(.cs)   对比窗口
  Views/AboutWindow.xaml(.cs)     关于：环境信息一键复制
tests/FindFont.SmokeTests/        无第三方依赖的控制台冒烟测试（TestRunner + Tests/ 分层）
```

## 测试

```bat
build.cmd test
```

测试直接跑真实字体库，不 mock：扫描本机注册表、遍历全部字体文件、实际渲染位图。失败时返回非零退出码，CI 据此拦截。

1. `LanguageInference` 关键词推断（中 / 日 / 韩 / 西文 / 空名）
2. `FontMetadataReader.ContainsCjk` 的 CJK 判定与 null 安全
3. `FontItem.BestName` 三级回退与 `DedupKey` 大小写不敏感
4. 真实扫描本机注册表（断言字体数 > 0、字段非空）
5. `EnhanceAsync` 抽样补全，断言字重被回填；旧式 `.fon` 不得误判为异常
6. sfnt 直读覆盖率 100%（不退回 `GlyphTypeface` 慢路径），抽样核对 SimSun / Arial / 微软雅黑的家族名与中文名
7. `FontRenderCache` 实际出图，断言尺寸 > 0、已 `Freeze`、二次请求命中缓存
8. `MainViewModel` 真实加载后验证搜索、分类筛选、风格筛选与清空恢复
9. 1 万条字体的压测：装载、搜索、筛选、清空恢复各自的耗时上限
10. `CompareViewModel` 最多三栏上限与移除后栏数变化
11. 界面资源与主窗口可构造，分类索引行计数与选中分类下的可见项数一致，画布不小于 1600×1000
12. 动效助手遵循系统设置：`Motion.Evaluate(true/false)` 返回值与三档时长排序
13. 窗口材质回退不抛异常：`WindowBackdrop.Apply` 在任意系统版本上都返回合法档位
14. 尺寸与模板资源：默认画布、最小时宽、模板键存在性
15. 版本号来自 `version.txt`：程序集元数据、`AppInfo.DisplayVersion`、视图模型三处一致
16. 位图缓存受字节预算约束：塞入超预算的大图后总量回落到预算内
17. 同键并发请求只渲染一次：10 个并发请求共用同一张图
18. 未选中的行不渲染大图：构造样本行不触发 220px 渲染，选中后才出现
19. `RangeObservableCollection` 只发一次 Reset
20. 搜索快路径与原多字段匹配等价：200 个真实字体 × 6 组查询串逐一比对
21. 卸载不删系统字体文件：未经本工具安装的文件一律不算托管，只清登记
22. 网页字体（WOFF）明确拒装：`.woff/.woff2/.eot` 在碰系统目录之前就被拒绝，且文件未被复制
23. 字体注册表值名格式正确：`.ttc` 用「(TrueType collection)」、`.otf` 用「(OpenType)」，后缀不重复
24. 单实例互斥体只放行一个进程：第二次 `Acquire` 必须识别出已有实例
25. 设置的默认值与屏幕可见性判断（拔掉显示器后窗口坐标不会被恢复）
26. 设置经视图模型往返一致：预览文字 / 字号 / 分类 / 风格 / 搜索词
27. 渲染 DPI 只在合理范围生效：0 / 负数 / NaN / Infinity 一律回落到 96
28. DPI 参与位图缓存键：同一字体换 DPI 必须拿到另一张图
29. 崩溃日志落盘且带上下文，且不抛异常
30. 源文件不超过 300 行（与 CI 里的同名检查互为双保险）

当前全部通过（30 项）。

## 性能基线

本机（Windows 11 26H2、1333 个已装字体、1329 个不同字体文件）实测：

| 场景 | 耗时 |
| --- | --- |
| 注册表扫描出列表 | 约 640 ms |
| 全量补全文件名（直读 sfnt） | 约 580 ms（0.44 ms/文件） |
| 1 万条字体的搜索 / 筛选 / 清空恢复 | 各约 1 ms |
| 单个样本行首帧渲染（1120×92） | 约 5 ms |
| 内存占用（1333 个字体、可视区渲染完） | 约 160 MB，位图缓存上限 96 MB |

滚动性能的关键是「只有被选中的行才渲染大样本」：早期版本里每个行模板都在实例化时对 `LargeSample` 求值，滚过一行就多渲染一张 1500×220 的大图。现在大样本改由视图模型的 `RequestLargePreviews` 按选中集合主动请求，行模板只绑 `PreviewBitmapLarge` 属性。

## 版本与发布

版本号只写在仓库根的 `version.txt` 一处。`Directory.Build.props` 把它读成 `Version`，所以程序集元数据、窗口标题行、`dist/find-font-<version>-win-x64.zip` 的命名、CI 的打包与校验全都来自同一个值。

发一个新版本是三步：

```bash
echo 0.3.0-alpha > version.txt                        # 1. 改版本号
git commit -am "release: 0.3.0-alpha"                 # 2. 提交
git tag v0.3.0-alpha && git push origin main --tags   # 3. 打新 tag
```

tag 触发时 CI 会先校验 `v$(cat version.txt)` 与 tag 名完全一致，不一致直接失败，避免「新 tag 顶着旧版本号发出去」。每个版本用独立的新 tag——不要删掉旧 tag 再重建：复用同一个 tag 会让 Release 页面一直显示同一个版本号，也让「这次究竟改了什么」无法追溯。

## 已知限制

- 语言归属靠名字关键词推断，未解析字体二进制字符表，因此某些冷门字体会被归到「其他」。
- 旧式 `.fon` 位图字体不是 sfnt 容器，无法读取家族名，列表里以文件名做显示名（不标记为异常）。
- `.ttc/.otc` 字体集合只读取第一个字体，同集合中的其余成员不会单独列出。注册表若用不同名字登记同一文件的多个字面，列表会保留成多条——宁可多列，也不丢掉真实字面。
- 注册表扫描只覆盖 `HKLM`/`HKCU` 的 `Windows NT\CurrentVersion\Fonts`，不处理通过 GDI 私有加载的临时字体。
- 主程序是框架依赖单文件，依赖目标机已装 .NET 8 桌面运行时；缺失时由引导器提示并给出下载链接。

## 贡献

欢迎提 Issue 与 PR。提交前请确认：

1. `build.cmd test` 全绿（测试跑的是真实系统字体，本机数字与 CI 会不同，断言只看覆盖率与等价性，不看绝对耗时）。
2. XAML 里没有新增 `FontFamily` 绑定——这是这套架构的地基。
3. 长耗时操作是异步的，`ObservableCollection` 只在 UI 线程改。
4. 单个文件尽量控制在 300 行内，视图逻辑放视图模型。

## 许可

[MIT](LICENSE) © 2026 DHA404
