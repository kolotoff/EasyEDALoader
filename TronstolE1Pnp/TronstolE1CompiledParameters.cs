using RT_PCB;
using System;
using System.Collections.Generic;
using System.IO;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    internal static class TronstolE1CompiledParameters
    {
        public static Dictionary<string, string> Read(IPCB_ParameterList parameters)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (parameters == null) return values;

            // Use native IUnknown interfaces for the list and its returned parameters.
            // The SDK's IDispatch GetByIndex wrapper can crash inside the CLR while
            // repeatedly wrapping compiled component parameters on larger boards.
            int count = parameters.GetCount();
            if (count < 0 || count > 100000)
                throw new InvalidDataException("Invalid compiled component parameter count: " + count);
            for (int index = 0; index < count; index++)
            {
                IPCB_Parameter parameter = parameters.GetByIndex(index);
                if (parameter == null) continue;
                string name = parameter.GetName();
                if (!string.IsNullOrWhiteSpace(name) && !values.ContainsKey(name))
                    values.Add(name, TronstolE1Text.Normalize(parameter.GetValue()));
            }
            GC.KeepAlive(parameters);
            return values;
        }

        public static string Get(IReadOnlyDictionary<string, string> values, string name)
        {
            return values.TryGetValue(name, out string value) ? value : string.Empty;
        }
    }
}
