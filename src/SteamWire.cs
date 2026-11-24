using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace LocalSteamGuard
{
    // Subset of Protocol Buffers used by the Steam Web API.
    internal sealed class SteamWire
    {
        private readonly MemoryStream output = new MemoryStream();

        public SteamWire Number(int field, ulong value)
        {
            WriteVarint(output, (ulong)field << 3);
            WriteVarint(output, value);
            return this;
        }

        public SteamWire Fixed64(int field, ulong value)
        {
            WriteVarint(output, ((ulong)field << 3) | 1);
            for (int i = 0; i < 8; i++)
                output.WriteByte((byte)(value >> (8 * i)));
            return this;
        }

        public SteamWire Text(int field, string value)
        {
            return Bytes(field, Encoding.UTF8.GetBytes(value));
        }

        public SteamWire Bytes(int field, byte[] value)
        {
            WriteVarint(output, ((ulong)field << 3) | 2);
            WriteVarint(output, (ulong)value.Length);
            output.Write(value, 0, value.Length);
            return this;
        }

        public byte[] ToArray()
        {
            return output.ToArray();
        }

        private static void WriteVarint(Stream output, ulong value)
        {
            while (value >= 128)
            {
                output.WriteByte((byte)(value | 128));
                value >>= 7;
            }
            output.WriteByte((byte)value);
        }

        private static ulong ReadVarint(Stream input)
        {
            ulong value = 0;
            for (int shift = 0; shift <= 63; shift += 7)
            {
                int next = input.ReadByte();
                if (next < 0 || (shift == 63 && next > 1))
                    throw new InvalidDataException();
                value |= (ulong)(next & 127) << shift;
                if (next < 128)
                    return value;
            }
            throw new InvalidDataException();
        }

        internal sealed class Reply
        {
            private sealed class Field
            {
                public int Number;
                public ulong Scalar;
                public byte[] Bytes;
            }

            private readonly List<Field> fields = new List<Field>();

            public Reply(byte[] message)
            {
                using (var input = new MemoryStream(message, false))
                using (var reader = new BinaryReader(input))
                {
                    while (input.Position < input.Length)
                    {
                        ulong key = ReadVarint(input);
                        if (key < 8 || key > uint.MaxValue || fields.Count >= 8192)
                            throw new InvalidDataException();

                        var field = new Field { Number = (int)(key >> 3) };
                        switch (key & 7)
                        {
                            case 0: field.Scalar = ReadVarint(input); break;
                            case 1: field.Scalar = reader.ReadUInt64(); break;
                            case 2:
                                ulong length = ReadVarint(input);
                                if (length > (ulong)(input.Length - input.Position))
                                    throw new InvalidDataException();
                                field.Bytes = reader.ReadBytes((int)length);
                                break;
                            case 5: field.Scalar = reader.ReadUInt32(); break;
                            default: throw new InvalidDataException();
                        }
                        fields.Add(field);
                    }
                }
            }

            public ulong Number(int field)
            {
                var value = fields.LastOrDefault(x => x.Number == field && x.Bytes == null);
                return value == null ? 0 : value.Scalar;
            }

            public byte[] Bytes(int field)
            {
                var value = fields.LastOrDefault(x => x.Number == field && x.Bytes != null);
                return value == null ? new byte[0] : value.Bytes;
            }

            public string Text(int field)
            {
                return Encoding.UTF8.GetString(Bytes(field));
            }

            public IEnumerable<Reply> Messages(int field)
            {
                return fields.Where(x => x.Number == field && x.Bytes != null)
                    .Select(x => new Reply(x.Bytes));
            }

            public IEnumerable<ulong> RepeatedNumbers(int field)
            {
                foreach (Field value in fields.Where(x => x.Number == field))
                {
                    if (value.Bytes == null)
                    {
                        yield return value.Scalar;
                        continue;
                    }
                    using (var buffer = new MemoryStream(value.Bytes, false))
                        while (buffer.Position < buffer.Length)
                            yield return ReadVarint(buffer);
                }
            }

            public float Float(int field)
            {
                return BitConverter.ToSingle(BitConverter.GetBytes((uint)Number(field)), 0);
            }
        }
    }
}
