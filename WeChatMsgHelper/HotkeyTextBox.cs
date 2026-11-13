using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace WeChatMsgHelper
{
    internal sealed class HotkeyTextBox : TextBox
    {
        private static readonly IDictionary<Keys, string> KeyNames = new Dictionary<Keys, string>
        {
            { Keys.Return, "Enter" },
            { Keys.Escape, "Escape" },
            { Keys.Prior, "PageUp" },
            { Keys.Next, "PageDown" },
            { Keys.Capital, "CapsLock" },
            { Keys.Scroll, "ScrollLock" },
            { Keys.Snapshot, "PrintScreen" }
        };

        internal HotkeyTextBox(string accessibleName)
        {
            AccessibleName = accessibleName;
            ReadOnly = true;
            ShortcutsEnabled = false;
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            Keys modifiers = keyData & Keys.Modifiers;

            // Preserve normal keyboard navigation through the settings form.
            if (key == Keys.Tab && (modifiers == Keys.None || modifiers == Keys.Shift))
                return base.ProcessCmdKey(ref message, keyData);
            if (key == Keys.Escape && modifiers == Keys.None)
                return base.ProcessCmdKey(ref message, keyData);
            if (IsModifierKey(key)) return true;

            string value = FormatHotkey(key, modifiers);
            if (value.Length == 0) return true;
            Text = value;
            SelectionStart = TextLength;
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            return true;
        }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            SelectAll();
        }

        private static bool IsModifierKey(Keys key)
        {
            return key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey ||
                key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey ||
                key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu ||
                key == Keys.LWin || key == Keys.RWin;
        }

        private static string FormatHotkey(Keys key, Keys modifiers)
        {
            List<string> parts = new List<string>();
            if ((modifiers & Keys.Control) != 0) parts.Add("Ctrl");
            if ((modifiers & Keys.Alt) != 0) parts.Add("Alt");
            if ((modifiers & Keys.Shift) != 0) parts.Add("Shift");

            string name;
            if (!KeyNames.TryGetValue(key, out name)) name = key.ToString();
            if (string.IsNullOrWhiteSpace(name) || name == Keys.None.ToString()) return string.Empty;
            parts.Add(name);
            return string.Join("+", parts.ToArray());
        }
    }
}
