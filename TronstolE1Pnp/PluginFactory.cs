using System.Runtime.InteropServices;

namespace EasyEDA_Loader.TronstolE1Pnp
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.AutoDispatch)]
    public sealed class PluginFactory
    {
        public object InvokeOutputGenerator()
        {
            return new TronstolE1OutputGenerator();
        }
    }
}
