# PrivacyScreenGuard（摄像头隐私屏）

## 1. 项目简介与功能特性

PrivacyScreenGuard 是一款 Windows 桌面"摄像头隐私屏"软件（WPF，.NET 8）。它通过摄像头在本地检测人脸，并与主人特征比对：当屏幕前出现**非主人**的人脸时，自动在全屏覆盖一层隐私遮罩，把屏幕内容隐藏起来；主人回到镜头前后，遮罩自动解除。

功能特性：

- **本地人脸守护**：YuNet 人脸检测 + SFace 特征识别，与主人的加密特征模板余弦比对，全程本地推理，不依赖任何云端服务。
- **多屏遮罩**：支持"全部显示器 / 仅主显示器 / 指定显示器"三种守护范围，遮罩同时覆盖所选屏幕。
- **点击穿透**：遮罩窗口不接收鼠标、不抢焦点，鼠标操作可穿透到下层窗口；不出现在任务栏与 Alt+Tab。
- **全局热键**：默认 `Ctrl+Alt+P` 一键暂停/恢复守护（可在设置中更换，被占用时提示）。
- **系统托盘**：常驻托盘图标（绿=守护中 / 黄=已暂停 / 红=摄像头异常），右键菜单 + 关键事件气泡提醒。
- **加密存储**：主人特征模板使用 DPAPI（当前 Windows 用户）加密落盘，换用户/换机器均无法解密；提供一键清除生物特征。
- **单实例运行**：重复启动会提示"已在运行"并退出。
- **开机自启**：可选，注册表方式，设置文件为准双向同步。

## 2. 隐私承诺

- **全本地处理**：人脸检测、特征提取、比对全部在本机 CPU 完成。
- **唯一的联网行为**：仅在首次运行、模型文件缺失且用户点击确认后下载模型（约 37 MB，来自 OpenCV Zoo 官方仓库）。模型就绪后，正常运行期间不发起任何网络请求；可在设置完成后用防火墙/netstat 自行验证。
- **不上传**：不向任何服务器发送数据（包括人脸特征、画面、使用统计）。
- **不录制、不保存原始画面**：摄像头帧仅在内存中处理，处理完立即释放，绝不写盘、不缓存超过一帧、无临时文件。
- **仅保存加密特征**：落盘的只有 DPAPI 加密后的 128 维特征向量（`%LOCALAPPDATA%\PrivacyScreenGuard\owner.bin`），不含任何图像。
- **一键清除**：主窗口提供"清除主人人脸数据"，删除加密模板后需重新注册才能继续守护。

## 3. 环境要求

- Windows 10 / 11（x64）
- .NET 8 SDK（构建与运行源码时需要；单文件发布版无需安装运行时）
- 可用的摄像头（内置或 USB）
- （可选）Python 3：仅用于运行模型下载脚本

## 4. NuGet 依赖列表

以下依赖均来自 `src/PrivacyScreenGuard/PrivacyScreenGuard.csproj`：

| 包 | 版本 | 用途 |
|---|---|---|
| OpenCvSharp4 | 4.13.0.20260627 | OpenCV 的 .NET 封装：摄像头采集（VideoCapture）、DNN 推理（CvDnn）、图像处理（对齐变换等） |
| OpenCvSharp4.runtime.win | 4.13.0.20260627 | OpenCV 在 Windows 上的原生动态库（随单文件发布一并打包） |
| OpenCvSharp4.WpfExtensions | 4.13.0.20260627 | Mat 与 WPF BitmapSource 互转（首次注册向导的摄像头预览） |
| System.Management | 10.0.12 | 通过 WMI 查询摄像头设备名称 |
| System.Security.Cryptography.ProtectedData | 10.0.12 | DPAPI 加密主人特征模板 |
| System.Text.Json | 10.0.12 | 读写设置文件 settings.json |

> 说明：本项目使用的 OpenCvSharp 4.13 版本没有 `FaceRecognizerSF` 包装类，因此 SFace 识别通过 `CvDnn` 直接加载 ONNX 推理，5 点对齐使用 `Cv2.EstimateAffinePartial2D` 自行实现（与 OpenCV 官方 SFace 实现数值等价）。

