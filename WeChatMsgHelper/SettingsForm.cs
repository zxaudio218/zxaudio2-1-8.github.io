using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using WeChatMsgHelper.Channels;

namespace WeChatMsgHelper
{
    internal sealed class SettingsForm : Form
    {
        private readonly ComboBox modeBox = new ComboBox();
        private readonly ComboBox speechChannelBox = new ComboBox();
        private readonly TextBox baoyiChannelBox = new TextBox();
        private readonly ComboBox sapiVoiceBox = new ComboBox();
        private readonly CheckBox listeningBox = new CheckBox();
        private readonly CheckBox pollBox = new CheckBox();
        private readonly CheckBox nonTextBox = new CheckBox();
        private readonly CheckBox systemMessageBox = new CheckBox();
        private readonly CheckBox autoStartBox = new CheckBox();
        private readonly CheckBox keyInterruptBox = new CheckBox();
        private readonly NumericUpDown pollInterval = new NumericUpDown();
        private readonly NumericUpDown sapiRate = new NumericUpDown();
        private readonly NumericUpDown sapiVolume = new NumericUpDown();
        private readonly System.Windows.Forms.Timer voiceLoadTimeout = new System.Windows.Forms.Timer();
        private readonly string savedSapiVoice;
        private string readLatestHotkey;
        private string toggleNotifyHotkey;
        private string toggleModeHotkey;
        private string openSettingsHotkey;
        private bool voiceLoadStarted;
        private bool voicesLoaded;

        internal event EventHandler HotkeyCaptureStarted;
        internal event EventHandler HotkeyCaptureEnded;

        internal SettingsForm(AppConfig config)
        {
            savedSapiVoice = config.Sapi5Voice ?? string.Empty;
            readLatestHotkey = config.ReadLatestHotkey;
            toggleNotifyHotkey = config.ToggleNotifyHotkey;
            toggleModeHotkey = config.ToggleModeHotkey;
            openSettingsHotkey = config.OpenSettingsHotkey;
            Text = "微信消息助手设置";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(570, 556);
            AutoScaleMode = AutoScaleMode.Dpi;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(12);
            layout.ColumnCount = 2;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowCount = 16;
            layout.TabStop = false;
            for (int index = 0; index < 13; index++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            listeningBox.Text = "启动后自动监听微信";
            listeningBox.Checked = config.ListeningEnabled;
            modeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            modeBox.Items.AddRange(new object[] { "关闭", "仅提示音", "仅朗读", "提示音 + 朗读" });
            modeBox.SelectedIndex = (int)config.NotificationMode;

            speechChannelBox.DropDownStyle = ComboBoxStyle.DropDownList;
            speechChannelBox.Items.AddRange(new object[] { "自动选择", "争渡独立通道 WeChatMsg", "争渡读屏通道", "保益第三方独立通道", "保益读屏通道", "SAPI5" });
            speechChannelBox.SelectedIndex = ChannelIndex(config.SpeechChannel);
            speechChannelBox.SelectedIndexChanged += delegate
            {
                UpdateChannelControls();
                if (ChannelName(speechChannelBox.SelectedIndex) == SpeechChannelNames.Sapi5) BeginLoadSapiVoices();
            };

            baoyiChannelBox.Text = string.IsNullOrWhiteSpace(config.BaoyiChannelName) ? "WeChatMsg" : config.BaoyiChannelName.Trim();

            sapiVoiceBox.DropDownStyle = ComboBoxStyle.DropDownList;
            sapiVoiceBox.Items.Add("（系统默认）");
            if (!string.IsNullOrWhiteSpace(savedSapiVoice)) sapiVoiceBox.Items.Add(savedSapiVoice);
            sapiVoiceBox.SelectedIndex = string.IsNullOrWhiteSpace(savedSapiVoice) ? 0 : 1;
            sapiVoiceBox.DropDown += delegate { BeginLoadSapiVoices(); };
            voiceLoadTimeout.Interval = 3000;
            voiceLoadTimeout.Tick += delegate
            {
                voiceLoadTimeout.Stop();
                if (!voicesLoaded) AppLog.Write("Win10 兼容：SAPI5 语音列表加载超过 3 秒，设置窗口继续使用当前选择。");
            };
            sapiRate.Minimum = -10;
            sapiRate.Maximum = 10;
            sapiRate.Value = Math.Max(-10, Math.Min(10, config.Sapi5Rate));
            sapiVolume.Minimum = 0;
            sapiVolume.Maximum = 100;
            sapiVolume.Value = Math.Max(0, Math.Min(100, config.Sapi5Volume));

            pollBox.Text = "启用轮询兜底";
            pollBox.Checked = config.PollFallback;
            nonTextBox.Text = "朗读非文本消息占位词";
            nonTextBox.Checked = config.ReadNonTextMessages;
            systemMessageBox.Text = "忽略系统通知类消息";
            systemMessageBox.Checked = config.IgnoreSystemMessages;
            autoStartBox.Text = "登录 Windows 后自动启动";
            autoStartBox.Checked = config.AutoStart || StartupManager.IsEnabled;
            keyInterruptBox.Text = "按键可打断争渡朗读";
            keyInterruptBox.Checked = config.ZhengduKeyDownInterrupt;
            pollInterval.Minimum = 200;
            pollInterval.Maximum = 5000;
            pollInterval.Increment = 100;
            pollInterval.Value = config.PollIntervalMs;
            AddRow(layout, 0, "监听", listeningBox);
            AddRow(layout, 1, "通知方式", modeBox);
            AddRow(layout, 2, "语音通道", speechChannelBox);
            AddRow(layout, 3, "保益独立通道名称", baoyiChannelBox);
            AddRow(layout, 4, "SAPI5 语音", sapiVoiceBox);
            AddRow(layout, 5, "SAPI5 语速（-10 到 10）", sapiRate);
            AddRow(layout, 6, "SAPI5 音量（0 到 100）", sapiVolume);
            AddRow(layout, 7, "轮询兜底", pollBox);
            AddRow(layout, 8, "轮询间隔（毫秒）", pollInterval);
            AddRow(layout, 9, "非文本消息", nonTextBox);
            AddRow(layout, 10, "系统通知", systemMessageBox);
            AddRow(layout, 11, "开机启动", autoStartBox);
            AddRow(layout, 12, "按键打断", keyInterruptBox);

            Button hotkeySettingsButton = new Button();
            hotkeySettingsButton.Text = "快捷键设置...";
            hotkeySettingsButton.AccessibleName = "快捷键设置";
            hotkeySettingsButton.AutoSize = true;
            hotkeySettingsButton.TabIndex = 13;
            hotkeySettingsButton.Click += OpenHotkeySettings;
            layout.Controls.Add(FixedLabel("快捷键"), 0, 13);
            layout.Controls.Add(hotkeySettingsButton, 1, 13);

            Label note = FixedLabel("微信前台支持 F3、Alt+P/T/V；输入框聚焦时支持 Ctrl+Alt+上/下/Home/End。打开新会话后会自动朗读最后一条消息。配置和日志保存在当前用户的 LocalAppData，不写到桌面。") as Label;
            note.AutoSize = false;
            note.Dock = DockStyle.Fill;
            layout.Controls.Add(note, 0, 14);
            layout.SetColumnSpan(note, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Fill;
            buttons.TabStop = false;
            Button okButton = new Button();
            okButton.Text = "确定";
            okButton.AccessibleName = "确定";
            okButton.DialogResult = DialogResult.OK;
            okButton.TabIndex = 0;
            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.AccessibleName = "取消";
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.TabIndex = 1;
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);
            layout.Controls.Add(buttons, 0, 15);
            layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout);
            AcceptButton = okButton;
            CancelButton = cancelButton;
            UpdateChannelControls();
            Shown += delegate { if (ChannelName(speechChannelBox.SelectedIndex) == SpeechChannelNames.Sapi5) BeginLoadSapiVoices(); };
        }

