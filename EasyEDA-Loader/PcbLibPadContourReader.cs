using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace EasyEDA_Loader
{
    internal sealed class PersistedPadContour
    {
        public int PadIndex { get; set; }
        public List<PersistedPadPoint> Outline { get; } = new List<PersistedPadPoint>();
        public List<List<PersistedPadPoint>> Holes { get; } = new List<List<PersistedPadPoint>>();
        public bool Used { get; set; }
    }

    internal readonly struct PersistedPadPoint
    {
        public PersistedPadPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    internal static class PcbLibPadContourReader
    {
        private const int BlockSizeMask = 0x00FFFFFF;
        private const int MaxBlockSize = 64 * 1024 * 1024;
        private const int MaxVertexCount = 100000;

        public static IReadOnlyDictionary<string, List<PersistedPadContour>> Read(
            string libraryPath,
            IEnumerable<string> requestedFootprintNames = null)
        {
            var result = new Dictionary<string, List<PersistedPadContour>>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(libraryPath) || !File.Exists(libraryPath))
                return result;

            var requestedNames = new HashSet<string>(
                requestedFootprintNames?.Where(name => !string.IsNullOrWhiteSpace(name)) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            if (requestedNames.Count > 0)
            {
                var parsedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (byte[] data in StructuredStorageReader.ReadNamedChildDataStreams(libraryPath, requestedNames))
                {
                    try
                    {
                        string parsedName = ReadFootprint(data, result);
                        if (!string.IsNullOrWhiteSpace(parsedName))
                            parsedNames.Add(parsedName);
                    }
                    catch
                    {
                    }
                }

                if (requestedNames.All(parsedNames.Contains))
                    return result;

                result.Clear();
            }

            foreach (byte[] data in StructuredStorageReader.ReadChildDataStreams(libraryPath))
            {
                try
                {
                    ReadFootprint(data, result);
                }
                catch
                {
                    // A malformed or unsupported footprint must not prevent the remaining library export.
                }
            }

            return result;
        }

        private static string ReadFootprint(byte[] data, Dictionary<string, List<PersistedPadContour>> result)
        {
            using (var stream = new MemoryStream(data, writable: false))
            using (var reader = new BinaryReader(stream, Encoding.Latin1, leaveOpen: false))
            {
                string footprintName = ReadPascalStringBlock(reader);
                if (string.IsNullOrWhiteSpace(footprintName))
                    return "";

                var contours = new List<PersistedPadContour>();
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    byte objectId = reader.ReadByte();
                    if (objectId == 2)
                    {
                        for (int block = 0; block < 6; block++)
                            SkipBlock(reader);
                    }
                    else if (objectId == 5)
                    {
                        SkipBlock(reader);
                        SkipBlock(reader);
                    }
                    else if (objectId == 11)
                    {
                        PersistedPadContour contour = ReadRegion(reader);
                        if (contour != null && contour.PadIndex > 0 && contour.Outline.Count >= 3)
                            contours.Add(contour);
                    }
                    else
                    {
                        SkipBlock(reader);
                    }
                }

                result[footprintName] = contours;
                return footprintName;
            }
        }

        private static PersistedPadContour ReadRegion(BinaryReader reader)
        {
            int size = ReadBlockSize(reader);
            long start = reader.BaseStream.Position;
            long end = checked(start + size);
            if (size < 22 || end > reader.BaseStream.Length)
                throw new InvalidDataException("Invalid PCB region block.");

            reader.BaseStream.Position = start + 13;
            reader.ReadByte();
            ushort holeCount = reader.ReadUInt16();
            reader.ReadUInt16();
            string parameters = ReadCStringBlock(reader);
            int padIndex = ReadParameterInt(parameters, "PADINDEX");
            uint vertexCount = reader.ReadUInt32();
            if (vertexCount > MaxVertexCount || reader.BaseStream.Position + vertexCount * 16L > end)
                throw new InvalidDataException("Invalid PCB region outline.");

            var contour = new PersistedPadContour { PadIndex = padIndex };
            ReadPoints(reader, vertexCount, contour.Outline);
            for (int hole = 0; hole < holeCount; hole++)
            {
                if (reader.BaseStream.Position + 4 > end)
                    break;
                uint holeVertexCount = reader.ReadUInt32();
                if (holeVertexCount > MaxVertexCount || reader.BaseStream.Position + holeVertexCount * 16L > end)
                    throw new InvalidDataException("Invalid PCB region hole.");
                var holePoints = new List<PersistedPadPoint>((int)holeVertexCount);
                ReadPoints(reader, holeVertexCount, holePoints);
                contour.Holes.Add(holePoints);
            }

            reader.BaseStream.Position = end;
            return contour;
        }

        private static void ReadPoints(BinaryReader reader, uint count, List<PersistedPadPoint> target)
        {
            for (uint index = 0; index < count; index++)
                target.Add(new PersistedPadPoint(reader.ReadDouble(), reader.ReadDouble()));
        }

        private static int ReadParameterInt(string parameters, string name)
        {
            foreach (string field in (parameters ?? "").Split('|'))
            {
                int equals = field.IndexOf('=');
                if (equals <= 0 || !string.Equals(field.Substring(0, equals).Trim(), name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (int.TryParse(field.Substring(equals + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                    return value;
            }
            return 0;
        }

        private static string ReadPascalStringBlock(BinaryReader reader)
        {
            int size = ReadBlockSize(reader);
            long end = checked(reader.BaseStream.Position + size);
            if (size == 0)
                return "";
            if (end > reader.BaseStream.Length)
                throw new EndOfStreamException();

            int length = reader.ReadByte();
            if (length > size - 1)
                throw new InvalidDataException("Invalid PCB Pascal string block.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException();
            reader.BaseStream.Position = end;
            return Encoding.Latin1.GetString(bytes);
        }

        private static string ReadCStringBlock(BinaryReader reader)
        {
            int size = ReadBlockSize(reader);
            byte[] bytes = reader.ReadBytes(size);
            if (bytes.Length != size)
                throw new EndOfStreamException();
            int terminator = Array.IndexOf(bytes, (byte)0);
            return Encoding.Latin1.GetString(bytes, 0, terminator >= 0 ? terminator : bytes.Length);
        }

        private static void SkipBlock(BinaryReader reader)
        {
            int size = ReadBlockSize(reader);
            long next = checked(reader.BaseStream.Position + size);
            if (next > reader.BaseStream.Length)
                throw new EndOfStreamException();
            reader.BaseStream.Position = next;
        }

        private static int ReadBlockSize(BinaryReader reader)
        {
            int size = reader.ReadInt32() & BlockSizeMask;
            if (size < 0 || size > MaxBlockSize)
                throw new InvalidDataException("Invalid PCB data block size.");
            return size;
        }

        private static class StructuredStorageReader
        {
            private const int StgmRead = 0x00000000;
            private const int StgmShareDenyNone = 0x00000040;
            private const int StgmShareExclusive = 0x00000010;
            private const int StgmTransacted = 0x00010000;
            private const int StgtyStorage = 1;
            private const int StgtyStream = 2;

            public static IEnumerable<byte[]> ReadNamedChildDataStreams(string path, IEnumerable<string> footprintNames)
            {
                IStorage root = OpenRoot(path);
                var result = new List<byte[]>();
                try
                {
                    var storageNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string footprintName in footprintNames ?? Enumerable.Empty<string>())
                    {
                        foreach (string storageName in StorageNameCandidates(footprintName))
                        {
                            if (storageNames.Add(storageName) && TryReadDataStream(root, storageName, out byte[] data))
                                result.Add(data);
                        }
                    }
                }
                finally
                {
                    ReleaseComObject(root);
                }

                return result;
            }

            public static IEnumerable<byte[]> ReadChildDataStreams(string path)
            {
                IStorage root = OpenRoot(path);
                var result = new List<byte[]>();
                try
                {
                    root.EnumElements(0, IntPtr.Zero, 0, out IEnumSTATSTG enumerator);
                    try
                    {
                        var entries = new STATSTG[1];
                        while (enumerator.Next(1, entries, out uint fetched) == 0 && fetched == 1)
                        {
                            if (entries[0].type != StgtyStorage || string.Equals(entries[0].pwcsName, "Library", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (TryReadDataStream(root, entries[0].pwcsName, out byte[] data))
                                result.Add(data);
                        }
                    }
                    finally
                    {
                        ReleaseComObject(enumerator);
                    }
                }
                finally
                {
                    ReleaseComObject(root);
                }

                return result;
            }

            private static IStorage OpenRoot(string path)
            {
                int hr = StgOpenStorage(path, null, StgmRead | StgmShareDenyNone | StgmTransacted, IntPtr.Zero, 0, out IStorage root);
                if (hr < 0)
                    Marshal.ThrowExceptionForHR(hr);
                return root;
            }

            private static IEnumerable<string> StorageNameCandidates(string footprintName)
            {
                if (string.IsNullOrWhiteSpace(footprintName))
                    yield break;

                string normalized = footprintName.Replace('/', '_').Replace('\\', '_');
                yield return normalized.Length <= 31 ? normalized : normalized.Substring(0, 31);
                if (!string.Equals(normalized, footprintName, StringComparison.Ordinal))
                    yield return footprintName.Length <= 31 ? footprintName : footprintName.Substring(0, 31);
            }

            private static bool TryReadDataStream(IStorage root, string storageName, out byte[] data)
            {
                data = null;
                IStorage storage = null;
                IStream stream = null;
                try
                {
                    root.OpenStorage(storageName, null, StgmRead | StgmShareExclusive, IntPtr.Zero, 0, out storage);
                    storage.OpenStream("Data", IntPtr.Zero, StgmRead | StgmShareExclusive, 0, out stream);
                    stream.Stat(out STATSTG stat, 1);
                    if (stat.type != StgtyStream || stat.cbSize <= 0 || stat.cbSize > int.MaxValue)
                        return false;

                    var bytes = new byte[(int)stat.cbSize];
                    IntPtr bytesRead = Marshal.AllocCoTaskMem(sizeof(int));
                    try
                    {
                        stream.Read(bytes, bytes.Length, bytesRead);
                        if (Marshal.ReadInt32(bytesRead) != bytes.Length)
                            return false;
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(bytesRead);
                    }

                    data = bytes;
                    return true;
                }
                catch (COMException)
                {
                    return false;
                }
                finally
                {
                    ReleaseComObject(stream);
                    ReleaseComObject(storage);
                }
            }

            private static void ReleaseComObject(object value)
            {
                if (value != null && Marshal.IsComObject(value))
                    Marshal.FinalReleaseComObject(value);
            }

            [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
            private static extern int StgOpenStorage(
                string name,
                IStorage priorityStorage,
                int mode,
                IntPtr exclude,
                int reserved,
                out IStorage storage);

            [ComImport]
            [Guid("0000000B-0000-0000-C000-000000000046")]
            [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IStorage
            {
                void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, int mode, int reserved1, int reserved2, out IStream stream);
                void OpenStream([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr reserved1, int mode, int reserved2, out IStream stream);
                void CreateStorage([MarshalAs(UnmanagedType.LPWStr)] string name, int mode, int reserved1, int reserved2, out IStorage storage);
                void OpenStorage([MarshalAs(UnmanagedType.LPWStr)] string name, IStorage priorityStorage, int mode, IntPtr exclude, int reserved, out IStorage storage);
                void CopyTo(int excludeCount, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Guid[] excludeInterfaces, IntPtr excludeNames, IStorage destination);
                void MoveElementTo([MarshalAs(UnmanagedType.LPWStr)] string name, IStorage destination, [MarshalAs(UnmanagedType.LPWStr)] string newName, int flags);
                void Commit(int flags);
                void Revert();
                void EnumElements(int reserved1, IntPtr reserved2, int reserved3, out IEnumSTATSTG enumerator);
                void DestroyElement([MarshalAs(UnmanagedType.LPWStr)] string name);
                void RenameElement([MarshalAs(UnmanagedType.LPWStr)] string oldName, [MarshalAs(UnmanagedType.LPWStr)] string newName);
                void SetElementTimes([MarshalAs(UnmanagedType.LPWStr)] string name, FILETIME creationTime, FILETIME accessTime, FILETIME modificationTime);
                void SetClass(ref Guid classId);
                void SetStateBits(int stateBits, int mask);
                void Stat(out STATSTG stat, int flags);
            }

            [ComImport]
            [Guid("0000000D-0000-0000-C000-000000000046")]
            [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            private interface IEnumSTATSTG
            {
                [PreserveSig]
                int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray)] STATSTG[] elements, out uint fetched);
                void Skip(uint count);
                void Reset();
                void Clone(out IEnumSTATSTG clone);
            }
        }
    }
}
