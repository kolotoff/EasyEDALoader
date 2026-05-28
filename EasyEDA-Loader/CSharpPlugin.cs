using Altium.Controls;
using DXP;
using EasyEDA_Loader;
using System;
using System.Runtime.InteropServices;

namespace CSharpPlugin
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public class PluginFactory
    {
        public object InvokePluginFactory(IClient client)
        {
            EasyEDALoaderModule.Trace("InvokePluginFactory entered.");
            try
            {
                if (!client.ProductInfo().SupportsUIFeature("NoGUI", false))
                {
                    IUITheme uiTheme = (client as IUIThemeManager)?.CurrentUITheme();
                    if (uiTheme != null)
                        Style.Init(uiTheme.GetHRID(), uiTheme.GetAttributeDictionary());
                    else
                        Style.Init();
                }
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace($"Theme initialization failed: {ex}");
            }

            try
            {
                EasyEDALoaderModule.Trace("Creating EasyEDALoaderModule.");
                EasyEDALoaderModule module = new EasyEDALoaderModule(client);
                EasyEDALoaderModule.Trace("Returning EasyEDALoaderModule.");
                return module;
            }
            catch (Exception ex)
            {
                EasyEDALoaderModule.Trace($"Module construction failed: {ex}");
                throw;
            }
        }
    }
}
