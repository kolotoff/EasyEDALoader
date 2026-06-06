using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace StepF3DRenderLib
{
    public sealed class F3DRenderedImage
    {
        public string Name { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int ChannelCount { get; set; }
        public int ChannelType { get; set; }
        public int ChannelTypeSize { get; set; }
        public byte[] RawBytes { get; set; }
    }

    public sealed class F3DRenderedFile
    {
        public string ViewName { get; set; }
        public string OutputPath { get; set; }
    }

    public sealed class F3DPreviewCameraState
    {
        public double AzimuthDegrees { get; set; }
        public double ElevationDegrees { get; set; }
        public double PanRight { get; set; }
        public double PanUp { get; set; }
        public double ZoomFactor { get; set; } = 1.0;

        public F3DPreviewCameraState Clone()
        {
            return new F3DPreviewCameraState
            {
                AzimuthDegrees = AzimuthDegrees,
                ElevationDegrees = ElevationDegrees,
                PanRight = PanRight,
                PanUp = PanUp,
                ZoomFactor = ZoomFactor
            };
        }
    }

    public sealed class F3DPreviewCameraSnapshot
    {
        public double[] Position { get; set; }
        public double[] FocalPoint { get; set; }
        public double[] ViewUp { get; set; }
        public double ViewAngle { get; set; }
        public double OrthographicZoomFactor { get; set; } = 1.0;

        public F3DPreviewCameraSnapshot Clone()
        {
            return new F3DPreviewCameraSnapshot
            {
                Position = CloneVector(Position),
                FocalPoint = CloneVector(FocalPoint),
                ViewUp = CloneVector(ViewUp),
                ViewAngle = ViewAngle,
                OrthographicZoomFactor = OrthographicZoomFactor
            };
        }

        private static double[] CloneVector(double[] vector)
        {
            if (vector == null)
                return null;

            var clone = new double[vector.Length];
            Array.Copy(vector, clone, vector.Length);
            return clone;
        }
    }

    public sealed class F3DPreviewRenderPair
    {
        public F3DRenderedImage OriginalImage { get; set; }
        public F3DRenderedImage CleanImage { get; set; }
    }

    public enum F3DPreviewInteractionKind
    {
        MousePosition,
        MouseButtonPress,
        MouseButtonRelease,
        MouseWheel,
        ResetCamera
    }

    public enum F3DPreviewMouseButton
    {
        Left = 0,
        Right = 1,
        Middle = 2
    }

    public enum F3DPreviewWheelDirection
    {
        Forward = 0,
        Backward = 1
    }

    public enum F3DPreviewInputModifier
    {
        None = 0,
        Control = 1,
        Shift = 2,
        ControlShift = 3
    }

    public sealed class F3DPreviewInteraction
    {
        public F3DPreviewInteractionKind Kind { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public F3DPreviewMouseButton Button { get; set; }
        public F3DPreviewWheelDirection WheelDirection { get; set; }
        public F3DPreviewInputModifier Modifier { get; set; }
    }

    public static class F3DProjectionRenderer
    {
        private const string F3DLibraryName = "f3d_c_api";
        private const int PngFormat = 0;
        private const double PreviewMouseWheelZoomFactor = 1.1;

        private static readonly object NativeConfigurationLock = new object();
        private static readonly object NativeRenderLock = new object();
        private static bool _nativeResolverConfigured;
        private static string _f3dBinDirectory;

        private static readonly ViewSpec[] Views =
        {
            new ViewSpec("x_plus", -1, 0, 0, 0, 0, 1),
            new ViewSpec("x_minus", 1, 0, 0, 0, 0, 1),
            new ViewSpec("y_plus", 0, -1, 0, 0, 0, 1),
            new ViewSpec("y_minus", 0, 1, 0, 0, 0, 1),
            new ViewSpec("z_plus", 0, 0, -1, 0, 1, 0),
            new ViewSpec("z_minus", 0, 0, 1, 0, 1, 0)
        };

        public static IReadOnlyList<string> AllViewNames
        {
            get { return Views.Select(view => view.Name).ToList(); }
        }

        public static IReadOnlyList<F3DRenderedImage> RenderRawImages(
            byte[] stepData,
            int sizePixels,
            IReadOnlyList<string> viewNames)
        {
            return RenderRawImages(stepData, sizePixels, sizePixels, viewNames);
        }

        public static IReadOnlyList<F3DRenderedImage> RenderRawImages(
            byte[] stepData,
            int widthPixels,
            int heightPixels,
            IReadOnlyList<string> viewNames)
        {
            if (stepData == null || stepData.Length == 0)
                throw new ArgumentException("STEP data is required.", nameof(stepData));

            ViewSpec[] views = ParseViews(viewNames);
            ValidateSize(widthPixels, heightPixels);
            ConfigureNativeAccess();

            lock (NativeRenderLock)
                return RenderRawImagesCore(stepData, widthPixels, heightPixels, views);
        }

        public static F3DPreviewSession CreatePreviewSession(byte[] originalStepData, byte[] cleanStepData)
        {
            if (originalStepData == null || originalStepData.Length == 0)
                throw new ArgumentException("Original STEP data is required.", nameof(originalStepData));
            if (cleanStepData == null || cleanStepData.Length == 0)
                throw new ArgumentException("Clean STEP data is required.", nameof(cleanStepData));

            ConfigureNativeAccess();
            lock (NativeRenderLock)
                return new F3DPreviewSession(originalStepData, cleanStepData);
        }

        public static F3DPreviewSession CreatePreviewSession(byte[] stepData)
        {
            if (stepData == null || stepData.Length == 0)
                throw new ArgumentException("STEP data is required.", nameof(stepData));

            ConfigureNativeAccess();
            lock (NativeRenderLock)
                return new F3DPreviewSession(stepData);
        }

        public static F3DPreviewSession CreatePreviewSession(byte[] stepData, F3DPreviewCameraSnapshot cameraSnapshot)
        {
            if (stepData == null || stepData.Length == 0)
                throw new ArgumentException("STEP data is required.", nameof(stepData));

            ConfigureNativeAccess();
            lock (NativeRenderLock)
                return new F3DPreviewSession(stepData, cameraSnapshot);
        }

        private static IReadOnlyList<F3DRenderedImage> RenderRawImagesCore(
            byte[] stepData,
            int widthPixels,
            int heightPixels,
            ViewSpec[] views)
        {
            IntPtr engine = CreateEngine();
            try
            {
                ConfigureScene(engine, widthPixels, heightPixels);
                IntPtr scene = GetScene(engine);
                GCHandle pinnedStepData = GCHandle.Alloc(stepData, GCHandleType.Pinned);
                try
                {
                    if (f3d_scene_add_buffer(scene, pinnedStepData.AddrOfPinnedObject(), (UIntPtr)stepData.Length) == 0)
                        throw new InvalidOperationException("F3D failed to load STEP data from memory.");
                }
                finally
                {
                    pinnedStepData.Free();
                }

                IntPtr window = GetWindow(engine);
                var result = new List<F3DRenderedImage>();
                foreach (ViewSpec view in views)
                    result.Add(RenderViewToRawImage(window, view));

                return result;
            }
            finally
            {
                f3d_engine_delete(engine);
            }
        }

        public static IReadOnlyList<F3DRenderedFile> RenderPngFilesFromFile(
            string inputPath,
            string outputDirectory,
            int sizePixels,
            IReadOnlyList<string> viewNames)
        {
            return RenderPngFilesFromFile(inputPath, outputDirectory, sizePixels, sizePixels, viewNames);
        }

        public static IReadOnlyList<F3DRenderedFile> RenderPngFilesFromFile(
            string inputPath,
            string outputDirectory,
            int widthPixels,
            int heightPixels,
            IReadOnlyList<string> viewNames)
        {
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
                throw new FileNotFoundException("Input STEP file was not found.", inputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Output directory is required.", nameof(outputDirectory));

            string extension = Path.GetExtension(inputPath);
            if (!string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".stp", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Input file must be a STEP file.", nameof(inputPath));
            }

            ViewSpec[] views = ParseViews(viewNames);
            ValidateSize(widthPixels, heightPixels);
            ConfigureNativeAccess();
            Directory.CreateDirectory(outputDirectory);

            lock (NativeRenderLock)
                return RenderPngFilesFromFileCore(inputPath, outputDirectory, widthPixels, heightPixels, views);
        }

        private static IReadOnlyList<F3DRenderedFile> RenderPngFilesFromFileCore(
            string inputPath,
            string outputDirectory,
            int widthPixels,
            int heightPixels,
            ViewSpec[] views)
        {
            IntPtr engine = CreateEngine();
            try
            {
                ConfigureScene(engine, widthPixels, heightPixels);
                IntPtr scene = GetScene(engine);
                if (f3d_scene_add(scene, Path.GetFullPath(inputPath)) == 0)
                    throw new InvalidOperationException("F3D failed to load STEP file: " + inputPath);

                string modelName = Path.GetFileNameWithoutExtension(inputPath);
                IntPtr window = GetWindow(engine);
                var result = new List<F3DRenderedFile>();
                foreach (ViewSpec view in views)
                {
                    string outputPath = Path.Combine(outputDirectory, modelName + "__" + view.Name + ".png");
                    RenderViewToPngFile(window, view, outputPath);
                    result.Add(new F3DRenderedFile
                    {
                        ViewName = view.Name,
                        OutputPath = outputPath
                    });
                }

                return result;
            }
            finally
            {
                f3d_engine_delete(engine);
            }
        }

        public static IReadOnlyList<string> NormalizeViewNames(IReadOnlyList<string> viewNames)
        {
            return ParseViews(viewNames).Select(view => view.Name).ToList();
        }

        private static void ValidateSize(int sizePixels)
        {
            ValidateSize(sizePixels, sizePixels);
        }

        private static void ValidateSize(int widthPixels, int heightPixels)
        {
            if (widthPixels <= 0)
                throw new ArgumentException("Image width must be greater than zero.", nameof(widthPixels));
            if (heightPixels <= 0)
                throw new ArgumentException("Image height must be greater than zero.", nameof(heightPixels));
        }

        private static ViewSpec[] ParseViews(IReadOnlyList<string> viewNames)
        {
            if (viewNames == null || viewNames.Count == 0)
                return Views.ToArray();

            var selectedViews = new List<ViewSpec>();
            var selectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawName in viewNames)
            {
                string name = rawName == null ? "" : rawName.Trim();
                if (name.Length == 0)
                    continue;

                ViewSpec view = FindView(name);
                if (string.IsNullOrWhiteSpace(view.Name))
                    throw new ArgumentException("Unknown view name: " + name, nameof(viewNames));

                if (selectedNames.Add(view.Name))
                    selectedViews.Add(view);
            }

            if (selectedViews.Count == 0)
                throw new ArgumentException("At least one view name is required.", nameof(viewNames));

            return selectedViews.ToArray();
        }

        private static ViewSpec FindView(string name)
        {
            foreach (ViewSpec view in Views)
            {
                if (string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase))
                    return view;
            }

            return default;
        }

        private static IntPtr CreateEngine()
        {
            f3d_engine_autoload_plugins();
            f3d_engine_load_plugin("occt");

            IntPtr engine = f3d_engine_create_wgl(1);
            if (engine == IntPtr.Zero)
                engine = f3d_engine_create(1);
            if (engine == IntPtr.Zero)
                throw new InvalidOperationException("F3D engine creation failed.");

            return engine;
        }

        private static void ConfigureScene(IntPtr engine, int widthPixels, int heightPixels)
        {
            IntPtr options = f3d_engine_get_options(engine);
            if (options == IntPtr.Zero)
                throw new InvalidOperationException("F3D options handle was not available.");
            ConfigureRenderingOptions(options);

            IntPtr window = GetWindow(engine);
            f3d_window_set_size(window, widthPixels, heightPixels);
        }

        private static IntPtr GetScene(IntPtr engine)
        {
            IntPtr scene = f3d_engine_get_scene(engine);
            if (scene == IntPtr.Zero)
                throw new InvalidOperationException("F3D scene handle was not available.");
            return scene;
        }

        private static IntPtr GetWindow(IntPtr engine)
        {
            IntPtr window = f3d_engine_get_window(engine);
            if (window == IntPtr.Zero)
                throw new InvalidOperationException("F3D window handle was not available.");
            return window;
        }

        private static F3DRenderedImage RenderViewToRawImage(IntPtr window, ViewSpec view)
        {
            ApplyViewCamera(window, view);

            return RenderCurrentWindowToRawImage(window, view.Name);
        }

        private static F3DRenderedImage RenderCurrentWindowToRawImage(IntPtr window, string imageName)
        {
            IntPtr image = f3d_window_render_to_image(window, 0);
            if (image == IntPtr.Zero)
                throw new InvalidOperationException("F3D render failed for view " + imageName + ".");

            try
            {
                int width = checked((int)f3d_image_get_width(image));
                int height = checked((int)f3d_image_get_height(image));
                int channelCount = checked((int)f3d_image_get_channel_count(image));
                int channelType = f3d_image_get_channel_type(image);
                int channelTypeSize = checked((int)f3d_image_get_channel_type_size(image));
                IntPtr content = f3d_image_get_content(image);
                if (content == IntPtr.Zero || width <= 0 || height <= 0 || channelCount <= 0 || channelTypeSize <= 0)
                    throw new InvalidOperationException("F3D raw image content was not available for view " + imageName + ".");

                int byteCount = checked(width * height * channelCount * channelTypeSize);
                var rawBytes = new byte[byteCount];
                Marshal.Copy(content, rawBytes, 0, byteCount);
                return new F3DRenderedImage
                {
                    Name = imageName,
                    Width = width,
                    Height = height,
                    ChannelCount = channelCount,
                    ChannelType = channelType,
                    ChannelTypeSize = channelTypeSize,
                    RawBytes = rawBytes
                };
            }
            finally
            {
                f3d_image_delete(image);
            }
        }

        private static void RenderViewToPngFile(IntPtr window, ViewSpec view, string outputPath)
        {
            ApplyViewCamera(window, view);

            IntPtr image = f3d_window_render_to_image(window, 0);
            if (image == IntPtr.Zero)
                throw new InvalidOperationException("F3D render failed for view " + view.Name + ".");

            try
            {
                if (f3d_image_save(image, outputPath, PngFormat) == 0)
                    throw new InvalidOperationException("F3D failed to save output image: " + outputPath);
            }
            finally
            {
                f3d_image_delete(image);
            }
        }

        private static void ApplyViewCamera(IntPtr window, ViewSpec view)
        {
            IntPtr camera = f3d_window_get_camera(window);
            if (camera == IntPtr.Zero)
                throw new InvalidOperationException("F3D camera handle was not available.");

            double distance = 100.0;
            double[] focalPoint = { 0.0, 0.0, 0.0 };
            double[] position =
            {
                -view.DirectionX * distance,
                -view.DirectionY * distance,
                -view.DirectionZ * distance
            };
            double[] up = { view.UpX, view.UpY, view.UpZ };

            f3d_camera_set_focal_point(camera, focalPoint);
            f3d_camera_set_position(camera, position);
            f3d_camera_set_view_up(camera, up);
            f3d_camera_reset_to_bounds(camera, 0.9);
        }

        private static void ApplyInteractivePreviewCamera(IntPtr window, F3DPreviewCameraState state)
        {
            IntPtr camera = f3d_window_get_camera(window);
            if (camera == IntPtr.Zero)
                throw new InvalidOperationException("F3D camera handle was not available.");

            F3DPreviewCameraState actual = state ?? new F3DPreviewCameraState();
            double zoomFactor = actual.ZoomFactor;
            if (double.IsNaN(zoomFactor) || double.IsInfinity(zoomFactor) || zoomFactor <= 0.0)
                zoomFactor = 1.0;

            f3d_camera_reset_to_bounds(camera, 0.9);

            if (actual.AzimuthDegrees != 0.0)
                f3d_camera_azimuth(camera, actual.AzimuthDegrees);
            if (actual.ElevationDegrees != 0.0)
                f3d_camera_elevation(camera, actual.ElevationDegrees);
            if (zoomFactor != 1.0)
                f3d_camera_zoom(camera, zoomFactor);
            if (actual.PanRight != 0.0 || actual.PanUp != 0.0)
                f3d_camera_pan(camera, actual.PanRight, actual.PanUp, 0.0);
        }

        private static void ConfigureRenderingOptions(IntPtr options)
        {
            f3d_options_set_as_bool(options, "scene.camera.orthographic", 1);
            f3d_options_set_as_bool(options, "model.scivis.enable", 1);
            f3d_options_set_as_bool(options, "model.scivis.cells", 1);
            f3d_options_set_as_string(options, "model.scivis.array_name", "Colors");
            f3d_options_set_as_int(options, "model.scivis.component", -2);
            f3d_options_set_as_string(options, "render.effect.antialiasing.mode", "fxaa");
            f3d_options_set_as_bool(options, "render.effect.antialiasing.enable", 1);
            f3d_options_set_as_bool(options, "render.effect.ambient_occlusion", 0);
            f3d_options_set_as_double_vector(
                options,
                "render.background.color",
                new[] { 250.0 / 255.0, 250.0 / 255.0, 250.0 / 255.0 },
                (UIntPtr)3);
        }

        private static void ConfigureNativeAccess()
        {
            lock (NativeConfigurationLock)
            {
                if (!_nativeResolverConfigured)
                {
                    NativeLibrary.SetDllImportResolver(
                        typeof(F3DProjectionRenderer).Assembly,
                        (libraryName, assembly, searchPath) =>
                        {
                            if (!string.Equals(libraryName, F3DLibraryName, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(libraryName, F3DLibraryName + ".dll", StringComparison.OrdinalIgnoreCase))
                            {
                                return IntPtr.Zero;
                            }

                            string libraryPath = FindF3DLibraryPath();
                            if (string.IsNullOrWhiteSpace(libraryPath))
                                return IntPtr.Zero;

                            _f3dBinDirectory = Path.GetDirectoryName(libraryPath);
                            SetDllDirectory(_f3dBinDirectory);
                            return LoadNativeLibraryFromOwnDirectory(libraryPath);
                        });
                    _nativeResolverConfigured = true;
                }

                string resolvedLibraryPath = FindF3DLibraryPath();
                if (string.IsNullOrWhiteSpace(resolvedLibraryPath))
                    throw new FileNotFoundException("f3d_c_api.dll was not found. Set STEPCLEANER_F3D_LIB or install F3D.");

                _f3dBinDirectory = Path.GetDirectoryName(resolvedLibraryPath);
                SetDllDirectory(_f3dBinDirectory);
            }
        }

        private static string FindF3DLibraryPath()
        {
            string configuredPath = Environment.GetEnvironmentVariable("STEPCLEANER_F3D_LIB");
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
                return configuredPath;

            string baseDirectory = AppContext.BaseDirectory;
            string assemblyDirectory = Path.GetDirectoryName(typeof(F3DProjectionRenderer).Assembly.Location);
            var candidates = new List<string>
            {
                Path.Combine(baseDirectory, "f3d_c_api.dll"),
                Path.Combine(baseDirectory, "F3D", "bin", "f3d_c_api.dll"),
                Path.Combine(assemblyDirectory ?? string.Empty, "f3d_c_api.dll"),
                Path.Combine(assemblyDirectory ?? string.Empty, "F3D", "bin", "f3d_c_api.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "F3D", "bin", "f3d_c_api.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "F3D", "bin", "f3d_c_api.dll")
            };

            foreach (string candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static IntPtr LoadNativeLibraryFromOwnDirectory(string libraryPath)
        {
            const int LoadLibrarySearchDllLoadDir = 0x00000100;
            const int LoadLibrarySearchDefaultDirs = 0x00001000;

            IntPtr handle = LoadLibraryEx(
                libraryPath,
                IntPtr.Zero,
                LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);
            if (handle != IntPtr.Zero)
                return handle;

            int errorCode = Marshal.GetLastWin32Error();
            try
            {
                return NativeLibrary.Load(libraryPath);
            }
            catch (Exception ex)
            {
                throw new DllNotFoundException(
                    "Unable to load F3D native library from " + libraryPath +
                    ". LoadLibraryEx failed with Win32 error " +
                    errorCode.ToString(CultureInfo.InvariantCulture) + "." +
                    DescribeLoadedMsvcRuntimeCollision(libraryPath),
                    ex);
            }
        }

        private static string DescribeLoadedMsvcRuntimeCollision(string libraryPath)
        {
            string loadedMsvcpPath = GetLoadedModulePath("MSVCP140.dll");
            if (string.IsNullOrWhiteSpace(loadedMsvcpPath))
                return string.Empty;

            string f3dMsvcpPath = Path.Combine(Path.GetDirectoryName(libraryPath) ?? string.Empty, "MSVCP140.dll");
            string loadedVersion = GetFileVersion(loadedMsvcpPath);
            string f3dVersion = GetFileVersion(f3dMsvcpPath);
            return " Loaded MSVCP140.dll='" + loadedMsvcpPath +
                "' version='" + loadedVersion +
                "'; F3D MSVCP140.dll='" + f3dMsvcpPath +
                "' version='" + f3dVersion +
                "'. Update Altium's app-local MSVCP140.dll with BuildAndInstall-Altium.ps1 so F3D can initialize in-process.";
        }

        private static string GetLoadedModulePath(string moduleName)
        {
            IntPtr moduleHandle = GetModuleHandle(moduleName);
            if (moduleHandle == IntPtr.Zero)
                return null;

            var buffer = new char[32768];
            int length = GetModuleFileName(moduleHandle, buffer, buffer.Length);
            if (length <= 0)
                return null;

            return new string(buffer, 0, length);
        }

        private static string GetFileVersion(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return "missing";

                return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, int dwFlags);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int GetModuleFileName(IntPtr hModule, [Out] char[] lpFilename, int nSize);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr f3d_engine_create(int offscreen);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr f3d_engine_create_wgl(int offscreen);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_engine_delete(IntPtr engine);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr f3d_engine_get_options(IntPtr engine);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr f3d_engine_get_window(IntPtr engine);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr f3d_engine_get_scene(IntPtr engine);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr f3d_engine_get_interactor(IntPtr engine);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int f3d_engine_load_plugin([MarshalAs(UnmanagedType.LPUTF8Str)] string pathOrName);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_engine_autoload_plugins();

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int f3d_scene_add(IntPtr scene, [MarshalAs(UnmanagedType.LPUTF8Str)] string filePath);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int f3d_scene_add_buffer(IntPtr scene, IntPtr buffer, UIntPtr size);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_window_set_size(IntPtr window, int width, int height);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr f3d_window_get_camera(IntPtr window);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr f3d_window_render_to_image(IntPtr window, int noBackground);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int f3d_image_save(IntPtr image, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int format);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint f3d_image_get_width(IntPtr image);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint f3d_image_get_height(IntPtr image);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint f3d_image_get_channel_count(IntPtr image);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int f3d_image_get_channel_type(IntPtr image);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint f3d_image_get_channel_type_size(IntPtr image);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr f3d_image_get_content(IntPtr image);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_image_delete(IntPtr image);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_options_set_as_bool(IntPtr options, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int value);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_options_set_as_int(IntPtr options, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int value);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_options_set_as_string(
            IntPtr options,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_options_set_as_double_vector(
            IntPtr options,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [In] double[] values,
            UIntPtr count);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_camera_set_position(IntPtr camera, [In] double[] position);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_camera_set_focal_point(IntPtr camera, [In] double[] focalPoint);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_camera_set_view_up(IntPtr camera, [In] double[] viewUp);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_camera_get_position(IntPtr camera, [Out] double[] position);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_camera_get_focal_point(IntPtr camera, [Out] double[] focalPoint);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_camera_get_view_up(IntPtr camera, [Out] double[] viewUp);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_camera_set_view_angle(IntPtr camera, double angle);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern double f3d_camera_get_view_angle(IntPtr camera);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_camera_reset_to_bounds(IntPtr camera, double zoomFactor);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_camera_azimuth(IntPtr camera, double angle);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_camera_elevation(IntPtr camera, double angle);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_camera_pan(IntPtr camera, double right, double up, double forward);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_camera_zoom(IntPtr camera, double factor);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_interactor_init_commands(IntPtr interactor);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_interactor_init_bindings(IntPtr interactor);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_interactor_enable_camera_movement(IntPtr interactor);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_interactor_trigger_mod_update(IntPtr interactor, int modifier);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_interactor_trigger_mouse_button(IntPtr interactor, int action, int button);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_interactor_trigger_mouse_position(IntPtr interactor, double xpos, double ypos);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_interactor_trigger_mouse_wheel(IntPtr interactor, int direction);

        [DllImport(F3DLibraryName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void f3d_interactor_trigger_event_loop(IntPtr interactor, double deltaTime);

        public sealed class F3DPreviewSession : IDisposable
        {
            private readonly BlockingCollection<PreviewWorkItem> _workItems = new BlockingCollection<PreviewWorkItem>();
            private readonly Thread _renderThread;
            private readonly object _disposeLock = new object();
            private IntPtr _originalEngine;
            private IntPtr _cleanEngine;
            private double _orthographicZoomFactor = 1.0;
            private F3DPreviewCameraSnapshot _pendingCameraSnapshot;
            private bool _previewCameraInitialized;
            private bool _disposed;

            internal F3DPreviewSession(byte[] stepData)
            {
                _renderThread = CreateRenderThread();
                _renderThread.Start();

                RunOnRenderThread(() =>
                {
                    _originalEngine = CreatePreviewEngine(stepData);
                    return 0;
                });
            }

            internal F3DPreviewSession(byte[] stepData, F3DPreviewCameraSnapshot cameraSnapshot)
            {
                _renderThread = CreateRenderThread();
                _renderThread.Start();

                RunOnRenderThread(() =>
                {
                    _originalEngine = CreatePreviewEngine(stepData);
                    _pendingCameraSnapshot = cameraSnapshot?.Clone();
                    return 0;
                });
            }

            internal F3DPreviewSession(byte[] originalStepData, byte[] cleanStepData)
            {
                _renderThread = CreateRenderThread();
                _renderThread.Start();

                RunOnRenderThread(() =>
                {
                    _originalEngine = CreatePreviewEngine(originalStepData);
                    _cleanEngine = CreatePreviewEngine(cleanStepData);
                    return 0;
                });
            }

            public F3DRenderedImage RenderInteractivePreviewImage(
                int width,
                int height,
                F3DPreviewCameraState cameraState)
            {
                return RenderInteractivePreviewImage(width, height, cameraState, null);
            }

            public F3DRenderedImage RenderInteractivePreviewImage(
                int width,
                int height,
                F3DPreviewCameraState cameraState,
                IReadOnlyList<F3DPreviewInteraction> interactions)
            {
                ValidatePreviewSize(width, height);

                return RunOnRenderThread(() =>
                {
                    lock (NativeRenderLock)
                    {
                        PrepareInteractivePreviewFrame(width, height, cameraState, interactions);
                        return RenderPreviewImage(_originalEngine, "preview");
                    }
                });
            }

            public F3DPreviewCameraSnapshot GetCameraSnapshot()
            {
                return GetCameraSnapshot(null);
            }

            public F3DPreviewCameraSnapshot GetCameraSnapshot(IReadOnlyList<F3DPreviewInteraction> interactions)
            {
                return RunOnRenderThread(() =>
                {
                    lock (NativeRenderLock)
                    {
                        EnsurePreviewCameraInitialized();
                        ApplyPreviewInteractions(interactions);
                        return CaptureCameraSnapshot(GetWindow(_originalEngine), _orthographicZoomFactor);
                    }
                });
            }

            public F3DPreviewRenderPair RenderInteractivePreview(
                int width,
                int height,
                F3DPreviewCameraState cameraState)
            {
                return RenderInteractivePreview(width, height, cameraState, null);
            }

            public F3DPreviewRenderPair RenderInteractivePreview(
                int width,
                int height,
                F3DPreviewCameraState cameraState,
                IReadOnlyList<F3DPreviewInteraction> interactions)
            {
                ValidatePreviewSize(width, height);
                if (_cleanEngine == IntPtr.Zero)
                    throw new InvalidOperationException("The preview session was created for one STEP model.");

                return RunOnRenderThread(() =>
                {
                    lock (NativeRenderLock)
                    {
                        PrepareInteractivePreviewFrame(width, height, cameraState, interactions);
                        return new F3DPreviewRenderPair
                        {
                            OriginalImage = RenderPreviewImage(_originalEngine, "original"),
                            CleanImage = RenderPreviewImage(_cleanEngine, "clean")
                        };
                    }
                });
            }

            public void Dispose()
            {
                lock (_disposeLock)
                {
                    if (_disposed)
                        return;

                    try
                    {
                        RunOnRenderThread(() =>
                        {
                            DeleteEngine(ref _originalEngine);
                            DeleteEngine(ref _cleanEngine);
                            return 0;
                        });
                    }
                    finally
                    {
                        _disposed = true;
                        _workItems.CompleteAdding();
                        if (Thread.CurrentThread != _renderThread)
                            _renderThread.Join();
                        _workItems.Dispose();
                    }
                }
            }

            private Thread CreateRenderThread()
            {
                return new Thread(RenderThreadMain)
                {
                    IsBackground = true,
                    Name = "EasyEDA F3D Preview Renderer"
                };
            }

            private static void ValidatePreviewSize(int width, int height)
            {
                if (width <= 0)
                    throw new ArgumentOutOfRangeException(nameof(width), "Preview width must be greater than zero.");
                if (height <= 0)
                    throw new ArgumentOutOfRangeException(nameof(height), "Preview height must be greater than zero.");
            }

            private void ApplyCameraSnapshot(F3DPreviewCameraSnapshot snapshot)
            {
                if (snapshot == null)
                    return;

                _orthographicZoomFactor = SanitizeOrthographicZoomFactor(snapshot.OrthographicZoomFactor);
                ApplyCameraSnapshot(GetWindow(_originalEngine), snapshot);
                SyncOriginalCameraToClean();
                _previewCameraInitialized = true;
            }

            private T RunOnRenderThread<T>(Func<T> action)
            {
                if (action == null)
                    throw new ArgumentNullException(nameof(action));
                if (_disposed)
                    throw new ObjectDisposedException(nameof(F3DPreviewSession));
                if (Thread.CurrentThread == _renderThread)
                    return action();

                var workItem = new PreviewWorkItem(() => action());
                try
                {
                    _workItems.Add(workItem);
                }
                catch (InvalidOperationException ex)
                {
                    throw new ObjectDisposedException(nameof(F3DPreviewSession), ex);
                }

                workItem.Wait();
                if (workItem.Exception != null)
                    ExceptionDispatchInfo.Capture(workItem.Exception).Throw();

                return (T)workItem.Result;
            }

            private void RenderThreadMain()
            {
                foreach (PreviewWorkItem workItem in _workItems.GetConsumingEnumerable())
                {
                    try
                    {
                        workItem.Result = workItem.Action();
                    }
                    catch (Exception ex)
                    {
                        workItem.Exception = ex;
                    }
                    finally
                    {
                        workItem.Complete();
                    }
                }
            }

            private sealed class PreviewWorkItem
            {
                private readonly ManualResetEventSlim _completed = new ManualResetEventSlim();

                public PreviewWorkItem(Func<object> action)
                {
                    Action = action;
                }

                public Func<object> Action { get; }
                public object Result { get; set; }
                public Exception Exception { get; set; }

                public void Complete()
                {
                    _completed.Set();
                }

                public void Wait()
                {
                    _completed.Wait();
                    _completed.Dispose();
                }
            }
            private static IntPtr CreatePreviewEngine(byte[] stepData)
            {
                IntPtr engine = CreateEngine();
                try
                {
                    IntPtr options = f3d_engine_get_options(engine);
                    if (options == IntPtr.Zero)
                        throw new InvalidOperationException("F3D options handle was not available.");
                    ConfigureRenderingOptions(options);
                    ConfigurePreviewInteractor(engine);

                    IntPtr scene = GetScene(engine);
                    GCHandle pinnedStepData = GCHandle.Alloc(stepData, GCHandleType.Pinned);
                    try
                    {
                        if (f3d_scene_add_buffer(scene, pinnedStepData.AddrOfPinnedObject(), (UIntPtr)stepData.Length) == 0)
                            throw new InvalidOperationException("F3D failed to load STEP data from memory.");
                    }
                    finally
                    {
                        pinnedStepData.Free();
                    }

                    return engine;
                }
                catch
                {
                    f3d_engine_delete(engine);
                    throw;
                }
            }

            private static F3DRenderedImage RenderPreviewImage(
                IntPtr engine,
                string name)
            {
                IntPtr window = GetWindow(engine);
                return RenderCurrentWindowToRawImage(window, name);
            }

            private void PreparePreviewWindows(int width, int height)
            {
                f3d_window_set_size(GetWindow(_originalEngine), width, height);
                if (_cleanEngine != IntPtr.Zero)
                    f3d_window_set_size(GetWindow(_cleanEngine), width, height);
            }

            private void PrepareInteractivePreviewFrame(
                int width,
                int height,
                F3DPreviewCameraState cameraState,
                IReadOnlyList<F3DPreviewInteraction> interactions)
            {
                PreparePreviewWindows(width, height);
                if (!ApplyPendingCameraSnapshot())
                {
                    if (cameraState != null)
                    {
                        ApplyInteractivePreviewCamera(GetWindow(_originalEngine), cameraState);
                        SyncOriginalCameraToClean();
                        _previewCameraInitialized = true;
                    }
                    else
                    {
                        EnsurePreviewCameraInitialized();
                    }
                }

                ApplyPreviewInteractions(interactions);
            }

            private bool ApplyPendingCameraSnapshot()
            {
                if (_pendingCameraSnapshot == null)
                    return false;

                F3DPreviewCameraSnapshot snapshot = _pendingCameraSnapshot;
                _pendingCameraSnapshot = null;
                ApplyCameraSnapshot(snapshot);
                return true;
            }

            private void EnsurePreviewCameraInitialized()
            {
                if (_previewCameraInitialized)
                    return;

                ResetOriginalCameraToBounds();
                SyncOriginalCameraToClean();
                _previewCameraInitialized = true;
            }

            private void ApplyPreviewInteractions(IReadOnlyList<F3DPreviewInteraction> interactions)
            {
                if (interactions == null || interactions.Count == 0)
                    return;

                EnsurePreviewCameraInitialized();
                IntPtr interactor = GetInteractor(_originalEngine);
                foreach (F3DPreviewInteraction interaction in interactions)
                {
                    if (interaction == null)
                        continue;

                    if (interaction.Kind == F3DPreviewInteractionKind.ResetCamera)
                    {
                        ResetOriginalCameraToBounds();
                        _orthographicZoomFactor = 1.0;
                        continue;
                    }

                    f3d_interactor_trigger_mod_update(interactor, (int)interaction.Modifier);
                    if (interaction.Kind == F3DPreviewInteractionKind.MousePosition ||
                        interaction.Kind == F3DPreviewInteractionKind.MouseButtonPress ||
                        interaction.Kind == F3DPreviewInteractionKind.MouseButtonRelease ||
                        interaction.Kind == F3DPreviewInteractionKind.MouseWheel)
                    {
                        f3d_interactor_trigger_mouse_position(interactor, interaction.X, interaction.Y);
                    }

                    if (interaction.Kind == F3DPreviewInteractionKind.MouseButtonPress)
                        f3d_interactor_trigger_mouse_button(interactor, 0, (int)interaction.Button);
                    else if (interaction.Kind == F3DPreviewInteractionKind.MouseButtonRelease)
                        f3d_interactor_trigger_mouse_button(interactor, 1, (int)interaction.Button);
                    else if (interaction.Kind == F3DPreviewInteractionKind.MouseWheel)
                    {
                        f3d_interactor_trigger_mouse_wheel(interactor, (int)interaction.WheelDirection);
                        if (interaction.WheelDirection == F3DPreviewWheelDirection.Forward)
                            _orthographicZoomFactor *= PreviewMouseWheelZoomFactor;
                        else
                            _orthographicZoomFactor /= PreviewMouseWheelZoomFactor;
                    }

                    f3d_interactor_trigger_event_loop(interactor, 1.0 / 60.0);
                }

                SyncOriginalCameraToClean();
            }

            private void ResetOriginalCameraToBounds()
            {
                IntPtr camera = f3d_window_get_camera(GetWindow(_originalEngine));
                if (camera == IntPtr.Zero)
                    throw new InvalidOperationException("F3D camera handle was not available.");

                f3d_camera_reset_to_bounds(camera, 0.9);
            }

            private void SyncOriginalCameraToClean()
            {
                if (_cleanEngine == IntPtr.Zero)
                    return;

                ApplyCameraSnapshot(
                    GetWindow(_cleanEngine),
                    CaptureCameraSnapshot(GetWindow(_originalEngine), _orthographicZoomFactor));
            }

            private static void CopyCameraState(IntPtr sourceWindow, IntPtr destinationWindow)
            {
                ApplyCameraSnapshot(destinationWindow, CaptureCameraSnapshot(sourceWindow, 1.0));
            }

            private static F3DPreviewCameraSnapshot CaptureCameraSnapshot(IntPtr window, double orthographicZoomFactor)
            {
                IntPtr camera = f3d_window_get_camera(window);
                if (camera == IntPtr.Zero)
                    throw new InvalidOperationException("F3D camera handle was not available.");

                double[] position = new double[3];
                double[] focalPoint = new double[3];
                double[] viewUp = new double[3];
                f3d_camera_get_position(camera, position);
                f3d_camera_get_focal_point(camera, focalPoint);
                f3d_camera_get_view_up(camera, viewUp);

                return new F3DPreviewCameraSnapshot
                {
                    Position = position,
                    FocalPoint = focalPoint,
                    ViewUp = viewUp,
                    ViewAngle = f3d_camera_get_view_angle(camera),
                    OrthographicZoomFactor = SanitizeOrthographicZoomFactor(orthographicZoomFactor)
                };
            }

            private static void ApplyCameraSnapshot(IntPtr window, F3DPreviewCameraSnapshot snapshot)
            {
                IntPtr camera = f3d_window_get_camera(window);
                if (camera == IntPtr.Zero)
                    throw new InvalidOperationException("F3D camera handle was not available.");
                if (!IsValidCameraVector(snapshot.Position) ||
                    !IsValidCameraVector(snapshot.FocalPoint) ||
                    !IsValidCameraVector(snapshot.ViewUp))
                {
                    return;
                }

                double orthographicZoomFactor = SanitizeOrthographicZoomFactor(snapshot.OrthographicZoomFactor);
                f3d_camera_reset_to_bounds(camera, 0.9);
                f3d_camera_set_position(camera, snapshot.Position);
                f3d_camera_set_focal_point(camera, snapshot.FocalPoint);
                f3d_camera_set_view_up(camera, snapshot.ViewUp);
                if (!double.IsNaN(snapshot.ViewAngle) &&
                    !double.IsInfinity(snapshot.ViewAngle) &&
                    snapshot.ViewAngle > 0.0)
                {
                    f3d_camera_set_view_angle(camera, snapshot.ViewAngle);
                }
                if (orthographicZoomFactor != 1.0)
                    f3d_camera_zoom(camera, orthographicZoomFactor);
            }

            private static bool IsValidCameraVector(double[] vector)
            {
                return vector != null &&
                    vector.Length >= 3 &&
                    vector.Take(3).All(value => !double.IsNaN(value) && !double.IsInfinity(value));
            }

            private static double SanitizeOrthographicZoomFactor(double zoomFactor)
            {
                if (double.IsNaN(zoomFactor) || double.IsInfinity(zoomFactor) || zoomFactor <= 0.0)
                    return 1.0;

                return zoomFactor;
            }

            private static void ConfigurePreviewInteractor(IntPtr engine)
            {
                IntPtr interactor = GetInteractor(engine);
                f3d_interactor_init_commands(interactor);
                f3d_interactor_init_bindings(interactor);
                f3d_interactor_enable_camera_movement(interactor);
            }

            private static IntPtr GetInteractor(IntPtr engine)
            {
                IntPtr interactor = f3d_engine_get_interactor(engine);
                if (interactor == IntPtr.Zero)
                    throw new InvalidOperationException("F3D interactor handle was not available.");
                return interactor;
            }

            private static void DeleteEngine(ref IntPtr engine)
            {
                if (engine == IntPtr.Zero)
                    return;

                f3d_engine_delete(engine);
                engine = IntPtr.Zero;
            }
        }

        private readonly struct ViewSpec
        {
            public readonly string Name;
            public readonly double DirectionX;
            public readonly double DirectionY;
            public readonly double DirectionZ;
            public readonly double UpX;
            public readonly double UpY;
            public readonly double UpZ;

            public ViewSpec(
                string name,
                double directionX,
                double directionY,
                double directionZ,
                double upX,
                double upY,
                double upZ)
            {
                Name = name;
                DirectionX = directionX;
                DirectionY = directionY;
                DirectionZ = directionZ;
                UpX = upX;
                UpY = upY;
                UpZ = upZ;
            }
        }
    }
}
