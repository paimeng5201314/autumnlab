using System;
using System.IO;
using System.Text;

namespace AutumnOS.SingleFile
{
    // Layout parsing only. Certificate bytes are not evidence of a trusted signer.
    // Windows still applies its signature policy; the caller still hashes the payload.
    // PE certificate table: https://learn.microsoft.com/windows/win32/debug/pe-format
    internal static class BundleFooter
    {
        internal static void Read(Stream input, string expectedPayloadSha256, out long offset, out long length)
        {
            offset = 0;
            length = 0;
            if (input == null || !input.CanRead || !input.CanSeek || input.Length < 64 ||
                expectedPayloadSha256 == null || expectedPayloadSha256.Length != 64) throw Invalid();

            byte[] dos = ReadAt(input, 0, 64);
            if (dos[0] != 'M' || dos[1] != 'Z') throw Invalid();
            long pe = BitConverter.ToUInt32(dos, 60);
            if (pe < 64) throw Invalid();
            byte[] coff = ReadAt(input, pe, 24);
            if (coff[0] != 'P' || coff[1] != 'E' || coff[2] != 0 || coff[3] != 0) throw Invalid();
            int sections = BitConverter.ToUInt16(coff, 6);
            int optionalSize = BitConverter.ToUInt16(coff, 20);
            if (sections == 0 || sections > 96 || optionalSize < 96) throw Invalid();
            byte[] optional = ReadAt(input, pe + 24, optionalSize);
            int magic = BitConverter.ToUInt16(optional, 0);
            int directoryStart = magic == 0x10b ? 96 : magic == 0x20b ? 112 : 0;
            if (directoryStart == 0 || optionalSize < directoryStart) throw Invalid();
            uint directories = BitConverter.ToUInt32(optional, directoryStart - 4);
            if (directories > (optionalSize - directoryStart) / 8) throw Invalid();

            long sectionTable = pe + 24 + optionalSize;
            long headerEnd = sectionTable + sections * 40L;
            long imageEnd = BitConverter.ToUInt32(optional, 60);
            if (imageEnd < headerEnd || imageEnd > input.Length) throw Invalid();
            for (int index = 0; index < sections; index++)
            {
                byte[] section = ReadAt(input, sectionTable + index * 40L, 40);
                long rawLength = BitConverter.ToUInt32(section, 16);
                long rawOffset = BitConverter.ToUInt32(section, 20);
                if (rawLength == 0) continue;
                if (rawOffset < BitConverter.ToUInt32(optional, 60) || rawOffset > input.Length - rawLength) throw Invalid();
                imageEnd = Math.Max(imageEnd, rawOffset + rawLength);
            }

            long certificateOffset = 0;
            long certificateLength = 0;
            if (directories > 4)
            {
                certificateOffset = BitConverter.ToUInt32(optional, directoryStart + 4 * 8);
                certificateLength = BitConverter.ToUInt32(optional, directoryStart + 4 * 8 + 4);
            }
            bool certificatePresent = certificateOffset != 0 || certificateLength != 0;
            long contentEnd = input.Length;
            if (certificatePresent)
            {
                // Only the supported append-at-EOF signing layout is accepted. Never
                // ignore a malformed directory and search elsewhere for another footer.
                if (certificateOffset < imageEnd || (certificateOffset & 7) != 0 ||
                    certificateLength < 8 || (certificateLength & 7) != 0 ||
                    certificateOffset > input.Length - certificateLength ||
                    certificateOffset + certificateLength != input.Length) throw Invalid();
                long cursor = certificateOffset;
                while (cursor < input.Length)
                {
                    byte[] certificate = ReadAt(input, cursor, 8);
                    long size = BitConverter.ToUInt32(certificate, 0);
                    int revision = BitConverter.ToUInt16(certificate, 4);
                    int type = BitConverter.ToUInt16(certificate, 6);
                    if (size < 8 || size > input.Length - cursor ||
                        (revision != 0x100 && revision != 0x200) || type != 2) throw Invalid();
                    long aligned = (size + 7) & ~7L;
                    if (aligned > input.Length - cursor) throw Invalid();
                    RequireZero(input, cursor + size, (int)(aligned - size));
                    cursor += aligned;
                }
                if (cursor != input.Length) throw Invalid();
                contentEnd = certificateOffset;
            }

            int maximumPadding = certificatePresent ? 7 : 0;
            for (int padding = 0; padding <= maximumPadding; padding++)
            {
                long footerAt = contentEnd - padding - 64;
                if (footerAt < imageEnd) break;
                byte[] footer = ReadAt(input, footerAt, 64);
                if (Encoding.ASCII.GetString(footer, 48, 16) != "AUTUMNOSBUNDLE01") continue;
                RequireZero(input, contentEnd - padding, padding);
                long payloadOffset = BitConverter.ToInt64(footer, 0);
                long payloadLength = BitConverter.ToInt64(footer, 8);
                if (payloadOffset < 1024 || payloadOffset < imageEnd || payloadLength <= 0 ||
                    payloadOffset > footerAt || payloadLength != footerAt - payloadOffset) throw Invalid();
                var digest = new StringBuilder(64);
                for (int i = 16; i < 48; i++) digest.Append(footer[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                if (!string.Equals(digest.ToString(), expectedPayloadSha256, StringComparison.Ordinal)) throw Invalid();
                offset = payloadOffset;
                length = payloadLength;
                return;
            }
            throw Invalid();
        }

        private static byte[] ReadAt(Stream input, long position, int count)
        {
            if (position < 0 || count < 0 || position > input.Length - count) throw Invalid();
            input.Position = position;
            byte[] bytes = new byte[count];
            int read = 0;
            while (read < count)
            {
                int current = input.Read(bytes, read, count - read);
                if (current <= 0) throw Invalid();
                read += current;
            }
            return bytes;
        }

        private static void RequireZero(Stream input, long position, int count)
        {
            byte[] padding = ReadAt(input, position, count);
            for (int index = 0; index < padding.Length; index++) if (padding[index] != 0) throw Invalid();
        }

        private static IOException Invalid() { return new IOException("PORTABLE_PAYLOAD_INVALID"); }
    }
}
