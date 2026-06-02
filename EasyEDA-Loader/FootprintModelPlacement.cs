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

    internal static class FootprintModelPlacement
    {
        private const double RotationEpsilonDeg = 1e-6;

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
