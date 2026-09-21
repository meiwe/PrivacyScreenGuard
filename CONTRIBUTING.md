# 贡献指南

感谢你有兴趣为 PrivacyScreenGuard 做贡献。本文档面向开发者，说明如何搭建环境、跑测试、提交流代码。使用层面的说明请见 [README.md](README.md)。

## 环境准备

- Windows 10 / 11（x64）
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 可用的摄像头（内置或 USB）——涉及真实采集的调试需要
- 推荐 IDE：Visual Studio 2022 或 VS Code（需 C# Dev Kit 或 C# 扩展）

## 获取代码并构建

```powershell
git clone https://github.com/meiwe/PrivacyScreenGuard.git
cd PrivacyScreenGuard

# 还原依赖并构建整个解决方案
dotnet build PrivacyScreenGuard.sln

# 运行（需先准备好 models/ 目录，见 README「模型文件」一节）
dotnet run --project src/PrivacyScreenGuard
```

## 运行测试

```powershell
dotnet test
```

当前共 **54 个用例**，覆盖状态机判定、健康提醒、多人在场策略、姿态过滤与模板加解密。

> ⚠️ **重要**：`TemplateStoreTests` 直接读写真实的 `%LOCALAPPDATA%\PrivacyScreenGuard\owner.bin`，每个用例开头都会 `Delete()` 清场。**若你本机已注册主人人脸，请先备份该文件再执行 `dotnet test`**，否则模板会被清除：
>
> ```powershell
> # 备份
> Copy-Item "$env:LOCALAPPDATA\PrivacyScreenGuard\owner.bin" "$env:TEMP\owner.bin.bak"
> # 跑完测试后还原
> Copy-Item "$env:TEMP\owner.bin.bak" "$env:LOCALAPPDATA\PrivacyScreenGuard\owner.bin"
> ```

## 发布打包

单文件自包含发布（无需目标机器安装 .NET 运行时）：

