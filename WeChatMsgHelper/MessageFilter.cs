using System;

namespace WeChatMsgHelper
{
    internal static class MessageFilter
    {
        private static readonly string[] SystemMarkers =
        {
            "撤回了一条消息", "加入了群聊", "移出了群聊", "修改群名为", "群公告", "拍了拍",
            "你已添加了", "以上是打招呼的内容", "以下为新消息", "消息已发出，但被对方拒收了",
            "开启了朋友验证", "已成为好友"
        };

        internal static bool IsSystemMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (string marker in SystemMarkers)
                if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
    }
}
