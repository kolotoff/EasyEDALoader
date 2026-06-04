using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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

        public static string GetRawObjPath(string modelUuid)
        {
            return Path.Combine(GetModelCacheDirectory("Raw"), GetSafeFileName(modelUuid) + ".obj");
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
            return Path.Combine(GetLocalDataRoot(), "ModelCache", kind);
        }
    }
}
