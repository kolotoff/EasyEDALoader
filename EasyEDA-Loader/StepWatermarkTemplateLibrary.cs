using System;
using System.Collections.Generic;
using System.Globalization;

namespace EasyEDA_Loader
{
    public sealed class StepWatermarkTemplatePoint
    {
        public StepWatermarkTemplatePoint()
        {
        }

        public StepWatermarkTemplatePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; set; }
        public int Y { get; set; }
    }

    public sealed class StepWatermarkTemplate
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Text { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public IReadOnlyList<StepWatermarkTemplatePoint> EdgePoints { get; set; } = Array.Empty<StepWatermarkTemplatePoint>();
    }

    public sealed class StepWatermarkTemplateSource
    {
        public string TemplateName { get; set; }
        public string Kind { get; set; }
        public string Text { get; set; }
        public string ModelName { get; set; }
        public string ViewName { get; set; }
        public string MarkedFileName { get; set; }
        public string ProjectionFileName { get; set; }
        public int RectangleIndex { get; set; }
    }

    public static class StepWatermarkTemplateLibrary
    {
        public static IReadOnlyList<StepWatermarkTemplate> GetKnownTemplates()
        {
            return new[]
            {
                DecodeTemplate(
                    "LCEDA",
                    "text",
                    "LCEDA",
                    21,
                    96,
                    "AAIQQAACCEAAAQggAAEEIIAABBCAAALw//8HCOD+/9///wsEYIEALBCABQKwQAAWCMADAQggAMF/II44RHiM5n9TRthuCPYFgd0goAsEdIGALhDQBQK6QUAvCGgHge8g8AEEIIAA/P//AQI4QAD37+8CBV2goAsUdIGCLlDQBQq6wMEfCEAAAQggAAEE4P//DxDA/f+7/38XCOgCAd0goBsE9IKAVhDYGgLdRrCyi1vMnwkjGMH3IcAHHBCAHwIwXgAOD0APDwinB8GOI9jHB5vPYIMbbB6DfXyw4QinBzkc4PEAjBOAHwJwQAACCEAAAQggAAEEIIAABBCA"),
                DecodeTemplate(
                    "EasyEDA",
                    "text",
                    "EasyEDA",
                    96,
                    32,
                    "AAAAAAAAAAIBAAAAAAAAAAAAAAIBAAAAAAD8BwAAAAIBAAAAAAAHPAAAAAIBAAAAAMABYAAAg//3gwEAAGAAwAAAw//3hwMAADD8hwEA4/sxzwMAwB8GHAMA4+IxzgcA4AADMAYAY+I37AcAOIAAYAYAY+I37A8ADMAAQAwA4+Ix7g8AhH8AwDgA4+Ix/w8AxmAAwGAA///3NxwAYgAAgMMAv//3MxwAMwAAAI4BAP4BAAAAMQAAABgBAP4BAAAAEQAACBABAAAAAAAAEQAAHjABAAAAAAAAEfDBMTADPwAAvx8MM7x3IDADPwAAv38cIwYcPBABg+fPh3EeYgMABhgBw//Ph2E+xuGAA4wB33/vv2E2DLHh+IcA3/99v2F/GBgxCMAAw+9/h3F/MBgRCHAA/757v/9j4JkRGBwA//87v7/jgPEY8AcA//85v5/hAAMIAAAAAAA8AAAAAAYMAAAAAAAeAAAAALwHAAAAAAAMAAAAAPABAAAAAAAAAAAA"),
                DecodeTemplate(
                    "easyeda-logo",
                    "logo",
                    "",
                    31,
                    96,
                    "APgHAACPDwDAAAwAMAAMAAw/DADDeQyAMWAGQAwgAjAGMAMOBpiBAQbIQBgCZDA+AzOYkYExTIjAMGZmwDHjMYARYQiAmQEGwIgDAcAMjwFgBo4AIAPMABABRACIAEYAZAAiADMAM4AYPBFgBLMPEIOZAIbADIBhYAZ4GDADBAaYAcIBiIA5AMTgDADGHwMAhocAAAZgAAAGHAAA/gcAAHwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA8H/8H/g//g/cHQcA7o4DAHfHAYCT4R+AP+Af4B9wHLAPHBywBw4O/AMHB/6AgwN2gMGAe8B/wD7wf+Ab+D/gDdwdZwbujsMDJ8f/AQDAH8D/gT/g/wA3cHAAHjg44D8YHPg/vAf8H/wB7g59AHfHA4C74w8AAIAf4P/APvB/YB84OPADHBx/AAyGBwCOgwAA/wAAgD8AAOABAADwAwAA4A8AAOAfAACwDwAA+AEAAD8AAMAHAABgAAAA")
            };
        }

        private static StepWatermarkTemplate DecodeTemplate(
            string name,
            string kind,
            string text,
            int width,
            int height,
            string base64Bits)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Template dimensions must be positive.");

            byte[] bytes = Convert.FromBase64String(base64Bits);
            var points = new List<StepWatermarkTemplatePoint>();
            int expectedBits = checked(width * height);
            for (int index = 0; index < expectedBits; index++)
            {
                int byteIndex = index / 8;
                int bitIndex = index % 8;
                if (byteIndex >= bytes.Length)
                    break;
                if ((bytes[byteIndex] & (1 << bitIndex)) == 0)
                    continue;

                points.Add(new StepWatermarkTemplatePoint(
                    index % width,
                    index / width));
            }

            return new StepWatermarkTemplate
            {
                Name = name,
                Kind = kind,
                Text = text,
                Width = width,
                Height = height,
                EdgePoints = points
            };
        }
    }
}
