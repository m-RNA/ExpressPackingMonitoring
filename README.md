# 📦 极简打包监控 (Express Packing Monitoring)

一款基于 WPF 与 .NET 8 构建的现代化物流、电商打包监控与录制终端软件。该系统致力于通过外接摄像头和条码扫描枪，实现对包裹打包全流程的自动化录像、单号绑定、智能特写以及历史追溯，极大降低客诉率与错发现象。

## 🌟 核心特性

- 📹 **高可靠视频录制**：基于 AForge 采集画面，底座集成 FFmpeg 进行高性能音视频编码。支持直接调用 USB 摄像头及 OBS 虚拟摄像头。
- 📠 **条码指令智控**：通过扫描专用控制条码（`START`, `STOP`, `SHIP`, `BACK`, `CLEAR`）实现软件的无接触操作，并内置防重复扫描的动态冷却 UI 机制。
- 🔍 **智能特写 (Smart Zoom)**：当系统检测到包裹条码或核心操作时，可自动放大关键区域，记录清晰的贴单或封箱细节。
- 💾 **自动巡航与配额管理**：支持自定义录像存储路径、磁盘配额（GB）阈值管理，当存储空间不足时自动循环清理最旧的已送达录像。包含静止超时自动结束等容错策略。
- 🎬 **历史回放与防丢**：内置「历史录像检索与回放」看板，输入单号即可秒级追溯过去的打包录像。
- 📊 **效率统计看板**：可视化数据统计与看板图表，展示近 7 日的打包效率与工作量趋势。
- 🎨 **现代化 UI 体验**：使用 MVVM 架构，自带 Light/Dark/Auto（浅色/深色/跟随系统）主题，高度可用且美观。

## 🛠️ 技术栈

- **框架**：[.NET 8.0 Windows (WPF)](https://dotnet.microsoft.com/)
- **架构模式**：MVVM (依靠 `CommunityToolkit.Mvvm`)
- **视频与图像处理**：
  - [AForge.Video](http://www.aforgenet.com/) (DirectShow 摄像头接入)
  - [OpenCvSharp4] (画面处理，特写与识别辅助)
  - [FFmpeg] (用于音视频合并打轴的外部引擎)
- **本地存储**：`Microsoft.Data.Sqlite` (SQLite)
- **UI 组件库**：`Extended.Wpf.Toolkit` (数值调节等高级组件)

## 🚀 快速开始

### 运行环境要求

- Windows 10/11 x64
- 已安装 [.NET 8.0 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- 系统环境变量 (`PATH`) 中需包含 `ffmpeg`（或将 `ffmpeg.exe` 放置在程序运行根目录，推荐版本 v5.0+）。

### 编译与发布 

本项目支持单文件发布 (Single-File Publish)，非常适合将整个终端系统打包成一个独立的 `.exe` 给产查打包站使用，零额外依赖。

```powershell
# 克隆仓库后进入项目目录
cd ExpressPackingMonitoring

# 编译运行
dotnet run --project ExpressPackingMonitoring

# 发布为独立单文件无需安装运行时 (Win-x64)
dotnet publish ExpressPackingMonitoring -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## ⚙️ 核心配置说明

通过界面点击齿轮图标 ⚙️ 进入设置中心，可配置以下参数：

- **设备与画面**：配置视频输入源、分辨率、帧率及麦克风音频接入。
- **特写策略**：开启/关闭智能特写，配置特写倍率、延迟时间及持续时间。
- **录制控制**：条码扫码冷却时间保护、超时预警、单段录像时长上限与静止自动停止策略。
- **存储与检索**：修改录像存储位置与设定磁盘限额上限。
- **条码指令定制**：正则表达式截取真正的订单号，防误扫拦截。

## 📄 许可证

本项目开源，遵守 [AGPL-3.0 License](LICENSE)。
