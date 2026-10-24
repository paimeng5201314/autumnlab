using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AutumnOS.SingleFile;

namespace AutumnOS.Tests;

// Synthetic PE/certificate-layout fixtures only: no real signature, trust-store
// change, process launch, or claim that Smart App Control accepts an executable.
internal static class BundleFooterTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("bundle_footer.unsigned_pe32_and_pe32plus_preserve_region", () =>
        {
            Check(Fixture(false, false)); Check(Fixture(false, true));
        });
        yield return ("bundle_footer.certificate_layout_accepts_all_alignment_padding", () =>
        {
            for (int padding = 0; padding < 8; padding++) Check(Fixture(true, true, 40 - padding));
        });
        yield return ("bundle_footer.certificate_layout_pe32_supported", () => Check(Fixture(true, false)));
        yield return ("bundle_footer.multiple_certificate_records_walk_exact_table", () => Check(Fixture(true, true, 37, 2)));
        yield return ("bundle_footer.unsigned_does_not_search_before_trailing_bytes", () =>
        {
            var f = Fixture(false); Reject(f with { Bytes = [.. f.Bytes, 0] });
        });
        yield return ("bundle_footer.malformed_certificate_never_falls_back_to_eof", () =>
        {
            var f = Fixture(false); Put32(f.Bytes, SecurityDirectory, 512); Put32(f.Bytes, SecurityDirectory + 4, 16); Reject(f);
        });
        yield return ("bundle_footer.certificate_offset_or_size_missing_rejected", () =>
        {
            var f = Fixture(true); Put32(f.Bytes, SecurityDirectory, 0); Reject(f);
            f = Fixture(true); Put32(f.Bytes, SecurityDirectory + 4, 0); Reject(f);
        });
        yield return ("bundle_footer.certificate_misaligned_truncated_or_overlay_rejected", () =>
        {
            var f = Fixture(true); Put32(f.Bytes, SecurityDirectory, f.CertificateAt + 1); Reject(f);
            f = Fixture(true); Put32(f.Bytes, SecurityDirectory + 4, 15); Reject(f);
            f = Fixture(true); Reject(f with { Bytes = f.Bytes[..^1] });
            f = Fixture(true); Reject(f with { Bytes = [.. f.Bytes, 0] });
        });
        yield return ("bundle_footer.certificate_length_cannot_wrap_or_cross_table", () =>
        {
            var f = Fixture(true); Put32(f.Bytes, SecurityDirectory, uint.MaxValue); Reject(f);
            f = Fixture(true); Put32(f.Bytes, SecurityDirectory + 4, uint.MaxValue); Reject(f);
            f = Fixture(true); Put32(f.Bytes, f.CertificateAt, uint.MaxValue); Reject(f);
            f = Fixture(true); Put32(f.Bytes, f.CertificateAt, 0); Reject(f);
            f = Fixture(true); Put32(f.Bytes, f.CertificateAt, 7); Reject(f);
        });
        yield return ("bundle_footer.certificate_record_kind_and_padding_checked", () =>
        {
            var f = Fixture(true); Put16(f.Bytes, f.CertificateAt + 4, 0x300); Reject(f);
            f = Fixture(true); Put16(f.Bytes, f.CertificateAt + 6, 1); Reject(f);
            f = Fixture(true); f.Bytes[f.CertificateAt + 15] = 1; Reject(f);
        });
        yield return ("bundle_footer.pre_certificate_padding_zero_and_at_most_seven", () =>
        {
            var f = Fixture(true); f.Bytes[f.CertificateAt - 1] = 1; Reject(f);
            f = Fixture(true); var extra = new byte[f.Bytes.Length + 8];
            f.Bytes.AsSpan(0, f.CertificateAt).CopyTo(extra);
            f.Bytes.AsSpan(f.CertificateAt).CopyTo(extra.AsSpan(f.CertificateAt + 8));
            Put32(extra, SecurityDirectory, f.CertificateAt + 8); Reject(f with { Bytes = extra });
        });
        yield return ("bundle_footer.dos_pe_header_and_optional_magic_required", () =>
        {
            var f = Fixture(false); f.Bytes[0] = 0; Reject(f);
            f = Fixture(false); Put32(f.Bytes, 60, uint.MaxValue); Reject(f);
            f = Fixture(false); f.Bytes[Pe] = 0; Reject(f);
            f = Fixture(false); Put16(f.Bytes, Optional, 0x999); Reject(f);
        });
        yield return ("bundle_footer.optional_directory_and_sections_stay_in_bounds", () =>
        {
            var f = Fixture(false); Put16(f.Bytes, Pe + 20, 96); Reject(f);
            f = Fixture(false); Put32(f.Bytes, Optional + 108, 17); Reject(f);
            f = Fixture(false); Put16(f.Bytes, Pe + 6, 0); Reject(f);
            f = Fixture(false); Put16(f.Bytes, Pe + 6, 97); Reject(f);
            f = Fixture(false); Put32(f.Bytes, Section + 16, uint.MaxValue); Reject(f);
            f = Fixture(false); Put32(f.Bytes, Section + 20, 1); Reject(f);
            f = Fixture(false); Put32(f.Bytes, Optional + 60, 1); Reject(f);
        });
        yield return ("bundle_footer.footer_region_rejects_header_overlap_and_overflow", () =>
        {
            var f = Fixture(false); Put64(f.Bytes, f.FooterAt, 512); Reject(f);
            f = Fixture(false); Put64(f.Bytes, f.FooterAt, long.MaxValue); Reject(f);
            f = Fixture(false); Put64(f.Bytes, f.FooterAt + 8, long.MaxValue); Reject(f);
            f = Fixture(false); Put64(f.Bytes, f.FooterAt + 8, 0); Reject(f);
            f = Fixture(false); Put64(f.Bytes, f.FooterAt + 8, -1); Reject(f);
            f = Fixture(false); Put64(f.Bytes, f.FooterAt + 8, f.Payload.Length + 1); Reject(f);
        });
        yield return ("bundle_footer.hash_binding_and_marker_required", () =>
        {
            var f = Fixture(false); f.Bytes[f.FooterAt + 16] ^= 1; Reject(f);
            f = Fixture(false); f.Bytes[f.FooterAt + 63] = 0; Reject(f);
            f = Fixture(false); Reject(f with { Hash = new string('0', 64) });
        });
        yield return ("bundle_footer.truncated_input_and_short_reads_handled", () =>
        {
            var f = Fixture(false); Reject(f with { Bytes = f.Bytes[..60] });
            using var stream = new OneByteStream(f.Bytes);
            BundleFooter.Read(stream, f.Hash, out long offset, out long length);
            if (offset != 1024 || length != f.Payload.Length) throw new Exception("Partial stream reads broke the region.");
        });
    }

    private const int Pe = 0x80, Optional = Pe + 24, Section = Optional + 240, SecurityDirectory = Optional + 112 + 32;
    private sealed record FixtureData(byte[] Bytes, byte[] Payload, string Hash, int FooterAt, int CertificateAt);

    private static FixtureData Fixture(bool certificate, bool pe64 = true, int payloadLength = 37, int certificateCount = 1)
    {
        byte[] payload = Enumerable.Range(0, payloadLength).Select(x => (byte)(x + 1)).ToArray();
        byte[] hash = SHA256.HashData(payload);
        int footer = 1024 + payload.Length, originalLength = footer + 64;
        int certificateAt = (originalLength + 7) & ~7;
        byte[] bytes = new byte[certificate ? certificateAt + 16 * certificateCount : originalLength];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; Put32(bytes, 60, Pe);
        bytes[Pe] = (byte)'P'; bytes[Pe + 1] = (byte)'E';
        Put16(bytes, Pe + 6, 1); Put16(bytes, Pe + 20, pe64 ? 240 : 224);
        Put16(bytes, Optional, pe64 ? 0x20b : 0x10b); Put32(bytes, Optional + 60, 512);
        int directories = pe64 ? 112 : 96, section = Optional + (pe64 ? 240 : 224);
        Put32(bytes, Optional + directories - 4, 16);
        Put32(bytes, section + 16, 512); Put32(bytes, section + 20, 512);
        payload.CopyTo(bytes, 1024); Put64(bytes, footer, 1024); Put64(bytes, footer + 8, payload.Length);
        hash.CopyTo(bytes, footer + 16); Encoding.ASCII.GetBytes("AUTUMNOSBUNDLE01").CopyTo(bytes, footer + 48);
        if (certificate)
        {
            Put32(bytes, Optional + directories + 32, certificateAt);
            Put32(bytes, Optional + directories + 36, 16 * certificateCount);
            for (int i = 0; i < certificateCount; i++)
            {
                int record = certificateAt + i * 16;
                Put32(bytes, record, 12); Put16(bytes, record + 4, 0x200); Put16(bytes, record + 6, 2);
                Put32(bytes, record + 8, 0x01020304); // Deliberately not a real PKCS#7 signature.
            }
        }
        return new(bytes, payload, Convert.ToHexStringLower(hash), footer, certificateAt);
    }

    private static void Check(FixtureData fixture)
    {
        using var stream = new MemoryStream(fixture.Bytes);
        BundleFooter.Read(stream, fixture.Hash, out long offset, out long length);
        if (offset != 1024 || length != fixture.Payload.Length ||
            !fixture.Bytes.AsSpan((int)offset, (int)length).SequenceEqual(fixture.Payload))
            throw new Exception("Wrong signed-content payload region.");
    }

    private static void Reject(FixtureData fixture)
    {
        using var stream = new MemoryStream(fixture.Bytes);
        try { BundleFooter.Read(stream, fixture.Hash, out _, out _); }
        catch (IOException error) when (error.Message == "PORTABLE_PAYLOAD_INVALID") { return; }
        throw new Exception("Malformed synthetic bundle layout was accepted.");
    }

    private static void Put16(byte[] bytes, int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), (ushort)value);
    private static void Put32(byte[] bytes, int at, long value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), (uint)value);
    private static void Put64(byte[] bytes, int at, long value) => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value);

    private sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count));
    }
}
