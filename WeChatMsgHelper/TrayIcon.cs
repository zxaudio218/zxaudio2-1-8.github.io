using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace WeChatMsgHelper
{
    internal sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon icon;
        private readonly ToolStripMenuItem statusItem;
        private readonly ToolStripMenuItem listeningItem;
        private readonly ToolStripMenuItem channelItem;
        private readonly Dictionary<NotificationMode, ToolStripMenuItem> modeItems = new Dictionary<NotificationMode, ToolStripMenuItem>();
        private readonly Dictionary<string, ToolStripMenuItem> channelItems = new Dictionary<string, ToolStripMenuItem>(StringComparer.OrdinalIgnoreCase);

        internal TrayIcon(Action toggleListening, Action<NotificationMode> setMode, Action<string> setSpeechChannel, Action readLatest, Action testSpeech, Action openSettings, Action exit)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem("正在启动...");
            statusItem.Enabled = false;
            listeningItem = new ToolStripMenuItem("监听新消息");
            listeningItem.Click += delegate { toggleListening(); };
            ToolStripMenuItem modeMenu = new ToolStripMenuItem("通知方式");
            AddMode(modeMenu, NotificationMode.Off, "关闭", setMode);
            AddMode(modeMenu, NotificationMode.SoundOnly, "仅提示音", setMode);
            AddMode(modeMenu, NotificationMode.SpeechOnly, "仅朗读", setMode);
            AddMode(modeMenu, NotificationMode.SpeechAndSound, "提示音 + 朗读", setMode);
            channelItem = new ToolStripMenuItem("语音通道：初始化中");
            AddChannel(channelItem, SpeechChannelNames.Auto, "自动选择", setSpeechChannel);
            AddChannel(channelItem, SpeechChannelNames.ZhengduIndependent, "争渡独立通道 WeChatMsg", setSpeechChannel);
            AddChannel(channelItem, SpeechChannelNames.ZhengduScreenReader, "争渡读屏通道", setSpeechChannel);
            AddChannel(channelItem, SpeechChannelNames.BaoyiIndependent, "保益第三方独立通道", setSpeechChannel);
            AddChannel(channelItem, SpeechChannelNames.BaoyiScreenReader, "保益读屏通道", setSpeechChannel);
            AddChannel(channelItem, SpeechChannelNames.Sapi5, "SAPI5", setSpeechChannel);
            ToolStripMenuItem latestItem = new ToolStripMenuItem("朗读最新消息");
            latestItem.Click += delegate { readLatest(); };
            ToolStripMenuItem testSpeechItem = new ToolStripMenuItem("测试语音通道");
            testSpeechItem.Click += delegate { testSpeech(); };
            ToolStripMenuItem settingsItem = new ToolStripMenuItem("设置...");
            settingsItem.Click += delegate { openSettings(); };
            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { exit(); };
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(listeningItem);
            menu.Items.Add(modeMenu);
            menu.Items.Add(channelItem);
            menu.Items.Add(latestItem);
            menu.Items.Add(testSpeechItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            icon = new NotifyIcon();
            icon.Icon = SystemIcons.Application;
            icon.Text = "微信消息助手";
            icon.ContextMenuStrip = menu;
            icon.Visible = true;
            icon.DoubleClick += delegate { readLatest(); };
        }

        internal void Update(bool listening, NotificationMode mode, string status, string channel, string selectedChannel)
        {
            listeningItem.Checked = listening;
            foreach (KeyValuePair<NotificationMode, ToolStripMenuItem> pair in modeItems) pair.Value.Checked = pair.Key == mode;
            statusItem.Text = status;
            channelItem.Text = "语音通道：" + channel;
            selectedChannel = SpeechChannelNames.Normalize(selectedChannel);
            foreach (KeyValuePair<string, ToolStripMenuItem> pair in channelItems)
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
        }

        private void AddMode(ToolStripMenuItem parent, NotificationMode mode, string text, Action<NotificationMode> setMode)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += delegate { setMode(mode); };
            modeItems[mode] = item;
            parent.DropDownItems.Add(item);
        }

        private void AddChannel(ToolStripMenuItem parent, string channel, string text, Action<string> setSpeechChannel)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += delegate { setSpeechChannel(channel); };
            channelItems[channel] = item;
            parent.DropDownItems.Add(item);
        }
    }
}
