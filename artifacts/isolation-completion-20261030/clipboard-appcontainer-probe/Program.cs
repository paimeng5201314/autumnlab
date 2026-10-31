using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutumnOS.ClipboardAppContainerProbe;

internal static class Program
{
    private static StreamWriter? output;
    private static readonly object Gate = new();
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    internal static string Nonce { get; private set; } = "";
    internal static long Elapsed => Watch.ElapsedMilliseconds;
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string PathHash(string path) => "sha256:" + Hash(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant());
    internal static object Error(Exception exception) => new { exception_type = exception.GetType().Name,
        hresult = unchecked((uint)exception.HResult), native_error = (exception as Win32Exception)?.NativeErrorCode };
    internal static void Emit(string kind, object detail)
    {
        lock (Gate)
        {
            string json = JsonSerializer.Serialize(new { schema_version = 1, nonce = Nonce,
                host_pid = Environment.ProcessId, elapsed_ms = Elapsed, kind, detail,
                isolation_acceptance = "not_claimed_host_output_only" });
            if (Encoding.UTF8.GetByteCount(json) + 1 > 65536) throw new InvalidDataException("OUTPUT_LINE_LIMIT");
            output!.WriteLine(json); output.Flush();
        }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        using StreamWriter writer = new(Console.OpenStandardOutput(), new UTF8Encoding(false), 8192, leaveOpen: true) { NewLine = "\n" };
        output = writer;
        bool browser = args.Length == 10 && args[0] == "--browser";
        bool collector = args.Length == 9 && args[0] == "--collector";
        if ((!browser && !collector) || !Regex.IsMatch(args[1], "^[a-f0-9]{32}$")) return 64;
        Nonce = args[1];
        try
        {
            int metadata = browser ? 7 : 6;
            uint holderPid = uint.Parse(args[metadata], System.Globalization.CultureInfo.InvariantCulture);
            ulong creation = ulong.Parse(args[metadata + 1], System.Globalization.CultureInfo.InvariantCulture);
            ulong handle = ulong.Parse(args[metadata + 2], System.Globalization.CultureInfo.InvariantCulture);
            if (holderPid == 0 || creation == 0 || handle == 0 || handle > long.MaxValue) throw new InvalidDataException("PARENT_BOUND_HOLDER_METADATA_REQUIRED");
            // Only SYNCHRONIZE is granted to this original inherited object.
            // PID/creation are parent-bound metadata, never child identity proof.
            ProbeNative.AdoptWaitOnlyHolder(new IntPtr(checked((long)handle)));
            string station = browser ? args[4] : args[2], desktop = browser ? args[5] : args[3];
            string operation = browser ? args[6] : args[4];
            if (operation is not ("copy" or "cut" or "paste")) throw new InvalidDataException("OPERATION_INVALID");
            if (!Regex.IsMatch(station, "^AutumnOS_ClipboardProbe_[a-f0-9]{32}$") ||
                !Regex.IsMatch(desktop, "^[A-Za-z0-9_]{1,96}$")) throw new InvalidDataException("OWNED_OBJECT_NAME_INVALID");
            // Must precede every GUI, OLE, WebView2 and clipboard call. lpDesktop
            // must have been fixed by the audited CreateProcess startup info.
            ProbeNative.RequirePrivateBinding(station, desktop);
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using ProbeForm form = browser
                ? new BrowserChild(args[2], args[3], station, desktop, operation)
                : new ClipboardCollector(station, desktop, operation, args[5]);
            Application.Run(form);
            return form.Succeeded ? 0 : 1;
        }
        catch (Exception exception) { Emit("host_failure", Error(exception)); return 1; }
        finally { ProbeNative.ReleaseHolder(); }
    }
}

internal abstract class ProbeForm : Form
{
    private bool finished;
    private readonly string station, desktop;
    protected readonly System.Windows.Forms.Timer Timer = new() { Interval = 100 };
    internal bool Succeeded { get; private set; }
    protected bool Finished => finished;
    protected ProbeForm(string station, string desktop)
    {
        this.station = station; this.desktop = desktop; Width = 800; Height = 600; ShowInTaskbar = false;
        Text = "AutumnOS · 私有非交互窗口站合成数据验证 · 派蒙";
        Shown += (_, _) => { ProbeNative.RequirePrivateBinding(station, desktop); _ = Task.Run(ReadCommands); Timer.Start(); Start(); };
        FormClosing += (_, args) => { if (!finished) { args.Cancel = true; Failure("native_window_close"); } };
        Timer.Tick += (_, _) => Tick();
    }
    protected void RequireBinding() => ProbeNative.RequirePrivateBinding(station, desktop);
    protected abstract void Start();
    protected abstract void Tick();
    protected abstract void Command(string command, int? index);
    protected abstract void Failure(string reason);
    protected void Finish(bool succeeded)
    { if (finished) return; Succeeded = succeeded; finished = true; Timer.Stop(); Close(); }
    private void Queue(Action action)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)(() => { if (!finished) action(); })); }
        catch (InvalidOperationException) { }
    }
    private void ReadCommands()
    {
        try
        {
            using Stream input = Console.OpenStandardInput(); byte[] bytes = new byte[1024]; int count = 0, commands = 0;
            while (true)
            {
                int value = input.ReadByte();
                if (value < 0) { Queue(() => Failure("stdin_eof_before_completion")); return; }
                if (value == 10)
                {
                    if (++commands > 64) { Queue(() => Failure("command_count_limit")); return; }
                    int length = count > 0 && bytes[count - 1] == 13 ? count - 1 : count;
                    string json = new UTF8Encoding(false, true).GetString(bytes, 0, length); count = 0;
                    Queue(() => ParseCommand(json)); continue;
                }
                if (count == bytes.Length) { Queue(() => Failure("command_line_limit")); return; }
                bytes[count++] = checked((byte)value);
            }
        }
        catch (Exception exception) { Queue(() => { Program.Emit("command_reader_failed", Program.Error(exception)); Failure("command_reader_failed"); }); }
    }
    private void ParseCommand(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 3 });
            JsonElement root = document.RootElement;
            JsonProperty[] properties = root.EnumerateObject().ToArray();
            if (properties.Length is not (3 or 4) || properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
                root.GetProperty("schema_version").GetInt32() != 1 || root.GetProperty("nonce").GetString() != Program.Nonce)
                throw new InvalidDataException("COMMAND_BINDING_INVALID");
            string command = root.GetProperty("command").GetString() ?? "";
            int? index = properties.Length == 4 ? root.GetProperty("sample_index").GetInt32() : null;
            if ((command is "sample" or "sample_paste") != index.HasValue || index is < 0 or > 10)
                throw new InvalidDataException("COMMAND_INDEX_INVALID");
            RequireBinding(); Command(command, index);
        }
        catch (Exception exception) { Program.Emit("command_rejected", Program.Error(exception)); Failure("invalid_command"); }
    }
    protected override void Dispose(bool disposing) { if (disposing) Timer.Dispose(); base.Dispose(disposing); }
}
