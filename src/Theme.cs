using System.Drawing;
using System.Windows.Forms;

namespace LocalSteamGuard
{
    internal static class Theme
    {
        public static readonly Color Background = Color.FromArgb(15, 22, 30);
        public static readonly Color Surface = Color.FromArgb(24, 35, 46);
        public static readonly Color Field = Color.FromArgb(35, 50, 63);
        public static readonly Color Selected = Color.FromArgb(39, 71, 75);
        public static readonly Color Text = Color.FromArgb(235, 244, 250);
        public static readonly Color Muted = Color.FromArgb(167, 183, 197);
        public static readonly Color Accent = Color.FromArgb(126, 226, 197);
        public static readonly Font Body = new Font("Segoe UI", 10);
        public static readonly Font Section = new Font("Segoe UI Semibold", 12);
        public static readonly Font Heading = new Font("Segoe UI Semibold", 24);
        public static readonly Font Account = new Font("Segoe UI Semibold", 19);
        public static readonly Font Code = new Font("Consolas", 49, FontStyle.Bold);

        public static Label Label(string text, Font font, Color color)
        {
            return new Label
            {
                Text = text, Font = font, ForeColor = color, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty
            };
        }

        public static Button Button(string text, int width, bool primary = false)
        {
            var button = new Button
            {
                Text = text, Width = width, Height = 37, Font = Body, FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false, BackColor = primary ? Accent : Field,
                ForeColor = primary ? Background : Text, Cursor = Cursors.Hand,
                Margin = new Padding(0, 0, 8, 0)
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(162, 240, 219) : Selected;
            return button;
        }

        public static TextBox FieldBox(string accessibleName, int maxLength)
        {
            return new TextBox
            {
                AccessibleName = accessibleName, BackColor = Field, ForeColor = Text,
                BorderStyle = BorderStyle.FixedSingle, Font = Body, MaxLength = maxLength,
                Dock = DockStyle.Fill, Margin = Padding.Empty
            };
        }

        public static TableLayoutPanel Rows(params int[] heights)
        {
            var layout = new TableLayoutPanel
            {
                ColumnCount = 1, RowCount = heights.Length, Dock = DockStyle.Fill,
                Margin = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in heights)
                layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute,
                    height == 0 ? 100 : height));
            return layout;
        }

        public static FlowLayoutPanel ButtonRow(bool rightAligned)
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty,
                FlowDirection = rightAligned ? FlowDirection.RightToLeft : FlowDirection.LeftToRight
            };
        }
    }

    internal sealed class TimeBar : Control
    {
        private int seconds;

        public TimeBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                | ControlStyles.OptimizedDoubleBuffer, true);
            TabStop = false;
        }

        public int Seconds
        {
            get { return seconds; }
            set { seconds = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Field);
            using (var fill = new SolidBrush(Theme.Accent))
                e.Graphics.FillRectangle(fill, 0, 0, Width * seconds / GuardCode.Period, Height);
        }
    }

}
