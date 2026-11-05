using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Threading;
using System.Windows.Forms;
using WeChatMsgHelper.Channels;

namespace WeChatMsgHelper
{
    internal sealed class MainForm : Form
    {
        private const int WmHotkey = 0x0312;
        private readonly Discovery discovery = new Discovery();
        private readonly MessageSoundPlayer messageSounds = new MessageSoundPlayer();
        private readonly WeChatNavigator navigator = new WeChatNavigator();
        private readonly System.Windows.Forms.Timer discoveryTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer contextTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer hotkeyFallbackTimer = new System.Windows.Forms.Timer();
        private AppConfig config;
        private MessageExtractor extractor;
        private MessageListener listener;
        private SpeechQueue speechQueue;
        private HotkeyManager hotkeys;
        private TrayIcon tray;
        private MessageReviewManager reviewManager;
        private SearchResultAnnouncer searchAnnouncer;
        private readonly UpdateService updateService = new UpdateService();
        private DateTime suppressUntil;
        private string statusText = "正在启动...";
        private bool exiting;
        private bool settingsOpen;
        private int discoveryPending;
        private bool updateCheckPending;

        public MainForm()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1, 1);
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            Opacity = 0D;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Text = "WeChatMsgHelper";
            CreateHandle();

            IList<string> migrationWarnings = AppDataPaths.Initialize(AppDomain.CurrentDomain.BaseDirectory);
            AppLog.Initialize(AppDataPaths.DataDirectory);
            foreach (string warning in migrationWarnings) AppLog.Write(warning);
            config = LoadConfig();
            MigrateLegacyHotkeys();
            tray = new TrayIcon(ToggleListening, SetMode, SetSpeechChannel, ReadLatest, TestSpeech, OpenSettings,
                delegate { CheckForUpdates(true); }, ShowCurrentReleaseNotes, ExitApplication);
            hotkeys = new HotkeyManager(Handle);
            hotkeys.RegisterAll(config, ReportHotkeyFailure);
            CreateListener();
            RebuildSpeech(false);
            searchAnnouncer = new SearchResultAnnouncer(Dispatch, SpeakText);
            ApplySavedStartup();

