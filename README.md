# find-font

[![build](https://github.com/DHA404/find-font/actions/workflows/build.yml/badge.svg)](https://github.com/DHA404/find-font/actions/workflows/build.yml)

Windows 本地字体预览与管理工具。WPF + .NET 8 + MVVM，单文件分发，附带一个 .NET Framework 4.8 引导器负责运行时检测。

![find-font 主界面](docs/screenshot.png)

## 现在下载

到 [Releases](../../releases) 下载最新的 `find-font-<version>-win-x64.zip`，解压后双击 **`FindFont.exe`** 即可。绿色版，免安装。

需要 .NET 8 桌面运行时；没装的话引导器会弹框并给出下载链接。

## 它解决什么

Windows 自带的字体查看器只能看文件名，无法一眼看清一个字体在中英文混排下长什么样，也不能批量清理注册表里指向已删除文件的「僵尸字体」。find-font 把本机所有字体按真实字形渲染成缩略图，支持搜索、筛选、对比、安装与卸载。

## 版本与发布

版本号只写在仓库根的 `version.txt` 一处。`Directory.Build.props` 把它读成 `Version`，所以程序集元数据、窗口标题行、`dist\find-font-<version>-win-x64.zip` 的命名、CI 的打包与校验全都来自同一个值。

发一个新版本就是三步：

```bash
echo 0.3.0-alpha > version.txt                        # 1. 改版本号
git commit -am "release: 0.3.0-alpha"                 # 2. 提交
git tag v0.3.0-alpha && git push origin main --tags   # 3. 打新 tag
```

tag 触发时 CI 会先校验 `v$(cat version.txt)` 与 tag 名完全一致，不一致直接失败，避免「新 tag 顶着旧版本号发出去」。每个版本用独立的新 tag —— 不要删掉旧 tag 再重建：复用同一个 tag 会让 Release 页面一直显示同一个版本号，也让「这次究竟改了什么」无法追溯。

## 界面

默认 1600×1000（最小 1280×780），三栏：左栏是分类与风格的索引栏（272px，计数随筛选实时变化），中部是样本列表，右栏「档案」在选中字体时出现（380px），未选中时收起，把宽度还给列表。

视觉上只有一套灰阶 + 一个强调色：纸灰底 `#ECEDEF`、面板 `#F6F7F8`、墨色 `#101215`、发丝线 `#CDD1D6`，以及唯一用来表达「被选中 / 有焦点 / 已失效」的品红 `#C4005E`。界面字体是 Cambria（标题、样本元数据）与 Segoe UI（控件），全部无圆角，靠 1px 线分层而不是靠卡片阴影。

样本行默认 92px，选中后容器高度动画长到 220px，小样本淡出、大样本延迟 60ms 淡入 —— 字形始终垂直居中，观感是「行张开」而不是换图。筛选结果会逐行错开入场（每行延迟 12ms、封顶 180ms），输入框聚焦时底边从中心长出一条 2px 品红标尺。

窗口材质走 `DWMWA_SYSTEMBACKDROP_TYPE`（Win11 22H2+），旧的 `DWMWA_MICA_EFFECT` 与 Win10 的 `SetWindowCompositionAttribute` 逐级回退，全部失败时留在不透明纸色上；材质真正生效时内容层铺 `#F0ECEDEF` 半透明纸色，保证正文对比度不随壁纸变化。所有动画都读 `SystemParameters.ClientAreaAnimation`，系统关掉动画即全部退化为瞬时切换。

样本文字只用离屏渲染出的位图，XAML 里不会出现 `FontFamily` 绑定（见下节），因此界面字体与字体列表渲染互不影响。

## 核心设计约束

| 问题 | 方案 | 代码位置 |
| --- | --- | --- |
| 绑定 `FontFamily` 会让 WPF 加载并常驻上千套字形，内存暴涨 | XAML 里**禁止**出现 `FontFamily` 绑定；改用 `FormattedText` + `RenderTargetBitmap` 在独立 STA 线程离屏出图，缓存为冻结的 `BitmapSource` | `Services/FontRenderCache.cs`、`Converters/FontToImageConverter.cs` |
| 上万个字体逐一解析会卡住界面 | 先同步读注册表立即出列表，再后台异步补全本地化名；补名走直读 sfnt 表，约 0.31 ms/文件 | `Services/FontScanner.cs`、`Helpers/FontMetadataReader.cs` |
| 滚动时反复渲染导致掉帧 | `VirtualizingStackPanel` + `VirtualizationMode=Recycling`，图片走 LRU 位图缓存 | `Views/MainWindow.xaml` |
| 输入每个字符都过滤一次会卡顿 | 300 ms 防抖，`CancellationToken` 取消未完成的一轮 | `ViewModels/MainViewModel.cs` |
| 安装/卸载字体需要管理员权限，但主程序不该整体提权 | 主进程保持普通权限，`Verb=runas` 拉起同一个 exe 的微进程只做文件与注册表操作 | `Services/ElevationHost.cs` |
| 注册表里指向不存在文件的条目会让扫描崩溃 | 缺失文件生成占位项（标红、可一键清理），解析失败静默捕获 | `Services/FontScanner.cs` |
| 用户没装 .NET 8 时双击 exe 毫无反应 | 入口是 .NET Framework 4.8 引导器（Win10 1803+ 自带），先检测桌面运行时，缺了就弹框并给出下载链接 | `src/FindFont.Launcher/Program.cs` |

## 构建

> 本机 `%TEMP%\MSBuildTemp`、`%USERPROFILE%\.templateengine`、NuGet v3-cache 均不可写，`build.cmd` 会把 `TMP`/`TEMP`/`NUGET_PACKAGES`/`DOTNET_CLI_HOME` 全部重定向到仓库内的 `.build`、`.nuget` 目录。

```bat
build.cmd          :: 编译
build.cmd test     :: 跑冒烟测试（扫描真实系统字体并实际渲染一张位图）
build.cmd run      :: 直接启动
build.cmd publish  :: 发布到 publish\ 并打包 dist\find-font-<version>-win-x64.zip
```

`publish\` 里有两个可执行文件：

| 文件 | 说明 |
| --- | --- |
| `FindFont.exe` | 双击入口，.NET Framework 4.8 引导器（约 7 KB），先检查运行时再启动主程序 |
| `FindFont.App.exe` | 主程序，框架依赖单文件（约 370 KB） |

手动构建等价于：

```bat
set TMP=%~dp0.build\tmp
set NUGET_PACKAGES=%~dp0.nuget\packages
set DOTNET_CLI_HOME=%~dp0.build\home
dotnet build FindFont.slnx -c Debug
```

`build.cmd` 本身必须是纯 ASCII：`cmd.exe` 用 OEM 代码页解析批处理，UTF-8 中文注释会被拆成乱码命令。

## 项目结构

```
src/FindFont.Launcher/           .NET Framework 4.8 引导器（运行时检测 + 启动 FindFont.App.exe）
src/FindFont.App/
  Models/FontItem.cs              字体条目模型（含 DedupKey、BestName 回退链、小样本/大样本两个位图槽位）
  Models/FacetItem.cs             左栏分类索引行（名称固定、计数可就地更新）
  Helpers/NativeMethods.cs        AddFontResourceEx / RemoveFontResourceEx / WM_FONTCHANGE 广播
  Helpers/FontMetadataReader.cs   直读 sfnt 的 name/OS/2/head 表取家族名、本地化名、字重、斜体（GlyphTypeface 仅作回退）
  Helpers/LanguageInference.cs    按名字关键词推断 CJK 归属（计划书 6A，不解析字符表）
  Services/FontScanner.cs         注册表扫描 + 缺失文件占位 + 异步补全
  Services/FontRenderCache.cs     STA 渲染线程 + LRU 位图缓存 + 占位图降级
  Services/FontInstaller.cs       安装 / 卸载 / 清理异常（仅提权进程执行）
  Services/ElevationHost.cs       --install-font 等参数协议的微进程宿主
  Converters/                     FontToImage（MultiBinding → BitmapSource）、可见性转换器
  Themes/Tokens.xaml              色板、字体、尺寸常量（唯一配色来源）
  Themes/Controls.xaml            发丝线、文本框、按钮、索引行、样本行、档案区样式
  ViewModels/MainViewModel.cs     扫描、防抖搜索、筛选、多选、分类计数、档案面板、安装卸载
  ViewModels/CompareViewModel.cs  最多三款字体同屏对照
  Views/MainWindow.xaml(.cs)      主窗口
  Views/CompareWindow.xaml(.cs)   对比窗口
tests/FindFont.SmokeTests/        无第三方依赖的控制台冒烟测试
```

## 冒烟测试覆盖

```bat
build.cmd test
```

1. `LanguageInference` 关键词推断（中文 / 日文 / 韩文 / 西文 / 空名）
2. `FontMetadataReader.ContainsCjk` 的 CJK 判定与 null 安全
3. `FontItem.BestName` 三级回退与 `DedupKey` 大小写不敏感
4. 真实扫描本机注册表（断言字体数 > 0，字段非空）
5. `EnhanceAsync` 抽样补全，断言字重被回填；旧式 `.fon` 不得误判为异常
6. `FontMetadataReader.TryRead` 遍历本机全部字体文件，断言 sfnt 直读覆盖率 100%（不退回 `GlyphTypeface` 慢路径），并抽样核对 SimSun / Arial / 微软雅黑的家族名与中文名
7. `FontRenderCache` 实际出图，断言尺寸 > 0、已 `Freeze`、二次请求命中缓存
8. `MainViewModel` 真实加载后验证搜索、分类筛选、风格筛选与清空恢复
9. 1 万条字体的压测：装载、搜索、筛选、清空恢复各自的耗时上限（搜索/筛选 < 500 ms）
10. `CompareViewModel` 最多三栏上限与移除后栏数变化
11. 界面资源与主窗口可构造：主题字典已合并、`MainWindow`/`CompareWindow` 能实例化、分类索引行的计数与选中分类下的可见项数一致，且画布不小于 1600×1000
12. 动效助手遵循系统设置：`Motion.Evaluate(true/false)` 的返回值与三档时长排序
13. 窗口材质回退不抛异常：`WindowBackdrop.Apply` 在任意系统版本上都返回合法档位
14. 尺寸与模板资源：默认画布、最小时宽的断言与模板键的存在性
15. 版本号来自 `version.txt`：程序集元数据、`AppInfo.DisplayVersion`、视图模型三处一致

当前全部通过（15 项）。

## 使用说明

- **搜索**：输入任意语言、任意大小写，匹配显示名 / 本地化名 / 中文名 / 家族名 / 文件名 / 样式名。
- **筛选**：左栏「分类」列出全部 / 中文 / 西文 / 其他及各自数量；「风格」是四个方块开关（全部 / 常规 / 粗体 / 斜体，互斥）；「只看失效登记」把范围收窄到注册表里指向已删除文件的条目。
- **看细节**：单击任意一行，该行展开成 150px 的大样本，右侧「档案」面板列出家族、本地化名、样式、字重、文件、路径、语言与状态。
- **对比选中**：按住 Ctrl 选 1–3 个字体后点「对比选中」，或直接双击某一行。对比窗口里改文字会立刻重渲染所有栏位。
- **从文件安装**：选 `.ttf/.otf/.ttc/.otc`，会弹 UAC；取消 UAC 视为放弃操作，不会留下半成品。
- **卸载字体**：对选中项执行，同样要 UAC。
- **清理失效登记**：只删除注册表中指向缺失文件的登记项，**不会删除任何字体文件**。
- **快捷键**：`Ctrl+F` 聚焦搜索框，`Esc` 清空搜索（再按一次取消选中），`Enter` 打开选中项的对比窗口。

## 性能基线

本机（1333 个已装字体、1329 个不同字体文件）实测：

| 场景 | 耗时 |
| --- | --- |
| 注册表扫描出列表 | 约 500 ms |
| 全量补全文件名（直读 sfnt） | 约 480 ms（0.36 ms/文件） |
| 1 万条字体的搜索 / 筛选 | 175 ms / 110 ms |
| 内存占用（1333 个字体、可视区渲染完） | 约 160 MB |

## 已知限制

- 语言归属靠名字关键词推断，未解析字体二进制字符表，因此某些冷门字体会被归到「其他」。
- 旧式 `.fon` 位图字体不是 sfnt 容器，无法读取家族名，列表里会以文件名做显示名。
- `.ttc/.otc` 字体集合只读取第一个字体，同集合中的其余成员不会单独列出。注册表若用不同名字登记同一文件的多个字面，列表会把它们保留成多条（宁可多列也不丢掉真实字面）。
- 注册表扫描只覆盖 `HKLM`/`HKCU` 的 `Windows NT\CurrentVersion\Fonts`，不处理通过 GDI 私有加载的临时字体。
- 主程序是框架依赖单文件（约 370 KB），依赖目标机已安装 .NET 8 桌面运行时；缺失时由引导器提示并给出下载链接。
