using System;
using System.Drawing;
using System.Windows.Forms;

namespace WeChatMsgHelper
{
    internal sealed class HotkeySettingsForm : Form
    {
        private readonly HotkeyTextBox readLatestBox = new HotkeyTextBox("朗读最新消息");
        private readonly HotkeyTextBox toggleNotifyBox = new HotkeyTextBox("启动或停止监听");
        private readonly HotkeyTextBox toggleModeBox = new HotkeyTextBox("切换通知方式");
        private readonly HotkeyTextBox openSettingsBox = new HotkeyTextBox("打开设置");
        private bool captureActive;

        internal event EventHandler HotkeyCaptureStarted;
        internal event EventHandler HotkeyCaptureEnded;

        internal HotkeySettingsForm(string readLatest, string toggleNotify, string toggleMode, string openSettings)
        {
            Text = "快捷键设置";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(500, 250);
            AutoScaleMode = AutoScaleMode.Dpi;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(12);
            layout.ColumnCount = 2;
            layout.RowCount = 6;
            layout.TabStop = false;
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int index = 0; index < 4; index++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            readLatestBox.Text = readLatest ?? string.Empty;
            toggleNotifyBox.Text = toggleNotify ?? string.Empty;
            toggleModeBox.Text = toggleMode ?? string.Empty;
            openSettingsBox.Text = openSettings ?? string.Empty;

            AddRow(layout, 0, "朗读最新消息", readLatestBox);
            AddRow(layout, 1, "启动/停止监听", toggleNotifyBox);
            AddRow(layout, 2, "切换通知方式", toggleModeBox);
            AddRow(layout, 3, "打开设置", openSettingsBox);

            Button restoreButton = new Button();
            restoreButton.Text = "恢复默认快捷键";
            restoreButton.AccessibleName = "恢复默认快捷键";
            restoreButton.AutoSize = true;
            restoreButton.TabIndex = 4;
            restoreButton.Click += RestoreDefaults;
            layout.Controls.Add(restoreButton, 1, 4);

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
            layout.Controls.Add(buttons, 0, 5);
            layout.SetColumnSpan(buttons, 2);

            Controls.Add(layout);
            AcceptButton = okButton;
            CancelButton = cancelButton;

            WireHotkeyCapture(readLatestBox);
            WireHotkeyCapture(toggleNotifyBox);
            WireHotkeyCapture(toggleModeBox);
            WireHotkeyCapture(openSettingsBox);
        }

        internal string ReadLatestHotkey { get { return readLatestBox.Text.Trim(); } }
        internal string ToggleNotifyHotkey { get { return toggleNotifyBox.Text.Trim(); } }
        internal string ToggleModeHotkey { get { return toggleModeBox.Text.Trim(); } }
        internal string OpenSettingsHotkey { get { return openSettingsBox.Text.Trim(); } }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            EndCapture();
            base.OnFormClosed(e);
        }

        private void RestoreDefaults(object sender, EventArgs args)
        {
            DialogResult result = MessageBox.Show(this,
                "是否恢复程序默认快捷键？",
                "恢复默认快捷键",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes) return;

            readLatestBox.Text = AppConfig.DefaultReadLatestHotkey;
            toggleNotifyBox.Text = AppConfig.DefaultToggleNotifyHotkey;
            toggleModeBox.Text = AppConfig.DefaultToggleModeHotkey;
            openSettingsBox.Text = AppConfig.DefaultOpenSettingsHotkey;
            readLatestBox.Focus();
            readLatestBox.SelectAll();
        }

        private void WireHotkeyCapture(Control control)
        {
            control.Enter += delegate { BeginCapture(); };
            control.Leave += delegate { EndCapture(); };
        }

        private void BeginCapture()
        {
            if (captureActive) return;
            captureActive = true;
            EventHandler handler = HotkeyCaptureStarted;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void EndCapture()
        {
            if (!captureActive) return;
            captureActive = false;
            EventHandler handler = HotkeyCaptureEnded;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static void AddRow(TableLayoutPanel layout, int row, string labelText, HotkeyTextBox control)
        {
            Label label = new Label();
            label.Text = labelText;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.TabStop = false;

            control.Dock = DockStyle.Fill;
            control.TabIndex = row;
            layout.Controls.Add(label, 0, row);
            layout.Controls.Add(control, 1, row);
        }
    }
}
