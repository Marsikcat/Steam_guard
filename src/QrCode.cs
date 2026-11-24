using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace LocalSteamGuard
{
    // QR Code Model 2, version 4, error correction M, byte mode, mask 0.
    internal sealed class QrCode
    {
        public const int Size = 33;
        public const int MaxBytes = 62;
        private const int DataPerBlock = 32;
        private const int ParityPerBlock = 18;
        private readonly bool[,] dark = new bool[Size, Size];
        private readonly bool[,] reserved = new bool[Size, Size];

        public QrCode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            if (data.Length > MaxBytes)
                throw new ArgumentException("QR payload is too long.", "text");
            DrawPatterns();
            DrawData(Encode(data));
        }

        public bool IsDark(int x, int y) { return dark[x, y]; }

        private void SetPattern(int x, int y, bool value)
        {
            dark[x, y] = value;
            reserved[x, y] = true;
        }

        private void DrawFinder(int centerX, int centerY)
        {
            for (int y = -4; y <= 4; y++)
                for (int x = -4; x <= 4; x++)
                {
                    int column = centerX + x;
                    int row = centerY + y;
                    if (column < 0 || column >= Size || row < 0 || row >= Size)
                        continue;
                    int distance = Math.Max(Math.Abs(x), Math.Abs(y));
                    SetPattern(column, row, distance != 2 && distance != 4);
                }
        }

        private void DrawPatterns()
        {
            for (int i = 0; i < Size; i++)
            {
                SetPattern(6, i, i % 2 == 0);
                SetPattern(i, 6, i % 2 == 0);
            }
            DrawFinder(3, 3);
            DrawFinder(Size - 4, 3);
            DrawFinder(3, Size - 4);
            for (int y = -2; y <= 2; y++)
                for (int x = -2; x <= 2; x++)
                    SetPattern(26 + x, 26 + y, Math.Max(Math.Abs(x), Math.Abs(y)) != 1);

            const int format = 0x5412; // BCH format word for M and mask zero.
            for (int i = 0; i < 6; i++)
                SetPattern(8, i, Bit(format, i));
            SetPattern(8, 7, Bit(format, 6));
            SetPattern(8, 8, Bit(format, 7));
            SetPattern(7, 8, Bit(format, 8));
            for (int i = 9; i < 15; i++)
                SetPattern(14 - i, 8, Bit(format, i));
            for (int i = 0; i < 8; i++)
                SetPattern(Size - 1 - i, 8, Bit(format, i));
            for (int i = 8; i < 15; i++)
                SetPattern(8, Size - 15 + i, Bit(format, i));
            SetPattern(8, Size - 8, true);
        }

        private void DrawData(byte[] words)
        {
            int index = 0;
            bool upwards = true;
            for (int right = Size - 1; right >= 1; right -= 2)
            {
                if (right == 6)
                    right--;
                for (int row = 0; row < Size; row++)
                {
                    int y = upwards ? Size - 1 - row : row;
                    for (int x = right; x > right - 2; x--)
                    {
                        if (reserved[x, y])
                            continue;
                        bool value = index < words.Length * 8 && Bit(words[index / 8], 7 - index % 8);
                        dark[x, y] = value ^ ((x + y) % 2 == 0);
                        index++;
                    }
                }
                upwards = !upwards;
            }
        }

        private static byte[] Encode(byte[] payload)
        {
            var bits = new List<bool>(DataPerBlock * 16);
            AppendBits(bits, 4, 4);
            AppendBits(bits, payload.Length, 8);
            foreach (byte value in payload)
                AppendBits(bits, value, 8);
            AppendBits(bits, 0, 4);

            byte[] data = new byte[DataPerBlock * 2];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i])
                    data[i / 8] |= (byte)(128 >> (i % 8));
            for (int i = bits.Count / 8; i < data.Length; i++)
                data[i] = (byte)((i - bits.Count / 8) % 2 == 0 ? 236 : 17);

            byte[] polynomial = Generator();
            byte[] first = Parity(data, 0, polynomial);
            byte[] second = Parity(data, DataPerBlock, polynomial);
            byte[] result = new byte[data.Length + first.Length + second.Length];
            for (int i = 0; i < DataPerBlock; i++)
            {
                result[2 * i] = data[i];
                result[2 * i + 1] = data[DataPerBlock + i];
            }
            for (int i = 0; i < ParityPerBlock; i++)
            {
                result[data.Length + 2 * i] = first[i];
                result[data.Length + 2 * i + 1] = second[i];
            }
            return result;
        }

        private static void AppendBits(List<bool> bits, int value, int length)
        {
            for (int i = length - 1; i >= 0; i--)
                bits.Add(Bit(value, i));
        }

        private static bool Bit(int value, int index) { return ((value >> index) & 1) != 0; }

        private static byte[] Generator()
        {
            byte[] polynomial = { 1 };
            byte root = 1;
            for (int degree = 0; degree < ParityPerBlock; degree++)
            {
                var next = new byte[polynomial.Length + 1];
                for (int i = 0; i < polynomial.Length; i++)
                {
                    next[i] ^= polynomial[i];
                    next[i + 1] ^= Multiply(polynomial[i], root);
                }
                polynomial = next;
                root = Multiply(root, 2);
            }
            return polynomial;
        }

        private static byte[] Parity(byte[] data, int offset, byte[] generator)
        {
            var remainder = new byte[ParityPerBlock];
            for (int i = offset; i < offset + DataPerBlock; i++)
            {
                byte factor = (byte)(data[i] ^ remainder[0]);
                for (int j = 0; j < remainder.Length; j++)
                    remainder[j] = (byte)((j + 1 < remainder.Length ? remainder[j + 1] : 0)
                        ^ Multiply(generator[j + 1], factor));
            }
            return remainder;
        }

        private static byte Multiply(int left, int right)
        {
            int result = 0;
            while (right != 0)
            {
                if ((right & 1) != 0)
                    result ^= left;
                left <<= 1;
                if ((left & 256) != 0)
                    left ^= 0x11D;
                right >>= 1;
            }
            return (byte)result;
        }
    }

    internal sealed class QrView : Control
    {
        private QrCode code;

        public QrView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Theme.Surface;
            AccessibleName = "QR-код для сканирования в Steam Mobile";
            TabStop = false;
        }

        public void ShowCode(string text)
        {
            code = text == null ? null : new QrCode(text);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (code == null)
                return;
            const int quietZone = 4;
            const int paddedSize = QrCode.Size + 2 * quietZone;
            int scale = Math.Min(ClientSize.Width, ClientSize.Height) / paddedSize;
            if (scale == 0)
                return;
            int size = paddedSize * scale;
            int left = (ClientSize.Width - size) / 2;
            int top = (ClientSize.Height - size) / 2;
            e.Graphics.FillRectangle(Brushes.White, left, top, size, size);
            for (int y = 0; y < QrCode.Size; y++)
                for (int x = 0; x < QrCode.Size; x++)
                    if (code.IsDark(x, y))
                        e.Graphics.FillRectangle(Brushes.Black, left + (x + quietZone) * scale,
                            top + (y + quietZone) * scale, scale, scale);
        }
    }
}
