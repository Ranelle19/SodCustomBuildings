using System;
using System.Drawing;
using System.Windows.Forms;

namespace BuildingEditor
{
    /// <summary>The editor's icon (icon.ico, built into the .exe) on its windows.</summary>
    public static class AppIcon
    {
        private static Icon _icon;
        private static bool _tried;

        public static void Apply(Form f)
        {
            if (!_tried)
            {
                _tried = true;
                try { _icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { }
            }
            if (_icon != null) f.Icon = _icon;
        }
    }
}
