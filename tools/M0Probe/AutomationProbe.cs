using System;
using System.Collections.Generic;
using System.Windows.Automation;

namespace M0Probe
{
    internal static class AutomationProbe
    {
        private const int MaximumTreeDepth = 8;

        internal static void PrintTrees()
        {
            IList<WechatWindowInfo> windows = WindowProbe.FindWechatWindows();
            if (windows.Count == 0)
            {
                Console.WriteLine("未找到微信主窗口，无法打印 UIA 树。");
                return;
            }

            foreach (WechatWindowInfo window in windows)
            {
                Console.WriteLine("===== 0x{0:X} {1} {2} =====", window.Handle.ToInt64(), window.ClassName, window.Title);
                try
                {
                    PrintElement(AutomationElement.FromHandle(window.Handle), 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("无法访问 UIA：{0}", ex.Message);
                }
                Console.WriteLine();
            }
        }

        internal static void PrintLatestMessages()
        {
            IList<WechatWindowInfo> windows = WindowProbe.FindWechatWindows();
            if (windows.Count == 0)
            {
                Console.WriteLine("未找到微信主窗口，无法读取消息。");
                return;
            }

            bool foundList = false;
            foreach (WechatWindowInfo window in windows)
            {
                AutomationElement root;
                try { root = AutomationElement.FromHandle(window.Handle); }
                catch (Exception ex)
                {
                    Console.WriteLine("窗口 0x{0:X} 无法访问 UIA：{1}", window.Handle.ToInt64(), ex.Message);
                    continue;
                }

                AutomationElement list = FindMessageList(root);
                if (list == null)
                {
                    Console.WriteLine("窗口 0x{0:X} 未找到 chat_message_list 或名称为“消息”的列表。", window.Handle.ToInt64());
                    continue;
                }

                foundList = true;
                try
                {
                    AutomationElementCollection items = list.FindAll(
                        TreeScope.Children,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                    if (items.Count == 0)
                    {
                        Console.WriteLine("已找到消息列表，但当前没有直接 ListItem 子项。");
                        continue;
                    }

                    AutomationElement latest = items[items.Count - 1];
                    Console.WriteLine("窗口      : 0x{0:X} {1}", window.Handle.ToInt64(), window.Title);
                    Console.WriteLine("列表标识  : {0}", SafeProperty(list, AutomationElement.AutomationIdProperty));
                    Console.WriteLine("消息正文  : {0}", SafeProperty(latest, AutomationElement.NameProperty));
                    Console.WriteLine("RuntimeId : {0}", ReadRuntimeId(latest));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("读取消息列表失败：{0}", ex.Message);
                }
            }

            if (!foundList)
            {
                Console.WriteLine("建议：进入具体聊天，并对比开启/关闭“微信设置 > 通用 > 读屏优化模式”后的结果。");
            }
        }

        private static AutomationElement FindMessageList(AutomationElement root)
        {
            try
            {
                AutomationElement exact = root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "chat_message_list"));
                if (exact != null) return exact;

                return root.FindFirst(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "消息"),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.List)));
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }
        }

        private static void PrintElement(AutomationElement element, int depth)
        {
            if (element == null || depth > MaximumTreeDepth) return;
            try
            {
                Console.WriteLine(
                    "{0}[{1}] AutomationId=\"{2}\" ClassName=\"{3}\" Name=\"{4}\"",
                    new string(' ', depth * 2), SafeControlType(element),
                    Escape(SafeProperty(element, AutomationElement.AutomationIdProperty)),
                    Escape(SafeProperty(element, AutomationElement.ClassNameProperty)),
                    Escape(SafeProperty(element, AutomationElement.NameProperty)));
                if (depth == MaximumTreeDepth) return;

                TreeWalker walker = TreeWalker.RawViewWalker;
                AutomationElement child = walker.GetFirstChild(element);
                while (child != null)
                {
                    PrintElement(child, depth + 1);
                    child = walker.GetNextSibling(child);
                }
            }
            catch (ElementNotAvailableException)
            {
                Console.WriteLine("{0}<元素已失效>", new string(' ', depth * 2));
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine("{0}<无权访问：{1}>", new string(' ', depth * 2), ex.Message);
            }
        }

        private static string SafeControlType(AutomationElement element)
        {
            try
            {
                ControlType type = element.Current.ControlType;
                return type == null ? "Unknown" : type.ProgrammaticName;
            }
            catch { return "Unavailable"; }
        }

        private static string SafeProperty(AutomationElement element, AutomationProperty property)
        {
            try
            {
                object value = element.GetCurrentPropertyValue(property, true);
                if (value == null || value == AutomationElement.NotSupported) return string.Empty;
                return Convert.ToString(value) ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        private static string ReadRuntimeId(AutomationElement element)
        {
            try
            {
                int[] values = element.GetRuntimeId();
                if (values == null || values.Length == 0) return "<空>";
                return string.Join("-", Array.ConvertAll(values, delegate(int value) { return value.ToString(); }));
            }
            catch (Exception ex) { return "<读取失败：" + ex.Message + ">"; }
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\"", "\\\"");
        }
    }
}
