using System.Runtime.InteropServices;
using System.Text;

namespace AutumnOS.NativeHost.Windows;

// Only kernel observations by the parent establish disappearance. The child
// reports the original name so the parent can open and verify its real object.
internal sealed record NativeHostContextFacts(string StationName, string PackageSid)
{
    private string? profilePath;
    internal bool ProfileCreationPermitted { get; set; }

    internal static async Task WriteNameAsync(Stream output, string name)
    {
        ValidateName(name);
        byte[] bytes = Encoding.ASCII.GetBytes(name);
        await output.WriteAsync(new byte[] { checked((byte)bytes.Length) }).ConfigureAwait(false);
        await output.WriteAsync(bytes).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    internal static async Task<NativeHostContextFacts> CaptureAsync(Stream input, string ownerSid, string packageSid)
    {
        byte[] length = new byte[1];
        await input.ReadExactlyAsync(length).ConfigureAwait(false);
        if (length[0] == 0) throw new InvalidDataException("NATIVE_STATION_NAME_REQUIRED");
        byte[] bytes = new byte[length[0]];
        await input.ReadExactlyAsync(bytes).ConfigureAwait(false);
        string name = Encoding.ASCII.GetString(bytes);
        ValidateName(name);
        IntPtr station = SessionNative.OpenWindowStationW(name, false, 0x20002);
        Native.Check(station != IntPtr.Zero, "verify_original_private_station");
        try { _ = OwnedStation.VerifyStationHandle(station, name, ownerSid, packageSid); }
        finally { Native.Check(SessionNative.CloseWindowStation(station), "close_original_private_station_query"); }

        return new(name, packageSid);
    }

    internal void CaptureOriginalProfile()
    {
        int result = Native.GetAppContainerFolderPath(PackageSid, out IntPtr path);
        try
        {
            if (result != 0 || path == IntPtr.Zero) throw new InvalidDataException("NATIVE_PROFILE_PATH_BINDING_REQUIRED");
            string folder = Path.GetFullPath(Marshal.PtrToStringUni(path)!);
            OwnedFiles.FixedLocal(folder); OwnedFiles.PlainAncestors(folder);
            profilePath = folder;
        }
        finally { if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path); }
    }

    internal bool VerifyAbsent()
    {
        IntPtr station = SessionNative.OpenWindowStationW(StationName, false, 2);
        int stationError = Marshal.GetLastWin32Error();
        if (station != IntPtr.Zero) { Native.Check(SessionNative.CloseWindowStation(station), "close_original_station_postcheck"); return false; }
        if (stationError is not (2 or 3)) return false;
        if (!ProfileCreationPermitted) return true;
        if (profilePath is null) return false;
        if (Native.GetFileAttributesW(profilePath) != uint.MaxValue || Marshal.GetLastWin32Error() is not (2 or 3)) return false;
        int result = Native.GetAppContainerFolderPath(PackageSid, out IntPtr path);
        if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path);
        return result is unchecked((int)0x80070002) or unchecked((int)0x80070003);
    }

    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 255 || name.Equals("WinSta0", StringComparison.OrdinalIgnoreCase) ||
            name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '$')))
            throw new InvalidDataException("NATIVE_PRIVATE_STATION_NAME_INVALID");
    }

    internal static void RequireReply(NativeViewKind request, NativeViewPacket reply, long expectedSequence)
    {
        if (reply.Sequence != expectedSequence || !ReplyKind(request, reply.Kind))
            throw new InvalidDataException("NATIVE_VIEW_REQUEST_REPLY_BINDING_REQUIRED");
    }

    internal static bool IsRequest(NativeViewKind kind) => kind is >= NativeViewKind.PrepareDocument and <= NativeViewKind.Resize;
    private static bool ReplyKind(NativeViewKind request, NativeViewKind response)
    {
        if (!IsRequest(request)) return false;
        if (response == NativeViewKind.Failed) return true;
        return request switch
        {
            NativeViewKind.PrepareDocument => response == NativeViewKind.Proof,
            NativeViewKind.ReleaseDocument => response == NativeViewKind.DocumentReady,
            NativeViewKind.Poll => response is NativeViewKind.Frame or NativeViewKind.Idle or NativeViewKind.SdkRequest,
            NativeViewKind.Delivery => response == NativeViewKind.Receipt,
            NativeViewKind.Close => response == NativeViewKind.Closed,
            NativeViewKind.Composition => response is NativeViewKind.Accepted or NativeViewKind.InputRejected,
            _ => response == NativeViewKind.Accepted
        };
    }
}
