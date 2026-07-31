using PCB;
using System;
using System.Reflection;

namespace EasyEDA_Loader
{
    internal sealed class JlcCamPcbAdapter
    {
        public IPCB_Board GetCurrentBoard() { return EEPCB.GetCurrentPcbBoard(); }
        public void Begin() { AltiumApi.GlobalVars.PCBServer.PreProcess(); }
        public void End() { AltiumApi.GlobalVars.PCBServer.PostProcess(); }
        public void Add(IPCB_Board board, object primitive) { board.AddPCBObject(primitive); }
        public void Remove(IPCB_Board board, object primitive) { Invoke(board, "RemovePCBObject", primitive); }
        public void Redraw(IPCB_Board board) { board?.ViewManager_FullUpdate(); }
        public object Create(TObjectId id) { return AltiumApi.GlobalVars.PCBServer.PCBObjectFactory(id, TDimensionKind.eNoDimension, TObjectCreationMode.eCreate_Default); }
        public static void Set(object target, string property, params object[] values) { Invoke(target, "SetState_" + property, values); }
        public static object Get(object target, string property) { return Invoke(target, "GetState_" + property); }
        public static object Invoke(object target, string name, params object[] values)
        {
            if (target == null) return null;
            MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return method == null ? null : method.Invoke(target, values);
        }
    }
}