## 5. 模型文件

应用启动时必须能找到以下两个模型（缺失则提示并退出）：

| 模型 | 文件名 | 用途 | 许可证 |
|---|---|---|---|
| YuNet 人脸检测 | `face_detection_yunet_2023mar.onnx`（约 0.22 MB） | 输出人脸框 + 5 关键点（左眼、右眼、鼻尖、左嘴角、右嘴角） | Apache-2.0（可商用） |
| SFace 人脸识别 | `face_recognition_sface_2021dec.onnx`（约 36.9 MB） | 从对齐后的 112×112 人脸提取 128 维特征 | Apache-2.0（可商用） |

**下载地址**（OpenCV Zoo 官方 GitHub 仓库）：

- https://github.com/opencv/opencv_zoo/raw/main/models/face_detection_yunet/face_detection_yunet_2023mar.onnx
- https://github.com/opencv/opencv_zoo/raw/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx

**放置路径**：

- 程序内自动下载（推荐）：首次启动时若模型缺失，程序会询问并自动下载到 exe 同目录的 `models/`；该位置不可写时（如安装在 `Program Files`）自动改用 `%LOCALAPPDATA%\PrivacyScreenGuard\models\`
- 手动放置：开发期放仓库根 `models/`；发布后放 exe 同目录的 `models/` 文件夹

程序按以下顺序查找模型目录（见 `ModelLocator`）：① exe 同目录 `models` → ② 当前工作目录 `models` → ③ exe 目录上溯三级的 `models`（兼容开发期 `bin/Debug` 结构）→ ④ `%LOCALAPPDATA%\PrivacyScreenGuard\models`。

> 下载源按顺序尝试：GitHub 官方 → 国内镜像（`ghfast.top`、`gh-proxy.com`），任一成功即停；下载先写入 `.tmp` 临时文件，校验大小后原子改名，避免残缺文件被当作模型使用。下载失败时窗口内可点"重试"（会重新遍历下载源）。

## 6. 构建与运行

```powershell
# 构建整个解决方案
dotnet build PrivacyScreenGuard.sln

# 运行（需已按第 5 节准备好 models/ 目录）
dotnet run --project src/PrivacyScreenGuard
```

**调试说明**：

- 使用 Visual Studio 或 VS Code 打开 `PrivacyScreenGuard.sln` 即可调试（VS Code 需 C# Dev Kit 或 C# 扩展）。
- 推荐断点：`GuardEngine.OnFrameCaptured` / `GuardEngine.ProcessFrame`（帧处理主链路）、`GuardStateMachine.Process`（状态机判定）。
- **首次运行**（无主人模板）会自动弹出注册向导；取消向导则应用退出。
- 单元测试：`dotnet test`（状态机与模板存储共 13 个测试）。

## 7. 发布打包

单文件自包含发布（无需目标机器安装 .NET 运行时）：

```powershell
dotnet publish src/PrivacyScreenGuard/PrivacyScreenGuard.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

发布完成后，需要把仓库根的 `models` 目录复制到 `publish` 目录旁边（与 `PrivacyScreenGuard.exe` 同级），否则启动时会提示模型缺失：

```powershell
Copy-Item -Recurse models publish\models
```

**MSIX 说明**：如需商店分发或打包安装器，可再用 Visual Studio 的"Windows 应用程序打包项目"向导把发布产物打包为 MSIX；本仓库直接提供单文件 exe 即可满足常规使用。

## 8. 使用说明

### 首次引导

首次启动（未检测到主人模板）会打开注册向导：选择摄像头 → 正对镜头采集 **3~5 张**正脸（支持自动采集，约每 0.9 秒一张，也可点"采集一张"手动采集）→ 完成注册。所有特征逐维平均并 L2 归一化后存为加密模板；采集画面仅在本地处理，不保存任何图像。

### 设置项说明

