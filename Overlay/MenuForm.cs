using System;
using System.Drawing;
using System.Windows.Forms;
using ROS64Hack.Features;

namespace ROS64Hack.Overlay
{
    /// <summary>
    /// Menu form — matches the "ROS ULTRAHACK V2.1 by Ashesh" reference UI.
    ///
    /// Layout:
    ///   Title bar: "ROS64 HACK v1.0 by Boss" (dark background, cyan text)
    ///   Tabs:      VISUAL | AIMBOT | NOCLIP | Misc
    ///   VISUAL:    left col  — PLAYER / LINES / HEALTH / DISTANCE / 2D BOX / BOT / BOT DISTANCE
    ///              right col — ITEM / DISTANCE / GOLD ITEM / VEHICLE / DISTANCE / SUPPLY BOX / PLANE
    ///   AIMBOT:    smoothing slider, FOV slider, bone selector
    ///   NOCLIP:    enable toggle, speed up/down
    ///   Misc:      range slider, debug info toggle
    ///
    /// Toggle buttons glow cyan when ON, dark grey when OFF.
    /// Press INSERT to show/hide. Press DELETE to exit.
    /// </summary>
    public class MenuForm : Form
    {
        // ── Colours ───────────────────────────────────────────────────────────

        private static readonly Color BgColor     = Color.FromArgb(18, 18, 22);
        private static readonly Color TabBg       = Color.FromArgb(28, 28, 35);
        private static readonly Color AccentOn    = Color.FromArgb(0, 220, 220);     // cyan ON
        private static readonly Color AccentOff   = Color.FromArgb(60, 60, 65);     // dark OFF
        private static readonly Color TitleColor  = Color.FromArgb(0, 210, 210);
        private static readonly Color LabelColor  = Color.WhiteSmoke;
        private static readonly Font  FontTitle   = new Font("Arial", 10f, FontStyle.Bold);
        private static readonly Font  FontLabel   = new Font("Arial", 8.5f, FontStyle.Regular);
        private static readonly Font  FontBtn     = new Font("Arial", 8f,   FontStyle.Bold);

        // ── Constructor ───────────────────────────────────────────────────────

        public MenuForm()
        {
            ConfigureForm();
            BuildUI();
        }

        private void ConfigureForm()
        {
            Text            = "ROS64 HACK v1.0";
            BackColor       = BgColor;
            ForeColor       = LabelColor;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            StartPosition   = FormStartPosition.Manual;
            Location        = new Point(800, 150);
            Size            = new Size(480, 340);
            TopMost         = true;
            ShowInTaskbar   = false;
            DoubleBuffered  = true;
        }

        // ── UI build ─────────────────────────────────────────────────────────

        private void BuildUI()
        {
            // ── Custom title panel ────────────────────────────────────────────
            var title = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 28,
                BackColor = Color.FromArgb(12, 12, 16),
            };
            var titleLabel = new Label
            {
                Text      = "ROS64 HACK v1.0  ~  by Boss",
                ForeColor = TitleColor,
                Font      = FontTitle,
                Dock      = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            title.Controls.Add(titleLabel);
            Controls.Add(title);

            // ── Tab control ───────────────────────────────────────────────────
            var tabs = new TabControl
            {
                Dock      = DockStyle.Fill,
                Padding   = new Point(8, 4),
                Font      = new Font("Arial", 8.5f, FontStyle.Bold),
            };
            tabs.DrawMode    = TabDrawMode.OwnerDrawFixed;
            tabs.DrawItem   += DrawTab;
            tabs.Appearance  = TabAppearance.Buttons;

            tabs.TabPages.Add(BuildVisualTab());
            tabs.TabPages.Add(BuildAimbotTab());
            tabs.TabPages.Add(BuildNoClipTab());
            tabs.TabPages.Add(BuildMiscTab());

            Controls.Add(tabs);
        }

        // ── Tab drawing ───────────────────────────────────────────────────────