            discoveryTimer.Interval = 500;
            discoveryTimer.Tick += delegate { BeginBindWechat(); };
            discoveryTimer.Start();
            contextTimer.Interval = 250;
            contextTimer.Tick += delegate { SafeUpdateContextHotkeys(); };
            contextTimer.Start();
            hotkeyFallbackTimer.Interval = 40;
            hotkeyFallbackTimer.Tick += delegate { PollHotkeyFallback(); };
            hotkeyFallbackTimer.Start();
            BeginBindWechat();
            UpdateTray();
            WarnAboutNvda();
            BeginInvoke(new Action(delegate
            {
                ShowInstalledReleaseNotes();
                UpdateInstaller.CleanupOldFiles();
                CheckForUpdates(false);
            }));
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey)
                HandleHotkey(message.WParam.ToInt32());
            base.WndProc(ref message);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting) exiting = true;
            discoveryTimer.Stop();
            contextTimer.Stop();
            hotkeyFallbackTimer.Stop();
            // Speak before UIA and worker cleanup so the exit announcement is immediate.
            if (speechQueue != null) speechQueue.ShutdownWithAnnouncement();
            if (listener != null) listener.Dispose();
            if (reviewManager != null) reviewManager.Dispose();
            if (searchAnnouncer != null) searchAnnouncer.Dispose();
            if (messageSounds != null) messageSounds.Dispose();
            if (hotkeys != null) hotkeys.Dispose();
            if (tray != null) tray.Dispose();
            discoveryTimer.Dispose();
            contextTimer.Dispose();
            hotkeyFallbackTimer.Dispose();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // A hidden tray host can leave the WinForms message loop alive on some
            // Win10/Win11 builds. Force the process to terminate after cleanup.
            Environment.Exit(0);
        }

        private void HandleHotkey(int id)
        {
            switch (id)
            {
                case HotkeyManager.ReadLatestId: ReadLatest(); break;
                case HotkeyManager.ToggleNotifyId: ToggleListening(); break;
                case HotkeyManager.ToggleModeId: CycleMode(); break;
                case HotkeyManager.OpenSettingsId: OpenSettings(); break;
                case HotkeyManager.TestSpeechId: TestSpeech(); break;
                case HotkeyManager.FocusOfficialAccountsId: FocusOfficialAccounts(); break;
                case HotkeyManager.FocusContactsId: FocusContacts(); break;
                case HotkeyManager.FocusVoipId: FocusVoip(); break;
                case HotkeyManager.ReviewPreviousId: reviewManager.ReadPrevious(); break;
                case HotkeyManager.ReviewNextId: reviewManager.ReadNext(); break;
                case HotkeyManager.ReviewFirstId: reviewManager.ReadFirst(); break;
                case HotkeyManager.ReviewLastId: reviewManager.ReadLast(); break;
                case HotkeyManager.NvdaModeId: CycleNvdaMode(); break;
            }
        }

        private void PollHotkeyFallback()
        {
            if (settingsOpen || exiting || hotkeys == null) return;
            foreach (int id in hotkeys.PollFallback()) HandleHotkey(id);
        }

        private AppConfig LoadConfig()
        {
            string path = AppDataPaths.ConfigPath;
            try { return AppConfig.Load(path); }
            catch (Exception ex)
            {
                AppLog.Write("加载配置失败：" + ex.Message);
                AppConfig fallback = new AppConfig();
                fallback.FilePath = path;
                return fallback;
            }
        }

        private void CreateListener()
        {
            if (listener != null) listener.Dispose();
            if (reviewManager != null) reviewManager.Dispose();
            extractor = new MessageExtractor(config.ReadNonTextMessages);
            listener = new MessageListener(extractor, config, ProcessIncomingMessage, ProcessConversationOpened, Dispatch);
            reviewManager = new MessageReviewManager(navigator, extractor, SpeakText, SuppressAutomaticNotifications);
        }

        private void BeginBindWechat()
        {
            if (exiting || settingsOpen || Interlocked.Exchange(ref discoveryPending, 1) != 0) return;
            Thread worker = new Thread(new ThreadStart(delegate
            {
                IList<DiscoveredWechatRoot> roots = null;
                Exception error = null;
                try { roots = discovery.FindWechatRoots(); }
                catch (Exception ex) { error = ex; }
                Dispatch(delegate
                {
                    Interlocked.Exchange(ref discoveryPending, 0);
                    if (exiting || settingsOpen) return;
                    if (error != null)
                    {
                        statusText = "微信 UIA 暂时无响应";
                        AppLog.Write("微信 UIA 发现失败：" + error.GetType().Name + "，" + error.Message);
                        UpdateTray();
                        return;
                    }
                    ApplyWechatRoots(roots ?? new List<DiscoveredWechatRoot>());
                });
            }));
            worker.IsBackground = true;
            worker.Name = "WeChatMsgHelper UIA Discovery";
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        private void ApplyWechatRoots(IList<DiscoveredWechatRoot> roots)
        {
            if (roots.Count == 0)
            {
                try { listener.Synchronize(roots, true); }
                catch (Exception ex) { AppLog.Write("清理微信 UIA 绑定失败：" + ex.Message); }
                statusText = "等待打开具体微信聊天";
                UpdateTray();
                return;
            }
            try
            {
                listener.Synchronize(roots, true);
                statusText = "正在监听 " + listener.BoundCount + " 个会话";
                UpdateTray();
            }
            catch (Exception ex)
            {
                statusText = "微信 UIA 绑定失败";
                AppLog.Write("绑定微信失败：" + ex.Message);
                UpdateTray();
            }
        }

        private void ProcessIncomingMessage(MessageData data)
        {
            AppLog.Write("捕获新消息，长度=" + data.Text.Length + "，方向=" + data.Direction + "，模式=" + (int)config.NotificationMode);
            if (ShouldIgnoreSystemMessage(data)) return;
            NotifyMessage(data, true, true);
        }

        private void ProcessConversationOpened(MessageData data)
        {
            AppLog.Write("打开会话，自动朗读最后一条，长度=" + data.Text.Length);
            if (ShouldIgnoreSystemMessage(data)) return;
            NotifyMessage(data, false, true);
        }

        private void NotifyMessage(MessageData data, bool respectSuppression, bool playMessageSound)
        {
            if (!config.ListeningEnabled || (respectSuppression && DateTime.Now < suppressUntil) || config.NotificationMode == NotificationMode.Off) return;
            if (config.SoundEnabled && (config.NotificationMode == NotificationMode.SoundOnly || config.NotificationMode == NotificationMode.SpeechAndSound))
            {
                if (playMessageSound) messageSounds.Play(data.Direction);
                else SystemSounds.Asterisk.Play();
            }
            if (config.NotificationMode == NotificationMode.SpeechOnly || config.NotificationMode == NotificationMode.SpeechAndSound)
                speechQueue.Enqueue(data.Text);
        }

        private void ReadLatest()
        {
            suppressUntil = DateTime.Now.AddSeconds(2);
            if (!listener.IsBound) BeginBindWechat();
            MessageData data;
            if (!listener.ReadLatest(out data) || data == null)
            {
                ShowWarning("未找到当前聊天的最新消息。请先打开一个具体聊天并开启读屏优化模式。");
                return;
            }
            speechQueue.Enqueue(data.Text);
        }

        private void TestSpeech()
        {
            speechQueue.Enqueue("微信消息助手语音通道测试");
            tray.ShowMessage("语音通道", "已提交测试文本，请留意是否听到朗读。", ToolTipIcon.Info);
        }

        private void FocusOfficialAccounts()
        {
            SpeakText(navigator.FocusOfficialAccountList() ? "公众号列表" : "未找到公众号列表");
        }

        private void FocusContacts()
        {
            SpeakText(navigator.FocusContactList() ? "通讯录列表" : "未找到通讯录列表");
        }

        private void FocusVoip()
        {
            SpeakText(navigator.FocusVoipTrayWindow() ? "音视频接听窗口" : "未找到音视频接听窗口");
        }

        private void CycleNvdaMode()
        {
            NotificationMode next;
            if (config.NotificationMode == NotificationMode.Off) next = NotificationMode.SoundOnly;
            else if (config.NotificationMode == NotificationMode.SoundOnly) next = NotificationMode.SpeechAndSound;
            else next = NotificationMode.Off;
            config.NotificationMode = next;
            SaveConfig();
            UpdateTray();
            SpeakText(ModeText(next));
        }

        private void SpeakText(string text)
        {
            if (speechQueue != null) speechQueue.Enqueue(text);
        }

        private void SuppressAutomaticNotifications()
        {
            suppressUntil = DateTime.Now.AddSeconds(2);
        }

        private void SafeUpdateContextHotkeys()
        {
            if (settingsOpen || exiting) return;
            try { hotkeys.UpdateWeChatContext(navigator.IsWechatForeground(), navigator.IsMessageInputFocused(), ReportHotkeyFailure); }
            catch (Exception ex) { AppLog.Write("微信快捷键上下文探测失败：" + ex.Message); }
        }

        private void ToggleListening()
        {
            config.ListeningEnabled = !config.ListeningEnabled;
            SaveConfig();
            UpdateTray();
            tray.ShowMessage("微信消息助手", config.ListeningEnabled ? "已开始监听新消息。" : "已停止监听新消息。", ToolTipIcon.Info);
        }

        private void CycleMode()
        {
            int next = ((int)config.NotificationMode + 1) % 4;
            SetMode((NotificationMode)next);
        }

        private void SetMode(NotificationMode mode)
        {
            config.NotificationMode = mode;
            SaveConfig();
            UpdateTray();
            tray.ShowMessage("通知方式", ModeText(mode), ToolTipIcon.Info);
        }

        private void SetSpeechChannel(string channel)
        {
            config.SpeechChannel = SpeechChannelNames.Normalize(channel);
            SaveConfig();
            RebuildSpeech(true);
            UpdateTray();
        }

        private void OpenSettings()
        {
            if (settingsOpen) return;
            settingsOpen = true;
            discoveryTimer.Stop();
            contextTimer.Stop();
            if (listener != null) listener.Suspend();
            try
            {
                using (SettingsForm form = new SettingsForm(config))
                {
                    if (form.ShowDialog() != DialogResult.OK) return;
                    form.ApplyTo(config);
                    string startupError;
                    if (!StartupManager.SetEnabled(config.AutoStart, out startupError))
                    {
                        config.AutoStart = StartupManager.IsEnabled;
                        ShowWarning("开机启动设置失败：" + startupError);
                    }
                    SaveConfig();
                    hotkeys.RegisterAll(config, ReportHotkeyFailure);
                    CreateListener();
                    RebuildSpeech(true);
                    BeginBindWechat();
                    UpdateTray();
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("设置窗口异常已隔离：" + ex.GetType().FullName + "，HRESULT=0x" + ex.HResult.ToString("X8") + "，" + ex.Message);
            }
            finally
            {
                settingsOpen = false;
                if (listener != null) listener.Resume();
                if (!exiting)
                {
                    discoveryTimer.Start();
                    contextTimer.Start();
                    BeginBindWechat();
                }
            }
        }

        private void RebuildSpeech(bool notify)
        {
            if (speechQueue != null) speechQueue.Dispose();
            ISpeechChannel channel = CreateSpeechChannel();
            speechQueue = new SpeechQueue(channel, delegate(bool available, string error)
            {
                Dispatch(delegate
                {
                    UpdateTray();
                    if (notify)
                    {
                        if (available) tray.ShowMessage("语音通道", "已启用：" + channel.Name, ToolTipIcon.Info);
                        else ShowWarning(channel.Name + "暂不可用：" + error);
                    }
                });
            });
        }

        private ISpeechChannel CreateSpeechChannel()
        {
            return SpeechChannelFactory.Create(config);
        }

        private bool ShouldIgnoreSystemMessage(MessageData data)
        {
            if (!config.IgnoreSystemMessages || data == null || string.IsNullOrEmpty(data.Text)) return false;
            if (!MessageFilter.IsSystemMessage(data.Text)) return false;
            AppLog.Write("已过滤系统通知：" + data.Text);
            return true;
        }

        private void ApplySavedStartup()
        {
            if (!config.AutoStart) return;
            string error;
            if (!StartupManager.SetEnabled(true, out error)) AppLog.Write("恢复开机启动失败：" + error);
        }

        private void MigrateLegacyHotkeys()
        {
            bool changed = false;
            if (config.ReadLatestHotkey.Equals("Ctrl+Alt+M", StringComparison.OrdinalIgnoreCase) ||
                config.ReadLatestHotkey.Equals("Ctrl+Shift+Alt+M", StringComparison.OrdinalIgnoreCase) ||
                config.ReadLatestHotkey.Equals("Ctrl+F11", StringComparison.OrdinalIgnoreCase))
            {
                config.ReadLatestHotkey = "Ctrl+Alt+F11";
                changed = true;
            }
            if (config.ToggleNotifyHotkey.Equals("Ctrl+Alt+N", StringComparison.OrdinalIgnoreCase) ||
                config.ToggleNotifyHotkey.Equals("Ctrl+Shift+Alt+N", StringComparison.OrdinalIgnoreCase) ||
                config.ToggleNotifyHotkey.Equals("Ctrl+Shift+F11", StringComparison.OrdinalIgnoreCase) ||
                config.ToggleNotifyHotkey.Equals("Alt+Shift+F8", StringComparison.OrdinalIgnoreCase))
            {
                config.ToggleNotifyHotkey = "Ctrl+Alt+Shift+F11";
                changed = true;
            }
            if (changed)
            {
                try { config.Save(); }
                catch (Exception ex) { AppLog.Write("迁移冲突热键失败：" + ex.Message); }
            }
        }

        private void SaveConfig()
        {
            try { config.Save(); }
            catch (Exception ex) { ShowWarning("保存设置失败：" + ex.Message); }
        }

        private void Dispatch(Action action)
        {
            if (exiting || IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(action);
                else action();
            }
            catch { }
        }

        private void UpdateTray()
        {
            if (tray == null) return;
            string channel = speechQueue == null ? "未初始化" : speechQueue.ChannelName;
            tray.Update(config.ListeningEnabled, config.NotificationMode, statusText, channel, config.SpeechChannel);
        }

        private void ShowWarning(string text)
        {
            AppLog.Write(text);
            if (tray != null) tray.ShowMessage("微信消息助手", text, ToolTipIcon.Warning);
        }

        private async void CheckForUpdates(bool manual)
        {
            if (updateCheckPending || exiting) return;
            updateCheckPending = true;
            if (manual) tray.ShowMessage("检查更新", "正在检查新版本...", ToolTipIcon.Info);
            UpdateCheckResult result = await updateService.CheckAsync();
            updateCheckPending = false;
            if (exiting) return;
            if (!string.IsNullOrEmpty(result.Error))
            {
                AppLog.Write("检查更新失败：" + result.Error);
                if (manual) ShowWarning("检查更新失败：" + result.Error);
                return;
            }
            if (!result.HasUpdate)
            {
                if (manual) tray.ShowMessage("检查更新", "当前已是最新版本。", ToolTipIcon.Info);
                return;
            }
            if (!manual && string.Equals(config.IgnoredUpdateVersion, result.Manifest.VersionText, StringComparison.OrdinalIgnoreCase)) return;

            using (UpdateDialog dialog = new UpdateDialog(result.Manifest))
            {
                dialog.ShowDialog();
                if (dialog.SelectedAction == UpdateDialogAction.Ignore)
                {
                    config.IgnoredUpdateVersion = result.Manifest.VersionText;
                    SaveConfig();
                    return;
                }
                if (dialog.SelectedAction == UpdateDialogAction.Install) DownloadAndInstallUpdate(result.Manifest);
            }
        }

        private async void DownloadAndInstallUpdate(UpdateManifest manifest)
        {
            try
            {
                tray.ShowMessage("软件更新", "正在下载版本 " + manifest.VersionText + "...", ToolTipIcon.Info);
                string package = await updateService.DownloadPackageAsync(manifest);
                updateService.BeginInstall(manifest, package);
                exiting = true;
                Application.Exit();
            }
            catch (Exception ex)
            {
                AppLog.Write("下载或安装更新失败：" + ex.Message);
                ShowWarning("更新失败：" + ex.Message);
            }
        }

        private void ShowCurrentReleaseNotes()
        {
            using (ReleaseNotesForm form = new ReleaseNotesForm("微信消息助手 1.8.0 更新日志", UpdateReleaseNotes.Current, false))
                form.ShowDialog();
        }

        private void ShowInstalledReleaseNotes()
        {
            InstalledRelease release = UpdateInstaller.ReadInstalledRelease();
            if (release == null || string.IsNullOrWhiteSpace(release.Version)) return;
            if (string.Equals(config.LastShownReleaseNotesVersion, release.Version, StringComparison.OrdinalIgnoreCase))
            {
                UpdateInstaller.DeleteInstalledRelease();
                return;
            }
            using (ReleaseNotesForm form = new ReleaseNotesForm("已更新到 " + release.Version, release.ReleaseNotes, true))
            {
                if (form.ShowDialog() != DialogResult.OK) return;
            }
            config.LastShownReleaseNotesVersion = release.Version;
            config.IgnoredUpdateVersion = string.Empty;
            SaveConfig();
            UpdateInstaller.DeleteInstalledRelease();
        }

        private void ReportHotkeyFailure(string text)
        {
            AppLog.Write(text);
        }

        private void ExitApplication()
        {
            exiting = true;
            // The tray Exit command is a hard exit. UIA unsubscription and reader
            // disposal can block inside external components; Windows releases those
            // process resources, so do not keep the hidden process alive for cleanup.
            if (speechQueue != null) speechQueue.ShutdownWithAnnouncement();
            Environment.Exit(0);
        }

        private void WarnAboutNvda()
        {
            if (HasProcess("nvda")) ShowWarning("检测到 NVDA 正在运行。为避免重复朗读，建议与微信插件二选一使用。");
        }

        private static bool HasProcess(string prefix)
        {
            foreach (Process process in Process.GetProcesses())
            {
                try { if (process.ProcessName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { }
                finally { process.Dispose(); }
            }
            return false;
        }

        private static string ModeText(NotificationMode mode)
        {
            if (mode == NotificationMode.Off) return "关闭";
            if (mode == NotificationMode.SoundOnly) return "仅提示音";
            if (mode == NotificationMode.SpeechOnly) return "仅朗读";
            return "提示音 + 朗读";
        }
    }
}
