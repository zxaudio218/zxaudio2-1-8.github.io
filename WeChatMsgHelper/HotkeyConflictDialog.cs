using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WeChatMsgHelper
{
    internal sealed class HotkeyConflictDialog : Form
    {
        private readonly CheckBox suppressBox = new CheckBox();

        internal HotkeyConflictDialog(IList<string> failures)
        {
            Text = "微信消息助手 - 快捷键冲突";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            ClientSize = new Size(540, 300);
            AutoScaleMode = AutoScaleMode.Dpi;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(12);
            layout.ColumnCount = 1;
            layout.RowCount = 5;
            layout.TabStop = false;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));

            Label intro = CreateLabel("以下快捷键无法注册，可能已被其他程序占用：");
            layout.Controls.Add(intro, 0, 0);

            ListBox conflictList = new ListBox();
            conflictList.Dock = DockStyle.Fill;
            conflictList.AccessibleName = "冲突的快捷键";
            conflictList.TabIndex = 0;
            foreach (string failure in failures) conflictList.Items.Add(failure);
            if (conflictList.Items.Count > 0) conflictList.SelectedIndex = 0;
            layout.Controls.Add(conflictList, 0, 1);

            Label question = CreateLabel("是否现在进入快捷键设置？选择“否”将保持当前设置不变。");
            layout.Controls.Add(question, 0, 2);

            suppressBox.Text = "不再显示这组快捷键";
            suppressBox.AccessibleName = "不再显示这组快捷键";
            suppressBox.AutoSize = true;
            suppressBox.TabIndex = 1;
            layout.Controls.Add(suppressBox, 0, 3);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Dock = DockStyle.Fill;
            buttons.TabStop = false;
            buttons.TabIndex = 2;

            Button yesButton = new Button();
            yesButton.Text = "是";
            yesButton.AccessibleName = "是，进入快捷键设置";
            yesButton.DialogResult = DialogResult.Yes;
            yesButton.TabIndex = 0;

            Button noButton = new Button();
            noButton.Text = "否";
            noButton.AccessibleName = "否，保持当前快捷键";
            noButton.DialogResult = DialogResult.No;
            noButton.TabIndex = 1;

            buttons.Controls.Add(yesButton);
            buttons.Controls.Add(noButton);
            layout.Controls.Add(buttons, 0, 4);

            Controls.Add(layout);
            AcceptButton = yesButton;
            CancelButton = noButton;
            Shown += delegate { conflictList.Focus(); };
        }

        internal bool SuppressThisGroup { get { return suppressBox.Checked; } }

        private static Label CreateLabel(string text)
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
