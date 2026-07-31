using PCB;
using System;
using System.Threading;
using System.Windows;

namespace EasyEDA_Loader
{
    public partial class JlcCamImportDialog : Window
    {
        private readonly JlcCamAnalysisSession session;
        private readonly IPCB_Board board;
        private readonly JlcCamImportOptions options = new JlcCamImportOptions();
        private CanvasZoomPanHelper zoom;
        internal JlcCamImportDialog(JlcCamAnalysisSession session, IPCB_Board board)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session)); this.board = board ?? throw new ArgumentNullException(nameof(board)); InitializeComponent();
            sourceText.Text = "Source: " + session.SourcePath + "  •  Units: mm"; statusText.Text = "Transform: " + session.Transform + "  •  Fit error: " + session.Transform.FitErrorMm.ToString("0.00") + " mm";
            holesGrid.ItemsSource = session.Holes; fiducialsGrid.ItemsSource = session.Fiducials; importButton.IsEnabled = session.CanImport;
        }
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            zoom = new CanvasZoomPanHelper(previewCanvas, previewViewport, true);
            Render();
            Dispatcher.BeginInvoke(new Action(() => zoom.FitToBoundingBox()));
        }
        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (zoom != null)
                Dispatcher.BeginInvoke(new Action(() => zoom.FitToBoundingBox()));
        }
        private void ImportOptionChanged(object sender, RoutedEventArgs e)
        {
            // IsChecked is assigned while BAML is still creating the dialog.  The
            // first checkbox event arrives before the remaining named controls exist.
            if (railsCheckBox == null || fiducialsCheckBox == null || holesCheckBox == null || importButton == null || previewCanvas == null)
                return;
            SyncOptions(); Render();
        }
        private void SyncOptions()
        {
            options.ImportRails = railsCheckBox.IsChecked == true; options.ImportFiducials = fiducialsCheckBox.IsChecked == true; options.ImportHoles = holesCheckBox.IsChecked == true;
            importButton.IsEnabled = session.CanImport && (options.ImportRails || options.ImportFiducials || options.ImportHoles);
        }
        private void Render() { if (previewCanvas != null) JlcCamPreviewRenderer.Render(previewCanvas, session, options); }
        private void CopyReport_Click(object sender, RoutedEventArgs e)
        {
            string report = JlcCamReportBuilder.Build(session, options); Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++) { try { Clipboard.SetText(report); statusText.Text = "Report copied to clipboard."; return; } catch (Exception ex) { last = ex; Thread.Sleep(75); } }
            MessageBox.Show(last?.Message ?? "Clipboard is unavailable.", "Import JLCCAM", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        private void Import_Click(object sender, RoutedEventArgs e)
        {
            SyncOptions(); try { JlcCamImportResult result = JlcCamPcbImporter.Import(session, options, board); statusText.Text = "Imported rails=" + result.RailsImported + ", holes=" + result.HolesImported + ", fiducials=" + result.FiducialsImported + "."; DialogResult = true; }
            catch (Exception ex) { EasyEDALoaderModule.Trace("JLCCAM import failed: " + ex); MessageBox.Show(ex.Message, "Import JLCCAM", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
        private void Window_Closed(object sender, EventArgs e) { session.Dispose(); }
    }
}
