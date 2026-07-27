using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyEDA_Loader
{
    internal sealed class EasyEdaCommandBridge : IDisposable
    {
        public const string PipeName = "EasyEDA-Loader.CommandBridge";
        public const string CommandOpenLoader = "open-loader";
        public const string CommandReproject3D = "reproject-3d";
        public const string CommandAlign3DModel = "align-3d-model";
        public const string CommandLayerTop = "layer-top";
        public const string CommandLayerBottom = "layer-bottom";
        public const string CommandLayerNext = "layer-next";
        public const string CommandLayerPrevious = "layer-previous";
        public const string CommandLayerSelectedPrimitive = "layer-selected-primitive";

        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task listenTask;
        private bool disposed;

        public event Func<string, CommandResponse> CommandReceived;

        public void Start()
        {
            if (listenTask != null)
                return;

            listenTask = Task.Run(() => ListenAsync(cancellation.Token));
        }

        private async Task ListenAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous))
                    {
                        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                        await HandleClientAsync(pipe, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException ex) when (IsPipeInstanceBusy(ex))
                {
                    EasyEDALoaderModule.Trace("EasyEdaCommandBridge disabled in this Altium process because another EasyEDALoader bridge is already listening.");
                    return;
                }
                catch (Exception ex)
                {
                    EasyEDALoaderModule.Trace("EasyEdaCommandBridge listen failed: " + ex);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static bool IsPipeInstanceBusy(IOException exception)
        {
            return exception != null
                && exception.Message.IndexOf("All pipe instances are busy", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task HandleClientAsync(Stream stream, CancellationToken cancellationToken)
        {
            using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
            {
                string request = await reader.ReadLineAsync().ConfigureAwait(false);
                CommandResponse response = ExecuteRequest(request);
                await writer.WriteLineAsync(response.ToJson()).ConfigureAwait(false);
            }
        }

        private CommandResponse ExecuteRequest(string request)
        {
            string command = NormalizeCommand(request);
            if (string.IsNullOrWhiteSpace(command))
                return CommandResponse.Error("invalid-command", "Missing or unknown EasyEDALoader command.");

            if (!IsAltiumWindowActive())
            {
                return CommandResponse.Error(
                    "altium-not-active",
                    "Altium window must be active before running EasyEDALoader bridge commands.");
            }

            Func<string, CommandResponse> handler = CommandReceived;
            if (handler == null)
                return CommandResponse.Error("bridge-not-ready", "EasyEDALoader command bridge is not ready.");

            try
            {
                return handler(command) ?? CommandResponse.Ok(command);
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("EasyEdaCommandBridge command failed: " + ex);
                return CommandResponse.Error("command-failed", ex.Message, command);
            }
        }

        private static string NormalizeCommand(string request)
        {
            string value = (request ?? string.Empty).Trim();
            if (value.StartsWith("{", StringComparison.Ordinal))
                value = ExtractJsonStringValue(value, "command");

            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            switch (value.Trim().ToLowerInvariant())
            {
                case "open":
                case "run":
                case "loader":
                case "open-loader":
                    return CommandOpenLoader;
                case "reproject":
                case "reproject3d":
                case "reproject-3d":
                    return CommandReproject3D;
                case "align":
                case "align3d":
                case "align-3d-model":
                    return CommandAlign3DModel;
                case "top":
                case "layer-top":
                    return CommandLayerTop;
                case "bottom":
                case "layer-bottom":
                    return CommandLayerBottom;
                case "next":
                case "layer-next":
                    return CommandLayerNext;
                case "previous":
                case "prev":
                case "layer-previous":
                    return CommandLayerPrevious;
                case "selected":
                case "selected-layer":
                case "selected-primitive":
                case "selected-primitive-layer":
                case "layer-selected-primitive":
                    return CommandLayerSelectedPrimitive;
                default:
                    return string.Empty;
            }
        }

        private static string ExtractJsonStringValue(string json, string propertyName)
        {
            string quotedProperty = "\"" + propertyName + "\"";
            int propertyIndex = json.IndexOf(quotedProperty, StringComparison.OrdinalIgnoreCase);
            if (propertyIndex < 0)
                return string.Empty;

            int colonIndex = json.IndexOf(':', propertyIndex + quotedProperty.Length);
            if (colonIndex < 0)
                return string.Empty;

            int valueStart = json.IndexOf('"', colonIndex + 1);
            if (valueStart < 0)
                return string.Empty;

            int valueEnd = json.IndexOf('"', valueStart + 1);
            if (valueEnd <= valueStart)
                return string.Empty;

            return json.Substring(valueStart + 1, valueEnd - valueStart - 1);
        }

        private static bool IsAltiumWindowActive()
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero)
                return false;

            uint foregroundProcessId;
            GetWindowThreadProcessId(foregroundWindow, out foregroundProcessId);
            return foregroundProcessId == Process.GetCurrentProcess().Id;
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            cancellation.Cancel();
            cancellation.Dispose();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        internal sealed class CommandResponse
        {
            public bool Success { get; private set; }
            public string Command { get; private set; }
            public string ErrorCode { get; private set; }
            public string Message { get; private set; }

            public static CommandResponse Ok(string command)
            {
                return new CommandResponse
                {
                    Success = true,
                    Command = command,
                    Message = "ok"
                };
            }

            public static CommandResponse Error(string errorCode, string message, string command = "")
            {
                return new CommandResponse
                {
                    Success = false,
                    Command = command ?? string.Empty,
                    ErrorCode = errorCode,
                    Message = message
                };
            }

            public string ToJson()
            {
                return "{" +
                    "\"success\":" + (Success ? "true" : "false") + "," +
                    "\"command\":\"" + JsonEscape(Command) + "\"," +
                    "\"errorCode\":\"" + JsonEscape(ErrorCode) + "\"," +
                    "\"message\":\"" + JsonEscape(Message) + "\"" +
                    "}";
            }

            private static string JsonEscape(string value)
            {
                return (value ?? string.Empty)
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"");
            }
        }
    }
}