| 设置项 | 默认值 | 范围/取值 | 说明 |
|---|---|---|---|
| 识别阈值 | 0.55 | 0.3–0.9 | 与主人特征的余弦相似度判定线，越大越严格（漏认主人多、误放陌生人少） |
| 触发延迟 | 500 ms | 300–800 ms | 检测到非主人人脸持续多久后触发遮罩（防瞬时误触发） |
| 恢复延迟 | 800 ms | 500–1000 ms | 主人回归后持续多久解除遮罩（防闪烁） |
| 采集帧率 | 8 FPS | 5–10 FPS | 摄像头采集与推理频率，越低越省电 |
| 无人策略 | 保持正常 | 保持正常 / 触发锁定 | 画面中无人脸时是否视为风险（"触发锁定"适用于离开工位自动锁屏场景） |
| 全局热键 | Ctrl+Alt+P | 可自定义 | 暂停/恢复守护；被其他程序占用时注册失败并提示更换 |
| 开机自启 | 关闭 | 开/关 | 写注册表 Run 键，与设置文件双向同步 |
| 摄像头 | 索引 0 | 下拉选择 | 枚举本机摄像头（名称经 WMI 对齐） |
| 守护范围 | 全部显示器 | 全部 / 仅主屏 / 指定 | 多屏遮罩覆盖范围 |
| 模糊强度 | 50 | 0–100 | 遮罩激活时截屏并高斯模糊的强度（sigma 随值放大），值越大越模糊；0 = 纯黑遮罩。下次遮罩显示时生效 |
| 清除生物特征 | — | 按钮 | 删除加密模板（需确认），之后须重新注册 |

设置持久化于 `%LOCALAPPDATA%\PrivacyScreenGuard\settings.json`，非法数值在读写时自动夹取到合法范围。

### 托盘菜单

托盘图标右键菜单：**显示主窗口** / **暂停守护·恢复守护**（文案随状态切换）/ **退出**；双击图标等同"显示主窗口"。触发遮罩、摄像头异常（无摄像头/被占用/断开/光线过暗/模型缺失）等关键事件会弹气泡提醒，图标圆点颜色实时反映守护状态。

## 9. 技术实现要点

- **识别流水线**：YuNet 检测（输出人脸框 + 5 关键点）→ 以 ArcFace 标准 5 点为参考，`Cv2.EstimateAffinePartial2D`（RANSAC 相似变换）把人脸对齐裁剪到 112×112 → SFace（CvDnn 直接加载 ONNX，预处理与 OpenCV 官方一致）提取 128 维特征 → 与主人模板做余弦相似度比对。主人模板由注册阶段多张特征**逐维平均后 L2 归一化**得到。
- **状态机（防闪烁）**：每帧产生观测结论（无人 / 主人在场 / 有人但非主人）；"有人但非主人"（以及无人策略为锁定时的"无人"）为风险帧——连续风险达到触发延迟才显示遮罩；遮罩激活后需连续安全达到恢复延迟才解除，避免单帧抖动造成遮罩闪烁。触发/恢复延迟与无人策略变更时状态机整体重建，重建/暂停时补发 Hide 保证遮罩不卡死。
- **多人判定**：对帧内每张人脸分别比对，只要**任意一张**匹配主人（相似度 ≥ 阈值）即视为安全帧。因此主人和陌生人同时在镜头前时按主人处理（不触发），后续版本可加强为"仅主人可解除"。
- **遮罩窗口**：每个目标显示器一个 WPF 无边框半透明窗口（约 88% 不透明深黑 + 提示文字），设置扩展样式 `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` 实现点击穿透、不进 Alt+Tab、不抢焦点，并钩 `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE` 双保险；用 `SetWindowPos` 以**物理像素**精确定位（规避 PerMonitorV2 下 WPF DIP 换算偏差），清单声明 `PerMonitorV2`，跨不同缩放率的多屏可正确全覆盖；窗口句柄复用，重复显隐不重建，切换 ≤200ms。
- **摄像头健壮性**：DSHOW 后端、640×480；打开失败每 2 秒自动重试（累计 5 次提示"可能被占用"），连续 3 次读帧失败判定断开并自动重连；帧平均亮度低于暗光阈值（默认 18）以 5 秒节流提示；拔掉摄像头应用不崩溃。
- **线程模型**：帧处理在摄像头后台采集线程上同步执行（天然限流）；引擎事件均在后台线程触发，UI/托盘操作一律经 Dispatcher 封送；普通状态文本 500ms 节流、推理异常 10 秒节流。

