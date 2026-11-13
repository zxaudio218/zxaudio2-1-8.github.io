using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using WeChatMsgHelper.Channels;

namespace WeChatMsgHelper
{
    internal sealed class MainForm : Form
    {
        private const int WmHotkey = 0x0312;
        private const int DiscoveryFastIntervalMs = 500;
        private const int DiscoveryStableIntervalMs = 2000;
        private const int DiscoveryIdleIntervalMs = 2000;
        private const int ContextProbeIntervalMs = 500;
        private const int VoipProbeIntervalMs = 1000;
        private const int SwShow = 5;
        private const int SwRestore = 9;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        private readonly Discovery discovery = new Discovery();
        private readonly MessageSoundPlayer messageSounds = new MessageSoundPlayer();
        private readonly WeChatNavigator navigator = new WeChatNavigator();
        private readonly System.Windows.Forms.Timer discoveryTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer contextTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer voipTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer hotkeyFallbackTimer = new System.Windows.Forms.Timer();
        private AppConfig config;
        private MessageExtractor extractor;
        private MessageListener listener;
        private SpeechQueue speechQueue;
        private HotkeyManager hotkeys;
        private TrayIcon tray;
        private SettingsForm activeSettingsForm;
        private MessageReviewManager reviewManager;
        private SearchResultAnnouncer searchAnnouncer;
        private readonly UpdateService updateService = new UpdateService();
        private DateTime suppressUntil;
        private string statusText = "正在启动...";
        private bool exiting;
        private bool settingsOpen;
        private bool wechatWasForeground;
        private int discoveryPending;
        private int voipProbePending;
        private int exitStarted;
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
            tray = new TrayIcon(ToggleListening, SetMode, SetSpeechChannel, ReadLatest, TestSpeech, RequestOpenSettings,
                delegate { CheckForUpdates(true); }, ShowCurrentReleaseNotes, ExportLog, ExitApplication);
            hotkeys = new HotkeyManager(Handle);
            IList<string> startupHotkeyFailures = hotkeys.RegisterAll(config, ReportHotkeyFailure);
            CreateListener();
            RebuildSpeech(false);
            searchAnnouncer = new SearchResultAnnouncer(Dispatch, SpeakText,
                delegate(string name) { if (listener != null) listener.NoteEmojiSent(name); });
            ApplySavedStartup();

