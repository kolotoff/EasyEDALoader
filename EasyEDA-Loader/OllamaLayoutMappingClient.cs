using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyEDA_Loader
{
    internal sealed class OllamaLayoutMappingClient
    {
        private static readonly Uri BaseUri = new Uri("http://localhost:11434/");
        private readonly HttpClient httpClient;

        public OllamaLayoutMappingClient(HttpClient httpClient = null)
        {
            this.httpClient = httpClient ?? new HttpClient { BaseAddress = BaseUri, Timeout = TimeSpan.FromMinutes(5) };
        }

        public async Task<IReadOnlyList<string>> GetInstalledModelsAsync(CancellationToken cancellationToken)
        {
            JObject root = await GetJsonAsync("api/tags", cancellationToken).ConfigureAwait(false);
            return ReadModelNames(root["models"]);
        }

        public async Task<IReadOnlyList<string>> GetLoadedModelsAsync(CancellationToken cancellationToken)
        {
            JObject root = await GetJsonAsync("api/ps", cancellationToken).ConfigureAwait(false);
            return ReadModelNames(root["models"]);
        }

        public static string SelectInitialModel(
            IReadOnlyCollection<string> installedModels,
            IReadOnlyCollection<string> loadedModels,
            string lastUsedModel)
        {
            string loadedLastUsed = FindModel(loadedModels, lastUsedModel);
            if (!string.IsNullOrWhiteSpace(loadedLastUsed))
                return loadedLastUsed;

            string loadedDefault = FindModel(loadedModels, LayoutDuplicationDefaults.DefaultModelName);
            if (!string.IsNullOrWhiteSpace(loadedDefault))
                return loadedDefault;

            string loadedFallback = FindModel(loadedModels, LayoutDuplicationDefaults.FallbackModelName);
            if (!string.IsNullOrWhiteSpace(loadedFallback))
                return loadedFallback;

            string firstLoaded = loadedModels?.FirstOrDefault(model => !string.IsNullOrWhiteSpace(model));
            if (!string.IsNullOrWhiteSpace(firstLoaded))
                return firstLoaded;

            string installedLastUsed = FindModel(installedModels, lastUsedModel);
            if (!string.IsNullOrWhiteSpace(installedLastUsed))
                return installedLastUsed;

            return LayoutDuplicationDefaults.DefaultModelName;
        }

        public async Task WarmModelAsync(string model, CancellationToken cancellationToken)
        {
            var payload = new JObject
            {
                ["model"] = model,
                ["stream"] = false,
                ["think"] = false,
                ["keep_alive"] = "30m",
                ["format"] = "json",
                ["messages"] = new JArray(new JObject
                {
                    ["role"] = "user",
                    ["content"] = "Return only JSON: {\"ok\":true}"
                }),
                ["options"] = new JObject
                {
                    ["temperature"] = 0,
                    ["num_predict"] = 16,
                    ["num_ctx"] = 8192
                }
            };

            await PostJsonAsync("api/chat", payload, cancellationToken).ConfigureAwait(false);
        }

        public async Task PullModelAsync(string model, IProgress<LayoutDuplicationProgress> progress, CancellationToken cancellationToken)
        {
            progress?.Report(new LayoutDuplicationProgress { Message = "Pulling Ollama model " + model + "...", IsIndeterminate = true });
            var payload = new JObject
            {
                ["name"] = model,
                ["stream"] = false
            };
            await PostJsonAsync("api/pull", payload, cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> RequestMappingAsync(string model, string prompt, CancellationToken cancellationToken)
        {
            var payload = new JObject
            {
                ["model"] = model,
                ["stream"] = false,
                ["format"] = "json",
                ["think"] = false,
                ["keep_alive"] = "30m",
                ["messages"] = new JArray(new JObject
                {
                    ["role"] = "user",
                    ["content"] = prompt
                }),
                ["options"] = new JObject
                {
                    ["temperature"] = 0,
                    ["num_predict"] = 512,
                    ["num_ctx"] = 8192
                }
            };

            JObject response = await PostJsonAsync("api/chat", payload, cancellationToken).ConfigureAwait(false);
            return response["message"]?["content"]?.ToString() ?? response.ToString(Formatting.None);
        }

        public static string GetLastModelPath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string directory = string.IsNullOrWhiteSpace(localAppData)
                ? Path.Combine(Path.GetTempPath(), "EasyEDA-Loader")
                : Path.Combine(localAppData, "EasyEDA-Loader");
            return Path.Combine(directory, LayoutDuplicationDefaults.LastModelFileName);
        }

        public static string LoadLastUsedModel()
        {
            try
            {
                string path = GetLastModelPath();
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        public static void SaveLastUsedModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model))
                return;

            try
            {
                string path = GetLastModelPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, model.Trim(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        private async Task<JObject> GetJsonAsync(string relativeUri, CancellationToken cancellationToken)
        {
            using (HttpResponseMessage response = await httpClient.GetAsync(relativeUri, cancellationToken).ConfigureAwait(false))
            {
                string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return JObject.Parse(content);
            }
        }

        private async Task<JObject> PostJsonAsync(string relativeUri, JObject payload, CancellationToken cancellationToken)
        {
            using (var content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response = await httpClient.PostAsync(relativeUri, content, cancellationToken).ConfigureAwait(false))
            {
                string responseContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return JObject.Parse(responseContent);
            }
        }

        private static IReadOnlyList<string> ReadModelNames(JToken modelsToken)
        {
            var result = new List<string>();
            if (!(modelsToken is JArray models))
                return result;

            foreach (JToken model in models)
            {
                string name = model["name"]?.ToString() ?? model["model"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name) && !result.Contains(name, StringComparer.OrdinalIgnoreCase))
                    result.Add(name);
            }

            return result;
        }

        private static string FindModel(IEnumerable<string> models, string model)
        {
            if (string.IsNullOrWhiteSpace(model) || models == null)
                return string.Empty;

            return models.FirstOrDefault(candidate => string.Equals(candidate, model, StringComparison.OrdinalIgnoreCase))
                ?? string.Empty;
        }
    }
}