## 10. 性能目标与实测建议

目标值（现代 4 核 CPU、CPU 推理）：

| 指标 | 目标 |
|---|---|
| CPU 占用 | ≤ 15%（默认 8 FPS） |
| 内存 | ≤ 300 MB |
| 遮罩显隐切换 | ≤ 200 ms |

实测建议：任务管理器观察进程 CPU/内存，让引擎以默认设置连续运行 30 分钟，确认无内存持续增长；用秒表/录屏逐帧核对遮罩出现与消失的响应时间；如 CPU 偏高，可将采集帧率下调（5–10 FPS 可调），帧率越低占用越低。

## 11. 重要限制（请务必了解）

- **系统级界面无法覆盖**：遮罩是普通桌面窗口，无法覆盖 UAC 提升对话框、安全桌面、锁屏、Ctrl+Alt+Del 界面，也无法覆盖受 DRM 保护的内容（如部分流媒体播放器窗口）。
- **不能防物理手段**：无法阻止他人用手机拍照、物理截屏或录屏软件录下屏幕内容；防截屏需系统级 API（`SetWindowDisplayAffinity` 仅对窗口内容生效），MVP 未实现。
- **只做"非主人人脸检测"**：不判断视线方向（是否"正在看屏幕"），陌生人正脸入镜即触发。
- **侧脸/贴边脸不参与判定（扭头看侧屏不误触发）**：只有"正脸 + 位于画面中部"的人脸才参与主人比对；明显侧脸（如主人扭头看侧屏）与贴边脸（路过/部分入镜）视为不可靠观测，既不算主人也不算陌生人——不触发遮罩但也**不起保护作用**。边界：陌生人刻意侧身+只露侧脸靠近屏幕时不会被遮罩拦截；主人短时间完全转出画面（画面里没人）按"无人策略"处理。
- **主人与陌生人同时在场按主人处理**：帧内任意一张正脸匹配主人即不触发遮罩，陌生人可能与主人一同观看屏幕，后续版本可加强。
- **"真模糊"已实现（含边界说明）**：遮罩激活瞬间会在后台对屏幕做一次 GDI 截屏 → OpenCV 高斯模糊（强度可在设置中调节，0 = 纯黑遮罩）→ 以模糊画面覆盖全屏，并叠加约 35% 压暗层保证不可读。模糊快照**仅存在于内存**、遮罩隐藏即释放、不落盘、不上传。边界：锁屏 / 安全桌面 / DRM 保护内容下截屏会失败或得到黑块，此时自动回退为半透明纯黑遮罩；模糊图截取的是触发瞬间的画面，遮罩期间屏幕内容变化不会实时刷新（本来就不可读）。
- **模型许可证**：本项目默认的 YuNet 与 SFace 模型均为 **Apache-2.0，可商用**。若追求更高精度换用 ArcFace（InsightFace）官方权重，其许可证为"研究/非商业用途"，**商用需自行评估授权**；ArcFace 仅作为可选高精度路线说明，本项目不随包分发。

## 12. 手动测试清单

以下用例来自 spec，验收时逐项执行。**已自动化验证**的有：`dotnet build` 构建通过、13 个单元测试通过（`dotnet test`，覆盖状态机判定与模板加解密）、`dotnet publish` 单文件发布成功 + 启动冒烟。**其余需实机人工执行**（涉及真实摄像头、多显示器与系统安全桌面，沙箱/自动化环境无法覆盖）：

| # | 用例 | 要点 | 验证方式 |
|---|---|---|---|
| 1 | 单人主人 | 主人正对屏幕不触发遮罩 | 人工 |
| 2 | 单人非主人 | 陌生人入镜约 500ms 后触发遮罩 | 人工 |
| 3 | 多人（含主人在场） | 主人 + 陌生人同时在画面：按主人处理不触发（已知限制，见第 11 节） | 人工 |
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

