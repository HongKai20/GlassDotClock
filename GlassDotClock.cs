using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Serialization;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle("GlassDotClock")]
[assembly: AssemblyDescription("透明灰度背景的点阵时钟与 Windows 屏幕保护程序")]
[assembly: AssemblyProduct("GlassDotClock")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace GlassDotClock
{
    public sealed class ClockSettings
    {
        public bool Use24Hour { get; set; }
        public bool ShowSeconds { get; set; }
        public bool ShowDate { get; set; }
        public bool Motion { get; set; }

        public ClockSettings()
        {
            Use24Hour = ShowSeconds = ShowDate = Motion = true;
        }

        private static string FileName
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassDotClock", "settings.xml"); }
        }

        public static ClockSettings Load()
        {
            try
            {
                if (File.Exists(FileName))
                    using (FileStream stream = File.OpenRead(FileName))
                        return (ClockSettings)new XmlSerializer(typeof(ClockSettings)).Deserialize(stream);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidOperationException) { }
            return new ClockSettings();
        }

        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FileName));
                using (FileStream stream = File.Create(FileName))
                    new XmlSerializer(typeof(ClockSettings)).Serialize(stream, this);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }

    internal static class Palette
    {
        public static Color Color(string value)
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }

        public static SolidColorBrush Solid(string value)
        {
            SolidColorBrush brush = new SolidColorBrush(Color(value));
            brush.Freeze();
            return brush;
        }

        public static LinearGradientBrush Linear(string start, string end, Point from, Point to)
        {
            LinearGradientBrush brush = new LinearGradientBrush(Color(start), Color(end), from, to);
            brush.Freeze();
            return brush;
        }

        public static RadialGradientBrush Glow(string center, string middle)
        {
            RadialGradientBrush brush = new RadialGradientBrush();
            brush.GradientStops.Add(new GradientStop(Color(center), 0));
            brush.GradientStops.Add(new GradientStop(Color(middle), 0.42));
            brush.GradientStops.Add(new GradientStop(Color("#00000000"), 1));
            brush.Freeze();
            return brush;
        }
    }

    public sealed class ClockSurface : Grid
    {
        private static readonly int[][] Digits = {
            new[] { 14, 17, 19, 21, 25, 17, 14 },
            new[] { 4, 12, 4, 4, 4, 4, 14 },
            new[] { 14, 17, 1, 2, 4, 8, 31 },
            new[] { 30, 1, 1, 14, 1, 1, 30 },
            new[] { 2, 6, 10, 18, 31, 2, 2 },
            new[] { 31, 16, 16, 30, 1, 1, 30 },
            new[] { 14, 16, 16, 30, 17, 17, 14 },
            new[] { 31, 1, 2, 4, 8, 8, 8 },
            new[] { 14, 17, 17, 14, 17, 17, 14 },
            new[] { 14, 17, 17, 15, 1, 1, 14 }
        };
        private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
        private static readonly Typeface Typeface = new Typeface("Microsoft YaHei UI");
        private static readonly Brush TintBrush = Palette.Solid("#1A808080");
        private static readonly Pen GlassRimPen = new Pen(Palette.Linear("#F2FFFFFF", "#4E3A4E5B", new Point(0, 0), new Point(0.62, 1)), 1.35);
        private static readonly Pen GlassInnerRimPen = new Pen(Palette.Linear("#62FFFFFF", "#36192D3A", new Point(0, 0), new Point(0, 1)), 0.9);
        private static readonly Brush DotBrush = Palette.Solid("#82D2F2");
        private static readonly Brush SecondsBrush = Palette.Solid("#82D2F2");
        private static readonly Brush OffDotBrush = Palette.Solid("#3082D2F2");
        private static readonly Brush HaloBrush = Palette.Glow("#6982D2F2", "#2482D2F2");
        private static readonly Brush MutedBrush = Palette.Solid("#82D2F2");
        private static readonly Brush LightBrush = Palette.Solid("#82D2F2");
        private static readonly Brush ChartGridBrush = Palette.Solid("#5082D2F2");
        private static readonly Brush ForecastBrush = Palette.Solid("#B082D2F2");
        private static readonly Dictionary<char, DotGlyph> Glyphs = new Dictionary<char, DotGlyph>();
        private static readonly Dictionary<int, Geometry> WeatherIcons = new Dictionary<int, Geometry>();

        private sealed class DotGlyph
        {
            public double Advance;
            public Geometry Dots;
        }

        private readonly ClockSettings settings;
        private readonly bool saver;
        private readonly bool preview;
        private readonly DispatcherTimer timer;
        private readonly Stopwatch elapsed = new Stopwatch();
        private readonly WeatherService weather;
        private readonly Button weatherButton;
        private bool weatherExpanded;
        private Rect weatherTarget;
        private DateTime now = DateTime.Now;

        internal ClockSurface(ClockSettings settings, bool saver, bool preview, WeatherService weather)
        {
            this.settings = settings;
            this.saver = saver;
            this.preview = preview;
            this.weather = weather;
            RoundedGlass = !saver && !preview;
            ClipToBounds = true;
            Focusable = false;
            if (!saver && !preview)
            {
                weatherButton = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0),
                    Cursor = Cursors.Hand,
                    FocusVisualStyle = null,
                    Template = (ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border Background='Transparent'/></ControlTemplate>")
                };
                weatherButton.Click += delegate
                {
                    if (weather != null && weather.Data != null && weather.Data.Hours.Count > 1)
                        weatherExpanded = !weatherExpanded;
                    else if (weather != null) weather.Refresh();
                    WeatherChanged(null, EventArgs.Empty);
                };
                weatherButton.MouseEnter += delegate { InvalidateVisual(); };
                weatherButton.MouseLeave += delegate { InvalidateVisual(); };
                weatherButton.GotKeyboardFocus += delegate { InvalidateVisual(); };
                weatherButton.LostKeyboardFocus += delegate { InvalidateVisual(); };
                Children.Add(weatherButton);
            }
            timer = new DispatcherTimer(DispatcherPriority.Background);
            timer.Tick += Tick;
            Loaded += delegate
            {
                elapsed.Start();
                RefreshSettings();
                timer.Start();
                if (weather != null) weather.Changed += WeatherChanged;
                WeatherChanged(null, EventArgs.Empty);
            };
            Unloaded += delegate
            {
                timer.Stop();
                elapsed.Stop();
                if (weather != null) weather.Changed -= WeatherChanged;
            };
            AutomationProperties.SetName(this, "GlassDotClock 本地时间时钟");
        }

        internal bool IsWeatherExpanded { get { return weatherExpanded; } }
        internal bool RoundedGlass { get; set; }

        private void WeatherChanged(object sender, EventArgs e)
        {
            if (weatherButton != null)
            {
                string action = weather != null && weather.Data != null ? (weatherExpanded ? "收起当日气温曲线" : "展开当日气温曲线") : "刷新天气";
                AutomationProperties.SetName(weatherButton, action);
                AutomationProperties.SetHelpText(weatherButton, "点击天气图标查看当天逐小时气温预报；城市按网络 IP 自动定位。");
                weatherButton.ToolTip = action + "\n城市按网络 IP 定位，可能受代理影响。天气数据：Open-Meteo.com（CC BY 4.0）。";
            }
            InvalidateVisual();
        }

        public void RefreshSettings()
        {
            timer.Interval = TimeSpan.FromMilliseconds(settings.Motion && SystemParameters.ClientAreaAnimation ? 50 : 250);
            now = DateTime.Now;
            InvalidateVisual();
        }

        private void Tick(object sender, EventArgs e)
        {
            DateTime next = DateTime.Now;
            bool changed = next.Second != now.Second || next.Date != now.Date;
            now = next;
            if (changed || (settings.Motion && SystemParameters.ClientAreaAnimation))
                InvalidateVisual();
        }

        public static string FormatTime(DateTime time, bool use24Hour, bool showSeconds)
        {
            string format = use24Hour ? "HH:mm" : "hh:mm";
            return time.ToString(showSeconds ? format + ":ss" : format, CultureInfo.InvariantCulture);
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double w = ActualWidth;
            double h = ActualHeight;
            if (w < 1 || h < 1) return;
            double seconds = elapsed.Elapsed.TotalSeconds;
            double motion = settings.Motion && SystemParameters.ClientAreaAnimation ? seconds : 0;

            // 参照样本最外层底板，仅叠加统一的低透明度冷白色，不添加纹理。
            dc.DrawRectangle(TintBrush, null, new Rect(0, 0, w, h));
            if (!saver && !preview)
            {
                double radius = RoundedGlass ? 8 : 0;
                dc.DrawRoundedRectangle(null, GlassRimPen, new Rect(0.5, 0.5, w - 1, h - 1), radius, radius);
                dc.DrawRoundedRectangle(null, GlassInnerRimPen, new Rect(1.5, 1.5, w - 3, h - 3), Math.Max(0, radius - 1), Math.Max(0, radius - 1));
            }

            double scale = Math.Min(w / 1280, h / 800);
            dc.PushTransform(new TranslateTransform((w - 1280 * scale) / 2, (h - 800 * scale) / 2));
            dc.PushTransform(new ScaleTransform(scale, scale));
            // 屏保中的时钟轻微漂移，避免始终占用同一组像素。
            if (saver && !preview)
                dc.PushTransform(new TranslateTransform(Math.Sin(seconds / 43) * 16, Math.Sin(seconds / 59) * 11));

            WeatherData data = weather == null ? null : weather.Data;
            bool chart = weatherExpanded && data != null && data.Hours.Count > 1 && !preview;
            double timeTop = chart ? (settings.ShowDate ? 220 : 245) : (settings.ShowDate ? 320 : 346);
            double infoOffset = chart ? 0 : 36;
            double weatherTop = (chart ? (settings.ShowDate ? 435 : 423) : (settings.ShowDate ? 538 : 513)) + infoOffset;
            if (!settings.Use24Hour)
                DrawDotText(dc, now.Hour < 12 ? "上午 / AM" : "下午 / PM", 640, timeTop - 40, 1.1, MutedBrush, 400);
            string time = FormatTime(now, settings.Use24Hour, settings.ShowSeconds);
            DrawMatrix(dc, time, settings.ShowSeconds ? 19.4 : 23.8, timeTop, motion);
            if (settings.ShowDate)
                DrawDotText(dc, now.ToString("yyyy年MM月dd日  dddd", Chinese), 640, (chart ? 378 : 478) + infoOffset, 1.65, LightBrush, 920);
            DrawWeather(dc, data, weatherTop, scale, (w - 1280 * scale) / 2, (h - 800 * scale) / 2);
            if (chart) DrawTemperatureChart(dc, data);
            if (saver && !preview)
            {
                dc.Pop();
                if (seconds < 6)
                {
                    dc.PushOpacity(Math.Min(1, Math.Max(0, 6 - seconds)));
                    DrawText(dc, "移动鼠标或按任意键退出", 640, 748, 12, MutedBrush, TextAlignment.Center);
                    dc.Pop();
                }
            }
            dc.Pop();
            dc.Pop();
        }

        private void DrawMatrix(DrawingContext dc, string time, double pitch, double top, double motion)
        {
            int columns = (time.Length - 1) * 2;
            foreach (char c in time) columns += c == ':' ? 1 : 5;
            double x = 640 - (columns - 1) * pitch / 2;
            double radius = pitch * 0.19;
            for (int index = 0; index < time.Length; index++)
            {
                char c = time[index];
                int width = c == ':' ? 1 : 5;
                bool secondsDigit = index >= 6;
                if (c == ':')
                    dc.PushOpacity(0.7 + 0.3 * Math.Cos(motion * Math.PI));
                for (int row = 0; row < 7; row++)
                {
                    int bits = c == ':' ? (row == 2 || row == 4 ? 1 : 0) : Digits[c - '0'][row];
                    for (int col = 0; col < width; col++)
                    {
                        Point point = new Point(x + col * pitch, top + row * pitch);
                        bool on = (bits & (1 << (width - col - 1))) != 0;
                        if (on)
                        {
                            dc.DrawEllipse(HaloBrush, null, point, radius * 3.2, radius * 3.2);
                            dc.DrawEllipse(secondsDigit ? SecondsBrush : DotBrush, null, point, radius, radius);
                        }
                        else dc.DrawEllipse(OffDotBrush, null, point, radius * 0.68, radius * 0.68);
                    }
                }
                if (c == ':') dc.Pop();
                x += (width + 2) * pitch;
            }
        }

        internal void SetWeatherExpanded(bool expanded)
        {
            weatherExpanded = expanded;
            WeatherChanged(null, EventArgs.Empty);
        }

        private static void AddDot(StreamGeometryContext context, double x, double y, double radius)
        {
            Size size = new Size(radius, radius);
            context.BeginFigure(new Point(x - radius, y), true, true);
            context.ArcTo(new Point(x + radius, y), size, 0, false, SweepDirection.Clockwise, true, false);
            context.ArcTo(new Point(x - radius, y), size, 0, false, SweepDirection.Clockwise, true, false);
        }

        private static DotGlyph GetGlyph(char value)
        {
            DotGlyph glyph;
            if (Glyphs.TryGetValue(value, out glyph)) return glyph;
            FormattedText text = new FormattedText(value.ToString(), Chinese, FlowDirection.LeftToRight, Typeface, 16, Brushes.White, 1);
            int width = Math.Max(2, (int)Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 2);
            const int height = 24;
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext drawing = visual.RenderOpen()) drawing.DrawText(text, new Point(0, 0));
            RenderTargetBitmap bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            byte[] pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            StreamGeometry dots = new StreamGeometry();
            using (StreamGeometryContext context = dots.Open())
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                        if (pixels[(y * width + x) * 4 + 3] >= 100)
                            AddDot(context, x + 0.5, y + 0.5, 0.35);
            dots.Freeze();
            glyph = new DotGlyph { Advance = width + 1, Dots = dots };
            Glyphs.Add(value, glyph);
            return glyph;
        }

        private static double DotTextWidth(string value)
        {
            double width = 0;
            foreach (char c in value) width += GetGlyph(c).Advance;
            return width;
        }

        private static void DrawDotText(DrawingContext dc, string value, double center, double top, double pitch, Brush brush, double maxWidth)
        {
            double width = DotTextWidth(value);
            if (width == 0) return;
            pitch = Math.Min(pitch, maxWidth / width);
            dc.PushTransform(new TranslateTransform(center - width * pitch / 2, top));
            dc.PushTransform(new ScaleTransform(pitch, pitch));
            double x = 0;
            foreach (char c in value)
            {
                DotGlyph glyph = GetGlyph(c);
                dc.PushTransform(new TranslateTransform(x, 0));
                dc.DrawGeometry(brush, null, glyph.Dots);
                dc.Pop();
                x += glyph.Advance;
            }
            dc.Pop();
            dc.Pop();
        }

        private static string WeatherDescription(int code)
        {
            if (code == 0) return "晴";
            if (code == 1) return "少云";
            if (code == 2) return "多云";
            if (code == 3) return "阴";
            if (code == 45 || code == 48) return "雾";
            if (code >= 51 && code <= 55) return "毛毛雨";
            if (code == 56 || code == 57 || code == 66 || code == 67) return "冻雨";
            if (code == 61) return "小雨";
            if (code == 63) return "中雨";
            if (code == 65) return "大雨";
            if (code == 71) return "小雪";
            if (code == 73) return "中雪";
            if (code == 75) return "大雪";
            if (code == 77) return "雪粒";
            if (code >= 80 && code <= 82) return "阵雨";
            if (code == 85 || code == 86) return "阵雪";
            if (code == 95) return "雷雨";
            if (code == 96 || code == 99) return "雷雨冰雹";
            return "天气";
        }

        private static Geometry GetWeatherIcon(int code, bool day)
        {
            int kind = code == 0 ? (day ? 0 : 1) : (code == 1 || code == 2 ? 2 : 3);
            if (code == 45 || code == 48) kind = 4;
            else if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82)) kind = 5;
            else if ((code >= 71 && code <= 77) || code == 85 || code == 86) kind = 6;
            else if (code >= 95) kind = 7;
            Geometry cached;
            if (WeatherIcons.TryGetValue(kind, out cached)) return cached;
            Func<int, int, bool> cloud = delegate(int x, int y)
            {
                return (x - 5) * (x - 5) + (y - 9) * (y - 9) <= 9 ||
                    (x - 9) * (x - 9) + (y - 7) * (y - 7) <= 16 ||
                    (x - 12) * (x - 12) + (y - 9) * (y - 9) <= 9 ||
                    (x >= 4 && x <= 13 && y >= 9 && y <= 11);
            };
            StreamGeometry icon = new StreamGeometry();
            using (StreamGeometryContext context = icon.Open())
                for (int y = 0; y < 16; y++)
                    for (int x = 0; x < 16; x++)
                    {
                        int dx = x - 7;
                        int dy = y - 7;
                        int radius = dx * dx + dy * dy;
                        bool on;
                        if (kind == 0)
                            on = (radius >= 9 && radius <= 18) || (radius >= 30 && radius <= 50 && (dx == 0 || dy == 0 || Math.Abs(dx) == Math.Abs(dy)));
                        else if (kind == 1)
                            on = radius <= 30 && (x - 10) * (x - 10) + (y - 5) * (y - 5) >= 27;
                        else if (kind == 4)
                            on = (y == 5 || y == 8 || y == 11) && x >= 2 + (y % 2) && x <= 13;
                        else
                        {
                            on = cloud(x, y) && (!cloud(x - 1, y) || !cloud(x + 1, y) || !cloud(x, y - 1) || !cloud(x, y + 1));
                            if (kind == 2 && x < 7 && y < 6)
                                on |= (x - 3) * (x - 3) + (y - 3) * (y - 3) >= 4 && (x - 3) * (x - 3) + (y - 3) * (y - 3) <= 9;
                            if (kind == 5) on |= (y == 13 && (x == 5 || x == 9 || x == 13)) || (y == 15 && (x == 4 || x == 8 || x == 12));
                            if (kind == 6) on |= (y == 13 || y == 15) && (x == 5 || x == 11) || y == 14 && (x >= 4 && x <= 6 || x >= 10 && x <= 12);
                            if (kind == 7) on |= (y == 12 && x == 9) || (y == 13 && x == 8) || (y == 14 && (x == 8 || x == 9)) || (y == 15 && x == 7);
                        }
                        if (on) AddDot(context, x * 2 + 1, y * 2 + 1, 0.68);
                    }
            icon.Freeze();
            WeatherIcons.Add(kind, icon);
            return icon;
        }

        private static string Temperature(double value)
        {
            return Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }

        private void DrawWeather(DrawingContext dc, WeatherData data, double top, double scale, double offsetX, double offsetY)
        {
            string text = data == null ? (weather == null ? "天气联网自动更新" : weather.StatusText) :
                data.City + "  " + WeatherDescription(data.WeatherCode) + "  " + Temperature(data.Temperature) + "°C   " + Temperature(data.Minimum) + "～" + Temperature(data.Maximum) + "°C";
            double units = DotTextWidth(text);
            double pitch = Math.Min(1.55, 820 / Math.Max(1, units));
            double width = units * pitch;
            double left = 640 - (width + 54) / 2;
            bool highlighted = weatherButton != null && (weatherButton.IsMouseOver || weatherButton.IsKeyboardFocused);
            Brush brush = data == null ? MutedBrush : (highlighted ? DotBrush : LightBrush);
            dc.PushTransform(new TranslateTransform(left + 4, top + 1));
            dc.PushTransform(new ScaleTransform(1.15, 1.15));
            dc.DrawGeometry(brush, null, GetWeatherIcon(data == null ? 3 : data.WeatherCode, data == null || data.IsDay));
            dc.Pop();
            dc.Pop();
            DrawDotText(dc, text, left + 54 + width / 2, top + 2, pitch, brush, 820);
            if (weatherButton != null)
            {
                for (int i = -2; i <= 2; i++)
                    dc.DrawEllipse(brush, null, new Point(left + 22 + i * 2.4, top + 43 + (weatherExpanded ? -1 : 1) * (2 - Math.Abs(i)) * 2), 0.7, 0.7);
                Rect target = new Rect(offsetX + (left - 5) * scale, offsetY + (top - 4) * scale, 55 * scale, 58 * scale);
                if (target != weatherTarget)
                {
                    weatherTarget = target;
                    weatherButton.Width = target.Width;
                    weatherButton.Height = target.Height;
                    weatherButton.Margin = new Thickness(target.X, target.Y, 0, 0);
                }
            }
            string hint;
            if (data == null) hint = preview ? "" : "联网后自动查询当地天气";
            else if (!string.IsNullOrEmpty(weather.StatusText)) hint = weather.StatusText;
            else hint = "IP 定位 · " + (weather.IsRefreshing ? "更新中" : "更新 " + data.UpdatedUtc.ToLocalTime().ToString("HH:mm")) + (saver ? "" : " · 点击图标" + (weatherExpanded ? "收起" : "展开"));
            if (hint.Length != 0 && !weatherExpanded) DrawDotText(dc, hint, 640, top + 55, 0.9, MutedBrush, 900);
        }

        private static void DrawDottedLine(DrawingContext dc, Point from, Point to, Brush brush, double pitch, double radius)
        {
            double dx = to.X - from.X;
            double dy = to.Y - from.Y;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / pitch));
            for (int i = 0; i <= steps; i++)
                dc.DrawEllipse(brush, null, new Point(from.X + dx * i / steps, from.Y + dy * i / steps), radius, radius);
        }

        private static void DrawTemperatureChart(DrawingContext dc, WeatherData data)
        {
            const double left = 260;
            const double top = 536;
            const double width = 780;
            const double height = 130;
            double minimum = data.Minimum;
            double maximum = data.Maximum;
            foreach (WeatherHour hour in data.Hours)
            {
                minimum = Math.Min(minimum, hour.Temperature);
                maximum = Math.Max(maximum, hour.Temperature);
            }
            minimum = Math.Floor(minimum - 2);
            maximum = Math.Ceiling(Math.Max(maximum + 2, minimum + 6));
            DrawDotText(dc, data.Hours[0].Time.ToString("MM月dd日", Chinese) + "  逐小时气温预报", 640, 503, 1.05, LightBrush, 760);
            for (int i = 0; i < 3; i++)
            {
                double y = top + height * i / 2;
                DrawDottedLine(dc, new Point(left, y), new Point(left + width, y), ChartGridBrush, 9, 0.65);
                DrawDotText(dc, Temperature(maximum - (maximum - minimum) * i / 2) + "°", left - 40, y - 11, 0.9, MutedBrush, 68);
            }
            DateTime date = data.Hours[0].Time.Date;
            double currentHour = (DateTime.UtcNow.AddSeconds(data.UtcOffsetSeconds) - date).TotalHours;
            List<Point> points = new List<Point>();
            foreach (WeatherHour hour in data.Hours)
                points.Add(new Point(left + (hour.Time - date).TotalHours / 23 * width, top + (maximum - hour.Temperature) / (maximum - minimum) * height));
            for (int i = 0; i < points.Count - 1; i++)
            {
                Point a = points[i];
                Point b = points[i + 1];
                Point previous = points[Math.Max(0, i - 1)];
                Point next = points[Math.Min(points.Count - 1, i + 2)];
                int steps = Math.Max(1, (int)Math.Ceiling((b.X - a.X) / 4.5));
                for (int j = 0; j < steps; j++)
                {
                    double t = (double)j / steps;
                    double y = 0.5 * (2 * a.Y + (-previous.Y + b.Y) * t + (2 * previous.Y - 5 * a.Y + 4 * b.Y - next.Y) * t * t + (-previous.Y + 3 * a.Y - 3 * b.Y + next.Y) * t * t * t);
                    y = Math.Max(Math.Min(a.Y, b.Y), Math.Min(Math.Max(a.Y, b.Y), y));
                    double x = a.X + (b.X - a.X) * t;
                    dc.DrawEllipse((x - left) / width * 23 <= currentHour ? SecondsBrush : ForecastBrush, null, new Point(x, y), 1.35, 1.35);
                }
            }
            foreach (Point point in points) dc.DrawEllipse(LightBrush, null, point, 2.2, 2.2);
            if (currentHour >= 0 && currentHour <= 23)
            {
                double x = left + currentHour / 23 * width;
                DrawDottedLine(dc, new Point(x, top - 1), new Point(x, top + height + 2), ChartGridBrush, 7, 0.8);
                dc.DrawEllipse(SecondsBrush, null, new Point(x, top + height + 2), 2.8, 2.8);
            }
            foreach (int hour in new[] { 0, 6, 12, 18, 23 })
                DrawDotText(dc, hour.ToString("00") + ":00", left + hour / 23.0 * width, top + height + 8, 0.9, MutedBrush, 85);
            DrawDotText(dc, "天气数据 Open-Meteo.com · IP 自动定位 · 15 分钟刷新", 640, 711, 0.8, MutedBrush, 900);
        }

        private void DrawText(DrawingContext dc, string value, double x, double y, double size, Brush brush, TextAlignment alignment)
        {
            FormattedText text = new FormattedText(value, Chinese, FlowDirection.LeftToRight, Typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            text.TextAlignment = alignment;
            dc.DrawText(text, new Point(x, y));
        }
    }

    internal static class Styles
    {
        public static ResourceDictionary Create()
        {
            return (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style TargetType='{x:Type Button}'>
    <Setter Property='Background' Value='#162F43'/>
    <Setter Property='Foreground' Value='#82D2F2'/>
    <Setter Property='BorderBrush' Value='#385669'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='15,8'/>
    <Setter Property='FontFamily' Value='Microsoft YaHei UI'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Button}'>
          <Border x:Name='frame' CornerRadius='8' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' Padding='{TemplateBinding Padding}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='frame' Property='Background' Value='#2A5369'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='frame' Property='Background' Value='#376F7A'/></Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='frame' Property='BorderBrush' Value='#B6F5F0'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='frame' Property='Opacity' Value='0.4'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='GlassActionButton' TargetType='{x:Type Button}' BasedOn='{StaticResource {x:Type Button}}'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Foreground' Value='#82D2F2'/>
    <Setter Property='BorderBrush' Value='Transparent'/>
    <Setter Property='BorderThickness' Value='0'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Button}'>
          <Border Background='Transparent' Padding='{TemplateBinding Padding}'>
            <ContentPresenter x:Name='label' HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter Property='Foreground' Value='#82D2F2'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='label' Property='Opacity' Value='0.6'/></Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter Property='Foreground' Value='#82D2F2'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='label' Property='Opacity' Value='0.4'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='{x:Type CheckBox}'>
    <Setter Property='Foreground' Value='#82D2F2'/>
    <Setter Property='FontFamily' Value='Microsoft YaHei UI'/>
    <Setter Property='FontSize' Value='14'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type CheckBox}'>
          <Grid Background='Transparent'>
            <Grid.ColumnDefinitions><ColumnDefinition Width='22'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
            <Border x:Name='box' Width='19' Height='19' CornerRadius='5' Background='#1410151C' BorderBrush='#66C8E1EA' BorderThickness='1' VerticalAlignment='Top' Margin='0,2,0,0'>
              <Path x:Name='check' Data='M 3,8 L 6.5,11.5 L 13,4.5' Stroke='#82D2F2' StrokeThickness='1.8' Visibility='Collapsed'/>
            </Border>
            <ContentPresenter Grid.Column='1' Margin='12,0,0,0' VerticalAlignment='Center'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='check' Property='Visibility' Value='Visible'/>
              <Setter TargetName='box' Property='Background' Value='#1AB4F0EC'/>
              <Setter TargetName='box' Property='BorderBrush' Value='#99B4F0EC'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='box' Property='BorderBrush' Value='#E3FFFF'/></Trigger>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='box' Property='BorderBrush' Value='#E3FFFF'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>");
        }

        public static TextBlock Text(string value, double size, string color)
        {
            return new TextBlock { Text = value, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = size, Foreground = Palette.Solid(color) };
        }

        public static Button Button(string text, string name, RoutedEventHandler clicked)
        {
            Button button = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(button, name);
            button.ToolTip = name;
            button.Click += clicked;
            return button;
        }
    }

    internal sealed class SettingsWindow : Window
    {
        public SettingsWindow(ClockSettings settings, Action changed)
        {
            Title = "GlassDotClock · 设置";
            Width = 482;
            Height = 570;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.Transparent;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Resources.Add(typeof(Button), FindResource("GlassActionButton"));
            SourceInitialized += delegate { Native.ApplyFrame(new WindowInteropHelper(this).Handle); };
            Loaded += delegate { Native.ApplyGlass(this, true); };

            Grid root = new Grid { Margin = new Thickness(30, 16, 30, 24) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid header = new Grid { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 16) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock caption = Styles.Text("时钟设置", 14, "#82D2F2");
            caption.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(caption);
            Button close = Styles.Button("×", "关闭设置", delegate { Close(); });
            close.Padding = new Thickness(10, 6, 10, 6);
            Grid.SetColumn(close, 1);
            header.Children.Add(close);
            header.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { DragMove(); e.Handled = true; };
            root.Children.Add(header);
            StackPanel body = new StackPanel();
            Grid.SetRow(body, 1);
            TextBlock title = Styles.Text("让时间，按你的方式流动。", 21, "#82D2F2");
            body.Children.Add(title);
            TextBlock subtitle = Styles.Text("时钟显示设置", 11, "#82D2F2");
            subtitle.Margin = new Thickness(0, 9, 0, 18);
            body.Children.Add(subtitle);

            CheckBox hour = Option("24 小时制", "关闭后显示 12 小时制及上午 / 下午", settings.Use24Hour);
            CheckBox seconds = Option("显示秒钟", "让点阵记录每一秒的流动", settings.ShowSeconds);
            CheckBox date = Option("显示日期", "同时显示年月日与星期", settings.ShowDate);
            CheckBox motion = Option("冒号呼吸动画", "柔和闪烁的分隔点；关闭可减少动画", settings.Motion);
            body.Children.Add(hour);
            body.Children.Add(seconds);
            body.Children.Add(date);
            body.Children.Add(motion);

            TextBlock tip = Styles.Text("设为自动屏保：右键 GlassDotClock.scr → 安装，然后选择等待时间。锁屏保护由 Windows 的“在恢复时显示登录屏幕”选项控制。", 11, "#82D2F2");
            tip.TextWrapping = TextWrapping.Wrap;
            tip.LineHeight = 19;
            tip.Margin = new Thickness(0, 20, 0, 0);
            body.Children.Add(tip);
            root.Children.Add(body);

            Grid bottom = new Grid();
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button system = Styles.Button("系统屏保设置", "打开 Windows 屏幕保护程序设置", delegate
            {
                Process.Start(new ProcessStartInfo("control.exe", "desk.cpl,,@screensaver") { UseShellExecute = true });
            });
            system.Margin = new Thickness(0);
            system.HorizontalAlignment = HorizontalAlignment.Left;
            bottom.Children.Add(system);
            Button cancel = Styles.Button("取消", "取消设置", delegate { Close(); });
            cancel.IsCancel = true;
            Grid.SetColumn(cancel, 1);
            bottom.Children.Add(cancel);
            Button save = Styles.Button("保存", "保存设置", delegate
            {
                settings.Use24Hour = hour.IsChecked == true;
                settings.ShowSeconds = seconds.IsChecked == true;
                settings.ShowDate = date.IsChecked == true;
                settings.Motion = motion.IsChecked == true;
                bool saved = settings.Save();
                if (changed != null) changed();
                if (!saved) MessageBox.Show(this, "设置已应用，但无法保存到本机。请检查本地应用数据目录的写入权限。", "GlassDotClock", MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            });
            save.IsDefault = true;
            save.Foreground = Palette.Solid("#82D2F2");
            save.FontWeight = FontWeights.SemiBold;
            Grid.SetColumn(save, 2);
            bottom.Children.Add(save);
            Grid.SetRow(bottom, 2);
            root.Children.Add(bottom);
            Grid glass = new Grid { Background = Palette.Linear("#12FFFFFF", "#20182432", new Point(0, 0), new Point(0.6, 1)) };
            glass.Children.Add(new Border
            {
                BorderBrush = Palette.Linear("#A6FFFFFF", "#24FFFFFF", new Point(0, 0), new Point(0.8, 1)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                IsHitTestVisible = false
            });
            glass.Children.Add(root);
            Content = glass;
        }

        private static CheckBox Option(string label, string description, bool value)
        {
            StackPanel content = new StackPanel();
            content.Children.Add(Styles.Text(label, 14, "#82D2F2"));
            TextBlock detail = Styles.Text(description, 11, "#82D2F2");
            detail.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(detail);
            CheckBox option = new CheckBox { Content = content, IsChecked = value, Margin = new Thickness(0, 12, 0, 6) };
            AutomationProperties.SetName(option, label);
            return option;
        }
    }

    internal sealed class ClockWindow : Window
    {
        private readonly DesktopSession session;
        private readonly bool saver;
        private readonly Forms.Screen screen;
        private Rect normalBounds;
        private bool visuallyMaximized;
        public readonly ClockSurface Surface;

        public ClockWindow(DesktopSession session, ClockSettings settings, bool saver, Forms.Screen screen)
        {
            this.session = session;
            this.saver = saver;
            this.screen = screen;
            Title = saver ? "GlassDotClock · 屏保" : "GlassDotClock";
            Width = 1120;
            Height = 720;
            MinWidth = saver ? 0 : 800;
            MinHeight = saver ? 0 : 520;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStartupLocation = saver ? WindowStartupLocation.Manual : WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = saver;
            Surface = new ClockSurface(settings, saver, false, session.Weather);
            Grid root = new Grid();
            root.Children.Add(Surface);
            if (saver)
            {
                Cursor = Cursors.None;
                PreviewMouseDown += delegate { session.EndSaver(); };
            }
            else
            {
                Grid header = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(34, 23, 24, 0), Background = Brushes.Transparent };
                header.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
                {
                    if (e.ClickCount == 2) ToggleMaximize();
                    else DragMove();
                    e.Handled = true;
                };
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                TextBlock name = Styles.Text("GlassDotClock", 16, "#82D2F2");
                name.FontWeight = FontWeights.SemiBold;
                name.VerticalAlignment = VerticalAlignment.Center;
                header.Children.Add(name);
                StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                actions.Resources.Add(typeof(Button), FindResource("GlassActionButton"));
                actions.Children.Add(Styles.Button("进入屏保", "进入全屏屏保 (F11)", delegate { session.StartSaver(); }));
                actions.Children.Add(Styles.Button("设置", "时钟设置", delegate
                {
                    SettingsWindow dialog = new SettingsWindow(settings, Surface.RefreshSettings);
                    dialog.Owner = this;
                    dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    dialog.ShowDialog();
                }));
                actions.Children.Add(Styles.Button("—", "最小化", delegate { WindowState = WindowState.Minimized; }));
                actions.Children.Add(Styles.Button("×", "关闭时钟", delegate { Close(); }));
                Grid.SetColumn(actions, 1);
                header.Children.Add(actions);
                root.Children.Add(header);
                Grid footer = new Grid { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(34, 0, 34, 25), IsHitTestVisible = false };
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                footer.Children.Add(Styles.Text("F11  进入屏保      /      Esc  退出", 11, "#82D2F2"));
                TextBlock local = Styles.Text("时钟离线运行 · 天气联网更新", 11, "#82D2F2");
                Grid.SetColumn(local, 1);
                footer.Children.Add(local);
                root.Children.Add(footer);
            }
            Content = root;
            SourceInitialized += delegate
            {
                if (saver) PlaceOnScreen();
                Native.ApplyFrame(new WindowInteropHelper(this).Handle, !saver);
            };
            Loaded += delegate
            {
                if (saver) PlaceOnScreen();
                Native.ApplyGlass(this, !saver);
            };
            PreviewKeyDown += OnKey;
            Closed += delegate
            {
                if (saver) session.EndSaver();
                else session.CloseApplication();
            };
        }

        private void ToggleMaximize()
        {
            if (visuallyMaximized)
            {
                Left = normalBounds.Left;
                Top = normalBounds.Top;
                Width = normalBounds.Width;
                Height = normalBounds.Height;
                visuallyMaximized = false;
            }
            else
            {
                normalBounds = new Rect(Left, Top, ActualWidth, ActualHeight);
                IntPtr handle = new WindowInteropHelper(this).Handle;
                Forms.Screen target = Forms.Screen.FromHandle(handle);
                System.Drawing.Rectangle area = target.WorkingArea;
                Native.SetWindowPos(handle, IntPtr.Zero, area.Left, area.Top, area.Width, area.Height, 0x0004 | 0x0010);
                visuallyMaximized = true;
            }
            Surface.RoundedGlass = !visuallyMaximized;
            Surface.InvalidateVisual();
            Native.ApplyFrame(new WindowInteropHelper(this).Handle, !visuallyMaximized);
        }

        private void PlaceOnScreen()
        {
            System.Drawing.Rectangle bounds = screen.Bounds;
            Native.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040 | 0x0010);
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (saver)
            {
                e.Handled = true;
                session.EndSaver();
            }
            else if (e.Key == Key.F11)
            {
                e.Handled = true;
                session.StartSaver();
            }
            else if (e.Key == Key.Escape) Close();
        }
    }

    internal sealed class TrayMenuRenderer : Forms.ToolStripRenderer
    {
        private static readonly System.Drawing.Color MenuColor = System.Drawing.Color.FromArgb(244, 244, 244);
        private static readonly System.Drawing.Color BorderColor = System.Drawing.Color.FromArgb(205, 205, 205);
        private static readonly System.Drawing.Color HoverColor = System.Drawing.Color.FromArgb(225, 225, 225);
        private static readonly System.Drawing.Color TextColor = System.Drawing.Color.FromArgb(7, 193, 96);
        private static readonly System.Drawing.Color DisabledTextColor = System.Drawing.Color.FromArgb(100, 7, 193, 96);
        private static readonly System.Drawing.Color SeparatorColor = System.Drawing.Color.FromArgb(183, 183, 183);

        internal static System.Drawing.Drawing2D.GraphicsPath RoundedPath(System.Drawing.Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            System.Drawing.Drawing2D.GraphicsPath path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            System.Drawing.Rectangle bounds = new System.Drawing.Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (System.Drawing.Drawing2D.GraphicsPath path = RoundedPath(bounds, 13))
            using (System.Drawing.SolidBrush brush = new System.Drawing.SolidBrush(MenuColor))
                e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            System.Drawing.Rectangle bounds = new System.Drawing.Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (System.Drawing.Drawing2D.GraphicsPath path = RoundedPath(bounds, 13))
            using (System.Drawing.Pen pen = new System.Drawing.Pen(BorderColor))
                e.Graphics.DrawPath(pen, path);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            System.Drawing.Rectangle bounds = new System.Drawing.Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using (System.Drawing.Drawing2D.GraphicsPath path = RoundedPath(bounds, 8))
            using (System.Drawing.SolidBrush brush = new System.Drawing.SolidBrush(HoverColor))
                e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            System.Drawing.Color color = e.Item.Enabled ? TextColor : DisabledTextColor;
            System.Drawing.Rectangle textBounds = new System.Drawing.Rectangle(10, 0, Math.Max(0, e.Item.Width - 44), e.Item.Height);
            Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, textBounds, color,
                Forms.TextFormatFlags.Left | Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine |
                Forms.TextFormatFlags.EndEllipsis | Forms.TextFormatFlags.NoPrefix);

            Forms.ToolStripMenuItem item = e.Item as Forms.ToolStripMenuItem;
            if (item == null || !item.Checked) return;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int x = e.Item.Width - 18;
            int y = e.Item.Height / 2;
            using (System.Drawing.Pen pen = new System.Drawing.Pen(TextColor, 1.7f))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                e.Graphics.DrawLines(pen, new[] {
                    new System.Drawing.Point(x - 7, y),
                    new System.Drawing.Point(x - 3, y + 4),
                    new System.Drawing.Point(x + 4, y - 4)
                });
            }
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e) { }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (System.Drawing.Pen pen = new System.Drawing.Pen(SeparatorColor))
                e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
    }

    internal sealed class TrayContextMenu : Forms.ContextMenuStrip
    {
        private readonly System.Drawing.Font menuFont;

        public TrayContextMenu()
        {
            menuFont = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            Font = menuFont;
            BackColor = System.Drawing.Color.FromArgb(244, 244, 244);
            ForeColor = System.Drawing.Color.FromArgb(18, 18, 18);
            Renderer = new TrayMenuRenderer();
            ShowImageMargin = false;
            ShowCheckMargin = false;
            DropShadowEnabled = true;
            Padding = new Forms.Padding(4, 6, 4, 6);
            MinimumSize = new System.Drawing.Size(132, 0);
        }

        protected override void OnOpened(EventArgs e)
        {
            ApplyRoundedRegion();
            base.OnOpened(e);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ApplyRoundedRegion();
        }

        private void ApplyRoundedRegion()
        {
            if (Width < 2 || Height < 2) return;
            System.Drawing.Region previous = Region;
            using (System.Drawing.Drawing2D.GraphicsPath path = TrayMenuRenderer.RoundedPath(new System.Drawing.Rectangle(0, 0, Width, Height), 13))
                Region = new System.Drawing.Region(path);
            if (previous != null) previous.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                System.Drawing.Region current = Region;
                Region = null;
                if (current != null) current.Dispose();
            }
            base.Dispose(disposing);
            if (disposing) menuFont.Dispose();
        }
    }

    internal sealed class DesktopSession
    {
        private const string AutoStartKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AutoStartName = "GlassDotClock";
        private readonly Application application;
        private readonly ClockSettings settings;
        internal readonly WeatherService Weather;
        private readonly List<ClockWindow> screens = new List<ClockWindow>();
        private readonly DispatcherTimer mouseTimer = new DispatcherTimer();
        private readonly Stopwatch sinceStart = new Stopwatch();
        private ClockWindow main;
        private Forms.NotifyIcon tray;
        private Forms.ContextMenuStrip trayMenu;
        private Forms.ToolStripMenuItem autoStartItem;
        private System.Drawing.Icon trayIcon;
        private Native.Point anchor;
        private bool saving;
        private bool closing;

        public DesktopSession(Application application, ClockSettings settings)
        {
            this.application = application;
            this.settings = settings;
            Weather = new WeatherService(application.Dispatcher);
            application.Exit += delegate
            {
                DisposeTray();
                Weather.Dispose();
            };
            Weather.Start();
            mouseTimer.Interval = TimeSpan.FromMilliseconds(100);
            mouseTimer.Tick += delegate
            {
                Native.Point current;
                if (!Native.GetCursorPos(out current)) return;
                // 忽略启动时的鼠标抖动，之后累计位移超过阈值才退出。
                if (sinceStart.ElapsedMilliseconds < 1200) { anchor = current; return; }
                if (Math.Abs((long)current.X - anchor.X) > 6 || Math.Abs((long)current.Y - anchor.Y) > 6)
                    EndSaver();
            };
        }

        public void ShowWindow()
        {
            main = new ClockWindow(this, settings, false, null);
            application.MainWindow = main;
            CreateTray();
            main.Show();
        }

        private void CreateTray()
        {
            if (tray != null) return;
            autoStartItem = new Forms.ToolStripMenuItem("开机自启");
            autoStartItem.Checked = IsAutoStartEnabled();
            autoStartItem.Click += delegate
            {
                bool enabled = !autoStartItem.Checked;
                if (SetAutoStart(enabled)) autoStartItem.Checked = enabled;
                else MessageBox.Show("无法修改开机自启设置，请检查当前账户的注册表权限。", "GlassDotClock", MessageBoxButton.OK, MessageBoxImage.Warning);
            };
            autoStartItem.AutoSize = false;
            autoStartItem.Size = new System.Drawing.Size(124, 36);
            autoStartItem.Margin = Forms.Padding.Empty;
            Forms.ToolStripMenuItem closeItem = new Forms.ToolStripMenuItem("关闭");
            closeItem.AutoSize = false;
            closeItem.Size = new System.Drawing.Size(124, 36);
            closeItem.Margin = Forms.Padding.Empty;
            closeItem.Click += delegate { CloseApplication(); };
            Forms.ToolStripSeparator separator = new Forms.ToolStripSeparator();
            separator.AutoSize = false;
            separator.Size = new System.Drawing.Size(124, 9);
            separator.Margin = Forms.Padding.Empty;
            trayMenu = new TrayContextMenu();
            trayMenu.Items.Add(autoStartItem);
            trayMenu.Items.Add(separator);
            trayMenu.Items.Add(closeItem);
            trayMenu.Opening += delegate { autoStartItem.Checked = IsAutoStartEnabled(); };

            trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            if (trayIcon == null) trayIcon = (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
            tray = new Forms.NotifyIcon();
            tray.Icon = trayIcon;
            tray.Text = "GlassDotClock · 双击显示窗口";
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { ShowMainWindow(); };
            tray.Visible = true;
        }

        private void ShowMainWindow()
        {
            if (closing || main == null) return;
            if (saving) EndSaver();
            if (!main.IsVisible) main.Show();
            if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
            main.Activate();
        }

        private static string AutoStartCommand
        {
            get { return "\"" + Assembly.GetExecutingAssembly().Location + "\""; }
        }

        private static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AutoStartKey, false))
                {
                    object value = key == null ? null : key.GetValue(AutoStartName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    return value != null && string.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), AutoStartCommand, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        private static bool SetAutoStart(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AutoStartKey))
                {
                    if (key == null) return false;
                    if (enabled) key.SetValue(AutoStartName, AutoStartCommand, RegistryValueKind.String);
                    else key.DeleteValue(AutoStartName, false);
                    return true;
                }
            }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        private void DisposeTray()
        {
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
            if (trayMenu != null)
            {
                trayMenu.Dispose();
                trayMenu = null;
            }
            autoStartItem = null;
            if (trayIcon != null)
            {
                trayIcon.Dispose();
                trayIcon = null;
            }
        }

        public void StartSaver()
        {
            if (saving || closing) return;
            saving = true;
            Native.GetCursorPos(out anchor);
            sinceStart.Restart();
            if (main != null) main.Hide();
            ClockWindow primary = null;
            foreach (Forms.Screen screen in Forms.Screen.AllScreens)
            {
                ClockWindow window = new ClockWindow(this, settings, true, screen);
                if (main != null) window.Surface.SetWeatherExpanded(main.Surface.IsWeatherExpanded);
                screens.Add(window);
                window.Show();
                if (screen.Primary) primary = window;
            }
            if (primary != null) primary.Activate();
            mouseTimer.Start();
        }

        public void EndSaver()
        {
            if (!saving) return;
            saving = false;
            mouseTimer.Stop();
            sinceStart.Stop();
            foreach (ClockWindow window in screens.ToArray()) window.Close();
            screens.Clear();
            if (closing) return;
            if (main == null) application.Shutdown();
            else { main.Show(); main.Activate(); }
        }

        public void CloseApplication()
        {
            if (closing) return;
            closing = true;
            mouseTimer.Stop();
            DisposeTray();
            application.Shutdown();
        }
    }

    internal sealed class PreviewHost : IDisposable
    {
        private readonly IntPtr parent;
        private readonly HwndSource source;
        private readonly DispatcherTimer timer;
        private readonly Application application;
        private int width;
        private int height;

        public PreviewHost(IntPtr parent, ClockSettings settings, Application application)
        {
            this.parent = parent;
            this.application = application;
            Native.Rect rect;
            Native.GetClientRect(parent, out rect);
            width = Math.Max(1, rect.Right - rect.Left);
            height = Math.Max(1, rect.Bottom - rect.Top);
            HwndSourceParameters parameters = new HwndSourceParameters("GlassDotClockPreview");
            parameters.ParentWindow = parent;
            parameters.WindowStyle = unchecked((int)0x50000000); // WS_CHILD | WS_VISIBLE
            parameters.Width = width;
            parameters.Height = height;
            source = new HwndSource(parameters);
            source.CompositionTarget.BackgroundColor = Palette.Color("#171D25");
            source.RootVisual = new ClockSurface(settings, true, true, null);
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += ResizeOrClose;
            timer.Start();
        }

        private void ResizeOrClose(object sender, EventArgs e)
        {
            if (!Native.IsWindow(parent))
            {
                timer.Stop();
                application.Shutdown();
                return;
            }
            Native.Rect rect;
            if (!Native.GetClientRect(parent, out rect)) return;
            int newWidth = Math.Max(1, rect.Right - rect.Left);
            int newHeight = Math.Max(1, rect.Bottom - rect.Top);
            if (newWidth == width && newHeight == height) return;
            width = newWidth;
            height = newHeight;
            Native.SetWindowPos(source.Handle, IntPtr.Zero, 0, 0, width, height, 0x0004 | 0x0010);
        }

        public void Dispose()
        {
            timer.Stop();
            source.Dispose();
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")]
        internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")]
        internal static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [StructLayout(LayoutKind.Sequential)]
        private struct Margins { public int Left; public int Right; public int Top; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy { public int State; public int Flags; public int Color; public int Animation; }
        [StructLayout(LayoutKind.Sequential)]
        private struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
        [DllImport("user32.dll")]
        private static extern bool SetWindowCompositionAttribute(IntPtr window, ref CompositionData data);

        private static void ApplyFrameCore(IntPtr window, bool rounded)
        {
            int dark = 1;
            int round = rounded ? 2 : 1;
            DwmSetWindowAttribute(window, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(window, 33, ref round, sizeof(int));
        }

        private static bool SetAccent(IntPtr handle, int state)
        {
            AccentPolicy accent = new AccentPolicy { State = state, Flags = 0, Color = 0 };
            int size = Marshal.SizeOf(typeof(AccentPolicy));
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, pointer, false);
                CompositionData data = new CompositionData { Attribute = 19, Data = pointer, Size = size };
                return SetWindowCompositionAttribute(handle, ref data);
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }

        internal static void ApplyFrame(IntPtr window, bool rounded = true)
        {
            ApplyFrameCore(window, rounded);
        }

        internal static void ApplyGlass(Window window, bool rounded)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            HwndSource source = HwndSource.FromHwnd(handle);
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            ApplyFrame(handle, rounded);
            int backdrop = 1; // DWMSBT_NONE
            DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
            SetAccent(handle, 0); // 完全关闭模糊，背景清晰实时透出。
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string argument = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            string mode = argument.Split(':')[0].Replace('-', '/');
            string handleValue = argument.IndexOf(':') >= 0 ? argument.Substring(argument.IndexOf(':') + 1) : (args.Length > 1 ? args[1] : "");
            bool isScreenSaverFile = string.Equals(Path.GetExtension(Assembly.GetExecutingAssembly().Location), ".scr", StringComparison.OrdinalIgnoreCase);
            IntPtr parent = IntPtr.Zero;
            if (mode == "/p")
            {
                long value;
                if (!long.TryParse(handleValue, out value) || value == 0 || (IntPtr.Size == 4 && (value > int.MaxValue || value < int.MinValue))) return;
                parent = new IntPtr(value);
                if (!Native.IsWindow(parent)) return;
            }
            Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources = Styles.Create();
            ClockSettings settings = ClockSettings.Load();
            if (mode == "/p")
            {
                using (PreviewHost preview = new PreviewHost(parent, settings, application)) application.Run();
            }
            else if (mode == "/c")
            {
                SettingsWindow dialog = new SettingsWindow(settings, null);
                dialog.Closed += delegate { application.Shutdown(); };
                application.Run(dialog);
            }
            else
            {
                DesktopSession session = new DesktopSession(application, settings);
                if (mode == "/s" || (argument.Length == 0 && isScreenSaverFile)) session.StartSaver();
                else session.ShowWindow();
                application.Run();
            }
        }
    }
}
