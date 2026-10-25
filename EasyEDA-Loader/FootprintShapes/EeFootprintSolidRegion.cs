using PCB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EasyEDA_Loader
{
    public class EeFootprintSolidRegion : EeFootprintShape
    {
        public string LayerId { get; set; }
        public string PathData { get; set; }
        public string FillRule { get; set; }
        public string Id { get; set; }

        public static EeFootprintSolidRegion FromString(string data)
        {
            var parts = data.Split('~');
            if (parts.Length < 6)
                throw new FormatException("Incomplete SOLIDREGION record.");
            return new EeFootprintSolidRegion { LayerId = parts[1], PathData = parts[3], FillRule = parts[4], Id = parts[5] };
        }

        internal Geometry BuildGeometry(IEnumerable<EeFootprintSolidRegion> regions)
        {
            Geometry geometry = ShapePathGeometry.Parse(PathData);
            foreach (var cutout in regions.Where(region => region.LayerId == LayerId && region.FillRule == "cutout"))
                geometry = Geometry.Combine(geometry, ShapePathGeometry.Parse(cutout.PathData), GeometryCombineMode.Exclude, null);
            return geometry;
        }

        public override List<UIElement> AddToCanvas(Canvas canvas, EeFootprintContext ctx)
        {
            if (FillRule != "solid") return new List<UIElement>();
            var geometry = BuildGeometry(ctx.SolidRegions).Clone();
            double scale = ConvertToMM(1);
            geometry.Transform = new MatrixTransform(scale, 0, 0, scale, -ctx.Box.X, -ctx.Box.Y);
            return new List<UIElement> { new Path { Data = geometry, Fill = new SolidColorBrush(ColorHelper.FromHex(ctx.Layers.GetLayerColor(LayerId))) } };
        }

        public override bool AddToComponent(IPCB_LibComponent component, EeFootprintContext ctx)
        {
            if (FillRule == "cutout") return true;
            try
            {
                if (FillRule != "solid")
                    throw new NotSupportedException("Unsupported SOLIDREGION type: " + FillRule);
                var layer = ctx.Layers.GetLayer(LayerId);
                if (layer == null || !EEPCB.TryEELayerToAltium(layer.Name, ctx.ImportLcscMechanicalLayers, out var target, ctx.ImportAdditionalLayers))
                    return true;
                var contours = ShapePathGeometry.Flatten(BuildGeometry(ctx.SolidRegions), true);
                foreach (var contour in contours)
                    foreach (var point in contour.Points)
                    {
                        point.X = ConvertX(ConvertToMM(point.X), ctx);
                        point.Y = ConvertY(ConvertToMM(point.Y), ctx);
                    }
                foreach (var island in ShapePathGeometry.SplitRegions(contours))
                    EEPCB.AddToPCB(component, EEPCB.CreateSolidRegion(component, target, island));
            }
            catch (Exception exception)
            {
                if (ctx.Exception == null) throw;
                return ctx.Exception(exception);
            }
            return true;
        }
    }
}
