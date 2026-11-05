# CP 最终验收记录

> 当前版本：1.7.2.0  
> 更新日期：2026-11-05  
> 状态：程序侧检查完成，等待用户最终实机验收

## 构建与发布

- [x] .NET Framework 4.8 / Release x64 编译通过，0 警告、0 错误。
- [x] `dist` 发布目录只保留 `WeChatMsgHelper.exe`。
- [x] 无 NuGet 和第三方托管 DLL。
- [x] 配置和日志保存在 `%LOCALAPPDATA%\WeChatMsgHelper`。
- [ ] 单 exe 不超过 100KB：1.6 当前为 129,024 字节，因用户要求原始 MP3 音质而未满足。

## 安全边界

- [x] 未发现 `WriteProcessMemory`、`VirtualAllocEx`、`CreateRemoteThread`、`SetWindowsHookEx` 或 `OpenProcess`。
- [x] 微信数据只通过 UI Automation 读取。
- [x] P/Invoke 仅用于热键、窗口发现、争渡官方 DLL、历史滚动和音效播放。

## 功能检查

- [x] 争渡独立、争渡读屏和 SAPI5 通道。
- [x] 保益 byCtrl DLL 独立通道、Web API 兜底和自动选择。
- [x] 多会话事件监听与 500ms 轮询。
- [x] 打开会话朗读最后消息并播放方向音效。
- [x] 虚拟消息队列和历史加载。
- [x] 设置窗口、托盘通道切换、系统消息过滤。
- [x] Win10 LTSC 超时隔离和后台 SAPI5 语音枚举。

## 待用户验收

- [ ] 争渡环境连续收发 20 条，无漏读和重复。
- [ ] 打字回声与独立通道消息互不打断。
- [ ] 多群、多独立聊天窗口并发。
- [ ] 保益独立通道下微信连续收发和打字并行。
- [ ] Windows 10 LTSC 设置、监听和退出稳定性。
- [ ] 常用杀软扫描结果。

最终结果：等待点 E 未完成。
