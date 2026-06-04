using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace StepF3DRender
{
    internal static class Program
    {
        private const string F3DLibraryName = "f3d_c_api";
        private const int PngFormat = 0;
        private const int Success = 0;
        private const int UsageError = 2;
        private const int RuntimeError = 1;

        private static readonly ViewSpec[] Views =
        {
            new ViewSpec("x_plus", -1, 0, 0, 0, 0, 1),
            new ViewSpec("x_minus", 1, 0, 0, 0, 0, 1),
            new ViewSpec("y_plus", 0, -1, 0, 0, 0, 1),
            new ViewSpec("y_minus", 0, 1, 0, 0, 0, 1),
            new ViewSpec("z_plus", 0, 0, -1, 0, 1, 0),
            new ViewSpec("z_minus", 0, 0, 1, 0, 1, 0)
        };

        private static string _f3dBinDirectory;

        private static int Main(string[] args)
        {
            try
            {
                RenderRequest request = ParseArguments(args);
                if (request == null)
                    return UsageError;

                if (!request.WriteRawImagesToStdout)
                    Directory.CreateDirectory(request.OutputDirectory);
                ConfigureNativeLibraryResolver();
                ConfigureF3DLibrarySearchPath();

                var stopwatch = Stopwatch.StartNew();
                if (request.WriteRawImagesToStdout)
                {
                    string json = RenderSixSidesRawJson(request, stopwatch);
                    Console.WriteLine(json);
                }
                else
                {
                    RenderSixSides(request);
                    stopwatch.Stop();
                    Console.WriteLine("six_side_f3d_library_ms=" + stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                }
                return Success;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return RuntimeError;
            }
        }

        private static RenderRequest ParseArguments(string[] args)
        {
            if (args == null || args.Length < 3 ||
                (!IsOption(args[0], "--six-sides") && !IsOption(args[0], "--six-sides-stdout")))
            {
                WriteUsage();
                return null;
            }

            bool writeRawImagesToStdout = IsOption(args[0], "--six-sides-stdout");
            string inputPath = args[1];
            string outputDirectory = writeRawImagesToStdout ? null : args[2];
            string modelName = writeRawImagesToStdout ? args[2] : null;
            int sizePixels = 1600;
            ViewSpec[] requestedViews = Views;

            for (int i = 3; i < args.Length; i++)
            {
                if (IsOption(args[i], "--size") && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out sizePixels))
                        throw new ArgumentException("Invalid --size value.");
                    continue;
                }

                if (IsOption(args[i], "--views"))
                {
                    if (i + 1 >= args.Length)
                        throw new ArgumentException("--views must include at least one view name.");
                    requestedViews = ParseViews(args[++i]);
                    continue;
                }

                throw new ArgumentException("Unknown argument: " + args[i]);
            }

            if (string.IsNullOrWhiteSpace(inputPath) ||
                (!writeRawImagesToStdout || inputPath != "-") && !File.Exists(inputPath))
                throw new FileNotFoundException("Input STEP file was not found.", inputPath);
            if (!writeRawImagesToStdout && string.IsNullOrWhiteSpace(outputDirectory))
                throw new ArgumentException("Output directory is required.");
            if (writeRawImagesToStdout && string.IsNullOrWhiteSpace(modelName))
                throw new ArgumentException("Model name is required.");
            if (sizePixels <= 0)
                throw new ArgumentException("--size must be greater than zero.");

            return new RenderRequest
            {
                InputPath = inputPath == "-" ? inputPath : Path.GetFullPath(inputPath),
                OutputDirectory = string.IsNullOrEmpty(outputDirectory) ? null : Path.GetFullPath(outputDirectory),
                ModelName = modelName,
                SizePixels = sizePixels,
                Views = requestedViews,
                WriteRawImagesToStdout = writeRawImagesToStdout
            };
        }

        private static ViewSpec[] ParseViews(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("--views must include at least one view name.");

            var selectedViews = new List<ViewSpec>();
            var selectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string rawName in value.Split(','))
            {
                string name = rawName.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                ViewSpec view = FindView(name);
                if (string.IsNullOrWhiteSpace(view.Name))
                    throw new ArgumentException("Unknown view name: " + name);

                if (selectedNames.Add(view.Name))
                    selectedViews.Add(view);
            }

            if (selectedViews.Count == 0)
                throw new ArgumentException("--views must include at least one view name.");

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

        private static void WriteUsage()
        {
            Console.Error.WriteLine("Usage: StepF3DRender --six-sides <input.step> <output-directory> [--size pixels] [--views x_plus,y_plus,z_plus]");
            Console.Error.WriteLine("Usage: StepF3DRender --six-sides-stdout <input.step|-> <model-name> [--size pixels] [--views x_plus,y_plus,z_plus]");
        }

        private static bool IsOption(string actual, string expected)
        {
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static void RenderSixSides(RenderRequest request)
        {
            string loadedInputPath = PrepareInputPath(request, out string temporaryInputPath);
            try
            {
                RenderSixSidesFromPath(request, loadedInputPath);
            }
            finally
            {
                DeleteTemporaryInputPath(temporaryInputPath);
            }
        }

        private static string RenderSixSidesRawJson(RenderRequest request, Stopwatch stopwatch)
        {
            string loadedInputPath = PrepareInputPath(request, out string temporaryInputPath);
            try
            {
                IReadOnlyList<RawRenderedView> images = RenderRawViewsFromPath(request, loadedInputPath);
                stopwatch.Stop();
                return WriteRawViewsJson(images, stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                DeleteTemporaryInputPath(temporaryInputPath);
            }
        }

        private static void RenderSixSidesFromPath(RenderRequest request, string inputPath)
        {
            f3d_engine_autoload_plugins();
            f3d_engine_load_plugin("occt");

            IntPtr engine = f3d_engine_create_wgl(1);
            if (engine == IntPtr.Zero)
                engine = f3d_engine_create(1);
            if (engine == IntPtr.Zero)
                throw new InvalidOperationException("F3D engine creation failed.");

            try
            {
                IntPtr options = f3d_engine_get_options(engine);
                if (options == IntPtr.Zero)
                    throw new InvalidOperationException("F3D options handle was not available.");
                ConfigureRenderingOptions(options);

                IntPtr window = f3d_engine_get_window(engine);
                if (window == IntPtr.Zero)
                    throw new InvalidOperationException("F3D window handle was not available.");
                f3d_window_set_size(window, request.SizePixels, request.SizePixels);

                IntPtr scene = f3d_engine_get_scene(engine);
                if (scene == IntPtr.Zero)
                    throw new InvalidOperationException("F3D scene handle was not available.");
                if (f3d_scene_add(scene, inputPath) == 0)
                    throw new InvalidOperationException("F3D failed to load STEP file: " + inputPath);

                string modelName = Path.GetFileNameWithoutExtension(inputPath);
                foreach (ViewSpec view in request.Views)
                {
                    string outputPath = Path.Combine(request.OutputDirectory, modelName + "__" + view.Name + ".png");
                    RenderView(window, view, outputPath);
                    Console.WriteLine("view=" + view.Name + " output=" + outputPath);
                }
            }
            finally
            {
                f3d_engine_delete(engine);
            }
        }

        private static IReadOnlyList<RawRenderedView> RenderRawViewsFromPath(RenderRequest request, string inputPath)
        {
            f3d_engine_autoload_plugins();
            f3d_engine_load_plugin("occt");

            IntPtr engine = f3d_engine_create_wgl(1);
            if (engine == IntPtr.Zero)
                engine = f3d_engine_create(1);
            if (engine == IntPtr.Zero)
                throw new InvalidOperationException("F3D engine creation failed.");

            try
            {
                IntPtr options = f3d_engine_get_options(engine);
                if (options == IntPtr.Zero)
                    throw new InvalidOperationException("F3D options handle was not available.");
                ConfigureRenderingOptions(options);

                IntPtr window = f3d_engine_get_window(engine);
                if (window == IntPtr.Zero)
                    throw new InvalidOperationException("F3D window handle was not available.");
                f3d_window_set_size(window, request.SizePixels, request.SizePixels);

                IntPtr scene = f3d_engine_get_scene(engine);
                if (scene == IntPtr.Zero)
                    throw new InvalidOperationException("F3D scene handle was not available.");
                if (f3d_scene_add(scene, inputPath) == 0)
                    throw new InvalidOperationException("F3D failed to load STEP file: " + inputPath);

                var result = new List<RawRenderedView>();
                foreach (ViewSpec view in request.Views)
                    result.Add(RenderViewToRawImage(window, view));

                return result;
            }
            finally
            {
                f3d_engine_delete(engine);
            }
        }

        private static string PrepareInputPath(RenderRequest request, out string temporaryInputPath)
        {
            temporaryInputPath = null;
            if (!request.WriteRawImagesToStdout || request.InputPath != "-")
                return request.InputPath;

            temporaryInputPath = Path.Combine(Path.GetTempPath(), "StepF3DRender_" + Guid.NewGuid().ToString("N") + ".step");
            using (Stream input = Console.OpenStandardInput())
            using (Stream output = File.Create(temporaryInputPath))
                input.CopyTo(output);
            return temporaryInputPath;
        }

        private static void DeleteTemporaryInputPath(string temporaryInputPath)
        {
            if (string.IsNullOrWhiteSpace(temporaryInputPath))
                return;

            try { File.Delete(temporaryInputPath); }
            catch { }
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

        private static void RenderView(IntPtr window, ViewSpec view, string outputPath)
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

        private static RawRenderedView RenderViewToRawImage(IntPtr window, ViewSpec view)
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
                return new RawRenderedView
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

        private static string WriteRawViewsJson(IReadOnlyList<RawRenderedView> images, long elapsedMilliseconds)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"elapsedMs\":");
            builder.Append(elapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"views\":[");
            for (int i = 0; i < images.Count; i++)
            {
                if (i > 0)
                    builder.Append(',');

                RawRenderedView image = images[i];
                builder.Append('{');
                builder.Append("\"name\":\"");
                builder.Append(image.Name);
                builder.Append("\",\"width\":");
                builder.Append(image.Width.ToString(CultureInfo.InvariantCulture));
                builder.Append(",\"height\":");
                builder.Append(image.Height.ToString(CultureInfo.InvariantCulture));
                builder.Append(",\"channelCount\":");
                builder.Append(image.ChannelCount.ToString(CultureInfo.InvariantCulture));
                builder.Append(",\"channelType\":");
                builder.Append(image.ChannelType.ToString(CultureInfo.InvariantCulture));
                builder.Append(",\"channelTypeSize\":");
                builder.Append(image.ChannelTypeSize.ToString(CultureInfo.InvariantCulture));
                builder.Append(",\"rawBase64\":\"");
                builder.Append(Convert.ToBase64String(image.RawBytes));
                builder.Append("\"}");
            }

            builder.Append("]}");
            return builder.ToString();
        }

        private static void ConfigureNativeLibraryResolver()
        {
            NativeLibrary.SetDllImportResolver(
                Assembly.GetExecutingAssembly(),
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
        }

        private static void ConfigureF3DLibrarySearchPath()
        {
            string libraryPath = FindF3DLibraryPath();
            if (string.IsNullOrWhiteSpace(libraryPath))
                throw new FileNotFoundException("f3d_c_api.dll was not found. Set STEPCLEANER_F3D_LIB or install F3D.");

            _f3dBinDirectory = Path.GetDirectoryName(libraryPath);
            SetDllDirectory(_f3dBinDirectory);
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

        private sealed class RenderRequest
        {
            public string InputPath { get; set; }
            public string OutputDirectory { get; set; }
            public string ModelName { get; set; }
            public int SizePixels { get; set; }
            public ViewSpec[] Views { get; set; }
            public bool WriteRawImagesToStdout { get; set; }
        }

        private sealed class RawRenderedView
        {
            public string Name { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public int ChannelCount { get; set; }
            public int ChannelType { get; set; }
            public int ChannelTypeSize { get; set; }
            public byte[] RawBytes { get; set; }
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
