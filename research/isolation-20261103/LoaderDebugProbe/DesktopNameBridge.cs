using System.Runtime.InteropServices;
using System.Text;

namespace AutumnOS.SyntheticLuaLauncher;
// Only the one name-query dependency needed by the linked desktop helper.
internal static class Native
{
    internal static string UserObjectName(IntPtr handle)
    {
        var text = new StringBuilder(512);
        if (handle == IntPtr.Zero || !GetUserObjectInformation(handle, 2, text, text.Capacity * 2, out _))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "USER_OBJECT_NAME_QUERY_FAILED");
        return text.ToString();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int kind, StringBuilder value, int size, out int required);
}
