using EDP;
using SCH;
using System;
using System.Reflection;
using System.Windows.Forms;

namespace EasyEDA_Loader
{
    public class EESCH
    {
        public static ISch_Lib GetCurrentSchLibrary()
        {
            var schDoc = AltiumApi.GlobalVars.SCHServer.GetCurrentSchDocument();
            if (schDoc == null)
            {
                MessageBox.Show("This is not a SCH library document", "EasyEDA Loader Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return null;
            }
            if (schDoc != null && schDoc.GetState_ObjectId() != SCH.TObjectId.eSchLib)
            {
                MessageBox.Show("Open schematic library", "EasyEDA Loader Error", MessageBoxButtons.OK, MessageBoxIcon.Hand);
                return null;
            }

            return schDoc as ISch_Lib;
        }
        public static ISch_Component CreateComponent(string name, string desc, string designator)
        {
            string comment = SymbolImportRules.SelectLibraryComment(name);
            string visibleDesignator = SymbolImportRules.SelectVisibleDesignator(designator);
            var schComponent = AltiumApi.GlobalVars.SCHServer.SchObjectFactory(SCH.TObjectId.eSchComponent, SCH.TObjectCreationMode.eCreate_Default) as ISch_Component;
            if (schComponent == null)
                return null;

            schComponent.SetState_CurrentPartID(1);
            schComponent.SetState_DisplayMode(0);
            schComponent.SetState_LibReference(name);
            schComponent.SetState_DesignItemId(comment);
            schComponent.SetState_ComponentDescription(desc);
            SetComponentComment(schComponent, comment);
            SetComponentDesignator(schComponent, visibleDesignator, CreateGostFontInfo());
            SetComponentSpecialStringText(schComponent, "Comment", comment);
            SetComponentSpecialStringText(schComponent, "Designator", visibleDesignator);
            return schComponent;
        }


        public static void AddParameter(ISch_Component c, string name, string value)
        {
            AddOrUpdateParameter(c, name, value);
        }

        public static ISch_Parameter AddOrUpdateParameter(ISch_Component c, string name, string value)
        {
            if (c == null || string.IsNullOrWhiteSpace(name))
                return null;

            ISch_Parameter param = FindParameter(c, name);
            if (param == null)
                param = c.AddSchParameter();

            if (param == null)
                return null;

            param.SetState_Name(name);
            param.SetState_Text(value ?? "");
            param.SetState_ShowName(false);
            param.SetState_IsHidden(true);
            SetTextObjectStyle(param, CreateGostFontInfo());
            return param;
        }

        public static void ApplyRequiredSymbolParameters(ISch_Component c, string footprintName)
        {
            ApplyGostPropertySet(c, new SchematicPropertySet { Footprint = footprintName });
        }

        public static void ApplyGostPropertySet(ISch_Component c, SchematicPropertySet properties)
        {
            if (properties == null)
                properties = new SchematicPropertySet();

            AddOrUpdateParameter(c, "Manufacturer", properties.Manufacturer);
            AddOrUpdateParameter(c, "PartNumber", "=Comment");
            AddOrUpdateParameter(c, "ValueType", properties.ValueType);
            AddOrUpdateParameter(c, "PartDenotation", properties.PartDenotation);
            AddOrUpdateParameter(c, "PartNote", properties.PartNote);
            AddOrUpdateParameter(c, "TU", properties.TU);
            AddOrUpdateParameter(c, "AlternateManufacturer", properties.AlternateManufacturer);
            AddOrUpdateParameter(c, "AlternatePartNumber", properties.AlternatePartNumber);
        }

        public static bool IsRuleManagedParameter(string name)
        {
            return string.Equals(name, "Comment", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Description", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Designator", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Footprint", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "FootprintLibrary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Package", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Manufacturer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "PartNumber", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "ValueType", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "PartDenotation", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "PartNote", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "TU", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "AlternateManufacturer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "AlternatePartNumber", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Mounting", StringComparison.OrdinalIgnoreCase);
        }

        public static int RGB(int r, int g, int b)
        {
            return (b << 16) | (g << 8) | r;
        }

        public class FontInfo
        {
            public int Size { get; set; }
            public int Rotation { get; set; } = 0;
            public bool Underline { get; set; } = false;
            public bool Italic { get; set; } = false;
            public bool Bold { get; set; } = false;
            public bool Strikout { get; set; } = false;
            public string Name { get; set; }
            public int Color { get; set; } = 0;
        }

        public class SchematicPropertySet
        {
            public string Manufacturer { get; set; } = "";
            public string ValueType { get; set; } = "";
            public string PartDenotation { get; set; } = "";
            public string PartNote { get; set; } = "";
            public string TU { get; set; } = "";
            public string AlternateManufacturer { get; set; } = "";
            public string AlternatePartNumber { get; set; } = "";
            public string Footprint { get; set; } = "";
            public string FootprintLibrary { get; set; } = "";
            public string Package { get; set; } = "";
            public string Mounting { get; set; } = "";
        }

        public static FontInfo CreateGostFontInfo()
        {
            return new FontInfo
            {
                Name = "GOST type B",
                Size = 12,
                Color = 0
            };
        }

        private static int GetFontId(FontInfo fontInfo)
        {
            return AltiumApi.GlobalVars.SCHServer.GetState_FontManager().GetFontID(fontInfo.Size, fontInfo.Rotation, fontInfo.Underline, fontInfo.Italic, fontInfo.Bold, fontInfo.Strikout, fontInfo.Name);
        }

        private static bool StartsWithMP(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.StartsWith("MP", StringComparison.OrdinalIgnoreCase);
        }

        private static ISch_Parameter FindParameter(ISch_Component c, string name)
        {
            return TryInvokeResult(c, "GetState_SchParameterByName", name) as ISch_Parameter
                ?? TryInvokeResult(c, "Internal_GetState_SchParameterByName", name) as ISch_Parameter;
        }

        private static object GetComponentDesignator(ISch_Component c)
        {
            try
            {
                return c.GetState_SchDesignator();
            }
            catch
            {
                return TryInvokeResult(c, "GetState_SchDesignator")
                    ?? TryInvokeResult(c, "Internal_GetState_SchDesignator");
            }
        }

        private static object GetComponentComment(ISch_Component c)
        {
            try
            {
                return c.GetState_SchComment();
            }
            catch
            {
                return TryInvokeResult(c, "GetState_SchComment")
                    ?? TryInvokeResult(c, "Internal_GetState_SchComment");
            }
        }

        private static void SetTextObjectStyle(object textObject, FontInfo fontInfo)
        {
            if (textObject == null || fontInfo == null)
                return;

            int fontId = GetFontId(fontInfo);
            TryInvoke(textObject, "SetState_FontId", fontId);
            TryInvoke(textObject, "SetState_Color", fontInfo.Color);
            TryInvoke(textObject, "SetState_TextColor", fontInfo.Color);
            try
            {
                dynamic dynamicText = textObject;
                dynamicText.SetState_FontId(fontId);
            }
            catch
            {
            }
            try
            {
                dynamic dynamicText = textObject;
                dynamicText.SetState_Color(fontInfo.Color);
            }
            catch
            {
            }
            try
            {
                dynamic dynamicText = textObject;
                dynamicText.SetState_TextColor(fontInfo.Color);
            }
            catch
            {
            }
        }

        private static void SetTextObjectLocation(object textObject, double xMils, double yMils)
        {
            DXP.Point location = new DXP.Point
            {
                X = AltiumApi.MilsToCoord(xMils),
                Y = AltiumApi.MilsToCoord(yMils)
            };
            TryInvoke(textObject, "SetState_Location", location);
            try
            {
                dynamic dynamicText = textObject;
                dynamicText.SetState_Location(location);
            }
            catch
            {
            }
        }

        private static void SetTextObjectText(object textObject, string text)
        {
            TryInvoke(textObject, "SetState_Text", text ?? "");
            try
            {
                dynamic dynamicText = textObject;
                dynamicText.SetState_Text(text ?? "");
            }
            catch
            {
            }
            try
            {
                dynamic dynamicText = textObject;
                dynamicText.Text = text ?? "";
            }
            catch
            {
            }
        }

        private static void SetComponentComment(ISch_Component c, string comment)
        {
            SetComponentSpecialStringText(c, "Comment", comment);
            object schComment = GetComponentComment(c);
            if (schComment == null)
                return;

            SetTextObjectText(schComment, comment);
            SetTextObjectStyle(schComment, CreateGostFontInfo());
        }

        private static void SetComponentDesignator(ISch_Component c, string designator, FontInfo fontInfo)
        {
            SetComponentSpecialStringText(c, "Designator", designator);
            object schDesignator = GetComponentDesignator(c);
            if (schDesignator == null)
                return;

            SetTextObjectText(schDesignator, designator);
            SetTextObjectStyle(schDesignator, fontInfo);
        }

        private static void SetComponentSpecialStringText(ISch_Component c, string propertyName, string text)
        {
            if (c == null || string.IsNullOrWhiteSpace(propertyName))
                return;

            object specialString = TryInvokeResult(c, "GetState_Sch" + propertyName)
                ?? TryInvokeResult(c, "Internal_GetState_Sch" + propertyName);
            SetTextObjectText(specialString, text);

            try
            {
                dynamic dynamicComponent = c;
                if (string.Equals(propertyName, "Comment", StringComparison.OrdinalIgnoreCase) && dynamicComponent.Comment != null)
                    dynamicComponent.Comment.Text = text ?? "";
                if (string.Equals(propertyName, "Designator", StringComparison.OrdinalIgnoreCase) && dynamicComponent.Designator != null)
                    dynamicComponent.Designator.Text = text ?? "";
            }
            catch
            {
            }
        }

        public static void SetComponentDesignatorLocation(ISch_Component c, double xMils, double yMils)
        {
            object schDesignator = GetComponentDesignator(c);
            if (schDesignator == null)
                return;

            SetTextObjectLocation(schDesignator, xMils, yMils);
            SetTextObjectStyle(schDesignator, CreateGostFontInfo());
        }

        private static void SetPinTextSettings(ISch_Pin schPin, int fontId, int color)
        {
            int zeroMargin = AltiumApi.MmToCoord(0);
            int designatorXMargin = AltiumApi.MmToCoord(1);

            schPin.SetState_Name_FontMode(TPinItemMode.ePinItemMode_Custom);
            schPin.SetState_Name_CustomFontID(fontId);
            schPin.SetState_Name_CustomColor(color);
            schPin.SetState_Name_PositionMode(TPinItemMode.ePinItemMode_Custom);
            schPin.SetState_Name_CustomPosition_Margin(zeroMargin);
            schPin.SetState_Name_CustomPosition_HorizontalMargin(zeroMargin);
            schPin.SetState_Name_CustomPosition_VerticalMargin(zeroMargin);

            schPin.SetState_Designator_FontMode(TPinItemMode.ePinItemMode_Custom);
            schPin.SetState_Designator_CustomFontID(fontId);
            schPin.SetState_Designator_CustomColor(color);
            schPin.SetState_Designator_PositionMode(TPinItemMode.ePinItemMode_Custom);
            schPin.SetState_Designator_CustomPosition_Margin(designatorXMargin);
            schPin.SetState_Designator_CustomPosition_HorizontalMargin(designatorXMargin);
            schPin.SetState_Designator_CustomPosition_VerticalMargin(zeroMargin);
        }

        public static ISch_Pin CreatePin(ISch_Lib schLib, ISch_Component c, double x, double y, string designator, string name, TRotationBy90 orientation, double length, TPinElectrical pinType, bool showName, FontInfo fontInfo)
        {
            var schPin = AltiumApi.GlobalVars.SCHServer.SchObjectFactory(SCH.TObjectId.ePin, SCH.TObjectCreationMode.eCreate_Default) as ISch_Pin;
            if (schPin == null)
                return null;

            schPin.SetState_Location(new DXP.Point
            {
                X = AltiumApi.MilsToCoord(x),
                Y = AltiumApi.MilsToCoord(y)
            });
            schPin.SetState_PinLength(AltiumApi.MilsToCoord(length));
            schPin.SetState_Color(0);
            schPin.SetState_Orientation(orientation);
            schPin.SetState_Designator(designator);
            schPin.SetState_Name(name);
            schPin.SetState_Electrical(pinType);
            schPin.SetState_ShowName(showName);
            if (StartsWithMP(designator) || StartsWithMP(name))
            {
                schPin.SetState_ShowDesignator(false);
                schPin.SetState_ShowName(true);
            }
            schPin.SetState_OwnerPartId(schLib.GetState_CurrentSchComponentPartId());
            schPin.SetState_OwnerPartDisplayMode(schLib.GetState_CurrentSchComponentDisplayMode());

            if (fontInfo != null)
            {
                int fontId = GetFontId(fontInfo);
                SetPinTextSettings(schPin, fontId, fontInfo.Color);
            }

            c.AddSchObject(schPin);
            return schPin;
        }

        public static ISch_Pin CreateLeftPin(ISch_Lib schLib, ISch_Component c, double x, double y, string designator, string name, double length, TPinElectrical pinType, bool showName, FontInfo fontInfo)
        {
            return CreatePin(schLib, c, x, y, designator, name, TRotationBy90.eRotate180, length, pinType, showName, fontInfo);
        }
        public static ISch_Pin CreateRightPin(ISch_Lib schLib, ISch_Component c, double x, double y, string designator, string name, double length, TPinElectrical pinType, bool showName, FontInfo fontInfo)
        {
            return CreatePin(schLib, c, x, y, designator, name, TRotationBy90.eRotate0, length, pinType, showName, fontInfo);
        }
        public static ISch_Pin CreateTopPin(ISch_Lib schLib, ISch_Component c, double x, double y, string designator, string name, double length, TPinElectrical pinType, bool showName, FontInfo fontInfo)
        {
            return CreatePin(schLib, c, x, y, designator, name, TRotationBy90.eRotate90, length, pinType, showName, fontInfo);
        }
        public static ISch_Pin CreateBottomPin(ISch_Lib schLib, ISch_Component c, double x, double y, string designator, string name, double length, TPinElectrical pinType, bool showName, FontInfo fontInfo)
        {
            return CreatePin(schLib, c, x, y, designator, name, TRotationBy90.eRotate270, length, pinType, showName, fontInfo);
        }

        public static void CreateRectangle(ISch_Lib schLib, ISch_Component c, double x1, double y1, double x2, double y2)
        {
            var rect = AltiumApi.GlobalVars.SCHServer.SchObjectFactory(SCH.TObjectId.eRectangle, SCH.TObjectCreationMode.eCreate_Default) as ISch_Rectangle;
            if (rect == null)
                return;
            rect.SetState_LineWidth(TSize.eSmall);
            rect.SetState_Location(new DXP.Point
            {
                X = AltiumApi.MilsToCoord(x1),
                Y = AltiumApi.MilsToCoord(y1)
            });
            rect.SetState_Corner(new DXP.Point
            {
                X = AltiumApi.MilsToCoord(x2),
                Y = AltiumApi.MilsToCoord(y2)
            });

            rect.SetState_Color(0);
            rect.SetState_AreaColor(RGB(248, 248, 248));
            rect.SetState_IsSolid(true);
            rect.SetState_OwnerPartId(schLib.GetState_CurrentSchComponentPartId());
            rect.SetState_OwnerPartDisplayMode(schLib.GetState_CurrentSchComponentDisplayMode());
            c.AddSchObject(rect);
        }

        public static void CreateLine(ISch_Lib schLib, ISch_Component c, double x1, double y1, double x2, double y2)
        {
            var line = AltiumApi.GlobalVars.SCHServer.SchObjectFactory(SCH.TObjectId.eLine, SCH.TObjectCreationMode.eCreate_Default) as ISch_Line;
            if (line == null)
                return;

            line.SetState_LineWidth(TSize.eSmall);
            line.SetState_Location(new DXP.Point
            {
                X = AltiumApi.MilsToCoord(x1),
                Y = AltiumApi.MilsToCoord(y1)
            });
            line.SetState_Corner(new DXP.Point
            {
                X = AltiumApi.MilsToCoord(x2),
                Y = AltiumApi.MilsToCoord(y2)
            });
            line.SetState_Color(0);
            line.SetState_OwnerPartId(schLib.GetState_CurrentSchComponentPartId());
            line.SetState_OwnerPartDisplayMode(schLib.GetState_CurrentSchComponentDisplayMode());
            c.AddSchObject(line);
        }

        public static void AssignFootprint(ISch_Component c, string libraryPath, string modelName, string modelMapping)
        {
            if (c == null || string.IsNullOrWhiteSpace(modelName))
                return;

            var modelType = "PCBLIB";
            var model = c.AddSchImplementation();
            model.ClearAllDatafileLinks();
            model.SetState_MapAsString(modelMapping);
            model.SetState_ModelName(modelName);
            model.SetState_ModelType(modelType);
            if (!string.IsNullOrWhiteSpace(libraryPath))
                model.AddDataFileLink(modelName, libraryPath, modelType);
            model.SetState_IsCurrent(true);
        }

        public static string SelectRuleDesignator(string sourceDesignator, string partName, string description, string package)
        {
            return SymbolImportRules.SelectDesignator(sourceDesignator, partName, description, package);
        }

        public static string SelectRuleValueType(string designator, string partName, string description, string package)
        {
            return SymbolImportRules.SelectValueType(designator, partName, description, package);
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            foreach (string needle in needles)
            {
                if (text.Contains(needle))
                    return true;
            }

            return false;
        }

        private static void TryInvoke(object target, string methodName, params object[] args)
        {
            TryInvokeResult(target, methodName, args);
        }

        private static object TryInvokeResult(object target, string methodName, params object[] args)
        {
            if (target == null)
                return null;

            foreach (var method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                var parameters = method.GetParameters();
                if (method.Name != methodName || parameters.Length != args.Length)
                    continue;

                try
                {
                    object[] convertedArgs = new object[args.Length];
                    for (int i = 0; i < args.Length; i++)
                    {
                        object arg = args[i];
                        Type parameterType = parameters[i].ParameterType;
                        if (arg != null && parameterType == typeof(int) && arg.GetType().IsEnum)
                            convertedArgs[i] = Convert.ToInt32(arg);
                        else
                            convertedArgs[i] = arg;
                    }

                    return method.Invoke(target, convertedArgs);
                }
                catch
                {
                }
            }

            return null;
        }

    }
}
