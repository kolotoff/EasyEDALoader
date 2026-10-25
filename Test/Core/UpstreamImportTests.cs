using EasyEDA_Loader;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

internal static class UpstreamImportTests
{
    internal static async Task SearchKeyword()
    {
        const string keyword = "C1 &other=value/+";
        using var handler = new SearchHandler();
        using var client = new HttpClient(handler);
        await new EasyedaApi(client).SearchProductInfoAsync(keyword);
        if (handler.Query != "?keyword=" + Uri.EscapeDataString(keyword))
            throw new InvalidOperationException("Required escaped keyword query was not sent: " + handler.Query);
    }

    internal static void NullableTimestamps()
    {
        foreach (string value in new[] { "null", "\"\"", "123" })
        {
            string json = "{\"utime\":" + value + "}";
            var footprint = JsonConvert.DeserializeObject<FootprintHead>(json);
            var symbol = JsonConvert.DeserializeObject<SymbolHead>(json);
            int? expected = value == "123" ? 123 : null;
            if (footprint.Utime != expected || symbol.Utime != expected)
                throw new InvalidOperationException("Timestamp was not preserved for " + value);
        }
        if (JsonConvert.DeserializeObject<FootprintHead>("{}").Utime != null ||
            JsonConvert.DeserializeObject<SymbolHead>("{}").Utime != null)
            throw new InvalidOperationException("Missing timestamp must stay unknown.");
    }

    internal static void OptionalLayers()
    {
        foreach (string layer in new[] { "BoardOutLine", "BottomAssembly" })
            if (FootprintLayerMap.NormalizeLayerName(layer, true) != null)
                throw new InvalidOperationException("Additional layer imported by default: " + layer);
        if (FootprintLayerMap.NormalizeLayerName("BoardOutLine", false, true) != "BoardOutline" ||
            FootprintLayerMap.NormalizeLayerName("BottomAssembly", false, true) != "TopAssembly")
            throw new InvalidOperationException("Additional layers were not mapped when enabled.");
        if (FootprintLayerMap.NormalizeLayerName("Document", false, true) != null ||
            FootprintLayerMap.NormalizeLayerName("BoardOutline", false) != "BoardOutline")
            throw new InvalidOperationException("Existing layer policy changed.");
        if (new ComponentSelection().ImportAdditionalLayers || new EeFootprintContext().ImportAdditionalLayers)
            throw new InvalidOperationException("Additional layers must default off.");
    }

    internal static void Diagnostics()
    {
        string directory = Path.Combine(Path.GetTempPath(), "import-log-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "errors.log");
        try
        {
            var log = new ImportLog(path);
            log.Error("Footprint C123", new InvalidOperationException("outer", new Exception("inner details")));
            string text = File.ReadAllText(path);
            if (log.ErrorCount != 1 || log.WriteFailed || !text.Contains("C123") || !text.Contains("inner details"))
                throw new InvalidOperationException("Full exception context was not retained.");
            if (new ImportLog(path).ErrorCount != 0)
                throw new InvalidOperationException("Warning counts leaked between imports.");
            var blocked = new ImportLog(Path.Combine(path, "cannot-write.log"));
            blocked.Error("test", new Exception("write failure"));
            if (!blocked.WriteFailed || blocked.ErrorCount != 1)
                throw new InvalidOperationException("An unavailable log must not interrupt or hide the warning count.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class SearchHandler : HttpMessageHandler
    {
        internal string Query;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Query = request.RequestUri.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"result\":{\"productList\":[]}}")
            });
        }
    }
}
