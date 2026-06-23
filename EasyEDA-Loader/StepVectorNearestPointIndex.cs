using System;
using System.Collections.Generic;
using System.Linq;

namespace EasyEDA_Loader
{
    internal readonly struct StepVectorNearestPoint
    {
        public StepVectorNearestPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    internal sealed class StepVectorNearestPointIndex
    {
        private const int LinearScanThreshold = 24;
        private readonly StepVectorNearestPoint[] points;
        private readonly Node root;

        public StepVectorNearestPointIndex(IEnumerable<StepVectorNearestPoint> sourcePoints)
        {
            points = sourcePoints == null
                ? Array.Empty<StepVectorNearestPoint>()
                : sourcePoints.ToArray();

            if (points.Length > LinearScanThreshold)
            {
                StepVectorNearestPoint[] sorted = points.ToArray();
                root = Build(sorted, 0, sorted.Length, 0);
            }
        }

        public double NearestDistanceSquared(double x, double y)
        {
            if (points.Length == 0)
                return double.MaxValue;

            if (root == null)
                return NearestDistanceSquaredLinear(x, y);

            double best = double.MaxValue;
            Search(root, x, y, ref best);
            return best;
        }

        public double NearestDistance(double x, double y)
        {
            return Math.Sqrt(NearestDistanceSquared(x, y));
        }

        private double NearestDistanceSquaredLinear(double x, double y)
        {
            double best = double.MaxValue;
            foreach (StepVectorNearestPoint point in points)
            {
                double dx = x - point.X;
                double dy = y - point.Y;
                double distance = dx * dx + dy * dy;
                if (distance < best)
                    best = distance;
            }

            return best;
        }

        private static Node Build(StepVectorNearestPoint[] items, int start, int count, int depth)
        {
            if (count <= 0)
                return null;

            int axis = depth % 2;
            Array.Sort(items, start, count, axis == 0 ? XComparer.Instance : YComparer.Instance);
            int middle = start + count / 2;
            int leftCount = middle - start;
            int rightStart = middle + 1;
            int rightCount = start + count - rightStart;

            return new Node
            {
                Point = items[middle],
                Axis = axis,
                Left = Build(items, start, leftCount, depth + 1),
                Right = Build(items, rightStart, rightCount, depth + 1)
            };
        }

        private static void Search(Node node, double x, double y, ref double best)
        {
            if (node == null)
                return;

            double dx = x - node.Point.X;
            double dy = y - node.Point.Y;
            double distance = dx * dx + dy * dy;
            if (distance < best)
                best = distance;

            double axisDelta = node.Axis == 0 ? dx : dy;
            Node near = axisDelta <= 0.0 ? node.Left : node.Right;
            Node far = axisDelta <= 0.0 ? node.Right : node.Left;

            Search(near, x, y, ref best);
            if (axisDelta * axisDelta <= best)
                Search(far, x, y, ref best);
        }

        private sealed class Node
        {
            public StepVectorNearestPoint Point;
            public int Axis;
            public Node Left;
            public Node Right;
        }

        private sealed class XComparer : IComparer<StepVectorNearestPoint>
        {
            public static readonly XComparer Instance = new XComparer();

            public int Compare(StepVectorNearestPoint left, StepVectorNearestPoint right)
            {
                int x = left.X.CompareTo(right.X);
                return x != 0 ? x : left.Y.CompareTo(right.Y);
            }
        }

        private sealed class YComparer : IComparer<StepVectorNearestPoint>
        {
            public static readonly YComparer Instance = new YComparer();

            public int Compare(StepVectorNearestPoint left, StepVectorNearestPoint right)
            {
                int y = left.Y.CompareTo(right.Y);
                return y != 0 ? y : left.X.CompareTo(right.X);
            }
        }
    }
}
