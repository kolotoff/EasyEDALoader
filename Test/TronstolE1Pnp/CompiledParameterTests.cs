using EasyEDA_Loader.TronstolE1Pnp;
using RT_PCB;
using System;
using System.Collections.Generic;

internal static class CompiledParameterTests
{
    public static void Run()
    {
        var input = new List<IPCB_Parameter>
        {
            new Parameter("partnumber", "  Part\tNumber\r\nValue  "),
            new Parameter("PartNumber", "Later duplicate"),
            new Parameter("Empty", null),
            null,
            new Parameter(" ", "Ignored")
        };
        for (int i = 0; i < 20000; i++) input.Add(new Parameter("Extra" + i, "Value" + i));
        input.Add(new Parameter("SolderingType", " Manual\tSoldering "));
        var native = new ParameterList(input);
        var snapshot = TronstolE1CompiledParameters.Read(native);
        Check(TronstolE1CompiledParameters.Get(snapshot, "PARTNUMBER") == "Part Number Value", "Case-insensitive first-value lookup and normalization");
        Check(TronstolE1CompiledParameters.Get(snapshot, "SolderingType") == "Manual Soldering", "Last parameter retained after a large list");
        Check(TronstolE1CompiledParameters.Get(snapshot, "Empty") == "" && TronstolE1CompiledParameters.Get(snapshot, "Missing") == "", "Null and missing parameters remain empty");
        Check(TronstolE1CompiledParameters.Read(null).Count == 0, "Absent parameter list supported");
        Check(native.CountReads == 1 && native.ItemReads == input.Count, "Compiled parameters are read once, not rescanned for every CSV field");
        for (int i = 0; i < 20000; i++)
            Check(TronstolE1CompiledParameters.Get(snapshot, "EXTRA" + i) == "Value" + i, "Large parameter snapshot lost a value");
        Check(native.ItemReads == input.Count, "Snapshot lookups do not call the native list again");
        Console.WriteLine("Compiled parameter tests passed (20,000 parameters plus nulls and duplicates).");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Parameter : IPCB_Parameter
    {
        private readonly string name, value;
        public Parameter(string name, string value) { this.name = name; this.value = value; }
        public string GetName() => name;
        public string GetValue() => value;
        public IPCB_ParameterAttributes GetAttributes() => null;
        public bool GetIsInherited() => false;
        public void SetName(string value) => throw new NotSupportedException();
        public void SetValue(string value) => throw new NotSupportedException();
        public void SetIsInherited(bool value) => throw new NotSupportedException();
    }

    private sealed class ParameterList : IPCB_ParameterList
    {
        private readonly List<IPCB_Parameter> values;
        public int CountReads { get; private set; }
        public int ItemReads { get; private set; }
        public ParameterList(List<IPCB_Parameter> values) { this.values = values; }
        public int GetCount() { CountReads++; return values.Count; }
        public IPCB_Parameter GetByIndex(int index) { ItemReads++; return values[index]; }
        public void Add(IPCB_Parameter parameter) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public IPCB_Parameter FindByName(string name) => throw new NotSupportedException();
        public IPCB_Parameter GetByName(string name) => throw new NotSupportedException();
        public void RemoveByIndex(int index) => throw new NotSupportedException();
        public void RemoveByName(string name) => throw new NotSupportedException();
        public void SetByIndex(int index, IPCB_Parameter parameter) => throw new NotSupportedException();
        public bool TryRemoveByName(string name) => throw new NotSupportedException();
    }
}
