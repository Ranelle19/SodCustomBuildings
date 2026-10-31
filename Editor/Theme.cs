using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BuildingEditor
{
    public enum Look { Light, Dark, Sod }

    /// <summary>
    /// The editor's look (Tools → Look, kept in settings.json): Light (Windows), Dark, or Shadows of Doubt (the game's own
    /// menus: deep navy, hatched panels, thin lavender frames, crimson for what's selected, a red glow round the window,
    /// white corner brackets on the button under the mouse). Every open window is themed, also small dialogs and controls a
    /// panel adds later. Colours set on purpose (the floor canvas, address colours, warnings) are left alone; switching
    /// back puts the originals back.
    /// </summary>
    public static class Theme
    {
        public static Look Mode { get; private set; }
        public static bool Dark => Mode != Look.Light;
        public static bool Sod => Mode == Look.Sod;

        // palette of the current look (dark values; Sod values set in Palette())
        public static Color Back, Panel, Input, Border, Text, SubText, Accent, Hover, Pressed, Glow, Heading;

        private static void Palette()
        {
            if (Mode == Look.Sod)
            {
                Back = Color.FromArgb(10, 11, 26);
                Panel = Color.FromArgb(24, 23, 54);
                Input = Color.FromArgb(15, 15, 38);
                Border = Color.FromArgb(104, 94, 146);
                Text = Color.FromArgb(237, 231, 242);
                SubText = Color.FromArgb(165, 156, 191);
                Accent = Color.FromArgb(142, 26, 68);
                Hover = Color.FromArgb(58, 38, 84);
                Pressed = Color.FromArgb(110, 22, 56);
                Glow = Color.FromArgb(232, 69, 94);
                Heading = Color.FromArgb(255, 143, 163);
            }
            else
            {
                Back = Color.FromArgb(32, 33, 36);
                Panel = Color.FromArgb(40, 42, 46);
                Input = Color.FromArgb(50, 52, 57);
                Border = Color.FromArgb(78, 81, 88);
                Text = Color.FromArgb(230, 230, 232);
                SubText = Color.FromArgb(160, 163, 170);
                Accent = Color.FromArgb(0, 120, 215);
                Hover = Color.FromArgb(62, 65, 71);
                Pressed = Color.FromArgb(72, 76, 84);
                Glow = Border;
                Heading = Text;
            }
        }

        private sealed class Orig
        {
            public Color Back, Fore;
            public FlatStyle? Flat;
            public Color? Link;
            public Font Font;
            public Padding Padding;
            public Image BackImage;
            public DrawMode? ListDraw;
            public TreeViewDrawMode? TreeDraw;
            public bool Hooks;
        }

        private static readonly ConditionalWeakTable<Control, Orig> Originals = new();
        private static readonly HashSet<IntPtr> Hooked = new();
        private static bool _idleHooked;

        /// <summary>Call once at start with the saved choice.</summary>
        public static void Init(Look mode)
        {
            Mode = mode;
            Palette();
            if (_idleHooked) return;
            _idleHooked = true;
            // every form, including the small code-built dialogs, gets themed as soon as it is open
            Application.Idle += (s, e) =>
            {
                foreach (Form f in Application.OpenForms)
                    if (f != null && !f.IsDisposed && f.IsHandleCreated && Hooked.Add(f.Handle))
                    {
                        Attach(f);
                        f.HandleDestroyed += (s2, e2) => Hooked.Remove(((Form)s2).Handle);
                    }
            };
        }

        public static void Set(Look mode)
        {
            Mode = mode;
            Palette();
            _tile = null;   // not disposed: controls may still show the old one until they are themed again
            foreach (Form f in Application.OpenForms)
                if (f != null && !f.IsDisposed) { ApplyTree(f); TitleBar(f); f.Invalidate(true); }
        }

        /// <summary>Themes a form now and every control added to it later.</summary>
        public static void Attach(Form f)
        {
            ApplyTree(f);
            if (f.IsHandleCreated) TitleBar(f); else f.HandleCreated += (s, e) => TitleBar(f);
        }

        private static void ApplyTree(Control c)
        {
            Apply(c);
            foreach (Control child in c.Controls) ApplyTree(child);
        }

        private static readonly ConditionalWeakTable<Control, object> AddedHooked = new();

        // ------------------------------------------------------------------ the font of the game's menus

        private static Font _sodFont;

        /// <summary>Century Gothic looks closest to the game's menu font; Segoe UI if it isn't installed.</summary>
        private static Font SodFont(Font like)
        {
            if (_sodFont == null)
            {
                bool has = FontFamily.Families.Any(f => f.Name == "Century Gothic");
                _sodFont = new Font(has ? "Century Gothic" : "Segoe UI", 9f);
            }
            return _sodFont;
        }

        // ------------------------------------------------------------------ per control

        private static void Apply(Control c)
        {
            if (c == null || c.IsDisposed) return;
            if (!AddedHooked.TryGetValue(c, out _))
            {
                AddedHooked.Add(c, null);
                c.ControlAdded += (s, e) => ApplyTree(e.Control);
            }
            if (c is Form f0 && f0.IsHandleCreated) TitleBar(f0);
            if (c.ContextMenuStrip != null) Strip(c.ContextMenuStrip);

            // things that draw themselves (3D view host, pictures) stay as they are
            if (c is PictureBox || c.GetType().Name == "ElementHost") return;

            if (!Originals.TryGetValue(c, out var o))
            {
                o = new Orig { Back = c.BackColor, Fore = c.ForeColor, Font = c is Form ? c.Font : null, Padding = c.Padding, BackImage = c.BackgroundImage };
                if (c is ButtonBase bb) o.Flat = bb.FlatStyle;
                else if (c is ComboBox cb0) o.Flat = cb0.FlatStyle;
                if (c is LinkLabel ll0) o.Link = ll0.LinkColor;
                if (c is ListBox lb0) o.ListDraw = lb0.DrawMode;
                if (c is ComboBox cbd) o.ListDraw = cbd.DrawMode;
                if (c is TreeView tv0) o.TreeDraw = tv0.DrawMode;
                Originals.Add(c, o);
            }

            Restore(c, o);
            if (Mode == Look.Light) return;

            bool input = c is TextBoxBase || c is ListBox || c is ComboBox || c is NumericUpDown || c is TreeView || c is ListView || c is DomainUpDown;
            bool plainBack = IsSystem(o.Back);
            if (plainBack) c.BackColor = input ? Input : c is Form ? Back : Panel;
            if (IsSystem(o.Fore)) c.ForeColor = Text;
            else if (o.Fore == Color.DimGray || o.Fore == Color.Gray || o.Fore == Color.DarkGray || o.Fore == SystemColors.GrayText) c.ForeColor = SubText;

            if (Sod) SodExtras(c, o, plainBack);

            switch (c)
            {
                case ButtonBase b when IsPushButton(b):
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Border;
                    b.FlatAppearance.MouseOverBackColor = Hover;
                    b.FlatAppearance.MouseDownBackColor = Pressed;
                    b.FlatAppearance.CheckedBackColor = Sod ? Accent : Color.FromArgb(0, 84, 153);
                    if (IsSystem(o.Back)) b.BackColor = Sod ? Panel : Input;
                    break;
                case ComboBox cb:
                    cb.FlatStyle = FlatStyle.Flat;
                    break;
                case TreeView tv:
                    tv.LineColor = SubText;
                    break;
                case LinkLabel ll:
                    ll.LinkColor = Sod ? Heading : Color.FromArgb(110, 170, 255);
                    break;
                case PropertyGrid pg:
                    pg.ViewBackColor = Input; pg.ViewForeColor = Text; pg.LineColor = Panel; pg.CategoryForeColor = Sod ? Heading : Text;
                    pg.CategorySplitterColor = Panel; pg.HelpBackColor = Panel; pg.HelpForeColor = Text; pg.CommandsBackColor = Panel;
                    pg.CommandsForeColor = Text; pg.DisabledItemForeColor = SubText; pg.ViewBorderColor = Border; pg.HelpBorderColor = Border;
                    pg.SelectedItemWithFocusBackColor = Accent; pg.SelectedItemWithFocusForeColor = Color.White;
                    break;
                case DataGridView dg:
                    dg.BackgroundColor = Panel; dg.GridColor = Border;
                    dg.DefaultCellStyle.BackColor = Input; dg.DefaultCellStyle.ForeColor = Text;
                    dg.DefaultCellStyle.SelectionBackColor = Accent; dg.DefaultCellStyle.SelectionForeColor = Color.White;
                    dg.ColumnHeadersDefaultCellStyle.BackColor = Panel; dg.ColumnHeadersDefaultCellStyle.ForeColor = Sod ? Heading : Text;
                    dg.EnableHeadersVisualStyles = false;
                    break;
                case SplitContainer sc:
                    if (Sod) sc.BackColor = Border;
                    break;
                case ToolStrip ts:
                    Strip(ts);
                    break;
            }
            Scrollbars(c);
        }

        private static bool IsPushButton(ButtonBase b) =>
            !(b is CheckBox cbx && cbx.Appearance == Appearance.Normal) && !(b is RadioButton rbx && rbx.Appearance == Appearance.Normal);

        /// <summary>Shadows of Doubt look: game font, hatched panels, see-through labels, painted buttons, frames, lists.</summary>
        private static void SodExtras(Control c, Orig o, bool plainBack)
        {
            if (c is Form form)
            {
                if (form.Font != SodFont(form.Font)) form.Font = SodFont(form.Font);
                if (form.TopLevel) form.Padding = new Padding(Math.Max(o.Padding.Left, 3), Math.Max(o.Padding.Top, 3), Math.Max(o.Padding.Right, 3), Math.Max(o.Padding.Bottom, 3));
            }
            // hatched background like the game's panels (only where the colour wasn't chosen on purpose)
            if (plainBack && o.BackImage == null && (c is System.Windows.Forms.Panel || c is TabPage || c is UserControl || c is Form) && !(c is SplitContainer))
            {
                c.BackgroundImage = Tile();
                c.BackgroundImageLayout = ImageLayout.Tile;
            }
            // text on panels: see-through, so the hatching shows
            if (plainBack && (c is Label || (c is CheckBox ck && ck.Appearance == Appearance.Normal) || (c is RadioButton rb && rb.Appearance == Appearance.Normal)))
                c.BackColor = Color.Transparent;
            if (c is GroupBox) c.ForeColor = Heading;

            // lists that Windows draws: draw them ourselves, so what's selected is crimson, not Windows blue
            if (c is ListBox lb && o.ListDraw == DrawMode.Normal && lb.DrawMode != DrawMode.OwnerDrawFixed) lb.DrawMode = DrawMode.OwnerDrawFixed;
            if (c is ComboBox cbo && o.ListDraw == DrawMode.Normal && cbo.DrawMode != DrawMode.OwnerDrawFixed) cbo.DrawMode = DrawMode.OwnerDrawFixed;
            if (c is TreeView tv && o.TreeDraw == TreeViewDrawMode.Normal && tv.DrawMode != TreeViewDrawMode.OwnerDrawText) tv.DrawMode = TreeViewDrawMode.OwnerDrawText;

            if (o.Hooks) return;
            o.Hooks = true;
            switch (c)
            {
                case Form f when f.TopLevel:
                    f.Paint += (s, e) => { if (Sod) PaintGlowFrame(e.Graphics, f.ClientRectangle); };
                    break;
                case GroupBox g:
                    g.Paint += (s, e) => { if (Sod) PaintGroup(g, e.Graphics); };
                    break;
                case ButtonBase b when IsPushButton(b):
                    b.MouseEnter += (s, e) => { Hot.Add(b); b.Invalidate(); };
                    b.MouseLeave += (s, e) => { Hot.Remove(b); b.Invalidate(); };
                    b.GotFocus += (s, e) => b.Invalidate();
                    b.LostFocus += (s, e) => b.Invalidate();
                    b.Paint += (s, e) => { if (Sod) PaintButton(b, e.Graphics); };
                    break;
                case ListBox l when o.ListDraw == DrawMode.Normal:
                    l.DrawItem += (s, e) => DrawListItem(e, l.Font, e.Index >= 0 && e.Index < l.Items.Count ? l.GetItemText(l.Items[e.Index]) : "");
                    break;
                case ComboBox cb when o.ListDraw == DrawMode.Normal:
                    cb.DrawItem += (s, e) => DrawListItem(e, cb.Font, e.Index >= 0 && e.Index < cb.Items.Count ? cb.GetItemText(cb.Items[e.Index]) : "");
                    break;
                case TreeView t when o.TreeDraw == TreeViewDrawMode.Normal:
                    t.DrawNode += (s, e) => DrawTreeNode(t, e);
                    break;
            }
        }

        private static void Restore(Control c, Orig o)
        {
            c.BackColor = o.Back;
            c.ForeColor = o.Fore;
            // the Shadows of Doubt extras are only undone when leaving that look (changing a list's draw mode makes Windows
            // rebuild it, so it isn't switched off and on again every time a control is themed)
            if (!Sod)
            {
                if (o.Font != null && c is Form && c.Font != o.Font) c.Font = o.Font;
                if (c is Form && c.Padding != o.Padding) c.Padding = o.Padding;
                if (c.BackgroundImage != o.BackImage) c.BackgroundImage = o.BackImage;
                if (c is ListBox lb && o.ListDraw is { } ld && lb.DrawMode != ld) lb.DrawMode = ld;
                if (c is ComboBox cbo && o.ListDraw is { } cd && cbo.DrawMode != cd) cbo.DrawMode = cd;
                if (c is TreeView t && o.TreeDraw is { } td && t.DrawMode != td) t.DrawMode = td;
            }
            if (o.Flat is { } fs)
            {
                if (c is ButtonBase b) b.FlatStyle = fs;
                else if (c is ComboBox cb) cb.FlatStyle = fs;
            }
            if (c is LinkLabel ll && o.Link is { } lc) ll.LinkColor = lc;
            if (c is TreeView tv) tv.LineColor = Color.Black;
            if (c is PropertyGrid pg)
            {
                pg.ViewBackColor = SystemColors.Window; pg.ViewForeColor = SystemColors.WindowText; pg.LineColor = SystemColors.InactiveBorder;
                pg.CategoryForeColor = SystemColors.ControlText; pg.CategorySplitterColor = SystemColors.Control; pg.HelpBackColor = SystemColors.Control;
                pg.HelpForeColor = SystemColors.ControlText; pg.CommandsBackColor = SystemColors.Control; pg.CommandsForeColor = SystemColors.ControlText;
                pg.DisabledItemForeColor = SystemColors.GrayText; pg.ViewBorderColor = SystemColors.ControlDark; pg.HelpBorderColor = SystemColors.ControlDark;
                pg.SelectedItemWithFocusBackColor = SystemColors.Highlight; pg.SelectedItemWithFocusForeColor = SystemColors.HighlightText;
            }
            if (c is ToolStrip ts) Strip(ts);
            if (Mode == Look.Light) Scrollbars(c);
        }

        private static bool IsSystem(Color c) => c.IsSystemColor || c.IsEmpty || c == Color.White || c == Color.Black;

        // ------------------------------------------------------------------ drawing the game's look

        private static Bitmap _tile;

        /// <summary>The fine diagonal hatching of the game's panels.</summary>
        private static Bitmap Tile()
        {
            if (_tile != null) return _tile;
            var bmp = new Bitmap(6, 6);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Panel);
                var line = Color.FromArgb(Panel.R + 9, Panel.G + 8, Math.Min(255, Panel.B + 16));
                for (int i = 0; i < 6; i++) bmp.SetPixel(i, 5 - i, line);
            }
            return _tile = bmp;
        }

        private static readonly HashSet<ButtonBase> Hot = new();

        /// <summary>A game-style button: hatched, thin lavender frame, crimson when on, corner brackets under the mouse.</summary>
        private static void PaintButton(ButtonBase b, Graphics g)
        {
            var r = b.ClientRectangle;
            bool on = (b is CheckBox cb && cb.Checked) || (b is RadioButton rb && rb.Checked);
            bool hot = Hot.Contains(b) && b.Enabled;
            bool custom = !IsSystem(Originals.TryGetValue(b, out var o) ? o.Back : b.BackColor);
            if (custom) using (var br = new SolidBrush(b.BackColor)) g.FillRectangle(br, r);
            else if (on) using (var br = new SolidBrush(Accent)) g.FillRectangle(br, r);
            else
            {
                using (var tb = new TextureBrush(Tile())) g.FillRectangle(tb, r);
                if (hot) using (var br = new SolidBrush(Color.FromArgb(70, Hover))) g.FillRectangle(br, r);
            }
            using (var pen = new Pen(on ? Glow : Border)) g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            using (var pen = new Pen(Color.FromArgb(60, Border))) g.DrawRectangle(pen, r.X + 2, r.Y + 2, r.Width - 5, r.Height - 5);
            if (hot || b.Focused) Brackets(g, r, Color.White);

            var text = b.Text.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
            var fore = !b.Enabled ? SubText : on ? Color.White : (IsSystem(o?.Fore ?? b.ForeColor) ? Text : b.ForeColor);
            var tr = r;
            if (b.Image != null)
            {
                int iy = r.Y + (r.Height - b.Image.Height) / 2;
                g.DrawImage(b.Image, r.X + 6, iy, b.Image.Width, b.Image.Height);
                tr = new Rectangle(r.X + b.Image.Width + 8, r.Y, r.Width - b.Image.Width - 10, r.Height);
            }
            TextRenderer.DrawText(g, text, b.Font, tr, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        }

        /// <summary>The white L-shaped brackets the game puts in the corners of what's under the cursor.</summary>
        private static void Brackets(Graphics g, Rectangle r, Color c)
        {
            int l = Math.Max(4, Math.Min(9, Math.Min(r.Width, r.Height) / 4));
            using var pen = new Pen(c, 2);
            int x0 = r.X + 2, y0 = r.Y + 2, x1 = r.Right - 3, y1 = r.Bottom - 3;
            g.DrawLines(pen, new[] { new Point(x0, y0 + l), new Point(x0, y0), new Point(x0 + l, y0) });
            g.DrawLines(pen, new[] { new Point(x1 - l, y0), new Point(x1, y0), new Point(x1, y0 + l) });
            g.DrawLines(pen, new[] { new Point(x0, y1 - l), new Point(x0, y1), new Point(x0 + l, y1) });
            g.DrawLines(pen, new[] { new Point(x1 - l, y1), new Point(x1, y1), new Point(x1, y1 - l) });
        }

        /// <summary>The red neon frame round the game's windows.</summary>
        private static void PaintGlowFrame(Graphics g, Rectangle r)
        {
            using (var p = new Pen(Color.FromArgb(90, Glow), 3)) g.DrawRectangle(p, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
            using (var p = new Pen(Glow, 1)) g.DrawRectangle(p, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
        }

        /// <summary>A panel with a title, like the game's: thin lavender frame, title in pink on a darker bar.</summary>
        private static void PaintGroup(GroupBox gb, Graphics g)
        {
            var r = gb.ClientRectangle;
            using (var tb = new TextureBrush(Tile())) g.FillRectangle(tb, r);
            var size = TextRenderer.MeasureText(gb.Text, gb.Font);
            int bar = size.Height + 4;
            using (var br = new SolidBrush(Color.FromArgb(38, 30, 70))) g.FillRectangle(br, r.X + 1, r.Y + 1, r.Width - 2, bar);
            using (var pen = new Pen(Border)) g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            using (var pen = new Pen(Color.FromArgb(120, Glow))) g.DrawLine(pen, r.X + 1, r.Y + bar + 1, r.Right - 2, r.Y + bar + 1);
            TextRenderer.DrawText(g, gb.Text, gb.Font, new Point(r.X + 6, r.Y + 3), Heading);
        }

        /// <summary>
        /// Background of a list entry in the current look (crimson when selected in the Shadows of Doubt look). For lists
        /// the editor draws itself: use instead of e.DrawBackground(), and draw the text in ItemFore(e).
        /// </summary>
        public static void DrawItemBackground(DrawItemEventArgs e)
        {
            if (!Dark) { e.DrawBackground(); return; }
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using var br = new SolidBrush(sel ? Accent : Input);
            e.Graphics.FillRectangle(br, e.Bounds);
            if (sel && Sod) using (var pen = new Pen(Glow)) e.Graphics.DrawRectangle(pen, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);
        }

        public static Color ItemFore(DrawItemEventArgs e) =>
            !Dark ? e.ForeColor : (e.State & DrawItemState.Selected) != 0 ? Color.White : Text;

        private static void DrawListItem(DrawItemEventArgs e, Font font, string text)
        {
            if (!Sod) { e.DrawBackground(); TextRenderer.DrawText(e.Graphics, text, font, e.Bounds, e.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left); return; }
            DrawItemBackground(e);
            if (e.Index < 0) return;
            var r = new Rectangle(e.Bounds.X + 3, e.Bounds.Y, e.Bounds.Width - 3, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, text, font, r, ItemFore(e), TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private static void DrawTreeNode(TreeView t, DrawTreeNodeEventArgs e)
        {
            if (!Sod || e.Node == null || e.Bounds.IsEmpty) { e.DrawDefault = true; return; }
            bool sel = e.Node.IsSelected;
            var r = e.Bounds;
            using (var br = new SolidBrush(sel ? Accent : t.BackColor)) e.Graphics.FillRectangle(br, r);
            if (sel) using (var pen = new Pen(Glow)) e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            var fore = sel ? Color.White : e.Node.ForeColor.IsEmpty ? Text : e.Node.ForeColor;
            TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont ?? t.Font, r, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }

        // ------------------------------------------------------------------ menus and bars

        private static void Strip(ToolStrip ts)
        {
            if (Dark) { ts.Renderer = new Renderer(); ts.BackColor = Sod ? Back : Panel; ts.ForeColor = Text; }
            else { ts.RenderMode = ToolStripRenderMode.ManagerRenderMode; ts.BackColor = SystemColors.Control; ts.ForeColor = SystemColors.ControlText; }
            if (Sod) ts.Font = SodFont(ts.Font);
            foreach (ToolStripItem i in ts.Items) Item(i);
        }

        private static void Item(ToolStripItem i)
        {
            i.ForeColor = Dark ? Text : SystemColors.ControlText;
            if (i is ToolStripDropDownItem dd)
            {
                if (Dark) dd.DropDown.Renderer = new Renderer();
                else dd.DropDown.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                dd.DropDown.BackColor = Dark ? Panel : SystemColors.Control;
                foreach (ToolStripItem sub in dd.DropDownItems) Item(sub);
            }
        }

        private sealed class Renderer : ToolStripProfessionalRenderer
        {
            public Renderer() : base(new Colors()) { RoundedEdges = false; }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                if (!Sod) { base.OnRenderToolStripBorder(e); return; }
                var r = e.AffectedBounds;
                if (e.ToolStrip is MenuStrip)
                    using (var p = new Pen(Glow)) e.Graphics.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
                else if (e.ToolStrip is StatusStrip)
                    using (var p = new Pen(Glow)) e.Graphics.DrawLine(p, r.Left, r.Top, r.Right, r.Top);
                else if (e.ToolStrip is ToolStripDropDown)
                {
                    using (var p = new Pen(Glow)) e.Graphics.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                }
                else base.OnRenderToolStripBorder(e);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!Sod || !(e.Item.Selected || e.Item.Pressed) || !e.Item.Enabled) { base.OnRenderMenuItemBackground(e); return; }
                var r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height - 1);
                using (var br = new SolidBrush(Accent)) e.Graphics.FillRectangle(br, r);
                using (var p = new Pen(Glow)) e.Graphics.DrawRectangle(p, r);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (Dark) e.TextColor = !e.Item.Enabled ? SubText : (e.Item.Selected && Sod) ? Color.White : Text;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                if (Dark) e.ArrowColor = Text;
                base.OnRenderArrow(e);
            }
        }

        private sealed class Colors : ProfessionalColorTable
        {
            private static Color Bar => Sod ? Back : Panel;
            public override Color MenuStripGradientBegin => Bar;
            public override Color MenuStripGradientEnd => Bar;
            public override Color ToolStripDropDownBackground => Panel;
            public override Color ImageMarginGradientBegin => Panel;
            public override Color ImageMarginGradientMiddle => Panel;
            public override Color ImageMarginGradientEnd => Panel;
            public override Color MenuItemSelected => Sod ? Accent : Hover;
            public override Color MenuItemSelectedGradientBegin => Sod ? Accent : Hover;
            public override Color MenuItemSelectedGradientEnd => Sod ? Accent : Hover;
            public override Color MenuItemPressedGradientBegin => Sod ? Accent : Input;
            public override Color MenuItemPressedGradientEnd => Sod ? Accent : Input;
            public override Color MenuItemBorder => Sod ? Glow : Border;
            public override Color MenuBorder => Sod ? Glow : Border;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Border;
            public override Color StatusStripGradientBegin => Bar;
            public override Color StatusStripGradientEnd => Bar;
            public override Color ToolStripGradientBegin => Bar;
            public override Color ToolStripGradientMiddle => Bar;
            public override Color ToolStripGradientEnd => Bar;
            public override Color ToolStripBorder => Bar;
            public override Color CheckBackground => Accent;
            public override Color CheckSelectedBackground => Accent;
            public override Color CheckPressedBackground => Accent;
            public override Color ButtonSelectedHighlight => Hover;
            public override Color ButtonSelectedGradientBegin => Hover;
            public override Color ButtonSelectedGradientEnd => Hover;
        }

        // ------------------------------------------------------------------ native bits (title bar and scrollbars)

        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        private static void TitleBar(Form f)
        {
            try
            {
                int on = Dark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
                // Windows 11: the game's colours on the title bar and window border (ignored on Windows 10)
                int def = unchecked((int)0xFFFFFFFF);
                int caption = Sod ? ColorRef(Back) : def, border = Sod ? ColorRef(Glow) : def, text = Sod ? ColorRef(Text) : def;
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
                DwmSetWindowAttribute(f.Handle, 34, ref border, 4);
                DwmSetWindowAttribute(f.Handle, 36, ref text, 4);
            }
            catch { }
        }

        private static void Scrollbars(Control c)
        {
            if (!(c is TreeView || c is ListBox || c is ListView || c is TextBoxBase || c is DataGridView || (c is ScrollableControl sc && sc.AutoScroll))) return;
            void Do() { try { SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { } }
            if (c.IsHandleCreated) Do(); else c.HandleCreated += (s, e) => Do();
        }
    }
}
