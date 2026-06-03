using System;

namespace EasyEDA_Loader
{
    internal readonly struct FootprintModelMove
    {
        public FootprintModelMove(double xMm, double yMm)
        {
            XMm = xMm;
            YMm = yMm;
        }

        public double XMm { get; }
        public double YMm { get; }
    }

    internal readonly struct FootprintModelRotation
    {
        public FootprintModelRotation(double xDeg, double yDeg, double zDeg)
        {
            X = xDeg;
            Y = yDeg;
            Z = zDeg;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }
    }

    internal static class FootprintModelPlacement
    {
        private const double RotationEpsilonDeg = 1e-6;
        private const double ZeroModelOffsetEpsilonMm = 0.01;

        public static FootprintModelMove CalculateCenteringMoveMm(
            StepSilhouetteBounds currentBounds,
            double targetCenterX,
            double targetCenterY)
        {
            if (currentBounds == null || currentBounds.Right <= currentBounds.Left || currentBounds.Top <= currentBounds.Bottom)
                return new FootprintModelMove(targetCenterX, targetCenterY);

            return new FootprintModelMove(
                targetCenterX - currentBounds.CenterX,
                targetCenterY - currentBounds.CenterY);
        }

        public static FootprintModelMove ResolveModelCenterMm(
            double? productCenterX,
            double? productCenterY,
            double footprintCenterX,
            double footprintCenterY)
        {
            if (!productCenterX.HasValue || !productCenterY.HasValue ||
                (Math.Abs(productCenterX.Value) <= ZeroModelOffsetEpsilonMm &&
                 Math.Abs(productCenterY.Value) <= ZeroModelOffsetEpsilonMm))
            {
                return new FootprintModelMove(footprintCenterX, footprintCenterY);
            }

            return new FootprintModelMove(productCenterX.Value, productCenterY.Value);
        }

        public static FootprintModelRotation ResolveAltiumModelRotationDeg(
            double easyEdaRotationX,
            double easyEdaRotationY,
            double easyEdaRotationZ)
        {
            return new FootprintModelRotation(
                NormalizeRotationDegrees(easyEdaRotationX),
                NormalizeRotationDegrees(easyEdaRotationY),
                NormalizeRotationDegrees(easyEdaRotationZ));
        }

        public static FootprintModelRotation ResolveProjectionModelRotationDeg(FootprintModelRotation altiumModelRotation)
        {
            return new FootprintModelRotation(
                altiumModelRotation.X,
                altiumModelRotation.Y,
                altiumModelRotation.Z);
        }

        public static double ProjectionPlacementRotationDeg(double correctionDeg)
        {
            return NormalizeRotationDegrees(correctionDeg);
        }

        private static double NormalizeRotationDegrees(double angleDeg)
        {
            double normalized = angleDeg % 360.0;
            if (normalized < 0)
                normalized += 360.0;

            if (Math.Abs(normalized) <= RotationEpsilonDeg || Math.Abs(normalized - 360.0) <= RotationEpsilonDeg)
                return 0.0;

            return normalized;
        }
    }
}
