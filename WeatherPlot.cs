using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace WeatherPlot
{
    public class ForecastPoint
    {
        public string Time { get; set; }
        public double Temperature { get; set; }
        public string WindSpeed { get; set; }
        public string WindDirection { get; set; }
        public string Forecast { get; set; }
    }

    public class LocationSeries
    {
        public string Name;
        public DateTime[] Times;
        public double[] Temperatures;
        public double[] WindSpeeds;     // parsed from "X mph"
        public ForecastPoint[] Points;
        public Color Color;
        public bool Visible = true;     // toggleable from legend
    }

    public static class WindParser
    {
        public static double ParseMph(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            string trimmed = s.Trim();
            // Examples: "12 mph", "10 to 15 mph", "0 mph"
            var sb = new StringBuilder();
            foreach (var c in trimmed)
            {
                if (char.IsDigit(c) || c == '.' || c == '-') sb.Append(c);
                else break;
            }
            if (sb.Length == 0) return 0;
            double v;
            if (double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out v))
                return v;
            return 0;
        }
    }

    public class ChartPanel : Panel
    {
        public List<LocationSeries> Series = new List<LocationSeries>();
        public bool ShowTemperature = true;
        public bool ShowWindSpeed = true;

        private const int PadLeft = 70;
        private const int PadTop = 50;
        private const int PadBottom = 60;
        private const int LegendWidth = 230;
        private const int WindAxisWidth = 56;

        private DateTime tMin, tMax;       // current visible (view) range
        private DateTime tMinData, tMaxData; // full data range
        public bool ZoomedIn { get; private set; }
        public event EventHandler ZoomChanged;
        private double yMin, yMax;     // temperature
        private double wMin, wMax;     // wind speed
        private Rectangle plotRect;

        // Hover state
        private LocationSeries hoverSeries;
        private int hoverIndex = -1;
        private Point hoverPoint;
        private bool hoverIsWind;

        // Legend interactivity
        private readonly List<RectangleF> legendRowRects = new List<RectangleF>();

        public ChartPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.FromArgb(24, 26, 32);
            // Make the panel focusable so it receives MouseWheel events (WinForms routes
            // MouseWheel to the focused control, not the one under the cursor).
            SetStyle(ControlStyles.Selectable, true);
            TabStop = false; // don't reach via Tab; only via mouse enter below

            this.MouseMove += OnMouseMoveChart;
            this.MouseEnter += (s, e) => { if (!Focused) Focus(); };
            this.MouseLeave += (s, e) => { hoverSeries = null; hoverIndex = -1; Invalidate(); };
        }

        public void SetData(List<LocationSeries> series)
        {
            Series = series;
            RecomputeBounds();
            Invalidate();
        }

        public void RefreshChart()
        {
            RecomputeBounds();
            Invalidate();
        }

        private IEnumerable<LocationSeries> Visibles
        {
            get { return Series.Where(s => s.Visible); }
        }

        private void RecomputeBounds()
        {
            if (Series == null || Series.Count == 0) return;

            // 1) Full data range (X)
            tMinData = DateTime.MaxValue; tMaxData = DateTime.MinValue;
            bool haveAnyVisible = false;
            foreach (var s in Visibles)
            {
                haveAnyVisible = true;
                for (int i = 0; i < s.Times.Length; i++)
                {
                    if (s.Times[i] < tMinData) tMinData = s.Times[i];
                    if (s.Times[i] > tMaxData) tMaxData = s.Times[i];
                }
            }
            if (!haveAnyVisible) return;

            // 2) Visible window: if not zoomed, mirror data range. If zoomed, clamp to data.
            if (!ZoomedIn)
            {
                tMin = tMinData;
                tMax = tMaxData;
            }
            else
            {
                if (tMin < tMinData) tMin = tMinData;
                if (tMax > tMaxData) tMax = tMaxData;
                if (tMin >= tMax) { tMin = tMinData; tMax = tMaxData; ZoomedIn = false; }
            }

            // 3) Y/Wind bounds based on points currently in view
            RecomputeYBounds();
        }

        private void RecomputeYBounds()
        {
            yMin = double.PositiveInfinity; yMax = double.NegativeInfinity;
            wMin = 0; wMax = double.NegativeInfinity;

            foreach (var s in Visibles)
            {
                for (int i = 0; i < s.Times.Length; i++)
                {
                    if (s.Times[i] < tMin || s.Times[i] > tMax) continue;
                    if (ShowTemperature)
                    {
                        if (s.Temperatures[i] < yMin) yMin = s.Temperatures[i];
                        if (s.Temperatures[i] > yMax) yMax = s.Temperatures[i];
                    }
                    if (ShowWindSpeed)
                    {
                        if (s.WindSpeeds[i] > wMax) wMax = s.WindSpeeds[i];
                    }
                }
            }

            if (ShowTemperature && yMin != double.PositiveInfinity)
            {
                double range = yMax - yMin;
                if (range < 1) range = 1;
                yMin = Math.Floor((yMin - range * 0.05) / 5.0) * 5.0;
                yMax = Math.Ceiling((yMax + range * 0.05) / 5.0) * 5.0;
            }
            if (ShowWindSpeed)
            {
                if (wMax == double.NegativeInfinity) wMax = 10;
                if (wMax < 10) wMax = 10;
                wMax = Math.Ceiling((wMax * 1.10) / 5.0) * 5.0;
                wMin = 0;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (Series.Count == 0) return;
            if (!plotRect.Contains(e.Location)) return;

            double viewSec = (tMax - tMin).TotalSeconds;
            if (viewSec <= 0) return;

            // Anchor: time under the cursor stays put after zoom
            double frac = (e.X - plotRect.Left) / (double)plotRect.Width;
            if (frac < 0) frac = 0; else if (frac > 1) frac = 1;
            var timeAtCursor = tMin.AddSeconds(frac * viewSec);

            // Scroll up (positive Delta) zooms in; down zooms out
            double zoomFactor = e.Delta > 0 ? (1.0 / 1.25) : 1.25;
            double newSec = viewSec * zoomFactor;

            double dataSec = (tMaxData - tMinData).TotalSeconds;
            if (newSec > dataSec) newSec = dataSec;
            const double minSec = 3600.0; // don't zoom in tighter than 1 hour
            if (newSec < minSec) newSec = minSec;

            var newMin = timeAtCursor.AddSeconds(-frac * newSec);
            var newMax = newMin.AddSeconds(newSec);

            if (newMin < tMinData) { newMin = tMinData; newMax = newMin.AddSeconds(newSec); }
            if (newMax > tMaxData) { newMax = tMaxData; newMin = newMax.AddSeconds(-newSec); }
            if (newMin < tMinData) newMin = tMinData;

            tMin = newMin;
            tMax = newMax;
            bool wasZoomed = ZoomedIn;
            ZoomedIn = (tMax - tMin).TotalSeconds < dataSec - 1;
            RecomputeYBounds();
            Invalidate();

            if (wasZoomed != ZoomedIn && ZoomChanged != null) ZoomChanged(this, EventArgs.Empty);
        }

        public void ResetZoom()
        {
            if (Series.Count == 0) return;
            tMin = tMinData;
            tMax = tMaxData;
            bool wasZoomed = ZoomedIn;
            ZoomedIn = false;
            RecomputeYBounds();
            Invalidate();
            if (wasZoomed && ZoomChanged != null) ZoomChanged(this, EventArgs.Empty);
        }

        // Render the chart to an in-memory Bitmap at the current panel size.
        // Used by the "Export PDF" feature in MainForm — re-runs OnPaint against a memory
        // Graphics so the snapshot exactly matches what's on screen (minus the hover tooltip,
        // which would be visual noise in an exported document).
        public Bitmap RenderToBitmap()
        {
            int w = Math.Max(ClientSize.Width, 100);
            int h = Math.Max(ClientSize.Height, 100);
            var bmp = new Bitmap(w, h);
            using (var g = Graphics.FromImage(bmp))
            {
                using (var bg = new SolidBrush(BackColor))
                    g.FillRectangle(bg, 0, 0, w, h);

                // Suppress hover state during the snapshot so the rendered output is clean.
                var savedHoverSeries = hoverSeries;
                var savedHoverIndex = hoverIndex;
                hoverSeries = null;
                hoverIndex = -1;
                try
                {
                    using (var pe = new PaintEventArgs(g, new Rectangle(0, 0, w, h)))
                    {
                        OnPaint(pe);
                    }
                }
                finally
                {
                    hoverSeries = savedHoverSeries;
                    hoverIndex = savedHoverIndex;
                }
            }
            return bmp;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try
            {
                base.OnPaint(e);
                PaintInner(e);
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "crash.log"),
                        string.Format("[{0:HH:mm:ss.fff}] OnPaint threw: {1}{2}",
                            DateTime.Now, ex, Environment.NewLine));
                }
                catch { }
            }
        }

        private void PaintInner(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int rightPad = LegendWidth + (ShowWindSpeed ? WindAxisWidth : 0);
            plotRect = new Rectangle(
                PadLeft, PadTop,
                Math.Max(50, ClientSize.Width - PadLeft - rightPad),
                Math.Max(50, ClientSize.Height - PadTop - PadBottom));

            using (var titleFont = new Font("Segoe UI Semibold", 14f))
            using (var subFont = new Font("Segoe UI", 9f))
            using (var titleBrush = new SolidBrush(Color.FromArgb(235, 235, 240)))
            using (var subBrush = new SolidBrush(Color.FromArgb(160, 165, 175)))
            {
                string title = "Weather Forecast — ";
                if (ShowTemperature && ShowWindSpeed) title += "Temperature & Wind by Location";
                else if (ShowTemperature)               title += "Temperature by Location";
                else if (ShowWindSpeed)                 title += "Wind Speed by Location";
                else                                    title += "(no series selected)";
                g.DrawString(title, titleFont, titleBrush, 12, 8);

                int visCount = Visibles.Count();
                string subtitle;
                if (visCount == 0)
                    subtitle = "No locations visible. Click a row in the legend to enable.";
                else
                {
                    int viewHours = (int)(tMax - tMin).TotalHours;
                    string zoomNote = "";
                    if (ZoomedIn)
                    {
                        int totalHours = (int)(tMaxData - tMinData).TotalHours;
                        zoomNote = string.Format("  [zoomed: {0} of {1} h]", viewHours, totalHours);
                    }
                    subtitle = string.Format("{0:MMM d, h:mm tt}  →  {1:MMM d, h:mm tt}   ({2} hours, {3} location{4}){5}",
                        tMin, tMax, viewHours, visCount, visCount == 1 ? "" : "s", zoomNote);
                }
                g.DrawString(subtitle, subFont, subBrush, 14, 30);
            }

            if (Series.Count == 0)
            {
                using (var f = new Font("Segoe UI", 11f))
                using (var b = new SolidBrush(Color.FromArgb(160, 165, 175)))
                    g.DrawString("No data loaded. Click Refresh to fetch current forecast.", f, b, PadLeft, PadTop + 20);
                return;
            }

            DrawPlotBackground(g);
            if (Visibles.Any() && (ShowTemperature || ShowWindSpeed))
            {
                if (ShowTemperature) DrawTempAxis(g);
                if (ShowWindSpeed)   DrawWindAxis(g);
                DrawXAxis(g);
                DrawSeries(g);
            }
            DrawLegend(g);
            DrawHoverTooltip(g);
        }

        private void DrawPlotBackground(Graphics g)
        {
            using (var bgBrush = new SolidBrush(Color.FromArgb(32, 35, 42)))
            using (var border = new Pen(Color.FromArgb(110, 115, 125), 1f))
            {
                g.FillRectangle(bgBrush, plotRect);
                g.DrawRectangle(border, plotRect);
            }
        }

        private void DrawTempAxis(Graphics g)
        {
            using (var gridPen = new Pen(Color.FromArgb(45, 50, 60)))
            using (var labelFont = new Font("Segoe UI", 8.5f))
            using (var labelBrush = new SolidBrush(Color.FromArgb(180, 185, 195)))
            using (var axisFont = new Font("Segoe UI", 9f))
            using (var axisBrush = new SolidBrush(Color.FromArgb(220, 225, 235)))
            {
                double yStep = NiceStep((yMax - yMin) / 8.0);
                double yStart = Math.Ceiling(yMin / yStep) * yStep;
                for (double y = yStart; y <= yMax; y += yStep)
                {
                    int py = TempToY(y);
                    g.DrawLine(gridPen, plotRect.Left, py, plotRect.Right, py);
                    string lbl = ((int)Math.Round(y)).ToString() + "°";
                    var sz = g.MeasureString(lbl, labelFont);
                    g.DrawString(lbl, labelFont, labelBrush, plotRect.Left - sz.Width - 6, py - sz.Height / 2);
                }

                var ylabel = "Temperature (°F)";
                var state = g.Save();
                g.TranslateTransform(18, plotRect.Top + plotRect.Height / 2f);
                g.RotateTransform(-90);
                var sz2 = g.MeasureString(ylabel, axisFont);
                g.DrawString(ylabel, axisFont, axisBrush, -sz2.Width / 2, -sz2.Height / 2);
                g.Restore(state);
            }
        }

        private void DrawWindAxis(Graphics g)
        {
            using (var tickPen = new Pen(Color.FromArgb(90, 100, 115), 1f))
            using (var labelFont = new Font("Segoe UI", 8.5f))
            using (var labelBrush = new SolidBrush(Color.FromArgb(180, 185, 195)))
            using (var axisFont = new Font("Segoe UI", 9f))
            using (var axisBrush = new SolidBrush(Color.FromArgb(220, 225, 235)))
            {
                double step = NiceStep((wMax - wMin) / 8.0);
                if (step < 1) step = 1;
                for (double w = 0; w <= wMax; w += step)
                {
                    int py = WindToY(w);
                    g.DrawLine(tickPen, plotRect.Right, py, plotRect.Right + 5, py);
                    string lbl = ((int)Math.Round(w)).ToString();
                    var sz = g.MeasureString(lbl, labelFont);
                    g.DrawString(lbl, labelFont, labelBrush, plotRect.Right + 8, py - sz.Height / 2);
                }

                var ylabel = "Wind Speed (mph)";
                var state = g.Save();
                g.TranslateTransform(plotRect.Right + WindAxisWidth - 4, plotRect.Top + plotRect.Height / 2f);
                g.RotateTransform(90);
                var sz2 = g.MeasureString(ylabel, axisFont);
                g.DrawString(ylabel, axisFont, axisBrush, -sz2.Width / 2, -sz2.Height / 2);
                g.Restore(state);
            }
        }

        private void DrawXAxis(Graphics g)
        {
            using (var gridPen = new Pen(Color.FromArgb(45, 50, 60)))
            using (var dayPen = new Pen(Color.FromArgb(70, 80, 95)))
            using (var labelFont = new Font("Segoe UI", 8.5f))
            using (var labelBrush = new SolidBrush(Color.FromArgb(180, 185, 195)))
            using (var axisFont = new Font("Segoe UI", 9f))
            using (var axisBrush = new SolidBrush(Color.FromArgb(220, 225, 235)))
            {
                DateTime tStart = new DateTime(tMin.Year, tMin.Month, tMin.Day, tMin.Hour, 0, 0);
                if (tStart < tMin) tStart = tStart.AddHours(1);
                double totalHours = (tMax - tMin).TotalHours;
                int hourStep = totalHours <= 24 ? 2 : totalHours <= 72 ? 6 : 12;
                tStart = tStart.AddHours((hourStep - (tStart.Hour % hourStep)) % hourStep);

                for (DateTime t = tStart; t <= tMax; t = t.AddHours(hourStep))
                {
                    int px = TimeToX(t);
                    g.DrawLine(gridPen, px, plotRect.Top, px, plotRect.Bottom);
                    string lbl = t.Hour == 0 ? t.ToString("MMM d") : t.ToString("h tt");
                    var sz = g.MeasureString(lbl, labelFont);
                    g.DrawString(lbl, labelFont, labelBrush, px - sz.Width / 2, plotRect.Bottom + 4);
                    if (t.Hour == 0) g.DrawLine(dayPen, px, plotRect.Top, px, plotRect.Bottom);
                }

                var xlabel = "Time";
                var xsz = g.MeasureString(xlabel, axisFont);
                g.DrawString(xlabel, axisFont, axisBrush, plotRect.Left + plotRect.Width / 2f - xsz.Width / 2, plotRect.Bottom + 28);
            }
        }

        private void DrawSeries(Graphics g)
        {
            var clip = g.Clip;
            g.SetClip(new Rectangle(plotRect.Left + 1, plotRect.Top + 1, plotRect.Width - 1, plotRect.Height - 1));

            foreach (var s in Visibles)
            {
                if (s.Times.Length < 2) continue;

                if (ShowTemperature)
                {
                    var pts = new PointF[s.Times.Length];
                    for (int i = 0; i < s.Times.Length; i++)
                        pts[i] = new PointF(TimeToX(s.Times[i]), TempToY(s.Temperatures[i]));
                    using (var pen = new Pen(s.Color, 2.0f) { LineJoin = LineJoin.Round })
                        g.DrawLines(pen, pts);
                    using (var brush = new SolidBrush(s.Color))
                        for (int i = 0; i < pts.Length; i++)
                            g.FillEllipse(brush, pts[i].X - 2.0f, pts[i].Y - 2.0f, 4.0f, 4.0f);
                }

                if (ShowWindSpeed)
                {
                    var pts = new PointF[s.Times.Length];
                    for (int i = 0; i < s.Times.Length; i++)
                        pts[i] = new PointF(TimeToX(s.Times[i]), WindToY(s.WindSpeeds[i]));
                    using (var pen = new Pen(s.Color, 1.6f) { DashStyle = DashStyle.Dash, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, pts);
                }
            }

            g.Clip = clip;
        }

        private void DrawLegend(Graphics g)
        {
            legendRowRects.Clear();

            int lx = ClientSize.Width - LegendWidth + 8;
            int ly = PadTop + 4;

            using (var headerFont = new Font("Segoe UI Semibold", 10f))
            using (var nameFont = new Font("Segoe UI Semibold", 9.5f))
            using (var subFont = new Font("Segoe UI", 8.25f))
            using (var hintFont = new Font("Segoe UI Italic", 8f))
            using (var headerBrush = new SolidBrush(Color.FromArgb(230, 235, 245)))
            using (var hintBrush = new SolidBrush(Color.FromArgb(140, 145, 155)))
            using (var panelBrush = new SolidBrush(Color.FromArgb(30, 34, 42)))
            using (var panelPen = new Pen(Color.FromArgb(60, 65, 75)))
            {
                int w = LegendWidth - 18;
                int headerH = 38;
                int rowH = 56;
                int legendH = headerH + Math.Max(1, Series.Count) * rowH + 10;
                g.FillRectangle(panelBrush, lx - 6, ly - 6, w, legendH);
                g.DrawRectangle(panelPen, lx - 6, ly - 6, w, legendH);

                g.DrawString("Locations", headerFont, headerBrush, lx, ly);
                g.DrawString("(click to toggle)", hintFont, hintBrush, lx + 76, ly + 2);
                g.DrawString("—— temp    - - wind", hintFont, hintBrush, lx, ly + 18);
                ly += headerH;

                foreach (var s in Series)
                {
                    var rowRect = new RectangleF(lx - 4, ly - 2, w - 8, rowH - 4);
                    legendRowRects.Add(rowRect);

                    Color cText = s.Visible ? Color.FromArgb(225, 230, 240) : Color.FromArgb(100, 105, 115);
                    Color cSub  = s.Visible ? Color.FromArgb(165, 170, 180) : Color.FromArgb(90, 95, 105);
                    Color cLine = s.Visible ? s.Color : Color.FromArgb(120, s.Color.R / 2 + 30, s.Color.G / 2 + 30, s.Color.B / 2 + 30);

                    using (var pen = new Pen(cLine, 2.5f))
                        g.DrawLine(pen, lx, ly + 10, lx + 22, ly + 10);
                    using (var pen = new Pen(cLine, 1.4f) { DashStyle = DashStyle.Dash })
                        g.DrawLine(pen, lx + 26, ly + 10, lx + 48, ly + 10);

                    using (var b = new SolidBrush(cText))
                        g.DrawString(s.Name, nameFont, b, lx + 56, ly - 1);

                    var current = s.Points.Length > 0 ? s.Points[0] : null;
                    if (current != null)
                    {
                        string line = string.Format("{0:0}°F   {1} {2}  ({3:0} mph)",
                            current.Temperature,
                            current.WindDirection ?? "",
                            current.WindSpeed ?? "",
                            s.WindSpeeds.Length > 0 ? s.WindSpeeds[0] : 0);
                        using (var b = new SolidBrush(cSub))
                            g.DrawString(line, subFont, b, lx, ly + 18);
                        string fcast = current.Forecast ?? "";
                        if (fcast.Length > 36) fcast = fcast.Substring(0, 34) + "...";
                        using (var b = new SolidBrush(cSub))
                            g.DrawString(fcast, subFont, b, lx, ly + 32);
                    }
                    if (!s.Visible)
                    {
                        using (var strike = new Pen(Color.FromArgb(120, 130, 140), 1f))
                            g.DrawLine(strike, lx + 56, ly + 8, lx + 56 + (int)g.MeasureString(s.Name, nameFont).Width, ly + 8);
                    }

                    ly += rowH;
                }
            }
        }

        private void DrawHoverTooltip(Graphics g)
        {
            if (hoverSeries == null || hoverIndex < 0) return;
            var pt = hoverSeries.Points[hoverIndex];
            string l1 = hoverSeries.Name + (hoverIsWind ? "   (wind)" : "   (temp)");
            string l2 = string.Format("{0:ddd MMM d, h:mm tt}", hoverSeries.Times[hoverIndex]);
            string l3 = string.Format("{0:0}°F   Wind {1} {2}", pt.Temperature, pt.WindDirection ?? "", pt.WindSpeed ?? "");
            string l4 = pt.Forecast ?? "";
            using (var f = new Font("Segoe UI", 9f))
            using (var fb = new Font("Segoe UI Semibold", 9.5f))
            {
                var w1 = g.MeasureString(l1, fb);
                var w2 = g.MeasureString(l2, f);
                var w3 = g.MeasureString(l3, f);
                var w4 = g.MeasureString(l4, f);
                float boxW = Math.Max(Math.Max(w1.Width, w2.Width), Math.Max(w3.Width, w4.Width)) + 16;
                float boxH = w1.Height + w2.Height + w3.Height + w4.Height + 14;

                float bx = hoverPoint.X + 14;
                float by = hoverPoint.Y - boxH - 10;
                if (bx + boxW > plotRect.Right) bx = hoverPoint.X - boxW - 14;
                if (by < plotRect.Top) by = hoverPoint.Y + 14;

                using (var bg = new SolidBrush(Color.FromArgb(235, 18, 20, 26)))
                using (var bd = new Pen(hoverSeries.Color, 1.5f))
                using (var txt = new SolidBrush(Color.FromArgb(240, 240, 245)))
                using (var sub = new SolidBrush(Color.FromArgb(180, 185, 195)))
                using (var nm  = new SolidBrush(hoverSeries.Color))
                {
                    g.FillRectangle(bg, bx, by, boxW, boxH);
                    g.DrawRectangle(bd, bx, by, boxW, boxH);
                    g.DrawString(l1, fb, nm, bx + 8, by + 6);
                    g.DrawString(l2, f, sub, bx + 8, by + 6 + w1.Height);
                    g.DrawString(l3, f, txt, bx + 8, by + 6 + w1.Height + w2.Height);
                    g.DrawString(l4, f, sub, bx + 8, by + 6 + w1.Height + w2.Height + w3.Height);
                }
                using (var hp = new Pen(hoverSeries.Color, 2f))
                    g.DrawEllipse(hp, hoverPoint.X - 5, hoverPoint.Y - 5, 10, 10);
            }
        }

        private void OnMouseMoveChart(object sender, MouseEventArgs e)
        {
            bool overLegend = false;
            foreach (var r in legendRowRects)
                if (r.Contains(e.Location)) { overLegend = true; break; }
            this.Cursor = overLegend ? Cursors.Hand : Cursors.Default;

            if (Series.Count == 0 || !plotRect.Contains(e.Location))
            {
                if (hoverSeries != null) { hoverSeries = null; hoverIndex = -1; Invalidate(); }
                return;
            }

            double bestDist = double.MaxValue;
            LocationSeries bestS = null;
            int bestI = -1;
            Point bestPt = Point.Empty;
            bool bestIsWind = false;

            foreach (var s in Visibles)
            {
                for (int i = 0; i < s.Times.Length; i++)
                {
                    int px = TimeToX(s.Times[i]);
                    if (ShowTemperature)
                    {
                        int py = TempToY(s.Temperatures[i]);
                        double d = (px - e.X) * (px - e.X) + (py - e.Y) * (py - e.Y);
                        if (d < bestDist) { bestDist = d; bestS = s; bestI = i; bestPt = new Point(px, py); bestIsWind = false; }
                    }
                    if (ShowWindSpeed)
                    {
                        int py = WindToY(s.WindSpeeds[i]);
                        double d = (px - e.X) * (px - e.X) + (py - e.Y) * (py - e.Y);
                        if (d < bestDist) { bestDist = d; bestS = s; bestI = i; bestPt = new Point(px, py); bestIsWind = true; }
                    }
                }
            }
            if (bestDist <= 25 * 25)
            {
                hoverSeries = bestS; hoverIndex = bestI; hoverPoint = bestPt; hoverIsWind = bestIsWind;
                Invalidate();
            }
            else if (hoverSeries != null)
            {
                hoverSeries = null; hoverIndex = -1; Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            for (int i = 0; i < legendRowRects.Count && i < Series.Count; i++)
            {
                if (legendRowRects[i].Contains(e.Location))
                {
                    Series[i].Visible = !Series[i].Visible;
                    RefreshChart();
                    return;
                }
            }
        }

        private int TimeToX(DateTime t)
        {
            double total = (tMax - tMin).TotalSeconds;
            double frac = total <= 0 ? 0 : (t - tMin).TotalSeconds / total;
            return (int)Math.Round(plotRect.Left + frac * plotRect.Width);
        }

        private int TempToY(double v)
        {
            if (yMax == yMin) return plotRect.Top + plotRect.Height / 2;
            double frac = (v - yMin) / (yMax - yMin);
            return (int)Math.Round(plotRect.Bottom - frac * plotRect.Height);
        }

        private int WindToY(double v)
        {
            if (wMax == wMin) return plotRect.Top + plotRect.Height / 2;
            double frac = (v - wMin) / (wMax - wMin);
            return (int)Math.Round(plotRect.Bottom - frac * plotRect.Height);
        }

        private static double NiceStep(double approx)
        {
            double[] candidates = { 1, 2, 5, 10, 20, 25, 50, 100, 200, 500 };
            double step = candidates[0];
            foreach (var c in candidates) { if (c >= approx) return c; step = c; }
            return step;
        }
    }

    public static class I3xClient
    {
        // Configured at runtime by LoginForm after the user successfully authenticates.
        // _baseUrl is the SERVER ROOT (e.g. "http://localhost:8885"); the i3x standard path
        // "/i3x/v1/..." is appended per call. The optional /data/v1/login endpoint sits under
        // the same root and is only used when the user picks the Username & Password mode.
        private static string _baseUrl = "http://localhost:8885";
        private static string _bearerToken = "";

        private const string I3xPathPrefix = "/i3x/v1";

        public static string DisplayBaseUrl { get { return _baseUrl; } }
        public static bool IsConfigured { get { return !string.IsNullOrEmpty(_bearerToken); } }

        public static void Configure(string baseUrl, string bearerToken)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _bearerToken = bearerToken ?? "";
        }

        static I3xClient()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        }

        // Shared request helper for all /i3x/v1/... calls (GET or POST).
        // `path` is a full path starting with '/' (e.g. "/i3x/v1/objects").
        // For GET, pass jsonBody = null. When _bearerToken is empty (No Auth mode) the
        // Authorization header is omitted entirely.
        private static string SendJson(string path, string httpMethod, string jsonBody)
        {
            var req = (HttpWebRequest)WebRequest.Create(_baseUrl + path);
            req.Method = httpMethod;
            req.Accept = "application/json";
            if (!string.IsNullOrEmpty(_bearerToken))
                req.Headers["Authorization"] = "Bearer " + _bearerToken;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;

            if (jsonBody != null)
            {
                req.ContentType = "application/json";
                var data = Encoding.UTF8.GetBytes(jsonBody);
                req.ContentLength = data.Length;
                using (var rs = req.GetRequestStream()) rs.Write(data, 0, data.Length);
            }

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException wex)
            {
                string body = "";
                if (wex.Response != null)
                {
                    try { using (var sr = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd(); }
                    catch { }
                }
                throw new Exception(wex.Message + (string.IsNullOrEmpty(body) ? "" : " -- " + body), wex);
            }
        }

        // GET /i3x/v1/objects returns every object in the catalog with { elementId, displayName,
        // typeElementId, parentId, isComposition, isExtended }. We filter to direct children of
        // the "Weather" node (parentId == "Weather") and return their displayName (e.g. "Boston").
        public static string[] GetLocations()
        {
            string json = SendJson(I3xPathPrefix + "/objects", "GET", null);
            var ser = new JavaScriptSerializer { MaxJsonLength = 50 * 1024 * 1024 };
            var root = ser.Deserialize<Dictionary<string, object>>(json);
            if (root == null || !root.ContainsKey("result")) return new string[0];
            var arr = (System.Collections.ArrayList)root["result"];
            var names = new List<string>();
            foreach (Dictionary<string, object> obj in arr)
            {
                object p; obj.TryGetValue("parentId", out p);
                if (!(p is string) || (string)p != "Weather") continue;
                object dn; obj.TryGetValue("displayName", out dn);
                if (dn is string) names.Add((string)dn);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }

        // POST /i3x/v1/objects/value with { elementIds: [ "Weather.<loc>.Forecast" ] } returns
        // { success, results: [ { success, elementId, result: { value: { Periods: [ {...} ] } } } ] }.
        // Each Periods entry has Time, Temperature, WindSpeed, WindDirection, Forecast — the same
        // shape the pipeline call returned before, so the rest of the app is unaffected.
        public static ForecastPoint[] GetForecast(string location)
        {
            string elementId = "Weather." + location + ".Forecast";
            string body = "{\"elementIds\":[\"" + EscapeJsonString(elementId) + "\"]}";
            string json = SendJson(I3xPathPrefix + "/objects/value", "POST", body);
            var ser = new JavaScriptSerializer { MaxJsonLength = 50 * 1024 * 1024 };
            var root = ser.Deserialize<Dictionary<string, object>>(json);
            if (root == null || !root.ContainsKey("results")) return new ForecastPoint[0];
            var results = (System.Collections.ArrayList)root["results"];
            if (results.Count == 0) return new ForecastPoint[0];
            var first = (Dictionary<string, object>)results[0];
            if (!first.ContainsKey("result")) return new ForecastPoint[0];
            var resultObj = (Dictionary<string, object>)first["result"];
            if (!resultObj.ContainsKey("value")) return new ForecastPoint[0];
            var valueObj = (Dictionary<string, object>)resultObj["value"];
            if (!valueObj.ContainsKey("Periods")) return new ForecastPoint[0];
            var periods = (System.Collections.ArrayList)valueObj["Periods"];
            var pts = new List<ForecastPoint>(periods.Count);
            foreach (Dictionary<string, object> p in periods)
            {
                pts.Add(new ForecastPoint
                {
                    Time = p.ContainsKey("Time") ? (p["Time"] as string) ?? "" : "",
                    Temperature = p.ContainsKey("Temperature")
                        ? Convert.ToDouble(p["Temperature"], System.Globalization.CultureInfo.InvariantCulture) : 0,
                    WindSpeed = p.ContainsKey("WindSpeed") ? (p["WindSpeed"] as string) ?? "" : "",
                    WindDirection = p.ContainsKey("WindDirection") ? (p["WindDirection"] as string) ?? "" : "",
                    Forecast = p.ContainsKey("Forecast") ? (p["Forecast"] as string) ?? "" : "",
                });
            }
            return pts.ToArray();
        }

        private static string EscapeJsonString(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 2);
            foreach (var c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\b') sb.Append("\\b");
                else if (c == '\f') sb.Append("\\f");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.AppendFormat("\\u{0:X4}", (int)c);
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    public class ConnectionSettings
    {
        public const string ModeToken = "token";
        public const string ModeNoAuth = "noauth";

        public string Url { get; set; }
        // "token" or "noauth". Old configs may deserialize with Mode = null; migration in Load()
        // normalizes that to ModeToken.
        public string Mode { get; set; }

        private static string GetPath()
        {
            return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "connection.json");
        }

        public static ConnectionSettings Load()
        {
            try
            {
                var p = GetPath();
                if (!File.Exists(p)) return Default();
                var ser = new JavaScriptSerializer();
                var s = ser.Deserialize<ConnectionSettings>(File.ReadAllText(p));
                if (s == null) return Default();
                if (string.IsNullOrEmpty(s.Url)) s.Url = "http://localhost:8885";
                if (s.Mode != ModeToken && s.Mode != ModeNoAuth) s.Mode = ModeToken;
                return s;
            }
            catch { return Default(); }
        }

        public void Save()
        {
            // Persist the URL and chosen auth mode only — never the bearer token itself.
            try
            {
                var ser = new JavaScriptSerializer();
                File.WriteAllText(GetPath(), ser.Serialize(this), new UTF8Encoding(false));
            }
            catch { }
        }

        private static ConnectionSettings Default()
        {
            return new ConnectionSettings { Url = "http://localhost:8885", Mode = ModeToken };
        }
    }

    public class LoginForm : Form
    {
        private TextBox urlBox;
        private RadioButton modeToken;
        private RadioButton modeNoAuth;
        private Label tokenLbl;
        private TextBox tokenBox;
        private Label noAuthInfoLbl;
        private Button connectBtn;
        private Button cancelBtn;
        private Label statusLbl;
        private bool busy;

        public string ResolvedUrl { get; private set; }
        public string ResolvedToken { get; private set; }

        public LoginForm()
        {
            Text = "Connect to HighByte i3x";
            ClientSize = new Size(480, 380);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(28, 30, 38);
            ForeColor = Color.White;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9.5f);

            BuildUi();

            var saved = ConnectionSettings.Load();
            urlBox.Text = saved.Url;
            bool useToken = saved.Mode != ConnectionSettings.ModeNoAuth;
            modeToken.Checked = useToken;
            modeNoAuth.Checked = !useToken;
            UpdateModeUi();

            AcceptButton = connectBtn;
            CancelButton = cancelBtn;
        }

        private void BuildUi()
        {
            int x = 24, w = 432;
            int y = 18;

            var title = new Label
            {
                Text = "HighByte i3x Connection",
                Font = new Font("Segoe UI Semibold", 14f),
                ForeColor = Color.FromArgb(235, 235, 240),
                AutoSize = true, Left = x, Top = y
            };
            Controls.Add(title);
            y += 36;

            var subtitle = new Label
            {
                Text = "Server URL is the host root; the i3x API lives under /i3x/v1.",
                ForeColor = Color.FromArgb(160, 165, 175),
                AutoSize = true, Left = x, Top = y
            };
            Controls.Add(subtitle);
            y += 28;

            Controls.Add(new Label { Text = "Server URL", Left = x, Top = y, AutoSize = true,
                ForeColor = Color.FromArgb(210, 215, 225) });
            y += 18;
            urlBox = new TextBox
            {
                Left = x, Top = y, Width = w,
                BackColor = Color.FromArgb(40, 44, 52), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(urlBox);
            y += 32;

            modeToken = new RadioButton
            {
                Text = "Bearer Token",
                Left = x, Top = y, AutoSize = true,
                ForeColor = Color.FromArgb(225, 230, 240),
                BackColor = Color.FromArgb(28, 30, 38),
            };
            modeToken.CheckedChanged += (s, e) => UpdateModeUi();
            Controls.Add(modeToken);

            modeNoAuth = new RadioButton
            {
                Text = "No Auth",
                Left = x + 200, Top = y, AutoSize = true,
                ForeColor = Color.FromArgb(225, 230, 240),
                BackColor = Color.FromArgb(28, 30, 38),
            };
            modeNoAuth.CheckedChanged += (s, e) => UpdateModeUi();
            Controls.Add(modeNoAuth);
            y += 30;

            // ----- Token mode controls
            tokenLbl = new Label { Text = "Bearer Token", Left = x, Top = y, AutoSize = true,
                ForeColor = Color.FromArgb(210, 215, 225) };
            Controls.Add(tokenLbl);
            tokenBox = new TextBox
            {
                Left = x, Top = y + 18, Width = w, UseSystemPasswordChar = true,
                BackColor = Color.FromArgb(40, 44, 52), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(tokenBox);

            // ----- No Auth mode control (occupies the same vertical space, toggled visible)
            noAuthInfoLbl = new Label
            {
                Text = "No credentials will be sent.\r\nRequests to /i3x/v1 will omit the Authorization header.",
                Left = x, Top = y, Width = w, Height = 40, AutoSize = false,
                ForeColor = Color.FromArgb(180, 185, 195),
                BackColor = Color.FromArgb(28, 30, 38),
            };
            Controls.Add(noAuthInfoLbl);

            statusLbl = new Label
            {
                Left = x, Top = ClientSize.Height - 110, Width = w, Height = 50,
                ForeColor = Color.FromArgb(255, 180, 100),
                Text = ""
            };
            Controls.Add(statusLbl);

            int btnY = ClientSize.Height - 48;
            cancelBtn = new Button
            {
                Text = "Cancel",
                Left = ClientSize.Width - 24 - 100 - 8 - 100,
                Top = btnY, Width = 100, Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 65, 80),
                ForeColor = Color.White,
                DialogResult = DialogResult.Cancel,
                TabStop = false,
            };
            cancelBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 95, 110);
            Controls.Add(cancelBtn);

            connectBtn = new Button
            {
                Text = "Connect",
                Left = ClientSize.Width - 24 - 100,
                Top = btnY, Width = 100, Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 120, 200),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9.5f),
                TabStop = false,
            };
            connectBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 150, 230);
            connectBtn.Click += (s, e) => DoConnect();
            Controls.Add(connectBtn);
        }

        private void UpdateModeUi()
        {
            bool useToken = modeToken.Checked;
            tokenLbl.Visible = useToken;
            tokenBox.Visible = useToken;
            noAuthInfoLbl.Visible = !useToken;
            ActiveControl = useToken ? (Control)tokenBox : (Control)connectBtn;
        }

        private void Fail(string msg)
        {
            statusLbl.ForeColor = Color.FromArgb(255, 130, 130);
            statusLbl.Text = msg;
            connectBtn.Enabled = true;
            connectBtn.Text = "Connect";
            busy = false;
        }

        private void DoConnect()
        {
            if (busy) return;
            busy = true;
            connectBtn.Enabled = false;
            connectBtn.Text = "Connecting...";

            string url = (urlBox.Text ?? "").Trim();
            if (url.Length == 0) { Fail("Server URL is required."); return; }
            if (!url.StartsWith("http://") && !url.StartsWith("https://"))
                url = "http://" + url;
            url = url.TrimEnd('/');

            try { new Uri(url); }
            catch { Fail("Server URL is not a valid URL."); return; }

            if (modeToken.Checked)
            {
                string token = (tokenBox.Text ?? "").Trim();
                if (string.IsNullOrEmpty(token)) { Fail("Bearer token is required."); return; }
                ResolvedUrl = url;
                ResolvedToken = token;
                SaveSettings(url, ConnectionSettings.ModeToken);
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            // No Auth mode: no token, no server round-trip. First real API call will surface any
            // 401 from the server if it does actually require auth.
            ResolvedUrl = url;
            ResolvedToken = "";
            SaveSettings(url, ConnectionSettings.ModeNoAuth);
            DialogResult = DialogResult.OK;
            Close();
        }

        private void SaveSettings(string url, string mode)
        {
            new ConnectionSettings { Url = url, Mode = mode }.Save();
        }
    }

    public class MainForm : Form
    {
        private ChartPanel chart;
        private Button refreshBtn;
        private CheckBox tempCheck;
        private CheckBox windCheck;
        private Label locLabel;
        private Button locButton;
        private ToolStripDropDown locDropDown;
        private CheckedListBox locCheckList;
        private Panel locPopupPanel;
        private Panel locButtonsRow;
        private Button selectAllBtn;
        private Button unselectAllBtn;
        private Button resetZoomBtn;
        private Button exportPdfBtn;
        private Label statusLbl;
        private Label updatedLbl;
        private Panel topBar;
        private bool suppressLocChange;
        private bool formReady;

        private static readonly Color[] Palette = new[]
        {
            Color.FromArgb(255, 99, 132),
            Color.FromArgb(54, 162, 235),
            Color.FromArgb(255, 206, 86),
            Color.FromArgb(75, 192, 192),
            Color.FromArgb(180, 130, 255),
            Color.FromArgb(255, 159, 64),
            Color.FromArgb(120, 220, 140),
            Color.FromArgb(230, 100, 230),
        };

        public MainForm()
        {
            Text = "Highbyte Weather Forecast";
            Width = 1480;
            Height = 820;
            MinimumSize = new Size(960, 520);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(20, 22, 28);
            ForeColor = Color.White;

            BuildUi();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // Force expected defaults in case anything got nudged by spurious events
            // during handle creation (we've seen this with FlatStyle controls on dark themes).
            // formReady is false here so handlers will no-op while we sync.
            tempCheck.Checked = true;
            windCheck.Checked = true;
            // CheckedListBox starts empty; RebuildLocationDropdown will populate it on first data
            // load with all items checked. Until then, the button text shows "All locations".
            if (chart != null)
            {
                chart.ShowTemperature = true;
                chart.ShowWindSpeed = true;
                foreach (var s in chart.Series) s.Visible = true;
                chart.RefreshChart();
            }

            // Move focus to the Refresh button so the dropdown doesn't grab initial focus
            // (we've seen it spontaneously open its popup when it does).
            this.ActiveControl = refreshBtn;

            formReady = true;

            try { LoadFromCacheFile(); }
            catch { /* no cache yet — chart shows empty-state message */ }

            BeginRefresh();
        }

        private void BuildUi()
        {
            topBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 44,
                BackColor = Color.FromArgb(28, 30, 38),
                Padding = new Padding(10, 6, 10, 6),
            };

            refreshBtn = new Button
            {
                Text = "Refresh",
                Width = 100,
                Height = 32,
                Left = 10,
                Top = 6,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 120, 200),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9.5f),
                Cursor = Cursors.Hand,
            };
            refreshBtn.FlatAppearance.BorderColor = Color.FromArgb(90, 150, 230);
            refreshBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(80, 140, 220);
            refreshBtn.Click += (s, e) => BeginRefresh();

            // AutoCheck=false + manual Click handler: stray spacebar keystrokes can't toggle
            // these (only mouse Click does). TabStop=false: Tab navigation won't park focus here.
            tempCheck = new CheckBox
            {
                Text = "Temperature",
                Checked = true,
                AutoCheck = false,
                TabStop = false,
                Left = 122,
                Top = 12,
                Width = 110,
                ForeColor = Color.FromArgb(225, 230, 240),
                BackColor = Color.FromArgb(28, 30, 38),
                Font = new Font("Segoe UI", 9.5f),
                Cursor = Cursors.Hand,
            };
            tempCheck.MouseClick += (s, e) =>
            {
                if (!formReady || chart == null) return;
                tempCheck.Checked = !tempCheck.Checked;
                chart.ShowTemperature = tempCheck.Checked;
                chart.RefreshChart();
            };

            windCheck = new CheckBox
            {
                Text = "Wind Speed",
                Checked = true,
                AutoCheck = false,
                TabStop = false,
                Left = 240,
                Top = 12,
                Width = 110,
                ForeColor = Color.FromArgb(225, 230, 240),
                BackColor = Color.FromArgb(28, 30, 38),
                Font = new Font("Segoe UI", 9.5f),
                Cursor = Cursors.Hand,
            };
            windCheck.MouseClick += (s, e) =>
            {
                if (!formReady || chart == null) return;
                windCheck.Checked = !windCheck.Checked;
                chart.ShowWindSpeed = windCheck.Checked;
                chart.RefreshChart();
            };

            locLabel = new Label
            {
                Text = "Location:",
                AutoSize = true,
                Left = 362,
                Top = 14,
                ForeColor = Color.FromArgb(225, 230, 240),
                BackColor = Color.FromArgb(28, 30, 38),
                Font = new Font("Segoe UI", 9.5f),
            };
            // Multi-select "dropdown": a Button that opens a ToolStripDropDown popup
            // containing a CheckedListBox. Button label summarizes the current selection.
            locButton = new Button
            {
                Left = 425,
                Top = 9,
                Width = 230,
                Height = 28,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.FromArgb(45, 48, 56),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9.5f),
                Text = "All locations  ▼",
                Padding = new Padding(8, 0, 8, 0),
                TabStop = false,
                Cursor = Cursors.Hand,
            };
            locButton.FlatAppearance.BorderColor = Color.FromArgb(80, 90, 110);
            locButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 70, 85);
            locButton.Click += (s, e) => ShowLocationDropdown();

            locCheckList = new CheckedListBox
            {
                CheckOnClick = true,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(40, 44, 52),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f),
            };
            locCheckList.ItemCheck += OnLocItemCheck;

            selectAllBtn = new Button
            {
                Text = "Select All",
                Left = 0,
                Top = 0,
                Width = 114,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 90, 160),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9f),
                Cursor = Cursors.Hand,
                TabStop = false,
            };
            selectAllBtn.FlatAppearance.BorderColor = Color.FromArgb(70, 110, 180);
            selectAllBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(70, 110, 180);
            selectAllBtn.Click += (s, e) => SelectAllLocations();

            unselectAllBtn = new Button
            {
                Text = "Unselect All",
                Left = 116,
                Top = 0,
                Width = 114,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(70, 75, 90),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9f),
                Cursor = Cursors.Hand,
                TabStop = false,
            };
            unselectAllBtn.FlatAppearance.BorderColor = Color.FromArgb(95, 100, 115);
            unselectAllBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(90, 95, 110);
            unselectAllBtn.Click += (s, e) => UnselectAllLocations();

            // Row holding both buttons side-by-side, docked at the top of the popup.
            locButtonsRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = Color.FromArgb(40, 44, 52),
            };
            locButtonsRow.Controls.Add(selectAllBtn);
            locButtonsRow.Controls.Add(unselectAllBtn);

            // Container panel holds the action buttons at the top and the checklist below.
            locPopupPanel = new Panel
            {
                Width = 230,
                Height = 28 + 180, // buttons row + list (resized in RebuildLocationDropdown)
                BackColor = Color.FromArgb(40, 44, 52),
            };
            // Add Fill first, then Top — WinForms docking layout processes children in reverse
            // Z-order, so the Fill child must be added first to receive the remaining space.
            locPopupPanel.Controls.Add(locCheckList);
            locPopupPanel.Controls.Add(locButtonsRow);

            locDropDown = new ToolStripDropDown
            {
                AutoClose = true,
                Padding = Padding.Empty,
            };
            var locHost = new ToolStripControlHost(locPopupPanel)
            {
                Padding = Padding.Empty,
                Margin = Padding.Empty,
                AutoSize = false,
                Size = locPopupPanel.Size,
            };
            locDropDown.Items.Add(locHost);

            resetZoomBtn = new Button
            {
                Text = "Reset Zoom",
                Left = 665,
                Top = 9,
                Width = 100,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 48, 56),
                ForeColor = Color.FromArgb(150, 155, 165), // dim by default (no zoom)
                Font = new Font("Segoe UI", 9.5f),
                Cursor = Cursors.Hand,
                TabStop = false,
                Enabled = false, // enabled only when zoomed in
            };
            resetZoomBtn.FlatAppearance.BorderColor = Color.FromArgb(80, 90, 110);
            resetZoomBtn.Click += (s, e) => { if (chart != null) chart.ResetZoom(); };

            exportPdfBtn = new Button
            {
                Text = "Export PDF",
                Left = 775,
                Top = 9,
                Width = 100,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 48, 56),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f),
                Cursor = Cursors.Hand,
                TabStop = false,
            };
            exportPdfBtn.FlatAppearance.BorderColor = Color.FromArgb(80, 90, 110);
            exportPdfBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 70, 85);
            exportPdfBtn.Click += (s, e) => ExportChartToPdf();

            statusLbl = new Label
            {
                AutoSize = true,
                Left = 890,
                Top = 13,
                ForeColor = Color.FromArgb(200, 205, 215),
                Font = new Font("Segoe UI", 9.5f),
                Text = "Ready.",
            };

            updatedLbl = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Right,
                Width = 460,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(160, 165, 175),
                Font = new Font("Segoe UI", 9f),
                Text = "Source: HighByte i3x (" + I3xClient.DisplayBaseUrl + "/i3x/v1)   |   Cache: weather_data.json",
            };

            topBar.Controls.Add(updatedLbl);
            topBar.Controls.Add(statusLbl);
            topBar.Controls.Add(exportPdfBtn);
            topBar.Controls.Add(resetZoomBtn);
            topBar.Controls.Add(locButton);
            topBar.Controls.Add(locLabel);
            topBar.Controls.Add(windCheck);
            topBar.Controls.Add(tempCheck);
            topBar.Controls.Add(refreshBtn);

            chart = new ChartPanel { Dock = DockStyle.Fill };
            // Sync chart state from checkbox values explicitly so behavior is deterministic
            // regardless of when CheckedChanged might or might not fire during layout.
            chart.ShowTemperature = tempCheck.Checked;
            chart.ShowWindSpeed = windCheck.Checked;
            chart.ZoomChanged += (s, e) =>
            {
                resetZoomBtn.Enabled = chart.ZoomedIn;
                resetZoomBtn.ForeColor = chart.ZoomedIn
                    ? Color.White
                    : Color.FromArgb(150, 155, 165);
            };

            Controls.Add(chart);
            Controls.Add(topBar);
        }

        private string CachePath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "weather_data.json"); }
        }

        private void OnLocItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (!formReady || suppressLocChange) return;
            // ItemCheck fires BEFORE the check state actually updates, so defer the read.
            BeginInvoke((Action)ApplySelectionToChart);
        }

        private void SelectAllLocations()
        {
            if (locCheckList == null || locCheckList.Items.Count == 0) return;
            suppressLocChange = true;
            for (int i = 0; i < locCheckList.Items.Count; i++)
                locCheckList.SetItemChecked(i, true);
            suppressLocChange = false;
            ApplySelectionToChart();
        }

        private void UnselectAllLocations()
        {
            if (locCheckList == null || locCheckList.Items.Count == 0) return;
            suppressLocChange = true;
            for (int i = 0; i < locCheckList.Items.Count; i++)
                locCheckList.SetItemChecked(i, false);
            suppressLocChange = false;
            ApplySelectionToChart();
        }

        private void ApplySelectionToChart()
        {
            if (chart == null) return;
            var checkedNames = new HashSet<string>();
            foreach (var item in locCheckList.CheckedItems)
                checkedNames.Add(item.ToString());
            foreach (var s in chart.Series)
                s.Visible = checkedNames.Contains(s.Name);
            chart.RefreshChart();
            UpdateLocButtonText();
        }

        private void UpdateLocButtonText()
        {
            int total = locCheckList.Items.Count;
            int n = locCheckList.CheckedItems.Count;
            string text;
            if (total == 0)            text = "All locations";
            else if (n == 0)           text = "(none selected)";
            else if (n == total)       text = "All locations";
            else if (n == 1)           text = locCheckList.CheckedItems[0].ToString();
            else if (n == 2)           text = locCheckList.CheckedItems[0] + ", " + locCheckList.CheckedItems[1];
            else                       text = string.Format("{0} locations selected", n);
            locButton.Text = text + "  ▼";
        }

        private void ShowLocationDropdown()
        {
            var pos = locButton.PointToScreen(new Point(0, locButton.Height));
            locDropDown.Show(pos);
        }

        private void RebuildLocationDropdown()
        {
            var prevChecked = new HashSet<string>();
            bool hadAny = locCheckList.Items.Count > 0;
            foreach (var item in locCheckList.CheckedItems)
                prevChecked.Add(item.ToString());

            suppressLocChange = true;
            locCheckList.BeginUpdate();
            locCheckList.Items.Clear();
            foreach (var s in chart.Series)
            {
                // First-time load: default everything to checked (= all visible).
                // Subsequent refreshes: preserve the prior check state by name.
                bool check = !hadAny || prevChecked.Contains(s.Name);
                locCheckList.Items.Add(s.Name, check);
                s.Visible = check;
            }
            locCheckList.EndUpdate();
            int rowH = locCheckList.ItemHeight > 0 ? locCheckList.ItemHeight : 18;
            int listH = Math.Min(Math.Max(1, locCheckList.Items.Count) * rowH + 6, 320);
            // Resize the popup container (button row on top + list below) and the host
            // that wraps it.
            locPopupPanel.Width = 230;
            locPopupPanel.Height = locButtonsRow.Height + listH;
            if (locDropDown.Items.Count > 0)
            {
                var h = locDropDown.Items[0] as ToolStripControlHost;
                if (h != null) h.Size = locPopupPanel.Size;
            }
            suppressLocChange = false;

            UpdateLocButtonText();
            chart.RefreshChart();
        }

        private void LoadFromCacheFile()
        {
            string jsonPath = CachePath;
            if (!File.Exists(jsonPath))
                throw new FileNotFoundException("weather_data.json not found", jsonPath);

            string raw = File.ReadAllText(jsonPath);
            var ser = new JavaScriptSerializer { MaxJsonLength = 50 * 1024 * 1024 };
            var root = ser.Deserialize<Dictionary<string, object>>(raw);
            var locArr = (System.Collections.ArrayList)root["Locations"];
            var series = new List<LocationSeries>();
            int ci = 0;
            foreach (Dictionary<string, object> loc in locArr)
            {
                var name = (string)loc["Name"];
                var fc = (System.Collections.ArrayList)loc["Forecast"];
                var pts = new ForecastPoint[fc.Count];
                for (int i = 0; i < fc.Count; i++)
                {
                    var p = (Dictionary<string, object>)fc[i];
                    pts[i] = new ForecastPoint
                    {
                        Time = (string)p["Time"],
                        Temperature = Convert.ToDouble(p["Temperature"], System.Globalization.CultureInfo.InvariantCulture),
                        WindSpeed = p.ContainsKey("WindSpeed") ? (p["WindSpeed"] as string) ?? "" : "",
                        WindDirection = p.ContainsKey("WindDirection") ? (p["WindDirection"] as string) ?? "" : "",
                        Forecast = p.ContainsKey("Forecast") ? (p["Forecast"] as string) ?? "" : "",
                    };
                }
                series.Add(BuildSeries(name, pts, ci++));
            }
            chart.SetData(series);
            RebuildLocationDropdown();

            try
            {
                var ts = File.GetLastWriteTime(jsonPath);
                updatedLbl.Text = string.Format("Cached: {0:MMM d, yyyy h:mm tt}   |   Source: HighByte i3x ({1}/i3x/v1)",
                    ts, I3xClient.DisplayBaseUrl);
            }
            catch { }
        }

        private LocationSeries BuildSeries(string name, ForecastPoint[] pts, int colorIndex)
        {
            var times = new DateTime[pts.Length];
            var temps = new double[pts.Length];
            var winds = new double[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                times[i] = DateTime.Parse(pts[i].Time, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
                temps[i] = pts[i].Temperature;
                winds[i] = WindParser.ParseMph(pts[i].WindSpeed);
            }
            return new LocationSeries
            {
                Name = name,
                Times = times,
                Temperatures = temps,
                WindSpeeds = winds,
                Points = pts,
                Color = Palette[colorIndex % Palette.Length],
                Visible = true,
            };
        }

        private void BeginRefresh()
        {
            if (!refreshBtn.Enabled) return;
            refreshBtn.Enabled = false;
            refreshBtn.Text = "Refreshing...";
            statusLbl.Text = "Contacting i3x server at " + I3xClient.DisplayBaseUrl + "/i3x/v1 ...";

            ThreadPool.QueueUserWorkItem(_ => RefreshWorker());
        }

        private void RefreshWorker()
        {
            string[] locationNames;
            try
            {
                UiInvoke(() => statusLbl.Text = "Fetching locations (GET /i3x/v1/objects, filter parentId=Weather) ...");
                locationNames = I3xClient.GetLocations();
            }
            catch (Exception ex)
            {
                string msg = "GetLocations failed: " + ex.Message;
                UiInvoke(() =>
                {
                    statusLbl.Text = msg;
                    refreshBtn.Text = "Refresh";
                    refreshBtn.Enabled = true;
                });
                return;
            }

            // Preserve visibility from any previously-loaded series
            var prevVis = new Dictionary<string, bool>();
            UiInvoke(() => { foreach (var s in chart.Series) prevVis[s.Name] = s.Visible; });

            var results = new List<LocationSeries>();
            var errors = new List<string>();
            int total = locationNames.Length;
            for (int idx = 0; idx < total; idx++)
            {
                string name = locationNames[idx];
                int idxCapture = idx;
                try
                {
                    UiInvoke(() => statusLbl.Text = string.Format("Fetching {0} forecast ... ({1}/{2})", name, idxCapture + 1, total));
                    var pts = I3xClient.GetForecast(name);
                    var s = BuildSeries(name, pts, idx);
                    bool prev;
                    if (prevVis.TryGetValue(name, out prev)) s.Visible = prev;
                    results.Add(s);
                }
                catch (Exception ex)
                {
                    errors.Add(string.Format("{0}: {1}", name, ex.Message));
                }
            }

            UiInvoke(() =>
            {
                if (results.Count > 0)
                {
                    chart.SetData(results);
                    RebuildLocationDropdown();
                    try { SaveCache(results); } catch { /* non-fatal */ }
                    updatedLbl.Text = string.Format("Refreshed: {0:MMM d, yyyy h:mm tt}   |   Source: HighByte i3x ({1}/i3x/v1)",
                        DateTime.Now, I3xClient.DisplayBaseUrl);
                }
                statusLbl.Text = errors.Count == 0
                    ? string.Format("Loaded {0} locations from i3x.", results.Count)
                    : string.Format("Loaded {0} of {1}. Errors: {2}", results.Count, total, string.Join("; ", errors.ToArray()));
                refreshBtn.Text = "Refresh";
                refreshBtn.Enabled = true;
            });
        }

        private void SaveCache(List<LocationSeries> series)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = 50 * 1024 * 1024 };
            var locArr = new System.Collections.ArrayList();
            foreach (var s in series)
            {
                var fc = new System.Collections.ArrayList();
                foreach (var p in s.Points)
                {
                    fc.Add(new Dictionary<string, object>
                    {
                        { "Time", p.Time },
                        { "Temperature", p.Temperature },
                        { "WindSpeed", p.WindSpeed },
                        { "WindDirection", p.WindDirection },
                        { "Forecast", p.Forecast },
                    });
                }
                locArr.Add(new Dictionary<string, object> { { "Name", s.Name }, { "Forecast", fc } });
            }
            var root = new Dictionary<string, object> { { "Locations", locArr } };
            string json = ser.Serialize(root);
            File.WriteAllText(CachePath, json, new UTF8Encoding(false));
        }

        private void UiInvoke(Action a)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch (ObjectDisposedException) { }
        }

        private void ExportChartToPdf()
        {
            if (chart == null || chart.Series.Count == 0)
            {
                MessageBox.Show(this,
                    "No chart data to export. Click Refresh to fetch the forecast first.",
                    "Export PDF", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string defaultName = "WeatherPlot_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".pdf";
            using (var sfd = new SaveFileDialog
            {
                Filter = "PDF files (*.pdf)|*.pdf|All files (*.*)|*.*",
                FileName = defaultName,
                DefaultExt = "pdf",
                AddExtension = true,
                Title = "Export chart as PDF",
                OverwritePrompt = true,
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                string outPath = sfd.FileName;

                Bitmap snapshot = null;
                try
                {
                    snapshot = chart.RenderToBitmap();

                    using (var pd = new PrintDocument())
                    {
                        pd.DocumentName = Path.GetFileNameWithoutExtension(outPath);
                        pd.PrinterSettings.PrinterName = "Microsoft Print to PDF";
                        if (!pd.PrinterSettings.IsValid)
                        {
                            MessageBox.Show(this,
                                "The \"Microsoft Print to PDF\" printer is not available on this " +
                                "machine.\r\nEnable it under Settings → Printers & scanners, then try again.",
                                "Export PDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        pd.PrinterSettings.PrintToFile = true;
                        pd.PrinterSettings.PrintFileName = outPath;
                        pd.DefaultPageSettings.Landscape = true; // chart is wide
                        pd.DefaultPageSettings.Margins = new Margins(40, 40, 40, 40);

                        pd.PrintPage += (s, e) =>
                        {
                            var page = e.MarginBounds;
                            // Scale to fit while preserving aspect ratio; center on the page.
                            double srcAR = (double)snapshot.Width / Math.Max(1, snapshot.Height);
                            double dstAR = (double)page.Width / Math.Max(1, page.Height);
                            Rectangle target;
                            if (srcAR > dstAR)
                            {
                                int th = (int)(page.Width / srcAR);
                                target = new Rectangle(page.X, page.Y + (page.Height - th) / 2, page.Width, th);
                            }
                            else
                            {
                                int tw = (int)(page.Height * srcAR);
                                target = new Rectangle(page.X + (page.Width - tw) / 2, page.Y, tw, page.Height);
                            }
                            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            e.Graphics.DrawImage(snapshot, target);
                            e.HasMorePages = false;
                        };
                        pd.Print();
                    }

                    statusLbl.Text = "Saved PDF: " + outPath;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this,
                        "Failed to export PDF:\r\n" + ex.Message,
                        "Export PDF", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally
                {
                    if (snapshot != null) snapshot.Dispose();
                }
            }
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
            {
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "crash.log"),
                        string.Format("[{0:HH:mm:ss.fff}] UNHANDLED: {1}{2}",
                            DateTime.Now, e.Exception, Environment.NewLine));
                }
                catch { }
            };

            // Show the connect dialog first. The user enters server URL and either credentials
            // (which we exchange via POST /data/v1/login for a bearer token) or a pre-issued
            // bearer token. Cancel/Esc exits the app.
            using (var login = new LoginForm())
            {
                if (login.ShowDialog() != DialogResult.OK) return;
                I3xClient.Configure(login.ResolvedUrl, login.ResolvedToken);
            }

            Application.Run(new MainForm());
        }
    }
}
