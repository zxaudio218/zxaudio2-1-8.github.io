using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WeChatMsgHelper
{
    internal sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon icon;
        private readonly ContextMenu menu;
        private readonly MenuItem statusItem;
        private readonly MenuItem listeningItem;
        private readonly MenuItem channelItem;
        private readonly Dictionary<NotificationMode, MenuItem> modeItems = new Dictionary<NotificationMode, MenuItem>();
        private readonly Dictionary<string, MenuItem> channelItems = new Dictionary<string, MenuItem>(StringComparer.OrdinalIgnoreCase);

        internal TrayIcon(Action toggleListening, Action<NotificationMode> setMode, Action<string> setSpeechChannel,
            Action readLatest, Action testSpeech, Action openSettings, Action checkUpdates, Action showReleaseNotes,
            Action exportLog, Action exit)
        {
            menu = new ContextMenu();
            statusItem = new MenuItem("正在启动...") { Enabled = false };
            listeningItem = new MenuItem("监听新消息", delegate { toggleListening(); });
            MenuItem modeMenu = new MenuItem("通知方式");
            AddMode(modeMenu, NotificationMode.Off, "关闭", setMode);
            AddMode(modeMenu, NotificationMode.SoundOnly, "仅提示音", setMode);
            AddMode(modeMenu, NotificationMode.SpeechOnly, "仅朗读", setMode);
            AddMode(modeMenu, NotificationMode.SpeechAndSound, "提示音 + 朗读", setMode);
            channelItem = new MenuItem("语音通道：初始化中");
            AddChannel(channelItem, SpeechChannelNames.Auto, "自动选择", setSpeechChannel);
            AddChannel(channelItem, SpeechChannelNames.ZhengduIndependent, "争渡独立通道 WeChatMsg", setSpeechChannel);
            AddChannel(channelItem, SpeechChannelNames.ZhengduScreenReader, "争渡读屏通道", setSpeechChannel);
            AddChannel(channelItem, SpeechChannelNames.BaoyiIndependent, "保益第三方独立通道", setSpeechChannel);
            AddChannel(channelItem, SpeechChannelNames.BaoyiScreenReader, "保益读屏通道", setSpeechChannel);
            AddChannel(channelItem, SpeechChannelNames.Sapi5, "SAPI5", setSpeechChannel);

            menu.MenuItems.Add(statusItem);
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(listeningItem);
            menu.MenuItems.Add(modeMenu);
            menu.MenuItems.Add(channelItem);
            menu.MenuItems.Add(new MenuItem("朗读最新消息", delegate { readLatest(); }));
            menu.MenuItems.Add(new MenuItem("测试语音通道", delegate { testSpeech(); }));
            menu.MenuItems.Add(new MenuItem("设置...", delegate { openSettings(); }));
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(new MenuItem("检查更新", delegate { checkUpdates(); }));
            menu.MenuItems.Add(new MenuItem("更新日志", delegate { showReleaseNotes(); }));
            menu.MenuItems.Add(new MenuItem("导出异常日志", delegate { exportLog(); }));
            menu.MenuItems.Add(new MenuItem("-"));
            menu.MenuItems.Add(new MenuItem("退出", delegate { exit(); }));

            icon = new NotifyIcon();
            icon.Icon = SystemIcons.Application;
            icon.Text = "微信消息助手";
            icon.ContextMenu = menu;
            icon.Visible = true;
            icon.DoubleClick += delegate { readLatest(); };
        }

        internal void Update(bool listening, NotificationMode mode, string status, string channel, string selectedChannel)
        {
            listeningItem.Checked = listening;
            foreach (KeyValuePair<NotificationMode, MenuItem> pair in modeItems) pair.Value.Checked = pair.Key == mode;
            statusItem.Text = status;
            channelItem.Text = "语音通道：" + channel;
            selectedChannel = SpeechChannelNames.Normalize(selectedChannel);
            foreach (KeyValuePair<string, MenuItem> pair in channelItems)
                pair.Value.Checked = string.Equals(pair.Key, selectedChannel, StringComparison.OrdinalIgnoreCase);
        }

        internal void ShowMessage(string title, string text, ToolTipIcon type)
        {
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = text;
            icon.BalloonTipIcon = type;
            icon.ShowBalloonTip(3000);
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
        }

        private void AddMode(MenuItem parent, NotificationMode mode, string text, Action<NotificationMode> setMode)
        {
            MenuItem item = new MenuItem(text, delegate { setMode(mode); });
            modeItems[mode] = item;
            parent.MenuItems.Add(item);
        }

        private void AddChannel(MenuItem parent, string channel, string text, Action<string> setSpeechChannel)
        {
            MenuItem item = new MenuItem(text, delegate { setSpeechChannel(channel); });
            channelItems[channel] = item;
            parent.MenuItems.Add(item);
        }
    }
}
