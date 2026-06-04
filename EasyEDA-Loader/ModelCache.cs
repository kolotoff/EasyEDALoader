using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace EasyEDA_Loader
{
    public sealed class ModelCacheResult
    {
        public byte[] Data { get; set; }
        public bool CacheHit { get; set; }
        public string CachePath { get; set; }
    }

    public static class ModelCache
    {
        private const string ProjectionPreviewVersion = "v1";

        public static Task<System.Collections.Generic.List<EasyedaApi.PartInfo>> GetSearchProductInfoAsync(
            EasyedaApi api,
            string lcscId,
            CancellationToken cancellationToken)
        {
            if (api == null)
                throw new ArgumentNullException(nameof(api));

            return GetJsonObjectAsync(
                Path.Combine(GetComponentCacheDirectory(lcscId), "product-search.json"),
                () => api.SearchProductInfoAsync(lcscId),
                cancellationToken);
        }

        public static Task<Root> GetComponentJsonAsync(
            EasyedaApi api,
            string lcscId,
            CancellationToken cancellationToken)
        {
            if (api == null)
                throw new ArgumentNullException(nameof(api));

            return GetJsonObjectAsync(
                Path.Combine(GetComponentCacheDirectory(lcscId), "component.json"),
                () => api.GetComponentJsonAsync(lcscId, cancellationToken),
                cancellationToken);
        }

        public static Task<EasyedaApi.ProductInfo> GetProductInfoAsync(
            EasyedaApi api,
            string search,
            string uuid,
            CancellationToken cancellationToken)
        {
            if (api == null)
                throw new ArgumentNullException(nameof(api));

            return GetJsonObjectAsync(
                Path.Combine(GetComponentCacheDirectory(search), "product-info-" + GetStableHash(uuid) + ".json"),
                () => api.GetProductInfoAsync(search, uuid),
                cancellationToken);
        }

        public static Task<byte[]> GetPngImageAsync(EasyedaApi api, string imageUrl, string partNumber, CancellationToken cancellationToken)
        {
            if (api == null)
                throw new ArgumentNullException(nameof(api));

            return GetOrDownloadAsync(
                Path.Combine(GetComponentCacheDirectory(partNumber), "thumbnail-" + GetStableHash(imageUrl) + ".png"),
                () => api.LoadPngBytesAsync(imageUrl, cancellationToken),
                cancellationToken);
        }

        public static Task<byte[]> GetStepModelAsync(EasyedaApi api, string modelUuid, CancellationToken cancellationToken)
        {
            if (api == null)
                throw new ArgumentNullException(nameof(api));

            return GetOrDownloadAsync(
                GetOriginalStepPath(modelUuid),
                () => api.LoadModelAsync(modelUuid, cancellationToken),
                cancellationToken);
        }

        public static Task<byte[]> GetRawObjModelAsync(EasyedaApi api, string modelUuid, CancellationToken cancellationToken)
        {
            if (api == null)
                throw new ArgumentNullException(nameof(api));

            return GetOrDownloadAsync(
                GetRawObjPath(modelUuid),
                () => api.LoadRawModelAsync(modelUuid, cancellationToken),
                cancellationToken);
        }

        public static Task<byte[]> GetProjectionPreviewPngAsync(
            string selectedComponentCacheKey,
            string modelUuid,
            int imageWidthPixels,
            int imageHeightPixels,
            Func<Task<byte[]>> render,
            CancellationToken cancellationToken)
        {
            if (render == null)
                throw new ArgumentNullException(nameof(render));

            return GetOrDownloadAsync(
                GetProjectionPreviewPath(selectedComponentCacheKey, modelUuid, imageWidthPixels, imageHeightPixels),
                render,
                cancellationToken);
        }

        public static async Task<byte[]> GetCleanStepModelAsync(string modelUuid, Func<Task<byte[]>> clean, CancellationToken cancellationToken)
        {
            if (clean == null)
                throw new ArgumentNullException(nameof(clean));

            ModelCacheResult result = await GetCleanStepModelWithStatusAsync(modelUuid, clean, cancellationToken)
                .ConfigureAwait(false);
            return result.Data;
        }

        public static async Task<ModelCacheResult> GetCleanStepModelWithStatusAsync(
            string modelUuid,
            Func<Task<byte[]>> clean,
            CancellationToken cancellationToken)
        {
            if (clean == null)
                throw new ArgumentNullException(nameof(clean));

            string cachePath = GetCleanStepPath(modelUuid);
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(cachePath))
            {
                byte[] cached = File.ReadAllBytes(cachePath);
                if (cached.Length > 0)
                {
                    return new ModelCacheResult
                    {
                        Data = cached,
                        CacheHit = true,
                        CachePath = cachePath
                    };
                }
            }

            byte[] data = await clean().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (data != null && data.Length > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                File.WriteAllBytes(cachePath, data);
            }

            return new ModelCacheResult
            {
                Data = data,
                CacheHit = false,
                CachePath = cachePath
            };
        }

        public static string GetOriginalStepPath(string modelUuid)
        {
            return Path.Combine(GetModelCacheDirectory("Original"), GetSafeFileName(modelUuid) + ".step");
        }

        public static string GetCleanStepPath(string modelUuid)
        {
            return Path.Combine(GetModelCacheDirectory("Clean"), GetSafeFileName(modelUuid) + "_clean.step");
        }

        public static string GetComponentModelCacheKey(string partNumber, string modelKey)
        {
            if (string.IsNullOrWhiteSpace(partNumber))
                return modelKey;
            if (string.IsNullOrWhiteSpace(modelKey))
                return partNumber;

            return partNumber + "__" + modelKey;
        }

        public static int DeleteCleanStepModels(string modelUuid)
        {
            int deletedCount = 0;
            foreach (string cleanModeKey in CleanStepCacheKeys.GetCleanModeKeys(modelUuid))
            {
                string cleanStepPath = GetCleanStepPath(cleanModeKey);
                if (!File.Exists(cleanStepPath))
                    continue;

                File.Delete(cleanStepPath);
                deletedCount++;
            }

            return deletedCount;
        }

        public static int DeleteSelectedComponentCache(string partNumber, string selectedComponentCacheKey, string modelUuid)
        {
            int deletedCount = 0;

            deletedCount += DeleteDirectory(GetComponentCacheDirectory(partNumber));
            deletedCount += DeleteCleanStepModels(selectedComponentCacheKey);
            deletedCount += DeleteProjectionPreviewPngs(selectedComponentCacheKey, modelUuid);

            if (!string.IsNullOrWhiteSpace(modelUuid))
            {
                deletedCount += DeleteFile(GetOriginalStepPath(modelUuid));
                deletedCount += DeleteFile(GetRawObjPath(modelUuid));
                deletedCount += DeleteFile(ModelZInfoCache.GetDefaultCachePath(modelUuid));
            }

            return deletedCount;
        }

        public static string GetRawObjPath(string modelUuid)
        {
            return Path.Combine(GetModelCacheDirectory("Raw"), GetSafeFileName(modelUuid) + ".obj");
        }

        public static string GetModelCacheRoot()
        {
            return Path.Combine(GetLocalDataRoot(), "ModelCache");
        }

        public static string GetLocalDataRoot()
        {
            string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localApplicationData)
                ? Path.Combine(Path.GetTempPath(), "EasyEDA-Loader")
                : Path.Combine(localApplicationData, "EasyEDA-Loader");
        }

        public static string GetSafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                value = Guid.NewGuid().ToString("N");

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
                value = value.Replace(invalidChar, '_');

            return value;
        }

        private static async Task<T> GetJsonObjectAsync<T>(
            string cachePath,
            Func<Task<T>> download,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(cachePath))
            {
                try
                {
                    string cachedJson = File.ReadAllText(cachePath, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(cachedJson))
                    {
                        T cached = JsonConvert.DeserializeObject<T>(cachedJson);
                        if (cached != null)
                            return cached;
                    }
                }
                catch
                {
                }
            }

            if (download == null)
                throw new ArgumentNullException(nameof(download));

            T data = await download().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (data != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                File.WriteAllText(cachePath, JsonConvert.SerializeObject(data), Encoding.UTF8);
            }

            return data;
        }

        private static async Task<byte[]> GetOrDownloadAsync(
            string cachePath,
            Func<Task<byte[]>> download,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(cachePath))
            {
                byte[] cached = File.ReadAllBytes(cachePath);
                if (cached.Length > 0)
                    return cached;
            }

            byte[] data = await download().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (data != null && data.Length > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                File.WriteAllBytes(cachePath, data);
            }

            return data;
        }

        private static string GetModelCacheDirectory(string kind)
        {
            return Path.Combine(GetModelCacheRoot(), kind);
        }

        private static string GetComponentCacheDirectory(string partNumber)
        {
            return Path.Combine(GetLocalDataRoot(), "ComponentCache", GetSafeFileName(partNumber));
        }

        private static string GetProjectionPreviewPath(
            string selectedComponentCacheKey,
            string modelUuid,
            int imageWidthPixels,
            int imageHeightPixels)
        {
            string key =
                GetSafeFileName(GetComponentModelCacheKey(selectedComponentCacheKey, modelUuid)) +
                "__original__z_plus__" +
                imageWidthPixels.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "x" +
                imageHeightPixels.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "__" +
                ProjectionPreviewVersion;
            return Path.Combine(GetModelCacheDirectory("ProjectionPreview"), key + ".png");
        }

        private static int DeleteProjectionPreviewPngs(string selectedComponentCacheKey, string modelUuid)
        {
            string directory = GetModelCacheDirectory("ProjectionPreview");
            if (!Directory.Exists(directory))
                return 0;

            string prefix = GetSafeFileName(GetComponentModelCacheKey(selectedComponentCacheKey, modelUuid)) + "__";
            int deletedCount = 0;
            foreach (string path in Directory.GetFiles(directory, prefix + "*.png"))
                deletedCount += DeleteFile(path);

            return deletedCount;
        }

        private static int DeleteDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return 0;

            int fileCount = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Length;
            Directory.Delete(directory, true);
            return fileCount;
        }

        private static int DeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return 0;

            File.Delete(path);
            return 1;
        }

        private static string GetStableHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                value = string.Empty;

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
