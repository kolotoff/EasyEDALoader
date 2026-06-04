using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using EasyEDA_Loader;
using Microsoft.Win32;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using System.Windows.Threading;
using System.Collections.Generic;

namespace Standalone
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        protected Task<Root>? DocumentTask;
        protected Task<BitmapImage>? ThumbnailTask;
        protected Task<byte[]>? ModelTask;
        protected EasyedaApi Api;

        public ComponentInfo? Component;
        public EeFootprint3dModel? Model;
        public ModelVisual3D? RawModel;
        public CancellationTokenSource cts;

        public CanvasZoomPanHelper _footprintHelper;
        public CanvasZoomPanHelper _symbolHelper;
        private int _previewLoadVersion;
        private string _previewLoadKey = string.Empty;

        public MainWindow()
        {
            cts = new CancellationTokenSource();
            Api = new EasyedaApi();
            InitializeComponent();

            _footprintHelper = new CanvasZoomPanHelper(FootprintCanvas);

            FootprintCanvasView.ScrollChanged += (s, e) =>
            {
                if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
                    _footprintHelper.FitToBoundingBox();
            };

            _symbolHelper = new CanvasZoomPanHelper(SymbolCanvas);

            SymbolCanvasView.ScrollChanged += (s, e) =>
            {
                if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
                    _symbolHelper.FitToBoundingBox();
            };

            var cam = ModelView.Camera as ProjectionCamera;
            if (cam != null)
            {
                cam.Position = new Point3D(0, 0, 30);       // Camera above the origin
                cam.LookDirection = new Vector3D(0, 0, -30); // Looking down at origin
                cam.UpDirection = new Vector3D(0, 1, 0);      // Y-axis as up

                ModelView.Camera = cam;

                // Optionally call ResetCamera() to update internals
                ModelView.ResetCamera();
            }
        }

        public static void SaveModelToFile(EeFootprint3dModel model, byte[] fileData)
        {
            var saveFileDialog = new SaveFileDialog
            {
                Title = "Save File As",
                Filter = "SwSTEP 2.0|*.step|All Files|*.*",
                FileName = $"{model.Name}.step",
                DefaultExt = "step"
            };

            bool? result = saveFileDialog.ShowDialog();
            if (result == true)
            {
                string selectedPath = saveFileDialog.FileName;
                File.WriteAllBytes(selectedPath, fileData);
            }
        }
        public static void SaveRawModelToFile(EeFootprint3dModel model, byte[] fileData)
        {
            var saveFileDialog = new SaveFileDialog
            {
                Title = "Save File As",
                Filter = "Obj|*.obj|All Files|*.*",
                FileName = $"{model.Name}.obj",
                DefaultExt = "obj"
            };

            bool? result = saveFileDialog.ShowDialog();
            if (result == true)
            {
                string selectedPath = saveFileDialog.FileName;
                File.WriteAllBytes(selectedPath, fileData);
            }
        }

        private void LoadPart(string partName)
        {
            LoadPartAsync(partName, null).GetAwaiter().GetResult();
        }

        private async Task LoadPartAsync(string partName, EasyedaApi.PartInfo? partInfo)
        {
            string loadKey = GetPreviewLoadKey(partName, partInfo);
            int loadVersion = Interlocked.Increment(ref _previewLoadVersion);
            _previewLoadKey = loadKey;

            if (RawModel != null)
            {
                ModelView.Children.Remove(RawModel);
                RawModel = null;
            }

            SymbolCanvas.Children.Clear();
            FootprintCanvas.Children.Clear();
            Thumbnail.Source = null;
            Component = null;
            Model = null;
            ModelButton.IsEnabled = false;
            ObjButton.IsEnabled = false;

            var componentJson = await LoadPreviewComponentJsonAsync(partName, partInfo);
            if (!IsCurrentPreviewLoad(loadVersion, loadKey))
                return;

            Root? root = componentJson.Root;
            string loadedPartNumber = componentJson.PartNumber;

            if (root?.Component == null)
            {
                ShowCanvasMessage(SymbolCanvas, "No component preview data was returned for this part.");
                ShowCanvasMessage(FootprintCanvas, "No component preview data was returned for this part.");
                return;
            }

            Component = root.Component;

            EasyedaApi.ProductInfo? productInfo = partInfo?.Info;
            if (productInfo == null && !string.IsNullOrWhiteSpace(Component.Owner?.Uuid))
            {
                try
                {
                    productInfo = await ModelCache.GetProductInfoAsync(Api, loadedPartNumber, Component.Owner.Uuid, cts.Token);
                    if (!IsCurrentPreviewLoad(loadVersion, loadKey))
                        return;
                }
                catch
                {
                }
            }

            PopulateParameters(productInfo);

            if (Component.Symbol?.Shapes != null && Component.Symbol.Shapes.Count > 0)
            {
                DrawSymbolPreviewSafely(Component.Symbol.Shapes);
                if (SymbolCanvas.Children.Count == 0)
                    ShowCanvasMessage(SymbolCanvas, "No symbol preview data was returned for this part.");
                await SymbolCanvas.Dispatcher.InvokeAsync(() =>
                {
                    _symbolHelper.FitToBoundingBox();
                }, DispatcherPriority.Loaded);
            }
            else
            {
                ShowCanvasMessage(SymbolCanvas, "No symbol preview data was returned for this part.");
            }

            var eeFootprint = Component.PackageDetail?.Footprint;
            if (eeFootprint == null)
            {
                ShowCanvasMessage(FootprintCanvas, "No footprint preview data was returned for this part.");
                return;
            }

            Model = eeFootprint.GetModel();

            ModelButton.IsEnabled = Model != null;
            ObjButton.IsEnabled = Model != null;

            try
            {
                if (!string.IsNullOrWhiteSpace(Component.Thumb))
                {
                    byte[] thumbnailData = await ModelCache.GetPngImageAsync(Api, Component.Thumb, loadedPartNumber, cts.Token);
                    if (!IsCurrentPreviewLoad(loadVersion, loadKey))
                        return;

                    BitmapImage? thumbnail = LoadBitmapImage(thumbnailData);
                    if (thumbnail != null)
                    {
                        Thumbnail.MaxWidth = thumbnail.Width;
                        Thumbnail.MaxHeight = thumbnail.Height;
                        Thumbnail.Source = thumbnail;
                    }
                }
            }
            catch (Exception)
            {
                Thumbnail.Source = null;
                Thumbnail.MaxWidth = 0;
                Thumbnail.MaxHeight = 0;
            }

            EeFootprintContext ctx = new()
            {
                Box = eeFootprint.BoundingBox,
                Layers = eeFootprint.Layers,
                CancelToken = cts.Token,
                Exception = null,
            };

            DrawFootprintPreviewSafely(eeFootprint, ctx);
            if (FootprintCanvas.Children.Count == 0)
                ShowCanvasMessage(FootprintCanvas, "No footprint preview data was returned for this part.");

            await FootprintCanvas.Dispatcher.InvokeAsync(() =>
            {
                _footprintHelper.FitToBoundingBox();
            }, DispatcherPriority.Loaded);
            if (!IsCurrentPreviewLoad(loadVersion, loadKey))
                return;

            if (Model != null)
            {
                await LoadRawModelPreviewAsync(ctx, Model, loadVersion, loadKey);
            }
        }

        private bool IsCurrentPreviewLoad(int loadVersion, string loadKey)
        {
            return Volatile.Read(ref _previewLoadVersion) == loadVersion ||
                string.Equals(_previewLoadKey, loadKey, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetPreviewLoadKey(string partName, EasyedaApi.PartInfo? partInfo)
        {
            return (partInfo?.Part ?? partName ?? string.Empty).Trim();
        }

        private async Task<(Root? Root, string PartNumber)> LoadPreviewComponentJsonAsync(string partName, EasyedaApi.PartInfo? partInfo)
        {
            foreach (string candidate in GetPreviewPartCandidates(partName, partInfo))
            {
                try
                {
                    Root root = await ModelCache.GetComponentJsonAsync(Api, candidate, cts.Token);
                    if (root?.Component != null)
                        return (root, candidate);
                }
                catch
                {
                }
            }

            return (null, GetPreviewPartCandidates(partName, partInfo).FirstOrDefault() ?? partName);
        }

        private static IEnumerable<string> GetPreviewPartCandidates(string partName, EasyedaApi.PartInfo? partInfo)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? candidate in new[] { partInfo?.Part, partName, partInfo?.Name })
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                string trimmed = candidate.Trim();
                if (seen.Add(trimmed))
                    yield return trimmed;
            }
        }

        private async Task LoadRawModelPreviewAsync(EeFootprintContext ctx, EeFootprint3dModel footprintModel, int loadVersion, string loadKey)
        {
            if (footprintModel == null)
                return;

            try
            {
                ctx.RawModelTask = ModelCache.GetRawObjModelAsync(Api, footprintModel.Uuid, cts.Token);
                var zInfoTask = footprintModel.GetZInfoFromOrigin(ctx);
                await Task.WhenAll(ctx.RawModelTask, zInfoTask);
                if (!IsCurrentPreviewLoad(loadVersion, loadKey))
                    return;

                byte[] rawModelData = await ctx.RawModelTask;
                if (rawModelData == null || rawModelData.Length == 0)
                    return;

                using var stream = new MemoryStream(rawModelData);
                var importer = new ObjReader();

                Model3D model = importer.Read(stream);
                if (model != null)
                {
                    RawModel = new ModelVisual3D { Content = model };
                    var transformGroup = new Transform3DGroup();
                    var rotationX = new AxisAngleRotation3D(new Vector3D(1, 0, 0), footprintModel.Rotation.X);
                    var rotationY = new AxisAngleRotation3D(new Vector3D(0, 1, 0), footprintModel.Rotation.Y);
                    var rotationZ = new AxisAngleRotation3D(new Vector3D(0, 0, 1), footprintModel.Rotation.Z);
                    var rotateTransformX = new RotateTransform3D(rotationX);
                    var rotateTransformY = new RotateTransform3D(rotationY);
                    var rotateTransformZ = new RotateTransform3D(rotationZ);
                    transformGroup.Children.Add(rotateTransformX);
                    transformGroup.Children.Add(rotateTransformY);
                    transformGroup.Children.Add(rotateTransformZ);
                    RawModel.Transform = transformGroup;
                    ModelView.Children.Add(RawModel);
                }
            }
            catch
            {
            }
        }

        private void DrawSymbolPreviewSafely(List<EeSymbolShape> shapes)
        {
            try
            {
                SymbolDrawing.DrawComponent(SymbolCanvas, shapes);
            }
            catch (Exception ex)
            {
                ShowCanvasMessage(SymbolCanvas, "Symbol preview failed: " + ex.Message);
            }
        }

        private void DrawFootprintPreviewSafely(FootprintData footprint, EeFootprintContext ctx)
        {
            try
            {
                footprint.DrawToCanvas(FootprintCanvas, ctx);
            }
            catch (Exception ex)
            {
                ShowCanvasMessage(FootprintCanvas, "Footprint preview failed: " + ex.Message);
            }
        }

        private static void ShowCanvasMessage(Canvas canvas, string message)
        {
            canvas.Children.Clear();
            var textBlock = new TextBlock
            {
                Text = message,
                Foreground = Brushes.Firebrick,
                TextWrapping = TextWrapping.Wrap,
                Width = Math.Max(100.0, canvas.ActualWidth > 0.0 ? canvas.ActualWidth : canvas.Width)
            };
            Canvas.SetLeft(textBlock, 8);
            Canvas.SetTop(textBlock, 8);
            canvas.Children.Add(textBlock);
        }

        private static BitmapImage? LoadBitmapImage(byte[] data)
        {
            if (data == null || data.Length == 0)
                return null;

            using var stream = new MemoryStream(data);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private async void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadSelectedPartAsync();
        }

        private async void ModelButton_Click(object sender, RoutedEventArgs e)
        {
            if(Model != null)
            {
                byte[] modelData = await ModelCache.GetStepModelAsync(Api, Model.Uuid, cts.Token);
                SaveModelToFile(Model, modelData);
            }
        }
        private async void ObjModelButton_Click(object sender, RoutedEventArgs e)
        {
            if (Model != null)
            {
                byte[] modelData = await ModelCache.GetRawObjModelAsync(Api, Model.Uuid, cts.Token);
                SaveRawModelToFile(Model, modelData);
            }
        }

        private void PopulateSearchBox(List<EasyedaApi.PartInfo> parts)
        {
            SearchBox.ItemsSource = parts?.ToList();
            NameColumn.Width = Double.NaN;
            PartColumn.Width = Double.NaN;
            DescColumn.Width = Double.NaN;
        }

        private async void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            string partName = PartId.Text;

            var productInfo = await ModelCache.GetSearchProductInfoAsync(Api, partName, cts.Token);
            PopulateSearchBox(productInfo);
            if (productInfo != null && productInfo.Count > 0)
                SearchBox.SelectedIndex = 0;
            else
            {
                ShowCanvasMessage(SymbolCanvas, "No search results were returned for this part.");
                ShowCanvasMessage(FootprintCanvas, "No search results were returned for this part.");
            }
        }

        private void PopulateParameters(EasyedaApi.ProductInfo? productInfo)
        {
            DetailsView.ItemsSource = productInfo?.Parameters;
            KeyColumn.Width = Double.NaN;
            ValueColumn.Width = Double.NaN;
        }

        private async void SearchBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SearchBox.SelectedItem is EasyedaApi.PartInfo selectedItem)
            {
                PartId.Text = selectedItem.Part;
                PopulateParameters(selectedItem.Info);
                await LoadPartAsync(selectedItem.Part, selectedItem);
            }
        }

        private async void SearchBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            await LoadSelectedPartAsync();
        }

        private async Task LoadSelectedPartAsync()
        {
            if (SearchBox.SelectedItem is EasyedaApi.PartInfo selectedItem)
                await LoadPartAsync(selectedItem.Part, selectedItem);
            else
                await LoadPartAsync(PartId.Text, null);
        }
    }
}