```powershell
dotnet publish src/PrivacyScreenGuard/PrivacyScreenGuard.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

发布完成后需把仓库根的 `models` 目录复制到 `publish` 旁边（与 `PrivacyScreenGuard.exe` 同级），否则启动时会提示模型缺失：

```powershell
Copy-Item -Recurse models publish\models
```

推送 `v*` 标签会触发 [GitHub Actions](.github/workflows/release.yml) 自动编译、跑测试并创建 Release。

## 目录结构

```text
PrivacyScreenGuard/
├── PrivacyScreenGuard.sln              # 解决方案
├── README.md                           # 项目说明
├── LICENSE                             # MIT 许可证
├── .github/workflows/release.yml       # CI：编译 + 测试 + 发布 Release
├── assets/
│   └── icon.svg                        # 应用图标设计源文件（矢量）
├── docs/
│   └── screenshots/                    # README 界面截图
├── design/
│   └── ui-mockup.html                  # 界面排版原型（HTML 预览稿）
├── models/                             # 模型目录（不入库，首次运行可自动下载）
├── src/PrivacyScreenGuard/             # 主项目（WPF，net8.0-windows）
│   ├── App.xaml.cs                     # 入口：单实例/接线/热键/托盘/向导
│   ├── app.manifest                    # PerMonitorV2 DPI 感知清单
│   ├── Assets/                         # 图标资产（已随仓库提供）
│   ├── Models/                         # 设置模型、枚举、人脸/健康数据结构
│   ├── Native/Win32.cs                 # SetWindowPos / WS_EX_* 等 P/Invoke
│   ├── Services/                       # 摄像头、检测、识别、守护引擎、状态机、托盘等
│   └── Windows/                        # 主窗口、注册向导、模型下载窗口、遮罩窗口
└── tests/PrivacyScreenGuard.Tests/     # 单元测试（54 个用例）
```

## 图标修改

应用图标的设计源文件是 `assets/icon.svg`（矢量），`src/PrivacyScreenGuard/Assets/` 下是已生成好的成品（`icon.ico` 供 exe/窗口/快捷方式使用，PNG 供托盘使用），已随仓库提供，**构建项目无需任何额外工具**。

若要修改图标：编辑 `assets/icon.svg`，用任意 SVG 工具（Inkscape、Illustrator、在线转换服务等）导出为多尺寸 `.ico`（建议包含 16/24/32/48/64/128/256）覆盖 `Assets/icon.ico`，并导出 256×256 PNG 覆盖 `Assets/icon_256.png`，重新 `dotnet build` 即生效。

## 手动测试清单

以下用例涉及真实摄像头、多显示器与系统安全桌面，无法在自动化环境中覆盖，**改动相关代码后请逐项实机验证**。已自动化验证的部分：`dotnet build` 构建通过、54 个单元测试通过、`dotnet publish` 单文件发布成功 + 启动冒烟。

| # | 用例 | 要点 | 验证方式 |
|---|---|---|---|
| 1 | 单人主人 | 主人正对屏幕不触发遮罩 | 人工 |
| 2 | 单人非主人 | 陌生人入镜约 500ms 后触发遮罩 | 人工 |
| 3 | 多人（含主人在场） | 主人 + 陌生人同时在画面：按主人处理不触发（已知限制，见 README 第 5 节） | 人工 |
| 4 | 照片/视频人脸攻击 | 拿照片/手机视频对准摄像头：仍按"检测到人脸"处理并触发遮罩（无活体检测） | 人工 |
| 5 | 侧脸 | 主人扭头看侧屏：**不触发遮罩**（姿态过滤），状态栏显示"检测到侧脸/边缘脸，暂不判定身份" | 人工 |
| 6 | 暗光 | 关灯/遮光：出现"光线过暗"气泡（5 秒节流），应用不崩溃 | 人工 |
| 7 | 主人离开 | 遮罩激活后主人离开再回来：按恢复延迟自动解除（无人策略=保持正常时） | 人工 |
| 8 | 多屏 | 双显示器：遮罩同时覆盖两屏、点击穿透生效 | 人工（需多屏） |
| 9 | 全屏游戏/视频 | 遮罩可覆盖全屏应用画面 | 人工 |
| 10 | 锁屏/安全桌面 | 遮罩无法覆盖（预期行为），验证应用不崩溃、解锁后恢复正常 | 人工 |
| 11 | UAC | 同上，UAC 弹窗不被遮罩覆盖，应用不崩溃 | 人工 |
| 12 | 误报/漏报统计 | 日常使用一段时间，统计主人被误触发与非主人漏触发次数 | 人工 |
| 13 | 触发/恢复延迟实测 | 对照设置值验证 500ms 触发、800ms 恢复（含切换 ≤200ms） | 人工 |
| 14 | 摄像头拔插 | 运行中拔掉摄像头：气泡提示"摄像头已断开"，应用不崩溃，重插自动恢复 | 人工 |

## 代码风格

- **注释用中文**，解释"为什么"而非"是什么"；类与公开成员建议加 XML 文档注释。
- 遵循现有文件的组织方式与命名习惯，不做与本次改动无关的重构或格式化。
- 提交前请确保 `dotnet build` 零警告、`dotnet test` 全绿。
- 涉及隐私相关逻辑（画面处理、特征存储、网络请求）的改动，请在 PR 描述中说明数据流向。

## 提交信息规范

本项目使用中文提交信息，格式为 `类型：简述`，类型沿用以下约定：

| 类型 | 用途 | 示例 |
|---|---|---|
| `功能` | 新增功能 | `功能：健康提醒系统——五类提醒与状态机` |
| `修复` | 修复缺陷 | `修复：遮罩不显示——Show 后尺寸为 0 导致定位越界` |
| `样式` | 界面与视觉调整 | `样式：引入 WPF-UI Fluent 控件样式` |
| `排版` | 字号、间距、层级 | `排版：8pt 网格落地——卡片化与字号阶梯` |
| `体验` | 交互体验优化 | `体验：滑块拖动时数值实时联动` |
| `文案` | 文字表述调整 | `文案：修正侧脸选项与实际功能不符` |
| `简化` | 删减冗余 | `简化：移除未使用的主题字典` |
| `CI` | 构建与发布配置 | `CI：新增 GitHub Actions 自动构建工作流` |

简述部分建议说明**改动的原因与影响**，而非罗列改了哪些文件。

## 提交 PR

1. Fork 本仓库并从 `main` 切出特性分支（如 `fix/mask-flicker`）。
2. 完成改动，确保构建与测试通过。
3. 提交 PR，在描述中说明：**改动目的、实现思路、验证方式**（哪些用例已实机验证）。
4. 涉及界面的改动，建议附上前后对比截图。

## 报告问题

- **普通 Bug 与功能建议**：使用 [Issue 模板](.github/ISSUE_TEMPLATE)，请附上系统版本、摄像头型号与复现步骤。
- **安全漏洞**：请**勿**公开提 Issue，按 [SECURITY.md](SECURITY.md) 的方式私下报告。
