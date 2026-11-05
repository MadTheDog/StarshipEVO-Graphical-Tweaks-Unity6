using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GraphicalTweaks
{
    // Fingerprints the transparent pass and its executable D3D11 programs.
    // Other passes, their compressed bytes and local asset IDs are excluded.
    internal static class ShaderProgramFingerprint
    {
        private readonly struct ProgramReference
        {
            internal readonly int Index, Offset, KeywordCount;
            internal ProgramReference(int index, int offset, int keywordCount)
            { Index = index; Offset = offset; KeywordCount = keywordCount; }
        }

        internal static string TransparentPass(byte[] shader, string shaderName)
        {
            int begin = FindName(shader, "5_TRANSPARENT_BASE");
            int end = FindName(shader, "6_DEFERRED_CUTOUT");
            if (begin < 4 || end <= begin || end - begin > 65536)
                throw new NotSupportedException("Native transparent pass metadata changed.");
            int tail = FindName(shader, shaderName);
            if (tail <= end) throw new NotSupportedException("Native shader metadata order changed.");
            using (var source = new MemoryStream(shader, false))
            using (var reader = new BinaryReader(source))
            {
                source.Position = Align(tail + Encoding.UTF8.GetByteCount(shaderName));
                SkipString(reader);
                SkipString(reader);
                if (reader.ReadInt32() != 0) throw new NotSupportedException("Native shader dependencies changed.");
                source.Position = Align(source.Position + 1) + 4; // Serialized Unity6 shader flags.
                int[] platforms = Array(reader, 16);
                int[][] offsets = Arrays(reader), compressed = Arrays(reader), expanded = Arrays(reader);
                int bytes = reader.ReadInt32();
                long blob = source.Position;
                if (bytes < 0 || bytes > shader.Length - blob || platforms.Length != offsets.Length ||
                    platforms.Length != compressed.Length || platforms.Length != expanded.Length)
                    throw new NotSupportedException("Native shader program table changed.");
                int platform = System.Array.IndexOf(platforms, 4); // D3D11.
                if (platform < 0 || offsets[platform].Length != 1 || compressed[platform].Length != 1 || expanded[platform].Length != 1)
                    throw new NotSupportedException("Native D3D11 shader segments changed.");
                int offset = offsets[platform][0], size = compressed[platform][0];
                if (offset < 0 || size < 0 || offset > bytes - size)
                    throw new NotSupportedException("Native compressed shader range is invalid.");
                byte[] programs = DecodeLz4(shader, checked((int)blob + offset), size, expanded[platform][0]);
                int programCount = Int(programs, 0);
                if (programCount < 1 || programCount > 16384 || 4L + programCount * 12L > programs.Length)
                    throw new NotSupportedException("Native shader program index is invalid.");
                var references = PassReferences(shader, begin, end, programCount);
                var metadata = new byte[end - begin];
                Buffer.BlockCopy(shader, begin, metadata, 0, metadata.Length);
                foreach (var reference in references)
                {
                    // Program blob indices are local identifiers.
                    System.Array.Clear(metadata, reference.Offset - begin, 4);
                }
                using (var output = new MemoryStream())
                using (var writer = new BinaryWriter(output))
                {
                    writer.Write(end - begin);
                    writer.Write(metadata);
                    foreach (var reference in references)
                    {
                        int index = reference.Index;
                        int entry = Int(programs, 4 + index * 12), length = Int(programs, 8 + index * 12);
                        if (Int(programs, 12 + index * 12) != 0 || entry < 0 || length < 32 || entry > programs.Length - length)
                            throw new NotSupportedException("Native transparent program range changed.");
                        int dxbc = Find(programs, Encoding.ASCII.GetBytes("DXBC"), entry, entry + length);
                        if (dxbc < 0 || dxbc > entry + length - 28) throw new NotSupportedException("Native transparent executable is missing.");
                        int executableSize = Int(programs, dxbc + 24);
                        if (executableSize < 28 || executableSize > entry + length - dxbc)
                            throw new NotSupportedException("Native transparent executable size changed.");
                        using (var keywords = new MemoryStream(programs, entry, length, false))
                        using (var keywordReader = new BinaryReader(keywords))
                        {
                            keywords.Position = 24;
                            int keywordCount = keywordReader.ReadInt32();
                            if (keywordCount != reference.KeywordCount) throw new NotSupportedException("Native transparent keywords changed.");
                            var names = new List<string>();
                            for (int keyword = 0; keyword < keywordCount; keyword++)
                            {
                                int keywordLength = keywordReader.ReadInt32();
                                if (keywordLength < 0 || keywordLength > 512 || keywordLength > keywords.Length - keywords.Position)
                                    throw new NotSupportedException("Native transparent keyword name changed.");
                                names.Add(Encoding.UTF8.GetString(keywordReader.ReadBytes(keywordLength)));
                                keywords.Position = Align(keywords.Position);
                            }
                            names.Sort(StringComparer.Ordinal);
                            writer.Write(names.Count);
                            foreach (string name in names) writer.Write(name);
                        }
                        writer.Write(executableSize);
                        writer.Write(programs, dxbc, executableSize);
                    }
                    writer.Flush();
                    using (var hash = SHA256.Create())
                        return BitConverter.ToString(hash.ComputeHash(output.ToArray())).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        private static List<ProgramReference> PassReferences(byte[] data, int begin, int end, int programs)
        {
            var result = new List<ProgramReference>();
            var stages = new HashSet<int>();
            for (int at = Align(begin); at <= end - 28; at += 4)
            {
                int count = Int(data, at);
                if (count < 2 || count > 64) continue;
                int cursor = at + 4, stage = -1;
                var indices = new List<ProgramReference>();
                for (int index = 0; index < count && cursor <= end - 24; index++)
                {
                    int program = Int(data, cursor), keywords = Int(data, cursor + 4);
                    if (program < 1 || program >= programs || keywords < 0 || keywords > 32) break;
                    int typeAt = Align(cursor + 8 + keywords * 2) + 8;
                    if (typeAt > end - 4) break;
                    int type = Int(data, typeAt);
                    if ((type != 15 && type != 17) || (stage != -1 && stage != type)) break;
                    stage = type;
                    indices.Add(new ProgramReference(program, cursor, keywords));
                    cursor = typeAt + 4;
                }
                if (indices.Count != count) continue;
                if (!stages.Add(stage)) throw new NotSupportedException("Native transparent program references are ambiguous.");
                result.AddRange(indices);
                at = cursor - 4;
            }
            if (!stages.Contains(15) || !stages.Contains(17)) throw new NotSupportedException("Native transparent vertex/pixel references changed.");
            return result;
        }

        private static byte[] DecodeLz4(byte[] source, int start, int size, int expected)
        {
            if (expected < 1 || expected > 8 * 1024 * 1024) throw new NotSupportedException("Native shader exceeds the decode budget.");
            var output = new byte[expected];
            int input = start, end = checked(start + size), written = 0;
            int Length(int value)
            {
                if (value != 15) return value;
                int part;
                do { if (input >= end) throw new InvalidDataException("Truncated shader LZ4 length."); part = source[input++]; value = checked(value + part); } while (part == 255);
                return value;
            }
            while (input < end)
            {
                int token = source[input++], literals = Length(token >> 4);
                if (literals > end - input || literals > expected - written) throw new InvalidDataException("Invalid shader LZ4 literal.");
                Buffer.BlockCopy(source, input, output, written, literals);
                input += literals; written += literals;
                if (input == end) break;
                if (input > end - 2) throw new InvalidDataException("Truncated shader LZ4 match.");
                int distance = source[input] | source[input + 1] << 8;
                input += 2;
                int match = checked(Length(token & 15) + 4);
                if (distance == 0 || distance > written || match > expected - written) throw new InvalidDataException("Invalid shader LZ4 match.");
                for (int index = 0; index < match; index++) { output[written] = output[written - distance]; written++; }
            }
            if (written != expected) throw new InvalidDataException("Native shader LZ4 output length differs.");
            return output;
        }

        private static int[] Array(BinaryReader reader, int maximum)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > maximum) throw new NotSupportedException("Native shader array exceeds its budget.");
            var result = new int[count];
            for (int index = 0; index < count; index++) result[index] = reader.ReadInt32();
            return result;
        }
        private static int[][] Arrays(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > 16) throw new NotSupportedException("Native shader platform count exceeds its budget.");
            var result = new int[count][];
            for (int index = 0; index < count; index++) result[index] = Array(reader, 16);
            return result;
        }
        private static void SkipString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Invalid native shader string.");
            reader.BaseStream.Position = Align(reader.BaseStream.Position + length);
        }
        private static int FindName(byte[] source, string name)
        {
            byte[] text = Encoding.UTF8.GetBytes(name);
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes))
            {
                writer.Write(text.Length); writer.Write(text); writer.Flush();
                int at = Find(source, bytes.ToArray(), 0, source.Length);
                if (at < 0) return -1;
                if (Find(source, bytes.ToArray(), at + 1, source.Length) >= 0)
                    throw new NotSupportedException("Native shader name is ambiguous.");
                return at + 4;
            }
        }
        private static int Find(byte[] source, byte[] pattern, int begin, int end)
        {
            for (int at = begin; at <= end - pattern.Length; at++)
            {
                int index = 0;
                while (index < pattern.Length && source[at + index] == pattern[index]) index++;
                if (index == pattern.Length) return at;
            }
            return -1;
        }
        private static int Int(byte[] source, int at) => BitConverter.ToInt32(source, at);
        private static int Align(int position) => checked((position + 3) & ~3);
        private static long Align(long position) => checked((position + 3) & ~3L);
    }
}