        internal void ApplyTo(AppConfig config)
        {
            config.ListeningEnabled = listeningBox.Checked;
            config.NotificationMode = (NotificationMode)Math.Max(0, modeBox.SelectedIndex);
            config.SpeechChannel = ChannelName(speechChannelBox.SelectedIndex);
            config.BaoyiChannelName = string.IsNullOrWhiteSpace(baoyiChannelBox.Text) ? "WeChatMsg" : baoyiChannelBox.Text.Trim();
            config.Sapi5Voice = sapiVoiceBox.SelectedIndex <= 0 ? string.Empty : sapiVoiceBox.SelectedItem.ToString();
            config.Sapi5Rate = (int)sapiRate.Value;
            config.Sapi5Volume = (int)sapiVolume.Value;
            config.PollFallback = pollBox.Checked;
            config.PollIntervalMs = (int)pollInterval.Value;
            config.ReadNonTextMessages = nonTextBox.Checked;
            config.IgnoreSystemMessages = systemMessageBox.Checked;
            config.AutoStart = autoStartBox.Checked;
            config.ZhengduKeyDownInterrupt = keyInterruptBox.Checked;
            config.ReadLatestHotkey = readLatestHotkey;
            config.ToggleNotifyHotkey = toggleNotifyHotkey;
            config.ToggleModeHotkey = toggleModeHotkey;
            config.OpenSettingsHotkey = openSettingsHotkey;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) voiceLoadTimeout.Dispose();
            base.Dispose(disposing);
        }

        private void UpdateChannelControls()
        {
            string channel = ChannelName(speechChannelBox.SelectedIndex);
            bool sapi = channel == SpeechChannelNames.Sapi5;
            baoyiChannelBox.Enabled = channel == SpeechChannelNames.Auto || channel == SpeechChannelNames.BaoyiIndependent;
            sapiVoiceBox.Enabled = sapi;
            sapiRate.Enabled = sapi;
            sapiVolume.Enabled = sapi;
            keyInterruptBox.Enabled = !sapi;
        }

