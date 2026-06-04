using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

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

    public static class F3DProjectionRenderer
    {
        private const string F3DLibraryName = "f3d_c_api";
        private const int PngFormat = 0;

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
            if (stepData == null || stepData.Length == 0)
                throw new ArgumentException("STEP data is required.", nameof(stepData));

            ViewSpec[] views = ParseViews(viewNames);
            ValidateSize(sizePixels);
            ConfigureNativeAccess();

            lock (NativeRenderLock)
                return RenderRawImagesCore(stepData, sizePixels, views);
        }

        private static IReadOnlyList<F3DRenderedImage> RenderRawImagesCore(
            byte[] stepData,
            int sizePixels,
            ViewSpec[] views)
        {
            IntPtr engine = CreateEngine();
            try
            {
                ConfigureScene(engine, sizePixels);
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
            ValidateSize(sizePixels);
            ConfigureNativeAccess();
            Directory.CreateDirectory(outputDirectory);

            lock (NativeRenderLock)
                return RenderPngFilesFromFileCore(inputPath, outputDirectory, sizePixels, views);
        }

        private static IReadOnlyList<F3DRenderedFile> RenderPngFilesFromFileCore(
            string inputPath,
            string outputDirectory,
            int sizePixels,
            ViewSpec[] views)
        {
            IntPtr engine = CreateEngine();
            try
            {
                ConfigureScene(engine, sizePixels);
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
            if (sizePixels <= 0)
                throw new ArgumentException("Image size must be greater than zero.", nameof(sizePixels));
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

        private static void ConfigureScene(IntPtr engine, int sizePixels)
        {
            IntPtr options = f3d_engine_get_options(engine);
            if (options == IntPtr.Zero)
                throw new InvalidOperationException("F3D options handle was not available.");
            ConfigureRenderingOptions(options);

            IntPtr window = GetWindow(engine);
            f3d_window_set_size(window, sizePixels, sizePixels);
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

            IntPtr image = f3d_window_render_to_image(window, 0);
            if (image == IntPtr.Zero)
                throw new InvalidOperationException("F3D render failed for view " + view.Name + ".");

            try
            {
                int width = checked((int)f3d_image_get_width(image));
                int height = checked((int)f3d_image_get_height(image));
                int channelCount = checked((int)f3d_image_get_channel_count(image));
                int channelType = f3d_image_get_channel_type(image);
                int channelTypeSize = checked((int)f3d_image_get_channel_type_size(image));
                IntPtr content = f3d_image_get_content(image);
                if (content == IntPtr.Zero || width <= 0 || height <= 0 || channelCount <= 0 || channelTypeSize <= 0)
                    throw new InvalidOperationException("F3D raw image content was not available for view " + view.Name + ".");

                int byteCount = checked(width * height * channelCount * channelTypeSize);
                var rawBytes = new byte[byteCount];
                Marshal.Copy(content, rawBytes, 0, byteCount);
                return new F3DRenderedImage
                {
                    Name = view.Name,
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

        private static void ConfigureRenderingOptions(IntPtr options)
        {
            f3d_options_set_as_bool(options, "scene.camera.orthographic", 1);
            f3d_options_set_as_bool(options, "model.scivis.enable", 1);
            f3d_options_set_as_bool(options, "model.scivis.cells", 1);
            f3d_options_set_as_string(options, "model.scivis.array_name", "Colors");
            f3d_options_set_as_int(options, "model.scivis.component", -2);
            f3d_options_set_as_string(options, "render.effect.antialiasing.mode", "fxaa");
            f3d_options_set_as_bool(options, "render.effect.antialiasing.enable", 1);
            f3d_options_set_as_bool(options, "render.effect.ambient_occlusion", 1);
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
                            return NativeLibrary.Load(libraryPath);
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
            var candidates = new List<string>
            {
                Path.Combine(baseDirectory, "f3d_c_api.dll"),
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

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

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
        private static extern void f3d_camera_reset_to_bounds(IntPtr camera, double zoomFactor);

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