            discoveryTimer.Interval = DiscoveryFastIntervalMs;
            discoveryTimer.Tick += delegate { BeginBindWechat(); };
            discoveryTimer.Start();
            contextTimer.Interval = ContextProbeIntervalMs;
            contextTimer.Tick += delegate { SafeUpdateContextHotkeys(); };
            contextTimer.Start();
            voipTimer.Interval = VoipProbeIntervalMs;
            voipTimer.Tick += delegate { BeginUpdateVoipContext(); };
            voipTimer.Start();
            hotkeyFallbackTimer.Interval = 40;
            hotkeyFallbackTimer.Tick += delegate { PollHotkeyFallback(); };
            BeginBindWechat();
            BeginUpdateVoipContext();
            UpdateTray();
            WarnAboutNvda();
            BeginInvoke(new Action(delegate
            {
                PromptToModifyConflictingHotkeys(startupHotkeyFailures);
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
            exiting = true;
            if (Interlocked.Exchange(ref exitStarted, 1) == 0) StartExitWatchdog();
            StopRuntimeTimers();
            if (speechQueue != null) speechQueue.PrepareForProcessExit();
            if (searchAnnouncer != null) searchAnnouncer.StopInputHooks();
            try { if (hotkeys != null) hotkeys.Dispose(); }
            catch { }
            try { if (tray != null) tray.Dispose(); }
            catch { }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            // A hidden tray host can leave the WinForms message loop alive on some
            // Win10/Win11 builds. Force the process to terminate after cleanup.
            AppLog.DeleteCurrent();
            Environment.Exit(0);
        }

        private void HandleHotkey(int id)
        {
            switch (id)
            {
                case HotkeyManager.ReadLatestId: ReadLatest(); break;
                case HotkeyManager.ToggleNotifyId: ToggleListening(); break;
                case HotkeyManager.ToggleModeId: CycleMode(); break;
                case HotkeyManager.OpenSettingsId: RequestOpenSettings(); break;
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
            listener = new MessageListener(extractor, config, ProcessIncomingMessage, ProcessConversationOpened, Dispatch, BeginBindWechat);
            reviewManager = new MessageReviewManager(navigator, extractor, SpeakText, SuppressAutomaticNotifications);
        }

        private void BeginBindWechat()
        {
            if (exiting || settingsOpen || Interlocked.Exchange(ref discoveryPending, 1) != 0) return;
            if (!ThreadPool.QueueUserWorkItem(delegate
            {
                IList<DiscoveredWechatRoot> roots = null;
                Exception error = null;
                Stopwatch stopwatch = Stopwatch.StartNew();
                try { roots = discovery.FindWechatRoots(); }
                catch (Exception ex) { error = ex; }
                stopwatch.Stop();
                Dispatch(delegate
                {
                    Interlocked.Exchange(ref discoveryPending, 0);
                    if (exiting || settingsOpen) return;
                    if (stopwatch.ElapsedMilliseconds >= 1000)
                        AppLog.Write("微信 UIA 发现耗时=" + stopwatch.ElapsedMilliseconds + "ms，窗口=" + (roots == null ? 0 : roots.Count));
                    if (error != null)
                    {
                        statusText = "微信 UIA 暂时无响应";
                        AppLog.Write("微信 UIA 发现失败：" + error.GetType().Name + "，" + error.Message);
                        UpdateTray();
                        return;
                    }
                    ApplyWechatRoots(roots ?? new List<DiscoveredWechatRoot>());
                });
            })) Interlocked.Exchange(ref discoveryPending, 0);
        }

        private void ApplyWechatRoots(IList<DiscoveredWechatRoot> roots)
        {
            if (roots.Count == 0)
            {
                discoveryTimer.Interval = navigator.IsWechatForeground()
                    ? DiscoveryFastIntervalMs
                    : DiscoveryIdleIntervalMs;
                try { listener.Synchronize(roots, true); }
                catch (Exception ex) { AppLog.Write("清理微信 UIA 绑定失败：" + ex.Message); }
                statusText = "等待打开具体微信聊天";
                UpdateTray();
                return;
            }
            try
            {
                listener.Synchronize(roots, true);
                discoveryTimer.Interval = DiscoveryStableIntervalMs;
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
            try
            {
                bool foreground = navigator.IsWechatForeground();
                if (foreground && !wechatWasForeground) BeginBindWechat();
                wechatWasForeground = foreground;
                hotkeys.UpdateWeChatContext(foreground, navigator.IsMessageInputFocused(foreground), ReportHotkeyFailure);
                UpdateHotkeyFallbackTimer();
            }
            catch (Exception ex) { AppLog.Write("微信快捷键上下文探测失败：" + ex.Message); }
        }

        private void BeginUpdateVoipContext()
        {
            if (settingsOpen || exiting || Interlocked.Exchange(ref voipProbePending, 1) != 0) return;
            if (!ThreadPool.QueueUserWorkItem(delegate
            {
                bool available = false;
                Exception error = null;
                try { available = navigator.HasVoipTrayWindow(); }
                catch (Exception ex) { error = ex; }
                Dispatch(delegate
                {
                    Interlocked.Exchange(ref voipProbePending, 0);
                    if (settingsOpen || exiting) return;
                    if (error != null)
                    {
                        AppLog.Write("VOIP window detection failed: " + error.Message);
                        return;
                    }
                    hotkeys.UpdateVoipContext(available, ReportHotkeyFailure);
                    UpdateHotkeyFallbackTimer();
                });
            })) Interlocked.Exchange(ref voipProbePending, 0);
        }

        private void UpdateHotkeyFallbackTimer()
        {
            if (hotkeys.HasFallbackHotkeys)
            {
                if (!hotkeyFallbackTimer.Enabled) hotkeyFallbackTimer.Start();
            }
            else if (hotkeyFallbackTimer.Enabled) hotkeyFallbackTimer.Stop();
        }

        private void ToggleListening()
        {
            config.ListeningEnabled = !config.ListeningEnabled;
            if (!config.ListeningEnabled && speechQueue != null) speechQueue.StopAndClear();
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

        private void RequestOpenSettings()
        {
            if (settingsOpen)
            {
                ActivateSettingsWindow();
                return;
            }
            try { BeginInvoke(new Action(OpenSettings)); }
            catch (Exception ex) { AppLog.Write("请求打开设置失败：" + ex.Message); }
        }

        private void OpenSettings()
        {
            if (settingsOpen)
            {
                ActivateSettingsWindow();
                return;
            }
            settingsOpen = true;
            IList<string> hotkeyFailures = null;
            bool settingsSaved = false;
            discoveryTimer.Stop();
            contextTimer.Stop();
            voipTimer.Stop();
            hotkeyFallbackTimer.Stop();
            if (listener != null) listener.Suspend();
            try
            {
                using (SettingsForm form = new SettingsForm(config))
                {
                    activeSettingsForm = form;
                    form.HotkeyCaptureStarted += SuspendHotkeysForCapture;
                    form.HotkeyCaptureEnded += RestoreHotkeysAfterCapture;
                    form.Shown += delegate { ActivateSettingsWindow(); };
                    if (form.ShowDialog() != DialogResult.OK) return;
                    form.ApplyTo(config);
                    string startupError;
                    if (!StartupManager.SetEnabled(config.AutoStart, out startupError))
                    {
                        config.AutoStart = StartupManager.IsEnabled;
                        ShowWarning("开机启动设置失败：" + startupError);
                    }
                    SaveConfig();
                    settingsSaved = true;
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
                hotkeyFailures = hotkeys.RegisterAll(config, ReportHotkeyFailure);
                activeSettingsForm = null;
                settingsOpen = false;
                if (listener != null) listener.Resume();
                if (!exiting)
                {
                    discoveryTimer.Start();
                    contextTimer.Start();
                    voipTimer.Start();
                    BeginBindWechat();
                    BeginUpdateVoipContext();
                    UpdateHotkeyFallbackTimer();
                }
            }
            if (settingsSaved && hotkeyFailures != null && hotkeyFailures.Count > 0 && !exiting)
            {
                try { BeginInvoke(new Action(delegate { PromptToModifyConflictingHotkeys(hotkeyFailures); })); }
                catch { }
            }
        }

        private void ActivateSettingsWindow()
        {
            SettingsForm form = activeSettingsForm;
            if (form == null || form.IsDisposed) return;
            IntPtr handle = form.Handle;
            ShowWindow(handle, form.WindowState == FormWindowState.Minimized ? SwRestore : SwShow);
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.BringToFront();
            form.Activate();
            SetForegroundWindow(handle);
        }

        private void SuspendHotkeysForCapture(object sender, EventArgs args)
        {
            hotkeyFallbackTimer.Stop();
            hotkeys.UnregisterAll();
        }

        private void RestoreHotkeysAfterCapture(object sender, EventArgs args)
        {
            if (exiting || !settingsOpen) return;
            hotkeys.RegisterAll(config, ReportHotkeyFailure);
            UpdateHotkeyFallbackTimer();
        }

        private void PromptToModifyConflictingHotkeys(IList<string> failures)
        {
            if (failures == null || failures.Count == 0 || exiting) return;
            string signature = string.Join("|", new List<string>(failures).ToArray());
            if (string.Equals(config.SuppressedHotkeyConflictSignature, signature, StringComparison.Ordinal))
            {
                AppLog.Write("已按用户设置跳过这组快捷键冲突提示：" + string.Join("；", new List<string>(failures).ToArray()));
                return;
            }
            AppLog.Write("检测到快捷键冲突：" + string.Join("；", new List<string>(failures).ToArray()));
            using (HotkeyConflictDialog dialog = new HotkeyConflictDialog(failures))
            {
                DialogResult result = dialog.ShowDialog();
                if (dialog.SuppressThisGroup)
                {
                    config.SuppressedHotkeyConflictSignature = signature;
                    SaveConfig();
                }
                if (result == DialogResult.Yes) OpenSettings();
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
                config.ReadLatestHotkey = AppConfig.DefaultReadLatestHotkey;
                changed = true;
            }
            if (config.ToggleNotifyHotkey.Equals("Ctrl+Alt+N", StringComparison.OrdinalIgnoreCase) ||
                config.ToggleNotifyHotkey.Equals("Ctrl+Shift+Alt+N", StringComparison.OrdinalIgnoreCase) ||
                config.ToggleNotifyHotkey.Equals("Ctrl+Shift+F11", StringComparison.OrdinalIgnoreCase) ||
                config.ToggleNotifyHotkey.Equals("Alt+Shift+F8", StringComparison.OrdinalIgnoreCase))
            {
                config.ToggleNotifyHotkey = AppConfig.DefaultToggleNotifyHotkey;
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

        private void ExportLog()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "导出微信消息助手日志";
                dialog.Filter = "日志文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                dialog.DefaultExt = "txt";
                dialog.AddExtension = true;
                dialog.FileName = "WeChatMsgHelper-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                if (Directory.Exists(desktop)) dialog.InitialDirectory = desktop;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                AppLog.Write("用户请求导出日志");
                string error;
                if (AppLog.TryExport(dialog.FileName, out error))
                {
                    tray.ShowMessage("日志导出成功", "请将此日志文件发送给开发者：" + dialog.FileName, ToolTipIcon.Info);
                    return;
                }
                ShowWarning("导出日志失败：" + error);
            }
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
            using (ReleaseNotesForm form = new ReleaseNotesForm("微信消息助手 " + UpdateReleaseNotes.CurrentVersion + " 更新日志", UpdateReleaseNotes.Current, false))
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
            if (Interlocked.Exchange(ref exitStarted, 1) != 0) return;
            exiting = true;
            StartExitWatchdog();
            if (speechQueue != null) speechQueue.AnnounceProcessExit("消息助手退出");
            try { BeginInvoke(new Action(Close)); }
            catch { Environment.Exit(0); }
        }

        private void StopRuntimeTimers()
        {
            discoveryTimer.Stop();
            contextTimer.Stop();
            voipTimer.Stop();
            hotkeyFallbackTimer.Stop();
        }

        private static void StartExitWatchdog()
        {
            Thread watchdog = new Thread(new ThreadStart(delegate
            {
                Thread.Sleep(1500);
                try
                {
                    using (Process current = Process.GetCurrentProcess()) current.Kill();
                }
                catch { }
            }));
            watchdog.IsBackground = true;
            watchdog.Name = "WeChatMsgHelper Exit Watchdog";
            watchdog.Start();
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
