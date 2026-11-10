# WeChatMsgHelper

面向 PC 微信 4.x 的轻量消息朗读辅助程序。运行目标为 .NET Framework 4.8 x64，只通过 Windows UI Automation 读取微信界面，不注入、不 Hook、不抓包，不使用第三方依赖。

当前版本 `1.8.2.1` 提供隐藏托盘常驻、多微信会话监听、打开会话自动朗读最后一条、自己和他人新消息提取、虚拟消息浏览、全局热键，以及争渡、保益和 SAPI5 多种实际语音通道。

语音通道初始化成功后会朗读完整的四段版本号。版本文本由程序集版本动态生成，后续升级会自动朗读新版本号；退出时会优先停止定时任务和全局输入监听，避免等待外部语音或 UI Automation 组件而拖慢系统。

## 构建

要求 Windows 10 1903+ 或 Windows 11，以及 .NET Framework 4.8 Developer Pack。项目不需要 NuGet 还原。

在项目根目录执行：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe" `
  .\WeChatMsgHelper.sln /t:Rebuild /p:Configuration=Release /p:Platform=x64
```

主程序输出：`WeChatMsgHelper\bin\x64\Release\WeChatMsgHelper.exe`。

可直接测试的发布文件：`dist\WeChatMsgHelper.exe`。发布目录只需要这一个文件。

## 快速开始

1. 在微信“设置 > 通用”中开启读屏优化模式。
2. 运行 `dist\WeChatMsgHelper.exe`。
3. 程序没有主窗口，会驻留在系统托盘。
4. 按 `Ctrl+Alt+S` 打开设置，选择通知方式和语音通道。

默认语音通道是争渡独立通道 `WeChatMsg`。另外可以选择自动、争渡读屏、保益第三方独立通道、保益读屏通道或 SAPI5。自动模式检测到保益主进程时优先使用保益第三方独立通道。

保益第三方独立通道名称可在设置中单独保存，程序会将该名称同时用于保益 DLL 和 Web API；保益按这个通道名称关联并保存语音库配置，默认值为 `WeChatMsg`。

新消息提示音按方向区分：自己发送的消息使用 `SentMessage.mp3`，对方发来的消息使用 `SIMToolkitSMS.mp3`。两个音效由项目根目录的源文件生成后嵌入 exe，发布目录不需要携带 MP3 文件。

配置和日志保存在 `%LOCALAPPDATA%\WeChatMsgHelper`。程序不会在桌面或 exe 同目录创建 `config.ini`、`log.txt`；旧版本遗留的同目录文件会在首次启动时自动迁移。

## 默认快捷键

- `Ctrl+Alt+F11`：朗读当前聊天的最新消息。
- `Ctrl+Alt+Shift+F11`：启动或停止自动监听。
- `Ctrl+Alt+P`：循环切换通知方式。
- `Ctrl+Alt+S`：打开设置。

微信位于前台时还支持 `F3`、`Alt+P/T/V`。全局搜索使用微信原生 `Ctrl+F`；输入框聚焦时支持 `Ctrl+Alt+上/下/Home/End` 浏览按聊天保存的虚拟消息队列。

## 当前验证环境

- 微信版本：4.1.11.24
- 微信进程：`Weixin.exe`
- 微信主窗口类：`mmui::MainWindow`
- 争渡 DLL：`ZDSRAPI_x64.dll`，x64，文件版本 1.0.0.5
- 保益 DLL：`byctrl-x64.dll`，x64，文件版本 2.2.0.0；Web API 端口 26609。
- Release exe 因嵌入原始未压缩 MP3 音效而超过原来的 100KB 体积约束。

争渡、保益 DLL、保益 Web API 和 SAPI5 均已完成实际消息提交验证。详细操作见 `docs\使用说明.md`，当前升级结果见 `docs\1.7.2_验证记录.md`。
