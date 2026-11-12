using System.ComponentModel;

namespace AutumnOS.NativeHost.Windows;

internal sealed class OwnedAssertionException(string code) : Exception(code) { }

internal static class NativeFailure
{
    // Never retain arbitrary exception messages, paths, tokens or credentials.
    internal static string Describe(Exception error) => error.GetType().Name + ":0x" + unchecked((uint)error.HResult).ToString("x8")
        + (error is Win32Exception native ? ":win32=" + native.NativeErrorCode : "")
        + (error is OwnedNativeException owned ? ":stage=" + owned.FixedStage : "");
}