        private void BeginLoadSapiVoices()
        {
            if (voiceLoadStarted || IsDisposed) return;
            voiceLoadStarted = true;
            voiceLoadTimeout.Start();
            Thread loader = new Thread(new ThreadStart(delegate
            {
                IList<string> voices;
                string error = string.Empty;
                try { voices = Sapi5Channel.GetInstalledVoiceNames(); }
                catch (Exception ex) { voices = new List<string>(); error = ex.Message; }
                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke((MethodInvoker)delegate { CompleteVoiceLoad(voices, error); });
                    }
                }
                catch { }
            }));
            loader.IsBackground = true;
            loader.Name = "WeChatMsgHelper SAPI5 Probe";
            loader.SetApartmentState(ApartmentState.STA);
            loader.Start();
        }

        private void CompleteVoiceLoad(IList<string> voices, string error)
        {
            if (IsDisposed) return;
            voiceLoadTimeout.Stop();
            voicesLoaded = true;
            string selected = sapiVoiceBox.SelectedIndex > 0 && sapiVoiceBox.SelectedItem != null
                ? sapiVoiceBox.SelectedItem.ToString() : savedSapiVoice;
            sapiVoiceBox.BeginUpdate();
            try
            {
                sapiVoiceBox.Items.Clear();
                sapiVoiceBox.Items.Add("（系统默认）");
                foreach (string voice in voices)
                    if (sapiVoiceBox.FindStringExact(voice) < 0) sapiVoiceBox.Items.Add(voice);
                int index = string.IsNullOrWhiteSpace(selected) ? 0 : sapiVoiceBox.FindStringExact(selected);
                if (index < 0 && !string.IsNullOrWhiteSpace(selected))
                {
                    sapiVoiceBox.Items.Add(selected);
                    index = sapiVoiceBox.Items.Count - 1;
                }
                sapiVoiceBox.SelectedIndex = Math.Max(0, index);
            }
            finally { sapiVoiceBox.EndUpdate(); }
            if (!string.IsNullOrEmpty(error)) AppLog.Write("SAPI5 语音列表加载失败：" + error);
        }

        private static int ChannelIndex(string channel)
        {
            channel = SpeechChannelNames.Normalize(channel);
            if (channel == SpeechChannelNames.Auto) return 0;
            if (channel == SpeechChannelNames.ZhengduIndependent) return 1;
            if (channel == SpeechChannelNames.ZhengduScreenReader) return 2;
            if (channel == SpeechChannelNames.BaoyiIndependent) return 3;
            if (channel == SpeechChannelNames.BaoyiScreenReader) return 4;
            return 5;
        }

        private static string ChannelName(int index)
        {
            if (index == 0) return SpeechChannelNames.Auto;
            if (index == 1) return SpeechChannelNames.ZhengduIndependent;
            if (index == 2) return SpeechChannelNames.ZhengduScreenReader;
            if (index == 3) return SpeechChannelNames.BaoyiIndependent;
            if (index == 4) return SpeechChannelNames.BaoyiScreenReader;
            return SpeechChannelNames.Sapi5;
        }

        private static void AddRow(TableLayoutPanel layout, int row, string labelText, Control control)
        {
            Label label = FixedLabel(labelText) as Label;
            control.Dock = DockStyle.Fill;
            control.TabIndex = row;
            if (string.IsNullOrEmpty(control.AccessibleName)) control.AccessibleName = labelText;
            layout.Controls.Add(label, 0, row);
            layout.Controls.Add(control, 1, row);
        }

        private void OpenHotkeySettings(object sender, EventArgs args)
        {
            using (HotkeySettingsForm form = new HotkeySettingsForm(
                readLatestHotkey, toggleNotifyHotkey, toggleModeHotkey, openSettingsHotkey))
            {
                form.HotkeyCaptureStarted += ForwardHotkeyCaptureStarted;
                form.HotkeyCaptureEnded += ForwardHotkeyCaptureEnded;
                if (form.ShowDialog(this) != DialogResult.OK) return;
                readLatestHotkey = form.ReadLatestHotkey;
                toggleNotifyHotkey = form.ToggleNotifyHotkey;
                toggleModeHotkey = form.ToggleModeHotkey;
                openSettingsHotkey = form.OpenSettingsHotkey;
            }
        }

        private void ForwardHotkeyCaptureStarted(object sender, EventArgs args)
        {
            EventHandler handler = HotkeyCaptureStarted;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void ForwardHotkeyCaptureEnded(object sender, EventArgs args)
        {
            EventHandler handler = HotkeyCaptureEnded;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static Control FixedLabel(string text)
        {
            Label label = new Label();
            label.Text = text;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.TabStop = false;
            return label;
        }
    }
}
