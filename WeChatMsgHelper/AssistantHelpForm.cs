using System.Drawing;
using System.Windows.Forms;

namespace WeChatMsgHelper
{
    internal static class AssistantHelpContent
    {
        internal static readonly string Text =
            "微信消息助手使用说明\r\n\r\n" +
            "一、基本使用\r\n" +
            "1. 程序运行后驻留在系统托盘，不会显示普通主窗口。\r\n" +
            "2. 登录微信并打开具体聊天后，助手会监听新消息，并按当前通知方式播放提示音或朗读。\r\n" +
            "3. 托盘菜单中的“监听新消息”用于启动或停止监听；停止监听时会停止当前朗读并清空等待队列。\r\n" +
            "4. 可通过托盘菜单“朗读最新消息”或双击托盘图标，朗读当前聊天的最新消息。\r\n\r\n" +
            "二、默认快捷键（可自定义）\r\n" +
            "朗读最新消息：" + AppConfig.DefaultReadLatestHotkey + "\r\n" +
            "启动或停止监听：" + AppConfig.DefaultToggleNotifyHotkey + "\r\n" +
            "切换通知方式：" + AppConfig.DefaultToggleModeHotkey + "\r\n" +
            "打开设置：" + AppConfig.DefaultOpenSettingsHotkey + "\r\n" +
            "修改方法：打开“设置...”，选择“快捷键设置”，在对应编辑框中直接按下新组合键，然后确认保存。\r\n" +
            "需要恢复时，可在快捷键设置窗口选择“恢复默认快捷键”。在线更新不会修改已经保存的自定义快捷键。\r\n\r\n" +
            "三、微信窗口快捷操作（固定快捷键）\r\n" +
            "Alt+P：微信位于前台时，定位公众号列表。\r\n" +
            "Alt+T：微信位于前台时，定位通讯录列表。\r\n" +
            "Alt+V：定位微信音视频接听窗口；检测到通话浮窗时，在桌面等其他窗口也可使用。\r\n" +
            "F3：微信位于前台时，在关闭、仅提示音、提示音加朗读之间切换。\r\n" +
            "Ctrl+Alt+上方向键：在当前聊天中朗读上一条消息。\r\n" +
            "Ctrl+Alt+下方向键：在当前聊天中朗读下一条消息。\r\n" +
            "Ctrl+Alt+Home：朗读当前已加载消息中的第一条。\r\n" +
            "Ctrl+Alt+End：朗读当前已加载消息中的最后一条。\r\n" +
            "消息浏览快捷键仅在微信聊天输入区域生效，以减少对其他程序快捷键的影响。\r\n\r\n" +
            "四、通知方式和语音通道\r\n" +
            "1. 在托盘菜单“通知方式”中可选择关闭、仅提示音、仅朗读或提示音加朗读。\r\n" +
            "2. 在托盘菜单“语音通道”中可直接切换自动选择、争渡独立通道、争渡读屏通道、保益第三方独立通道、保益读屏通道或 SAPI5。\r\n" +
            "3. 独立通道由助手直接提交朗读；读屏通道通过对应屏幕阅读器朗读。自动选择会根据可用语音接口选择通道。\r\n" +
            "4. 切换后可使用“测试语音通道”确认是否能够正常朗读。保益通道名称、SAPI5 发音人、语速和音量可在设置中调整。\r\n\r\n" +
            "五、更新和问题反馈\r\n" +
            "1. 使用“检查更新”手动检查新版本，使用“更新日志”查看当前版本及一个历史版本的更新内容。\r\n" +
            "2. 更新会保留程序原文件名以及用户配置，不会重置自定义快捷键。\r\n" +
            "3. 遇到异常时，使用“导出异常日志”保存日志文件，并将日志连同问题出现时间和操作步骤提供给开发者。";
    }

    internal sealed class AssistantHelpForm : Form
    {
        internal AssistantHelpForm()
        {
            Text = "微信消息助手说明";
            AccessibleName = "微信消息助手说明";
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(660, 500);
            MinimumSize = new Size(500, 360);
            MinimizeBox = false;
            MaximizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;

            TextBox helpText = new TextBox();
            helpText.Multiline = true;
            helpText.ReadOnly = true;
            helpText.ScrollBars = ScrollBars.Vertical;
            helpText.WordWrap = true;
            helpText.Text = AssistantHelpContent.Text;
            helpText.AccessibleName = "助手说明";
            helpText.TabIndex = 0;
            helpText.Dock = DockStyle.Fill;

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.Dock = DockStyle.Bottom;
            buttons.Height = 52;
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.Padding = new Padding(8);
            buttons.TabStop = false;

            Button close = new Button();
            close.Text = "关闭";
            close.AccessibleName = "关闭";
            close.AutoSize = true;
            close.MinimumSize = new Size(96, 32);
            close.TabIndex = 1;
            close.DialogResult = DialogResult.OK;

            buttons.Controls.Add(close);
            Controls.Add(helpText);
            Controls.Add(buttons);
            AcceptButton = close;
            CancelButton = close;
            Shown += delegate
            {
                helpText.SelectionStart = 0;
                helpText.SelectionLength = 0;
                helpText.Focus();
            };
        }
    }
}
