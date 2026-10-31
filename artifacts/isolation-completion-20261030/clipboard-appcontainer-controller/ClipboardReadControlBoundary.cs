namespace AutumnOS.OwnedBrowserParent;

// Report facts only. They never grant execution or replace kernel readback.
internal sealed class ClipboardReadControlBoundary
{
    public string? initial_DACL_sha256 { get; internal set; }
    public string? runtime_DACL_sha256 { get; internal set; }
    public string? final_DACL_sha256 { get; internal set; }
    public uint current_station_package_mask { get; internal set; } = 0x23;
    public uint desktop_package_mask => 0x87;
    public bool readcontrol_only_applied { get; internal set; }
    public bool restored_original_DACL { get; internal set; }
    public List<object> observations { get; } = [];
}
