using System.Globalization;
using System.Text;

namespace EasyEDA_Loader
{
    internal static class JlcCamReportBuilder
    {
        public static string Build(JlcCamAnalysisSession session, JlcCamImportOptions options = null, JlcCamImportResult result = null)
        {
            var text = new StringBuilder(); text.AppendLine("EasyEDA Loader — Import JLCCAM report");
            text.AppendLine("Units: mm"); text.AppendLine("Source: " + session.SourcePath); text.AppendLine("Package root: " + session.PackageRoot); text.AppendLine("Original outline: " + session.OriginalOutlinePath);
            text.AppendLine("Transform: " + session.Transform); text.AppendLine("Fit error: " + F(session.Transform.FitErrorMm) + " mm"); text.AppendLine("Original bounds: " + session.OriginalBounds); text.AppendLine("Panel bounds: " + session.PanelBounds);
            text.AppendLine(); text.AppendLine("Edge holes (# | X | Y | Nominal | Top mask | Bottom mask | CAM | Status)");
            foreach (JlcCamHole hole in session.Holes) text.AppendLine(hole.Number + " | " + F(hole.Center.X) + " | " + F(hole.Center.Y) + " | " + F(hole.NominalDiameterMm) + " | " + F(hole.TopMaskOpeningMm) + " | " + F(hole.BottomMaskOpeningMm) + " | " + F(hole.CamDiameterMm) + " | " + hole.Status);
            text.AppendLine(); text.AppendLine("Fiducials (# | X | Y | Layer | Nominal | Solder-mask opening | CAM | Status)");
            foreach (JlcCamFiducial f in session.Fiducials) text.AppendLine(f.Number + " | " + F(f.Center.X) + " | " + F(f.Center.Y) + " | " + f.Side + " | " + F(f.NominalDiameterMm) + " | " + F(f.MaskOpeningMm) + " | " + F(f.CamDiameterMm) + " | " + f.Status);
            if (session.Diagnostics.Count > 0) { text.AppendLine(); text.AppendLine("Diagnostics:"); foreach (string diagnostic in session.Diagnostics) text.AppendLine("- " + diagnostic); }
            if (options != null) text.AppendLine("Selected: edge rails=" + options.ImportRails + ", fiducials=" + options.ImportFiducials + ", edge holes=" + options.ImportHoles);
            if (result != null) { text.AppendLine("Import result: rails=" + result.RailsImported + ", holes=" + result.HolesImported + ", fiducials=" + result.FiducialsImported + ", skipped=" + result.Skipped); foreach (string message in result.Messages) text.AppendLine("- " + message); }
            return text.ToString();
        }
        private static string F(double? value) { return value.HasValue ? value.Value.ToString("0.######", CultureInfo.InvariantCulture) : "—"; }
        private static string F(double value) { return value.ToString("0.######", CultureInfo.InvariantCulture); }
    }
}
