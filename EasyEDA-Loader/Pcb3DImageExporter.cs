using PCB;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace EasyEDA_Loader
{
    internal sealed class Pcb3DImageExportOptions
    {
        public string Side { get; set; } = "top";
        public int Dpi { get; set; } = 600;
        public bool UseSystemColors { get; set; } = true;
        public int WorkspaceColor { get; set; } = -1;
        public int BoardColor { get; set; } = -1;
        public int SolderMaskColor { get; set; } = -1;
        public int SilkColor { get; set; } = -1;
        public int CopperColor { get; set; } = -1;
    }

    internal sealed class Pcb3DImageExportResult
    {
        public string GeneratorName { get; set; }
    }

    internal static class Pcb3DImageExporter
    {
        private sealed class OrthographicCamera
        {
            public float LookAtX { get; set; }
            public float LookAtY { get; set; }
            public float LookAtZ { get; set; }
            public double Zoom { get; set; }
            public int ViewX { get; set; }
            public int ViewY { get; set; }
        }

        private sealed class ActiveViewConfiguration : IDisposable
        {
            private PCBInterfaces.IPCB_Board2 board;
            private readonly string originalType;
            private readonly string originalConfig;
            private readonly bool originalPerspective;

            public ActiveViewConfiguration(PCBInterfaces.IPCB_Board2 board,
                string originalType, string originalConfig, bool originalPerspective)
            {
                this.board = board;
                this.originalType = originalType;
                this.originalConfig = originalConfig;
                this.originalPerspective = originalPerspective;
            }

            public void Dispose()
            {
                if (board == null)
                    return;
                try
                {
                    if (!board.SetState_ViewConfigFromString(originalType, originalConfig))
                    {
                        EasyEDALoaderModule.Trace(
                            "PCB 3D export warning: Altium did not restore the original view configuration.");
                    }
                    else
                    {
                        SetPerspective(board, originalPerspective);
                        board.Update_PCBGraphicalView(true, true);
                    }
                }
                catch (Exception ex)
                {
                    EasyEDALoaderModule.Trace(
                        "PCB 3D export warning: restoring the original view configuration failed: " + ex.Message);
                }
                finally
                {
                    board = null;
                }
            }
        }

        private sealed class GraphicalViewState
        {
            public bool HasCamera { get; set; }
            public float LookAtX { get; set; }
            public float LookAtY { get; set; }
            public float LookAtZ { get; set; }
            public float QuatX { get; set; }
            public float QuatY { get; set; }
            public float QuatZ { get; set; }
            public float QuatW { get; set; }
            public double Zoom { get; set; }
            public bool HasUp { get; set; }
            public float UpX { get; set; }
            public float UpY { get; set; }
            public float UpZ { get; set; }
            public bool HasFov { get; set; }
            public float Fov { get; set; }
            public bool HasViewPort { get; set; }
            public int ViewPortWidth { get; set; }
            public int ViewPortHeight { get; set; }
        }

        public static Pcb3DImageExportResult Export(IPCB_Board board, string outputPath, Pcb3DImageExportOptions options)
        {
            if (board == null)
                throw new ArgumentNullException(nameof(board));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            RT_PCB.IPCB_GraphicalView originalGraphicalView = GetGraphicalView(board);
            if (originalGraphicalView == null)
                throw new InvalidOperationException("Altium returned no main PCB graphical view.");
            GraphicalViewState originalState = CaptureViewState(originalGraphicalView);

            ActiveViewConfiguration activeView = null;
            RT_PCB.IPCB_GraphicalView exportView = null;
            bool temporaryWindowOpen = false;
            try
            {
                activeView = PrepareOrthographicViewConfig(board, options);
                exportView = GetGraphicalView(board);
                if (exportView == null)
                    throw new InvalidOperationException("Altium returned no PCB graphical view for export.");
                OrthographicCamera camera = GetOrthographicCamera(board, options);

                int pixelWidth = Math.Max(1, (int)Math.Round(
                    (double)camera.ViewX * options.Dpi / 10000000.0));
                int pixelHeight = Math.Max(1, (int)Math.Round(
                    (double)camera.ViewY * options.Dpi / 10000000.0));
                if (!exportView.SetState_TemporaryWindow(pixelWidth, pixelHeight))
                    throw new InvalidOperationException(
                        "Altium could not create a temporary PCB 3D rendering window.");
                temporaryWindowOpen = true;

                // GetExtents reports zoom for the editor window. Derive the export
                // zoom from the requested bitmap size and physical viewport instead.
                camera.Zoom = Math.Min(
                    pixelWidth / (double)camera.ViewX,
                    pixelHeight / (double)camera.ViewY);
                ConfigureCamera(exportView, camera, options.Side != "top");
                RenderViewToPng(exportView, outputPath, options.Dpi, pixelWidth, pixelHeight);

                if (!File.Exists(outputPath))
                    throw new InvalidOperationException("Altium returned without creating the requested PNG image.");
                return new Pcb3DImageExportResult { GeneratorName = "Altium PCB 3D graphical view" };
            }
            finally
            {
                if (temporaryWindowOpen)
                {
                    try { exportView.CloseState_TemporaryWindow(); }
                    catch (Exception ex)
                    {
                        EasyEDALoaderModule.Trace(
                            "PCB 3D export warning: closing the temporary render window failed: " +
                            ex.Message);
                    }
                }
                activeView?.Dispose();
                RestoreViewState(GetGraphicalView(board), originalState);
            }
        }

        private static OrthographicCamera GetOrthographicCamera(IPCB_Board board,
            Pcb3DImageExportOptions options)
        {
            RT_PCB.IPCB_GraphicalView graphicalView = GetGraphicalView(board);
            if (graphicalView == null)
                throw new InvalidOperationException("Altium returned no main PCB graphical view.");

            float lookAtX = 0;
            float lookAtY = 0;
            float lookAtZ = 0;
            double zoom = 0;
            int viewX = 0;
            int viewY = 0;
            bool fromBottom = options.Side != "top";
            if (!graphicalView.GetExtents_CameraIncludeComponents(
                    out lookAtX, out lookAtY, out lookAtZ, out zoom,
                    out viewX, out viewY, fromBottom))
            {
                throw new InvalidOperationException(
                    "Altium could not calculate the PCB 3D camera extents.");
            }

            EasyEDALoaderModule.Trace(string.Format(CultureInfo.InvariantCulture,
                "PCB 3D export: orthographic camera extents {0}x{1}, zoom {2:R}, " +
                "look-at ({3:R}, {4:R}, {5:R}).",
                viewX, viewY, zoom, lookAtX, lookAtY, lookAtZ));
            return new OrthographicCamera
            {
                LookAtX = lookAtX,
                LookAtY = lookAtY,
                LookAtZ = lookAtZ,
                Zoom = zoom,
                ViewX = viewX,
                ViewY = viewY
            };
        }

        private static RT_PCB.IPCB_GraphicalView GetGraphicalView(IPCB_Board board)
        {
            object value = board.GetState_MainGraphicalView();
            if (value == null)
                return null;

            IntPtr unknown = Marshal.GetIUnknownForObject(value);
            try
            {
                return (RT_PCB.IPCB_GraphicalView)Marshal.GetTypedObjectForIUnknown(
                    unknown, typeof(RT_PCB.IPCB_GraphicalView));
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        private static GraphicalViewState CaptureViewState(RT_PCB.IPCB_GraphicalView view)
        {
            var result = new GraphicalViewState();
            if (view == null)
                return result;

            result.HasCamera = view.GetState_Camera(
                out float lookAtX, out float lookAtY, out float lookAtZ,
                out float quatX, out float quatY, out float quatZ, out float quatW,
                out double zoom);
            result.LookAtX = lookAtX;
            result.LookAtY = lookAtY;
            result.LookAtZ = lookAtZ;
            result.QuatX = quatX;
            result.QuatY = quatY;
            result.QuatZ = quatZ;
            result.QuatW = quatW;
            result.Zoom = zoom;

            result.HasUp = view.GetState_CameraUpVector(
                out float upX, out float upY, out float upZ);
            result.UpX = upX;
            result.UpY = upY;
            result.UpZ = upZ;
            result.HasFov = view.GetState_CameraFov(out float fov);
            result.Fov = fov;
            result.HasViewPort = view.GetState_ViewPortMils(
                out int viewPortWidth, out int viewPortHeight);
            result.ViewPortWidth = viewPortWidth;
            result.ViewPortHeight = viewPortHeight;
            return result;
        }

        private static void RestoreViewState(RT_PCB.IPCB_GraphicalView view,
            GraphicalViewState state)
        {
            if (view == null || state == null)
                return;
            try
            {
                if (state.HasCamera)
                {
                    view.SetState_Camera(state.LookAtX, state.LookAtY, state.LookAtZ,
                        state.QuatX, state.QuatY, state.QuatZ, state.QuatW, state.Zoom);
                }
                if (state.HasUp)
                    view.SetState_CameraUpVector(state.UpX, state.UpY, state.UpZ);
                if (state.HasFov)
                    view.SetState_CameraFov(state.Fov);
                if (state.HasViewPort)
                    view.SetState_ViewPortMils(state.ViewPortWidth, state.ViewPortHeight);
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace(
                    "PCB 3D export warning: restoring the graphical view failed: " + ex.Message);
            }
        }

        private static void ConfigureCamera(RT_PCB.IPCB_GraphicalView view,
            OrthographicCamera camera, bool fromBottom)
        {
            if (!view.SetState_Camera(
                    camera.LookAtX, camera.LookAtY, camera.LookAtZ,
                    0, fromBottom ? 1 : 0, 0, fromBottom ? 0 : 1, camera.Zoom))
            {
                throw new InvalidOperationException(
                    "Altium could not set the PCB 3D export camera.");
            }
            if (!view.SetState_CameraUpVector(0, 1, 0))
                throw new InvalidOperationException(
                    "Altium could not set the PCB 3D camera up vector.");
            if (!view.SetState_ViewPortMils(camera.ViewX, camera.ViewY))
                throw new InvalidOperationException(
                    "Altium could not set the PCB 3D export viewport.");
        }

        private static void RenderViewToPng(RT_PCB.IPCB_GraphicalView view,
            string outputPath, int dpi, int pixelWidth, int pixelHeight)
        {
            var rectangle = new rt_basic.TRect
            {
                Left = 0,
                Top = 0,
                Right = pixelWidth,
                Bottom = pixelHeight
            };

            EasyEDALoaderModule.Trace(string.Format(CultureInfo.InvariantCulture,
                "PCB 3D export: rendering orthographic Altium view at {0}x{1} pixels, {2} DPI.",
                pixelWidth, pixelHeight, dpi));
            using (var bitmap = new Bitmap(
                pixelWidth, pixelHeight, PixelFormat.Format32bppArgb))
            {
                bitmap.SetResolution(dpi, dpi);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    IntPtr dc = graphics.GetHdc();
                    try
                    {
                        view.RenderToDCTransparent(
                            dc, pixelWidth, pixelHeight, rectangle, rectangle, false);
                    }
                    finally
                    {
                        graphics.ReleaseHdc(dc);
                    }
                }
                bitmap.Save(outputPath, ImageFormat.Png);
            }
        }

        private static ActiveViewConfiguration PrepareOrthographicViewConfig(IPCB_Board board,
            Pcb3DImageExportOptions options)
        {
            IntPtr boardUnknown = Marshal.GetIUnknownForObject(board);
            try
            {
                var board2 = (PCBInterfaces.IPCB_Board2)Marshal.GetTypedObjectForIUnknown(
                    boardUnknown, typeof(PCBInterfaces.IPCB_Board2));
                var boardEx = (RT_PCB.IPCB_BoardEx)Marshal.GetTypedObjectForIUnknown(
                    boardUnknown, typeof(RT_PCB.IPCB_BoardEx));

                if (!board2.GetState_ViewConfigAsString(
                    out string originalConfigType, out string originalConfig))
                    throw new InvalidOperationException(
                        "Altium could not serialize the current PCB view configuration.");
                bool originalPerspective = GetPerspective(board2);
                if (!boardEx.GetViewConfigurationByTypeAsString(
                    ".config_3d", out string threeDimensionalConfig) ||
                    string.IsNullOrEmpty(threeDimensionalConfig))
                {
                    throw new InvalidOperationException(
                        "Altium returned no PCB 3D view configuration.");
                }

                bool restoreOnFailure = false;
                try
                {
                    if (!board2.SetState_ViewConfigFromString(
                        ".config_3d", threeDimensionalConfig))
                    {
                        throw new InvalidOperationException(
                            "Altium could not activate the PCB 3D view configuration for export.");
                    }
                    restoreOnFailure = true;

                    GraphicalViewConfigurationInterface.IPCBGraphicalViewConfiguration1 graphicalView =
                        GetViewConfiguration(board2);
                    graphicalView.SetState_IsPerspective(false);
                    graphicalView.SetState_UseSysColorsFor3D(options.UseSystemColors);
                    if (!options.UseSystemColors)
                    {
                        ApplyColor(options.WorkspaceColor,
                            graphicalView.SetState_3DWorkspaceColor);
                        ApplyColor(options.BoardColor,
                            graphicalView.SetState_3DBoardCoreColor);
                        ApplyColor(options.BoardColor,
                            graphicalView.SetState_3DBoardPrepregColor);
                        ApplyColor(options.SolderMaskColor,
                            graphicalView.SetState_3DTopSolderMaskColor);
                        ApplyColor(options.SolderMaskColor,
                            graphicalView.SetState_3DBotSolderMaskColor);
                        ApplyColor(options.SilkColor,
                            graphicalView.SetState_3DTopSilkScreenColor);
                        ApplyColor(options.SilkColor,
                            graphicalView.SetState_3DBotSilkScreenColor);
                        ApplyColor(options.CopperColor,
                            graphicalView.SetState_3DCopperColor);
                    }
                    board2.Update_PCBGraphicalView(true, false);
                    if (GetPerspective(board2))
                        throw new InvalidOperationException(
                            "Altium did not apply orthographic projection to the PCB 3D view.");

                    EasyEDALoaderModule.Trace(
                        "PCB 3D export: active Altium graphical view is orthographic.");
                    restoreOnFailure = false;
                    return new ActiveViewConfiguration(
                        board2, originalConfigType, originalConfig, originalPerspective);
                }
                finally
                {
                    if (restoreOnFailure && !board2.SetState_ViewConfigFromString(
                            originalConfigType, originalConfig))
                    {
                        EasyEDALoaderModule.Trace(
                            "PCB 3D export warning: Altium did not restore the original view configuration.");
                    }
                }
            }
            finally
            {
                Marshal.Release(boardUnknown);
            }
        }

        private static GraphicalViewConfigurationInterface.IPCBGraphicalViewConfiguration1
            GetViewConfiguration(PCBInterfaces.IPCB_Board2 board)
        {
            object value = board.GetState_GraphicalViewConfiguration();
            if (value == null)
                throw new InvalidOperationException(
                    "Altium returned no graphical view configuration for the PCB document.");

            IntPtr unknown = Marshal.GetIUnknownForObject(value);
            try
            {
                return (GraphicalViewConfigurationInterface.IPCBGraphicalViewConfiguration1)
                    Marshal.GetTypedObjectForIUnknown(unknown,
                        typeof(GraphicalViewConfigurationInterface.IPCBGraphicalViewConfiguration1));
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        private static bool GetPerspective(PCBInterfaces.IPCB_Board2 board)
        {
            return GetViewConfiguration(board).GetState_IsPerspective();
        }

        private static void SetPerspective(PCBInterfaces.IPCB_Board2 board, bool value)
        {
            GetViewConfiguration(board).SetState_IsPerspective(value);
        }

        private static void ApplyColor(int value, Action<uint> setter)
        {
            if (value >= 0)
                setter(unchecked((uint)value));
        }
    }
}
