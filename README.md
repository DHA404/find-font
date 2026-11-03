# find-font

Windows 本地字体预览与管理工具。WPF + .NET 8 + MVVM，单文件分发，附带一个 .NET Framework 4.8 引导器负责运行时检测。

![find-font 主界面](docs/screenshot.png)

## 现在下载

到 [Releases](../../releases/latest) 下载 `find-font-win-x64.zip`，解压后双击 **`FindFont.exe`** 即可。绿色版，免安装。

需要 .NET 8 桌面运行时；没装的话引导器会弹框并给出下载链接。

## 它解决什么

Windows 自带的字体查看器只能看文件名，无法一眼看清一个字体在中英文混排下长什么样，也不能批量清理注册表里指向已删除文件的「僵尸字体」。find-font 把本机所有字体按真实字形渲染成缩略图，支持搜索、筛选、对比、安装与卸载。

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
build.cmd publish  :: 发布到 publish\ 并打包 dist\find-font-0.1.0-alpha-win-x64.zip
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
  Models/FontItem.cs              字体条目模型（含 DedupKey、BestName 回退链）
  Helpers/NativeMethods.cs        AddFontResourceEx / RemoveFontResourceEx / WM_FONTCHANGE 广播
  Helpers/FontMetadataReader.cs   直读 sfnt 的 name/OS/2/head 表取家族名、本地化名、字重、斜体（GlyphTypeface 仅作回退）
  Helpers/LanguageInference.cs    按名字关键词推断 CJK 归属（计划书 6A，不解析字符表）
  Services/FontScanner.cs         注册表扫描 + 缺失文件占位 + 异步补全
  Services/FontRenderCache.cs     STA 渲染线程 + LRU 位图缓存 + 占位图降级
  Services/FontInstaller.cs       安装 / 卸载 / 清理异常（仅提权进程执行）
  Services/ElevationHost.cs       --install-font 等参数协议的微进程宿主
  Converters/                     FontToImage（MultiBinding → BitmapSource）、可见性转换器
  ViewModels/MainViewModel.cs     扫描、防抖搜索、筛选、多选、安装卸载
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
6. `FontMetadataReader.TryRead` 遍历本机全部字体文件，断言 sfnt 字体零失败、平均耗时 < 5 ms/文件，并抽样核对 SimSun / Arial / 微软雅黑的家族名与中文名
7. `FontRenderCache` 实际出图，断言尺寸 > 0、已 `Freeze`、二次请求命中缓存
8. `MainViewModel` 真实加载后验证搜索、分类筛选、风格筛选与清空恢复
9. 1 万条字体的压测：装载、搜索、筛选、清空恢复各自的耗时上限（搜索/筛选 < 500 ms）
10. `CompareViewModel` 最多三栏上限与移除后栏数变化

当前全部通过（10 项）。

## 使用说明

- **搜索**：输入任意语言、任意大小写，匹配显示名 / 本地化名 / 中文名 / 家族名 / 文件名 / 样式名。
- **筛选**：分类（中文、西文、其他）+ 风格（常规、粗体、斜体）+ 只看异常。
- **对比**：按住 Ctrl 在列表里选 2–3 个字体，点「对比」。对比窗口里改文字会立刻重渲染所有栏位。
- **安装**：点「安装字体」选 `.ttf/.otf/.ttc/.otc`，会弹 UAC；取消 UAC 视为放弃操作，不会留下半成品。
- **清理异常**：只删除注册表中指向缺失文件的登记项，**不会删除任何字体文件**。

## 性能基线

本机（2256 个已装字体、1328 个不同字体文件）实测：

| 场景 | 耗时 |
| --- | --- |
| 注册表扫描出列表 | 约 500 ms |
| 全量补全文件名（直读 sfnt） | 约 420 ms（0.31 ms/文件） |
| 1 万条字体的搜索 / 筛选 | 75 ms / 24 ms |
| 内存占用（2256 个字体、可视区渲染完） | 约 160 MB |

## 已知限制

- 语言归属靠名字关键词推断，未解析字体二进制字符表，因此某些冷门字体会被归到「其他」。
- 旧式 `.fon` 位图字体不是 sfnt 容器，无法读取家族名，列表里会以文件名做显示名。
- `.ttc/.otc` 字体集合只读取第一个字体，同集合中的其余成员不会单独列出。
- 注册表扫描只覆盖 `HKLM`/`HKCU` 的 `Windows NT\CurrentVersion\Fonts`，不处理通过 GDI 私有加载的临时字体。
- 主程序是框架依赖单文件（约 370 KB），依赖目标机已安装 .NET 8 桌面运行时；缺失时由引导器提示并给出下载链接。
