using System.Runtime.InteropServices;

namespace EasyEDA_Loader.EasyEDAShapeSvg
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class PluginFactory
    {
        public object InvokeOutputGenerator()
        {
            return new EasyEDAShapeSvgOutputGenerator();
        }
    }
}
