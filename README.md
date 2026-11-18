# MeetingGuard

**面向视频会议的 AIGC 虚假内容识别与面部隐私保护**

MeetingGuard 是一个面向视频会议场景的 Windows 桌面客户端，用于连接音视频检测服务，采集会议音视频流、接收风险识别结果，并提供告警和面部隐私保护相关功能。

## 主要功能

- 视频画面与系统音频采集
- 通过 HTTP/gRPC 与检测服务通信
- AIGC 虚假内容风险结果展示与告警
- 面部隐私保护服务接入
- 本地用户、会话和审计数据管理
- 面向本地联调的 Python mock gRPC 服务

## 技术栈

- .NET 8 / WPF
- CommunityToolkit.Mvvm
- OpenCvSharp
- NAudio
- gRPC / Protocol Buffers
- SQLite
- Serilog

## 项目结构

```text
.
├── src/
│   ├── MeetingAgent.App/             # WPF 桌面应用
│   ├── MeetingAgent.Core/            # 核心模型与接口
│   ├── MeetingAgent.Capture/         # 视频采集
│   ├── MeetingAgent.Audio/           # 音频采集
│   ├── MeetingAgent.Transport/       # HTTP/gRPC 通信
│   ├── MeetingAgent.Alerting/        # 风险告警
│   └── MeetingAgent.Infrastructure/  # 配置、日志等基础设施
├── tools/mock-server/                # 本地联调服务
├── Directory.Build.props
└── MeetingAgent.sln
```

## 环境要求

- Windows 10 版本 2004（10.0.19041）或更高版本
- .NET 8 SDK
- Visual Studio 2022（推荐安装“.NET 桌面开发”工作负载）

## 构建与运行

```powershell
dotnet restore MeetingAgent.sln
dotnet build MeetingAgent.sln
dotnet run --project .\src\MeetingAgent.App\MeetingAgent.App.csproj
```

默认服务地址和采集参数位于 `src/MeetingAgent.App/appsettings.json`。如需连接实际检测服务，请按部署环境修改对应地址，并避免将认证令牌提交到仓库。

## 说明

本仓库仅包含 MeetingGuard 的 Windows 前端/桌面客户端代码。音频检测、视频检测及面部隐私保护后端服务需要单独部署。
