using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GraphicalTweaks
{
    internal static class NativeShaderContract
    {
        internal static string Fingerprint(Stream asset, string shaderName)
        {
            using (var reader = new BinaryReader(asset, Encoding.UTF8, true))
            {
                BigEndian(reader, 4); // Metadata size in the legacy header.
                long fileSize = BigEndian(reader, 4);
                long version = BigEndian(reader, 4);
                long dataOffset = BigEndian(reader, 4);
                if (reader.ReadByte() != 0) throw new NotSupportedException("Native shader asset endianness changed.");
                Read(reader, 3);
                if (version >= 22)
                {
                    BigEndian(reader, 4);
                    fileSize = BigEndian(reader, 8);
                    dataOffset = BigEndian(reader, 8);
                    BigEndian(reader, 8);
                }
                if (version < 21 || version > 23 || fileSize != asset.Length || dataOffset < asset.Position || dataOffset > fileSize)
                    throw new NotSupportedException("Native shader asset header changed.");
                int engineBytes = 0;
                while (reader.ReadByte() != 0)
                    if (++engineBytes > 128) throw new NotSupportedException("Native shader engine header is invalid.");
                reader.ReadInt32(); // Target platform.
                if (reader.ReadBoolean()) throw new NotSupportedException("Native shader asset type trees changed.");
                int typeCount = Count(reader, 4096);
                var classes = new int[typeCount];
                for (int index = 0; index < typeCount; index++)
                {
                    classes[index] = reader.ReadInt32();
                    reader.ReadBoolean();
                    reader.ReadInt16();
                    if (classes[index] == 114) Read(reader, 16);
                    Read(reader, 16);
                }
                int objectCount = Count(reader, 1000000);
                var shaders = new List<(long Offset, int Size)>();
                for (int index = 0; index < objectCount; index++)
                {
                    asset.Position = (asset.Position + 3) & ~3L;
                    reader.ReadInt64(); // Local object IDs may change between builds.
                    long relative = version >= 22 ? reader.ReadInt64() : reader.ReadUInt32();
                    uint size = reader.ReadUInt32();
                    int type = reader.ReadInt32();
                    if (type < 0 || type >= classes.Length || relative < 0 || relative > fileSize - dataOffset ||
                        size > fileSize - dataOffset - relative)
                        throw new NotSupportedException("Native shader object table is invalid.");
                    if (classes[type] == 48)
                    {
                        if (size > 16 * 1024 * 1024) throw new NotSupportedException("Native shader exceeds the inspection budget.");
                        shaders.Add((dataOffset + relative, (int)size));
                    }
                }
                byte[] name = Encoding.UTF8.GetBytes(shaderName);
                string result = null;
                foreach (var shader in shaders)
                {
                    asset.Position = shader.Offset;
                    byte[] bytes = Read(reader, shader.Size);
                    bool matches = false;
                    for (int offset = 4; offset <= bytes.Length - name.Length; offset++)
                    {
                        if (bytes[offset] != name[0] || BitConverter.ToInt32(bytes, offset - 4) != name.Length) continue;
                        int character = 0;
                        while (character < name.Length && bytes[offset + character] == name[character]) character++;
                        if (character == name.Length) { matches = true; break; }
                    }
                    if (!matches) continue;
                    if (result != null) throw new NotSupportedException("Native voxel shader identity is ambiguous.");
                    result = ShaderProgramFingerprint.TransparentPass(bytes, shaderName);
                }
                return result ?? throw new NotSupportedException("Native voxel shader is unavailable.");
            }
        }

        private static byte[] Read(BinaryReader reader, int count)
        {
            byte[] value = reader.ReadBytes(count);
            if (value.Length != count) throw new EndOfStreamException("Truncated native shader asset.");
            return value;
        }

        private static long BigEndian(BinaryReader reader, int count)
        {
            long value = 0;
            foreach (byte part in Read(reader, count)) value = checked((value << 8) | part);
            return value;
        }

        private static int Count(BinaryReader reader, int maximum)
        {
            int value = reader.ReadInt32();
            if (value < 0 || value > maximum) throw new NotSupportedException("Native shader table exceeds its budget.");
            return value;
        }
    }
}
