using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BuildingEditor
{
    /// <summary>
    /// First start (and File > Game folder and mod setup): pick the game folder, then check and install what's needed —
    /// BepInEx (IL2CPP) and the CustomBuildings plugin, which is built into the editor (both required), plus the optional
    /// loaders used by "Export for other mod loaders" (Asset Bundle Loader, Community Case Loader, DDSLoader, UniversalObjectLoader).
    /// </summary>
    public sealed class SetupForm : Form
    {
        private readonly EditorSettings _settings;
        private readonly ComboBox _game = new() { Width = 430, DropDownStyle = ComboBoxStyle.DropDown };
        private readonly Label _gameState = new(), _bepState = new(), _pluginState = new(), _exportState = new();
        private readonly Button _bepInstall = new() { Text = "Install for me", Width = 120, Height = 28 };
        private readonly Button _bepPage = new() { Text = "Download page", Width = 120, Height = 28 };
        private readonly Button _pluginInstall = new() { Text = "Install", Width = 120, Height = 28 };
        private readonly Button _startGame = new() { Text = "Start the game", Width = 120, Height = 28 };
        private readonly Button _continue = new() { Text = "Continue", Width = 130, Height = 30 };
        private readonly Label _busy = new() { AutoSize = false, Width = 560, Height = 22, ForeColor = Color.DimGray };

        private readonly System.Collections.Generic.List<(GameSetup.OptionalMod mod, Label state, Button install, Button page)> _optional = new();

        public string GameDir => _game.Text.Trim();

        public SetupForm(EditorSettings settings)
        {
            AppIcon.Apply(this);
            _settings = settings;
            Text = "Shadows of Doubt Building Editor: setup";
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(660, 470 + 60 + 34 * GameSetup.OptionalMods.Length);

            var t = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1 };
            Controls.Add(t);
            t.Controls.Add(new Label
            {
                Text = "Set up the game for custom buildings. This only needs doing once per computer.",
                AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 10),
            });

            // 1. game folder
            t.Controls.Add(Step("1. Where is Shadows of Doubt installed?"));
            var gameRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
            var browse = new Button { Text = "Browse…", Width = 90, Height = 28 };
            gameRow.Controls.Add(_game);
            gameRow.Controls.Add(browse);
            t.Controls.Add(gameRow);
            t.Controls.Add(_gameState);

            // 2. BepInEx
            t.Controls.Add(Step("2. BepInEx (the mod loader)"));
            t.Controls.Add(Row(_bepState, _bepInstall, _bepPage));

            // 3. plugin
            t.Controls.Add(Step("3. CustomBuildings plugin"));
            t.Controls.Add(Row(_pluginState, _pluginInstall));

            // 4. first start
            t.Controls.Add(Step("4. Start the game once"));
            t.Controls.Add(Row(_exportState, _startGame));

            // 5. optional mods: only for "Export for other mod loaders"; CustomBuildings itself needs none of them
            t.Controls.Add(Step("5. Optional: other mod loaders (not needed by CustomBuildings)"));
            t.Controls.Add(new Label
            {
                Text = "Only needed if you (or players) want to use Building > Export for other mod loaders instead of the CustomBuildings plugin.",
                AutoSize = false, Width = 600, Height = 20, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 2),
            });
            foreach (var mod in GameSetup.OptionalMods)
            {
                var state = new Label { AutoSize = false, Width = 400, Height = 28, Margin = new Padding(0, 4, 8, 0) };
                var install = new Button { Text = "Install for me", Width = 110, Height = 26 };
                var page = new Button { Text = "Page", Width = 60, Height = 26 };
                var m = mod;
                page.Click += (s, e) => GameSetup.OpenUrl(m.Page);
                install.Click += async (s, e) =>
                {
                    if (!ConfirmGameClosed()) return;
                    SetBusy(true);
                    try { await GameSetup.InstallOptionalAsync(GameDir, m, new Progress<string>(x => _busy.Text = x)); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, $"Installing {m.Title} didn't work:\n" + ex.Message +
                                              "\n\nYou can install it by hand (or with r2modman) from its page.", Text);
                        _busy.Text = "";
                    }
                    SetBusy(false);
                    Check();
                };
                _optional.Add((mod, state, install, page));
                t.Controls.Add(Row(state, install, page));
            }

            t.Controls.Add(_busy);
            var bottom = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8) };
            var cancel = new Button { Text = "Close", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
            bottom.Controls.Add(_continue);
            bottom.Controls.Add(cancel);
            Controls.Add(bottom);
            CancelButton = cancel;

            foreach (var l in new[] { _gameState, _bepState, _pluginState, _exportState })
            {
                l.AutoSize = false; l.Width = 330; l.Height = 40; l.Margin = new Padding(0, 4, 8, 0);
            }
            _gameState.Width = 560; _gameState.Height = 22;

            // events
            browse.Click += (s, e) =>
            {
                using var dlg = new FolderBrowserDialog { Description = "Pick the Shadows of Doubt folder (the one with Shadows of Doubt.exe)", UseDescriptionForTitle = true };
                if (Directory.Exists(GameDir)) dlg.SelectedPath = GameDir;
                if (dlg.ShowDialog(this) == DialogResult.OK) { _game.Text = dlg.SelectedPath; Check(); }
            };
            _game.TextChanged += (s, e) => Check();
            _bepPage.Click += (s, e) => GameSetup.OpenUrl(GameSetup.BepInExPage);
            _bepInstall.Click += async (s, e) =>
            {
                if (!ConfirmGameClosed()) return;
                SetBusy(true);
                try
                {
                    await GameSetup.InstallBepInExAsync(GameDir, new Progress<string>(m => _busy.Text = m));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Installing BepInEx didn't work:\n" + ex.Message +
                                          "\n\nYou can install it by hand: open the download page, download it, and copy the contents of its " +
                                          "BepInExPack folder into the game folder.", Text);
                    _busy.Text = "";
                }
                SetBusy(false);
                Check();
            };
            _pluginInstall.Click += (s, e) =>
            {
                if (!ConfirmGameClosed()) return;
                try
                {
                    GameSetup.InstallPlugin(GameDir);
                    _busy.Text = "CustomBuildings plugin installed.";
                }
                catch (Exception ex) { MessageBox.Show(this, "Installing the plugin didn't work:\n" + ex.Message, Text); }
                Check();
            };
            _startGame.Click += (s, e) =>
            {
                try { GameSetup.StartGame(); _busy.Text = "Starting the game through Steam… wait for the main menu, then come back here."; }
                catch (Exception ex) { MessageBox.Show(this, "Couldn't start the game:\n" + ex.Message, Text); }
            };
            _continue.Click += (s, e) =>
            {
                _settings.GameDir = GameDir;
                _settings.Save();
                DialogResult = DialogResult.OK;
                Close();
            };
            Activated += (s, e) => Check();   // e.g. after the game was started and closed

            // starting point: the saved folder, else what we can find
            var found = GameSetup.FindGameFolders();
            foreach (var f in found) _game.Items.Add(f);
            _game.Text = GameSetup.IsGameFolder(settings.GameDir) ? settings.GameDir : found.FirstOrDefault() ?? "";
            Check();
        }

        private static Label Step(string text) => new()
        {
            Text = text, AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 12, 0, 2),
        };

        private static FlowLayoutPanel Row(params Control[] cs)
        {
            var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            foreach (var c in cs) r.Controls.Add(c);
            return r;
        }

        private bool _busyNow;

        private void SetBusy(bool busy)
        {
            _busyNow = busy;
            UseWaitCursor = busy;
            foreach (var b in new[] { _bepInstall, _bepPage, _pluginInstall, _startGame, _continue }) b.Enabled = !busy;
            foreach (var o in _optional) o.install.Enabled = !busy;
        }

        private bool ConfirmGameClosed()
        {
            if (!GameSetup.IsGameRunning()) return true;
            MessageBox.Show(this, "Please close Shadows of Doubt first.", Text);
            return false;
        }

        private static void Show(Label l, bool ok, string text, bool warn = false)
        {
            l.Text = (ok ? "✔ " : warn ? "• " : "✘ ") + text;
            l.ForeColor = ok ? Color.SeaGreen : warn ? Color.DarkGoldenrod : Color.Firebrick;
        }

        private void Check()
        {
            if (_busyNow) return;
            var dir = GameDir;
            bool game = GameSetup.IsGameFolder(dir);
            Show(_gameState, game, game ? "Found the game." : "Shadows of Doubt.exe isn't in this folder.");

            var bep = game ? GameSetup.GetBepInEx(dir) : BepInExState.Missing;
            switch (bep)
            {
                case BepInExState.Ready: Show(_bepState, true, "Installed and working."); break;
                case BepInExState.Installed: Show(_bepState, true, "Installed. It finishes setting itself up the first time the game starts (takes a few minutes)."); break;
                case BepInExState.WrongKind: Show(_bepState, false, "A different BepInEx (not IL2CPP) is installed. It doesn't work with this game; install the IL2CPP one."); break;
                default: Show(_bepState, false, game ? $"Not installed. Needed to load mods (BepInEx {GameSetup.BepInExVersion}, IL2CPP)." : "—"); break;
            }
            _bepInstall.Visible = _bepPage.Visible = game && bep != BepInExState.Ready && bep != BepInExState.Installed;

            var plugin = game ? GameSetup.GetPlugin(dir) : PluginState.Missing;
            switch (plugin)
            {
                case PluginState.Current: Show(_pluginState, true, $"Installed, up to date ({GameSetup.InstalledVersion(dir)})."); break;
                case PluginState.Newer: Show(_pluginState, true, $"Installed: {GameSetup.InstalledVersion(dir)}, newer than this editor's {GameSetup.BundledVersion()}. Kept (a test build? \"Use {GameSetup.BundledVersion()}\" puts this editor's in)."); break;
                case PluginState.Different: Show(_pluginState, true, $"Installed ({GameSetup.InstalledVersion(dir)}), a different build of the same version. Kept; \"Reinstall\" puts this editor's in."); break;
                case PluginState.Outdated: Show(_pluginState, false, $"Installed: {GameSetup.InstalledVersion(dir)}, older than this editor's {GameSetup.BundledVersion()}.", warn: true); break;
                case PluginState.NotBundled: Show(_pluginState, false, "Not installed, and this editor doesn't contain it. Build it with build.bat."); break;
                default: Show(_pluginState, false, game ? "Not installed." : "—"); break;
            }
            _pluginInstall.Text = plugin == PluginState.Outdated ? "Update" : plugin == PluginState.Different ? "Reinstall"
                                : plugin == PluginState.Newer ? $"Use {GameSetup.BundledVersion()}" : "Install";
            // also when the installed one is "newer": the test builds were numbered 1.1–1.9 before the 1.0.0 release
            _pluginInstall.Visible = game && (plugin == PluginState.Missing || plugin == PluginState.Outdated || plugin == PluginState.Different ||
                                              plugin == PluginState.Newer);

            bool export = game && GameSetup.HasExport(dir);
            bool pluginOk = GameSetup.PluginOk(plugin);
            bool bepOk = bep == BepInExState.Ready || bep == BepInExState.Installed;
            if (export) Show(_exportState, true, "Done. The game's floors and buildings are available to the editor.");
            else Show(_exportState, false, bepOk && pluginOk
                ? "Start the game once and wait for the main menu. The plugin then hands the game's floors to the editor."
                : "After steps 2 and 3.", warn: true);
            _startGame.Visible = game && bepOk && pluginOk && !export;

            foreach (var (mod, state, install, page) in _optional)
            {
                bool has = game && GameSetup.IsInstalled(dir, mod);
                var missingNeeds = mod.Needs.Select(n => GameSetup.OptionalMods.FirstOrDefault(o => o.Name == n))
                                            .Where(o => o != null && game && !GameSetup.IsInstalled(dir, o)).Select(o => o.Title).ToList();
                if (!game) { state.Text = $"{mod.Title}: —"; state.ForeColor = Color.DimGray; }
                else if (has && missingNeeds.Count > 0) Show(state, false, $"{mod.Title}: installed, but needs {string.Join(" and ", missingNeeds)}.", warn: true);
                else if (has) Show(state, true, $"{mod.Title}: installed.");
                else { state.Text = $"○ {mod.Title}: not installed ({mod.Why})."; state.ForeColor = Color.DimGray; }
                install.Visible = game && bepOk && !has;
                page.Visible = game;
            }

            // only BepInEx and the CustomBuildings plugin are required; the optional mods never block Continue
            _continue.Enabled = game && bepOk && pluginOk;
            _continue.Text = export ? "Continue" : "Continue anyway";
        }
    }
}