## 13. 目录结构

```text
PrivacyScreenGuard/
├── PrivacyScreenGuard.sln              # 解决方案
├── assets/
│   └── icon.svg                        # 应用图标设计源文件（矢量，盾牌+镜头+守护斜杠）
├── models/                             # 模型目录（首次运行可自动下载）
│   ├── face_detection_yunet_2023mar.onnx       # YuNet 检测模型（约 0.22 MB）
│   └── face_recognition_sface_2021dec.onnx     # SFace 识别模型（约 36.9 MB）
├── src/PrivacyScreenGuard/             # 主项目（WPF，net8.0-windows）
│   ├── App.xaml.cs                     # 入口：单实例/接线/热键/托盘/向导
│   ├── app.manifest                    # PerMonitorV2 DPI 感知清单
│   ├── Assets/                         # 图标资产（由 build_icon.py 生成）
│   │   ├── icon.ico                    # exe/窗口/快捷方式图标（多尺寸）
│   │   ├── icon_32.png                 # 托盘底图
│   │   └── icon_256.png                # 托盘底图高清源
│   ├── Models/
│   │   ├── AppSettings.cs              # 设置项与默认值、范围夹取
│   │   ├── CoreTypes.cs                # 无人策略/遮罩动作等枚举
│   │   └── FaceInfo.cs                 # 人脸框+关键点
│   ├── Native/
│   │   └── Win32.cs                    # SetWindowPos/WS_EX_* 等 P/Invoke
│   ├── Services/
│   │   ├── CameraService.cs            # 摄像头采集（自动重试/断线重连/暗光提示）
│   │   ├── FaceDetectionService.cs     # YuNet 检测（框+5 关键点）
│   │   ├── FaceRecognitionService.cs   # SFace 对齐+128 维特征+余弦比对
│   │   ├── GuardEngine.cs              # 守护引擎（帧处理主链路）
│   │   ├── GuardStateMachine.cs        # 触发/恢复延迟状态机
│   │   ├── MaskWindowManager.cs        # 多屏遮罩管理（截屏模糊）
│   │   ├── ModelLocator.cs             # 模型文件定位
│   │   ├── ModelDownloadService.cs     # 模型下载（首次运行，多源重试）
│   │   ├── SettingsService.cs          # settings.json 读写
│   │   ├── TemplateStore.cs            # 主人模板 DPAPI 加密存储
│   │   ├── TrayIconService.cs          # 托盘图标与菜单
│   │   ├── HotkeyService.cs            # 全局热键注册
│   │   ├── AutostartService.cs         # 开机自启（注册表）
│   │   └── ICameraService.cs 等        # 接口定义
│   └── Windows/
│       ├── MainWindow.xaml(.cs)        # 主窗口（设置/状态/清除生物特征）
│       ├── SetupWizardWindow.xaml(.cs) # 首次注册向导（3~5 张）
│       ├── ModelDownloadWindow.xaml(.cs) # 模型下载窗口（进度/重试/取消）
│       └── MaskWindow.cs               # 遮罩窗口（点击穿透/置顶/物理像素定位）
├── tests/PrivacyScreenGuard.Tests/     # 单元测试（13 个：状态机+模板存储）
└── publish/                            # dotnet publish 输出（发布后生成）
```

### 图标修改

应用图标的设计源文件是 `assets/icon.svg`（矢量），`src/PrivacyScreenGuard/Assets/` 下是已生成好的成品（`icon.ico` 供 exe/窗口/快捷方式使用，PNG 供托盘使用），已随仓库提供，**构建项目无需任何额外工具**。

若要修改图标：编辑 `assets/icon.svg`，用任意 SVG 工具（Inkscape、Illustrator、在线转换服务等）导出为多尺寸 `.ico`（建议包含 16/24/32/48/64/128/256）覆盖 `Assets/icon.ico`，并导出 256×256 PNG 覆盖 `Assets/icon_256.png`，重新 `dotnet build` 即生效。
