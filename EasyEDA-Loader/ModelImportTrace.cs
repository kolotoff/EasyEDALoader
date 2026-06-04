using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;

namespace EasyEDA_Loader
{
    internal static class ModelImportTrace
    {
        public static void Measure(string phaseName, string modelIdentifier, Action action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                action();
            }
            finally
            {
                stopwatch.Stop();
                TraceElapsed(phaseName, modelIdentifier, stopwatch.ElapsedMilliseconds);
            }
        }

        public static T Measure<T>(string phaseName, string modelIdentifier, Func<T> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                return action();
            }
            finally
            {
                stopwatch.Stop();
                TraceElapsed(phaseName, modelIdentifier, stopwatch.ElapsedMilliseconds);
            }
        }

        public static async Task<T> MeasureAsync<T>(string phaseName, string modelIdentifier, Func<Task<T>> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                stopwatch.Stop();
                TraceElapsed(phaseName, modelIdentifier, stopwatch.ElapsedMilliseconds);
            }
        }

        private static void TraceElapsed(string phaseName, string modelIdentifier, long elapsedMilliseconds)
        {
            string safePhaseName = string.IsNullOrWhiteSpace(phaseName) ? "unknown" : phaseName;
            string safeModelIdentifier = string.IsNullOrWhiteSpace(modelIdentifier) ? "unknown" : modelIdentifier;
            WriteTrace(
                "Model import timing: phase=" +
                safePhaseName +
                " model=" +
                safeModelIdentifier +
                " elapsed_ms=" +
                elapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
        }

        private static void WriteTrace(string message)
        {
            try
            {
                Type moduleType = typeof(ModelImportTrace).Assembly.GetType("EasyEDA_Loader.EasyEDALoaderModule");
                MethodInfo traceMethod = moduleType?.GetMethod(
                    "Trace",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (traceMethod != null)
                {
                    traceMethod.Invoke(null, new object[] { message });
                    return;
                }
            }
            catch
            {
            }

            Debug.WriteLine(message);
        }
    }
}
