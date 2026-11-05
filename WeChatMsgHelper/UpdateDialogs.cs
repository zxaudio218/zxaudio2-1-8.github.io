using System;
using System.Drawing;
using System.Windows.Forms;

namespace WeChatMsgHelper
{
    internal enum UpdateDialogAction
    {
        Cancel,
        Install,
        Ignore
    }

    internal sealed class UpdateDialog : Form
    {
        internal UpdateDialogAction SelectedAction { get; private set; }

        internal UpdateDialog(UpdateManifest manifest)
        {
            Text = "发现新版本 " + manifest.VersionText;
            AccessibleName = "发现新版本 " + manifest.VersionText;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(580, 390);
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(480, 320);

            TextBox notes = new TextBox();
            notes.Multiline = true;
            notes.ReadOnly = true;
            notes.ScrollBars = ScrollBars.Vertical;
            notes.Text = string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? "本次更新未提供更新日志。" : manifest.ReleaseNotes;
            notes.AccessibleName = "更新日志";
            notes.TabIndex = 0;
            notes.Dock = DockStyle.Fill;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 52;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Padding = new Padding(8);
            buttons.TabStop = false;

            Button cancel = CreateButton("取消", "取消", 3);
            Button ignore = CreateButton("忽略此版本", "忽略此版本", 2);
            Button install = CreateButton("立即更新", "立即更新", 1);
            cancel.Click += delegate { SelectedAction = UpdateDialogAction.Cancel; DialogResult = DialogResult.Cancel; Close(); };
            ignore.Click += delegate { SelectedAction = UpdateDialogAction.Ignore; DialogResult = DialogResult.Ignore; Close(); };
            install.Click += delegate { SelectedAction = UpdateDialogAction.Install; DialogResult = DialogResult.OK; Close(); };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ignore);
            buttons.Controls.Add(install);
            Controls.Add(notes);
            Controls.Add(buttons);
            AcceptButton = install;
            CancelButton = cancel;
            SelectedAction = UpdateDialogAction.Cancel;
        }

        private static Button CreateButton(string text, string accessibleName, int tabIndex)
        {
            Button button = new Button();
            button.Text = text;
            button.AccessibleName = accessibleName;
            button.AutoSize = true;
            button.MinimumSize = new Size(96, 32);
            button.TabIndex = tabIndex;
            return button;
        }
    }

    internal sealed class ReleaseNotesForm : Form
    {
        internal ReleaseNotesForm(string title, string releaseNotes, bool acknowledgeOnly)
        {
            Text = title;
            AccessibleName = title;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(580, 390);
            MinimizeBox = false;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(480, 320);

            TextBox notes = new TextBox();
            notes.Multiline = true;
            notes.ReadOnly = true;
            notes.ScrollBars = ScrollBars.Vertical;
            notes.Text = releaseNotes ?? string.Empty;
            notes.AccessibleName = "更新日志";
            notes.TabIndex = 0;
            notes.Dock = DockStyle.Fill;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 52;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Padding = new Padding(8);
            buttons.TabStop = false;

            Button close = new Button();
            close.Text = acknowledgeOnly ? "我知道了" : "关闭";
            close.AccessibleName = close.Text;
            close.AutoSize = true;
            close.MinimumSize = new Size(96, 32);
            close.TabIndex = 1;
            close.DialogResult = DialogResult.OK;
            buttons.Controls.Add(close);
            Controls.Add(notes);
            Controls.Add(buttons);
            AcceptButton = close;
            if (!acknowledgeOnly) CancelButton = close;
        }
    }
}
