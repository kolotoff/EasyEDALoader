using PCB;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace EasyEDA_Loader
{
    public partial class EdgeRailsDialog : Window
    {
        private readonly IPCB_Board board;
        private readonly EdgeRailBounds boardBounds;
        private readonly EdgeRailContour boardContour;
        private readonly double cornerRMm;
        private CanvasZoomPanHelper zoom;
        private EdgeRailPlan lastPlan;

        internal EdgeRailsDialog(IPCB_Board board, EdgeRailBounds boardBounds, EdgeRailContour contour, double cornerRMm)
        {
            this.board = board ?? throw new ArgumentNullException(nameof(board));
            this.boardBounds = boardBounds ?? throw new ArgumentNullException(nameof(boardBounds));
            this.boardContour = contour ?? new EdgeRailContour();
            this.cornerRMm = cornerRMm;
            InitializeComponent();
            var defaults = new EdgeRailOptions();
            horizontalRailTextBox.Text = defaults.HorizontalRailMm.ToString("0.##");
            verticalRailTextBox.Text = defaults.VerticalRailMm.ToString("0.##");
            holeSizeTextBox.Text = defaults.HoleSizeMm.ToString("0.##");
            fiducialSizeTextBox.Text = defaults.FiducialSizeMm.ToString("0.##");
            string shape = cornerRMm > 0 ? ("rounded, corner R" + cornerRMm.ToString("0.0") + " mm") : "sharp corners";
            sourceText.Text = "Board: " + boardBounds.Width.ToString("0.0") + " × " + boardBounds.Height.ToString("0.0") + " mm  •  " + shape + "  •  Units: mm";
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            zoom = new CanvasZoomPanHelper(previewCanvas, previewViewport, true);
            Regenerate();
            Dispatcher.BeginInvoke(new Action(() => { if (zoom != null) zoom.FitToBoundingBox(); }));
        }

        private void Input_Changed(object sender, TextChangedEventArgs e)
        {
            if (horizontalRailTextBox == null || verticalRailTextBox == null || holeSizeTextBox == null || fiducialSizeTextBox == null || addButton == null || previewCanvas == null) return;
            Regenerate();
        }

        private void CornerOption_Changed(object sender, RoutedEventArgs e)
        {
            if (closeCornerRectanglesCheckBox == null || addButton == null || previewCanvas == null) return;
            Regenerate();
        }

        private void Regenerate()
        {
            if (previewCanvas == null) return;
            if (!TryReadOption(out EdgeRailOptions options, out string error))
            {
                hintText.Text = error ?? "Invalid input.";
                addButton.IsEnabled = false;
                EdgeRailsPreviewRenderer.Render(previewCanvas, EdgeRailsGenerator.Generate(boardBounds, cornerRMm, boardContour, options));
                lastPlan = null;
                return;
            }
            EdgeRailPlan plan = EdgeRailsGenerator.Generate(boardBounds, cornerRMm, boardContour, options);
            lastPlan = plan;
            EdgeRailsPreviewRenderer.Render(previewCanvas, plan);
            addButton.IsEnabled = plan.RailSegments.Count > 0;
            hintText.Text = "Preview: " + plan.RailSegments.Count + " rail segment(s), " + plan.Holes.Count + " tooling hole(s), " + plan.Fiducials.Count + " fiducial(s).";
        }

        private bool TryReadOption(out EdgeRailOptions options, out string error)
        {
            options = new EdgeRailOptions();
            error = null;
            if (!TryReadRail(horizontalRailTextBox.Text, out double h, out error)) return false;
            if (!TryReadRail(verticalRailTextBox.Text, out double v, out error)) return false;
            if (!TryReadRange(holeSizeTextBox.Text, 2, 4, "Tooling hole Ø", out double hole, out error)) return false;
            if (!TryReadRange(fiducialSizeTextBox.Text, 1, 4, "Fiducial Ø", out double fid, out error)) return false;
            if (h <= 0 && v <= 0) { error = "Enable at least one rail: enter 5–50 mm for horizontal or vertical (0 disables a pair)."; return false; }
            options.HorizontalRailMm = h;
            options.VerticalRailMm = v;
            options.CloseCornerRectangles = closeCornerRectanglesCheckBox.IsChecked == true;
            options.HoleSizeMm = hole;
            options.FiducialSizeMm = fid;
            return true;
        }

        private static bool TryReadRail(string text, out double value, out string error)
        {
            value = 0; error = null;
            if (!TryParseMm(text, out double raw)) { error = "Rail width must be a number (0 or 5–50 mm)."; return false; }
            raw = Math.Round(raw, 2);
            if (raw == 0) { value = 0; return true; }
            if (raw < 5 || raw > 50) { error = "Rail width must be 0 or between 5 and 50 mm."; return false; }
            value = raw; return true;
        }

        private static bool TryReadRange(string text, double min, double max, string label, out double value, out string error)
        {
            value = 0; error = null;
            string range = label + " must be between " + min.ToString("0.#", CultureInfo.InvariantCulture) + " and " + max.ToString("0.#", CultureInfo.InvariantCulture) + " mm.";
            if (!TryParseMm(text, out double raw) || raw <= 0) { error = range; return false; }
            raw = Math.Round(raw, 2);
            if (raw < min || raw > max) { error = range; return false; }
            value = raw; return true;
        }

        private static bool TryParseMm(string text, out double value)
        {
            return double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (lastPlan == null) return;
            try { EdgeRailsPcbWriter.Import(lastPlan, board); DialogResult = true; }
            catch (Exception ex) { EasyEDALoaderModule.Trace("Edge rails add failed: " + ex); MessageBox.Show(ex.Message, "Add Edge Rails", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
    }
}
