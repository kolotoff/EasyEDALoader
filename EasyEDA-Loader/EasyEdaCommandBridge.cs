using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        public const string CommandExportComponentAssembly = "export-component-assembly";
        public const string CommandExportBoardAssembly = "export-board-assembly";
        public const string CommandExportBoard3D = "export-board-3d";

        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task listenTask;
        private bool disposed;

        public event Func<CommandRequest, CommandResponse> CommandReceived;

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
            CommandRequest parsedRequest = CommandRequest.Parse(request);
            string command = NormalizeCommand(parsedRequest.Command);
            if (string.IsNullOrWhiteSpace(command))
                return CommandResponse.Error("invalid-command", "Missing or unknown EasyEDALoader command.");
            parsedRequest.Command = command;

            if (RequiresActiveAltiumWindow(command) && !IsAltiumWindowActive())
            {
                return CommandResponse.Error(
                    "altium-not-active",
                    "Altium window must be active before running EasyEDALoader bridge commands.");
            }

            Func<CommandRequest, CommandResponse> handler = CommandReceived;
            if (handler == null)
                return CommandResponse.Error("bridge-not-ready", "EasyEDALoader command bridge is not ready.");

            try
            {
                return handler(parsedRequest) ?? CommandResponse.Ok(command);
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace("EasyEdaCommandBridge command failed: " + ex);
                return CommandResponse.Error("command-failed", ex.Message, command);
            }
        }

        private static bool RequiresActiveAltiumWindow(string command)
        {
            return !string.Equals(command, CommandExportComponentAssembly, StringComparison.Ordinal)
                && !string.Equals(command, CommandExportBoardAssembly, StringComparison.Ordinal)
                && !string.Equals(command, CommandExportBoard3D, StringComparison.Ordinal);
        }

        private static string NormalizeCommand(string value)
        {
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
                case "component-assembly":
                case "export-assembly-component":
                case "export-component-assembly":
                    return CommandExportComponentAssembly;
                case "board-assembly":
                case "export-assembly-board":
                case "export-board-assembly":
                    return CommandExportBoardAssembly;
                case "board-3d":
                case "export-3d-board":
                case "export-board-3d":
                    return CommandExportBoard3D;
                default:
                    return string.Empty;
            }
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

        internal sealed class CommandRequest
        {
            private readonly JObject values;

            private CommandRequest(string command, JObject values)
            {
                Command = command ?? string.Empty;
                this.values = values ?? new JObject();
            }

            public string Command { get; set; }

            public string GetString(string name, string defaultValue = "")
            {
                JToken token = values.GetValue(name, StringComparison.OrdinalIgnoreCase);
                return token == null || token.Type == JTokenType.Null
                    ? defaultValue
                    : token.ToString();
            }

            public int GetInt32(string name, int defaultValue)
            {
                JToken token = values.GetValue(name, StringComparison.OrdinalIgnoreCase);
                return token != null && int.TryParse(token.ToString(), out int result)
                    ? result
                    : defaultValue;
            }

            public bool GetBoolean(string name, bool defaultValue)
            {
                JToken token = values.GetValue(name, StringComparison.OrdinalIgnoreCase);
                return token != null && bool.TryParse(token.ToString(), out bool result)
                    ? result
                    : defaultValue;
            }

            public static CommandRequest Parse(string request)
            {
                string value = (request ?? string.Empty).Trim();
                if (!value.StartsWith("{", StringComparison.Ordinal))
                    return new CommandRequest(value, null);

                try
                {
                    JObject json = JObject.Parse(value);
                    return new CommandRequest(
                        Convert.ToString(json.GetValue("command", StringComparison.OrdinalIgnoreCase)),
                        json);
                }
                catch (JsonException)
                {
                    return new CommandRequest(string.Empty, null);
                }
            }
        }

        internal sealed class CommandResponse
        {
            public bool Success { get; private set; }
            public string Command { get; private set; }
            public string ErrorCode { get; private set; }
            public string Message { get; private set; }
            public JObject Data { get; private set; }

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

            public CommandResponse WithData(string name, object value)
            {
                if (Data == null)
                    Data = new JObject();
                Data[name] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
                return this;
            }

            public string ToJson()
            {
                var json = new JObject
                {
                    ["success"] = Success,
                    ["command"] = Command ?? string.Empty,
                    ["errorCode"] = ErrorCode ?? string.Empty,
                    ["message"] = Message ?? string.Empty
                };
                if (Data != null)
                {
                    foreach (JProperty property in Data.Properties())
                        json[property.Name] = property.Value;
                }
                return json.ToString(Formatting.None);
            }
        }
    }
}
