using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace AutumnOS.NetOnlyStationProbe;

internal static class StationChild
{
    internal static async Task<int> Run(string nonce, string pipeName, uint parentPid)
    {
        if (!Program.Nonce(nonce) || pipeName != "AutumnOS.NetOnlyStation." + nonce || parentPid == 0) return 2;
        using var parent = Native.OpenProcess(0x101000, false, parentPid);
        if (parent.IsInvalid || Native.Exited(parent) || Native.GetProcessId(parent) != parentPid ||
            !string.Equals(Native.Image(parent), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return 3;
        using var current = Native.Current();
        TokenFacts self = Native.Identity(current), parentFacts = Native.Identity(parent);
        if (!Native.TokenAdmission(parentFacts, self)) return 4;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await pipe.ConnectAsync(budget.Token);
            if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || server != parentPid || Native.Exited(parent))
                throw new InvalidOperationException("PIPE_SERVER_IDENTITY");
            await Protocol.WriteAsync(pipe, new(1, nonce, "hello"), budget.Token);
            await Protocol.ReadAsync(pipe, nonce, "ready", budget.Token);
            await Protocol.ReadAsync(pipe, nonce, "create", budget.Token);
            if (Native.Exited(parent)) throw new InvalidOperationException("PARENT_ORIGINAL_EXITED");
            NetMessage result = Execute(nonce);
            await Protocol.WriteAsync(pipe, result, budget.Token);
            await Protocol.ReadAsync(pipe, nonce, "ack", budget.Token);
            return result.failure_stage == "none" && result.cleanup_confirmed == true ? 0 : 20;
        }
        catch { return 21; } // No exception text, account name, environment or credential logging.
    }

    private static NetMessage Execute(string nonce)
    {
        string stage = "original_station", failure = "none"; int error = 0, closeError = 0;
        IntPtr original = IntPtr.Zero, station = IntPtr.Zero, descriptor = IntPtr.Zero;
        string? originalName = null, name = null, descriptorHash = null;
        bool attempted = false, created = false, exact = false, invisible = false, originalUnchanged = false, closed = false;
        try
        {
            original = Native.GetProcessWindowStation();
            if (original == IntPtr.Zero) throw Native.Error(stage);
            originalName = Native.StationName(original);
            if (originalName != "WinSta0") throw new InvalidOperationException("ORIGINAL_STATION_NOT_WINSTA0");
            using var current = Native.Current();
            string sid = Native.Identity(current).UserSid;
            stage = "owned_descriptor";
            if (!Native.ConvertDescriptor($"O:{sid}G:{sid}D:P(A;;0xF037F;;;{sid})(A;;0xF037F;;;SY)S:(ML;;NW;;;LW)", 1, out descriptor, out _))
                throw Native.Error(stage);
            var attributes = new Native.SecurityAttributes { length = Marshal.SizeOf<Native.SecurityAttributes>(), descriptor = descriptor, inherit = false };
            stage = "create_null_create_only"; attempted = true;
            Native.ClearLastError(0); Marshal.SetLastPInvokeError(0);
            station = Native.CreateWindowStationW(null, 1 /* CWF_CREATE_ONLY */, 0xF037F, ref attributes);
            if (station == IntPtr.Zero) throw Native.Error(stage);
            created = true;
            stage = "created_station_name";
            name = Native.StationName(station);
            if (name.Equals(originalName, StringComparison.OrdinalIgnoreCase) || name.IndexOfAny(['\\', '/', '\0']) >= 0)
                throw new InvalidOperationException("NEW_AUTOMATIC_NAME_REQUIRED");
            exact = Native.StationName(station) == name;
            stage = "created_security_readback";
            descriptorHash = Native.RequireOwnedStation(station, sid); invisible = true;
        }
        catch (Exception ex) { failure = stage; error = ex is Win32Exception win ? win.NativeErrorCode : 0; }
        finally
        {
            // CreateWindowStation may associate the new station. Never close an associated
            // owned station before restoring the borrowed original station. No desktop switch.
            try
            {
                if (original != IntPtr.Zero)
                {
                    IntPtr actual = Native.GetProcessWindowStation();
                    if (actual == IntPtr.Zero) throw Native.Error("cleanup_current_station");
                    if (actual != original && !Native.SetProcessWindowStation(original)) throw Native.Error("restore_original_station");
                    originalUnchanged = Native.GetProcessWindowStation() == original && Native.StationName(original) == originalName;
                    if (!originalUnchanged) throw new InvalidOperationException("ORIGINAL_STATION_RESTORE_UNPROVEN");
                }
            }
            catch (Exception ex)
            {
                if (failure == "none") { failure = "restore_original_station"; error = ex is Win32Exception win ? win.NativeErrorCode : 1; }
            }
            try
            {
                if (station != IntPtr.Zero)
                {
                    if (!originalUnchanged) throw new InvalidOperationException("OWNED_STATION_STILL_ASSOCIATED");
                    if (!Native.CloseWindowStation(station)) { closeError = Marshal.GetLastWin32Error(); throw Native.Error("close_owned_station"); }
                    closed = true; station = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                if (failure == "none") { failure = "close_owned_station"; error = ex is Win32Exception win ? win.NativeErrorCode : 1; }
            }
            if (descriptor != IntPtr.Zero) Native.LocalFree(descriptor);
        }
        bool cleanup = originalUnchanged && (!created || closed);
        return new(1, nonce, "result", name, failure, Math.Max(0, error), cleanup, descriptorHash,
            originalUnchanged, attempted, created, exact, invisible, closed, Math.Max(0, closeError));
    }
}