        private void DrawTab(object s, DrawItemEventArgs e)
        {
            var tc   = (TabControl)s;
            var page = tc.TabPages[e.Index];
            bool sel = (e.Index == tc.SelectedIndex);

            Color bg  = sel ? BgColor : Color.FromArgb(22, 22, 28);
            Color fg  = sel ? AccentOn : Color.Gray;
            e.Graphics.FillRectangle(new SolidBrush(bg), e.Bounds);
            TextRenderer.DrawText(e.Graphics, page.Text, FontBtn, e.Bounds, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ── VISUAL tab ────────────────────────────────────────────────────────

        private TabPage BuildVisualTab()
        {
            var page = MakePage("VISUAL");

            // Left column — player-specific ESP
            int x1 = 10, y = 10;
            AddToggle(page, "PLAYER",      x1, y, () => Settings.EspPlayer,   v => Settings.EspPlayer   = v); y += 36;
            AddToggle(page, "LINES",       x1, y, () => Settings.EspLines,    v => Settings.EspLines    = v); y += 36;
            AddToggle(page, "HEALTH",      x1, y, () => Settings.EspHealth,   v => Settings.EspHealth   = v); y += 36;
            AddToggle(page, "DISTANCE",    x1, y, () => Settings.EspDistance, v => Settings.EspDistance = v); y += 36;
            AddToggle(page, "2D BOX",      x1, y, () => Settings.Esp2DBox,    v => Settings.Esp2DBox    = v); y += 36;
            AddToggle(page, "AIMBOT",      x1, y, () => Settings.AimbotEnabled, v => Settings.AimbotEnabled = v); y += 36;

            // Right column — world item ESP
            int x2 = 240, y2 = 10;
            AddToggle(page, "ITEM",        x2, y2, () => Settings.EspItem,       v => Settings.EspItem       = v); y2 += 36;
            AddToggle(page, "DISTANCE",    x2, y2, () => Settings.EspItemDist,   v => Settings.EspItemDist   = v); y2 += 36;
            AddToggle(page, "GOLD ITEM",   x2, y2, () => Settings.EspGoldItem,   v => Settings.EspGoldItem   = v); y2 += 36;
            AddToggle(page, "VEHICLE",     x2, y2, () => Settings.EspVehicle,    v => Settings.EspVehicle    = v); y2 += 36;
            AddToggle(page, "VEH DIST",    x2, y2, () => Settings.EspVehicleDist, v => Settings.EspVehicleDist = v); y2 += 36;
            AddToggle(page, "SUPPLY BOX",  x2, y2, () => Settings.EspSupplyBox,  v => Settings.EspSupplyBox  = v); y2 += 36;
            AddToggle(page, "PLANE",       x2, y2, () => Settings.EspPlane,      v => Settings.EspPlane      = v);

            return page;
        }

        // ── AIMBOT tab ────────────────────────────────────────────────────────

        private TabPage BuildAimbotTab()
        {
            var page = MakePage("AIMBOT");
            int y = 10;

            AddToggle(page, "AIMBOT ON",  10, y, () => Settings.AimbotEnabled, v => Settings.AimbotEnabled = v); y += 36;
            AddToggle(page, "SHOW FOV",   10, y, () => Settings.ShowAimbotFOV, v => Settings.ShowAimbotFOV = v); y += 36;

            // Bone selector
            AddLabel(page, "TARGET BONE:", 10, y + 4);
            var boneCombo = new ComboBox
            {
                Location     = new Point(130, y),
                Size         = new Size(110, 22),
                DropDownStyle= ComboBoxStyle.DropDownList,
                BackColor    = Color.FromArgb(35, 35, 42),
                ForeColor    = LabelColor,
                Font         = FontLabel,
            };
            boneCombo.Items.AddRange(new object[] { "HEAD", "BODY" });
            boneCombo.SelectedIndex = Settings.TargetBone == AimbotBone.Head ? 0 : 1;
            boneCombo.SelectedIndexChanged += (s, e) =>
                Settings.TargetBone = boneCombo.SelectedIndex == 0
                    ? AimbotBone.Head : AimbotBone.Body;
            page.Controls.Add(boneCombo);
            y += 36;

            // Smoothing slider
            AddLabel(page, $"AIM RESPONSE: {Settings.AimbotSmoothing:F2}", 10, y + 4, out var smoothLabel);
            var smoothSlider = MakeSlider(130, y, 1, 100, (int)(Settings.AimbotSmoothing * 100));
            smoothSlider.Scroll += (s, e) =>
            {
                Settings.AimbotSmoothing = smoothSlider.Value / 100f;
                smoothLabel.Text = $"AIM RESPONSE: {Settings.AimbotSmoothing:F2}";
            };
            page.Controls.Add(smoothSlider);
            y += 36;

            // FOV slider
            AddLabel(page, $"FOV: {Settings.AimbotFOV:F0} px", 10, y + 4, out var fovLabel);
            var fovSlider = MakeSlider(130, y, 20, 400, (int)Settings.AimbotFOV);
            fovSlider.Scroll += (s, e) =>
            {
                Settings.AimbotFOV = fovSlider.Value;
                fovLabel.Text = $"FOV: {Settings.AimbotFOV:F0} px";
            };
            page.Controls.Add(fovSlider);

            return page;
        }

        // ── NOCLIP tab ────────────────────────────────────────────────────────

        private TabPage BuildNoClipTab()
        {
            var page = MakePage("NOCLIP");
            int y = 10;

            AddToggle(page, "NOCLIP ON", 10, y, () => Settings.NoClipEnabled, v => Settings.NoClipEnabled = v); y += 36;

            AddLabel(page, $"SPEED: {Settings.NoClipSpeed:F1}", 10, y + 4, out var speedLabel);
            var speedSlider = MakeSlider(130, y, 1, 20, (int)Settings.NoClipSpeed);
            speedSlider.Scroll += (s, e) =>
            {
                Settings.NoClipSpeed = speedSlider.Value;
                speedLabel.Text = $"SPEED: {Settings.NoClipSpeed:F1}";
            };
            page.Controls.Add(speedSlider);
            y += 36;

            AddLabel(page, "CONTROLS:", 10, y + 4);
            y += 20;
            var info = new Label
            {
                Text      = "W/A/S/D — move\nSPACE    — up\nCTRL     — down",
                Location  = new Point(10, y),
                Size      = new Size(300, 60),
                ForeColor = Color.Gray,
                Font      = FontLabel,
            };
            page.Controls.Add(info);

            return page;
        }

        // ── MISC tab ──────────────────────────────────────────────────────────

        private TabPage BuildMiscTab()
        {
            var page = MakePage("Misc");
            int y = 10;

            AddToggle(page, "DEBUG INFO", 10, y, () => Settings.ShowDebugInfo, v => Settings.ShowDebugInfo = v); y += 36;

            AddLabel(page, $"ESP RANGE: {Settings.MaxESPRange:F0} m", 10, y + 4, out var rangeLabel);
            var rangeSlider = MakeSlider(160, y, 50, 1000, (int)Settings.MaxESPRange);
            rangeSlider.Scroll += (s, e) =>
            {
                Settings.MaxESPRange = rangeSlider.Value;
                rangeLabel.Text = $"ESP RANGE: {Settings.MaxESPRange:F0} m";
            };
            page.Controls.Add(rangeSlider);
            y += 36;

            AddLabel(page, "INSERT = toggle menu", 10, y + 4);  y += 20;
            AddLabel(page, "DELETE = exit",         10, y + 4);  y += 20;
            AddLabel(page, "SHIFT  = aimbot key",  10, y + 4);  y += 20;

            y += 8;
            var hint = new Label
            {
                Text      = "Health offset: NOT YET FOUND (shows ?? until RE complete)",
                Location  = new Point(10, y),
                Size      = new Size(440, 20),
                ForeColor = Color.OrangeRed,
                Font      = new Font("Arial", 7.5f, FontStyle.Italic),
            };
            page.Controls.Add(hint);

            return page;
        }

        // ── Widget helpers ────────────────────────────────────────────────────

        private TabPage MakePage(string title)
        {
            var p = new TabPage(title)
            {
                BackColor = TabBg,
                ForeColor = LabelColor,
            };
            return p;
        }

        /// <summary>Add a cyan toggle button that binds to a Settings bool.</summary>
        private void AddToggle(
            Control parent, string text, int x, int y,
            Func<bool> getter, Action<bool> setter)
        {
            // Label on the left
            var lbl = new Label
            {
                Text      = text,
                Location  = new Point(x, y + 6),
                Size      = new Size(100, 18),
                ForeColor = LabelColor,
                Font      = FontLabel,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            parent.Controls.Add(lbl);

            // Toggle button on the right of the label
            var btn = new Button
            {
                Text      = getter() ? "ON" : "OFF",
                Location  = new Point(x + 108, y + 2),
                Size      = new Size(48, 22),
                FlatStyle = FlatStyle.Flat,
                Font      = FontBtn,
                ForeColor = getter() ? AccentOn : Color.Gray,
                BackColor = getter() ? Color.FromArgb(0, 50, 50) : Color.FromArgb(35, 35, 42),
            };
            btn.FlatAppearance.BorderColor = getter() ? AccentOn : Color.FromArgb(55, 55, 65);
            btn.Click += (s, e) =>
            {
                setter(!getter());
                bool now = getter();
                btn.Text      = now ? "ON"  : "OFF";
                btn.ForeColor = now ? AccentOn : Color.Gray;
                btn.BackColor = now ? Color.FromArgb(0, 50, 50) : Color.FromArgb(35, 35, 42);
                btn.FlatAppearance.BorderColor = now ? AccentOn : Color.FromArgb(55, 55, 65);
            };
            parent.Controls.Add(btn);
        }

        private void AddLabel(Control parent, string text, int x, int y)
        {
            var lbl = new Label
            {
                Text      = text,
                Location  = new Point(x, y),
                Size      = new Size(200, 18),
                ForeColor = LabelColor,
                Font      = FontLabel,
            };
            parent.Controls.Add(lbl);
        }

        private void AddLabel(Control parent, string text, int x, int y, out Label label)
        {
            label = new Label
            {
                Text      = text,
                Location  = new Point(x, y),
                Size      = new Size(140, 18),
                ForeColor = LabelColor,
                Font      = FontLabel,
            };
            parent.Controls.Add(label);
        }

        private TrackBar MakeSlider(int x, int y, int min, int max, int value)
        {
            return new TrackBar
            {
                Location    = new Point(x, y - 2),
                Size        = new Size(200, 28),
                Minimum     = min,
                Maximum     = max,
                Value       = Math.Clamp(value, min, max),
                TickFrequency= (max - min) / 10,
                BackColor   = TabBg,
            };
        }
    }
}
