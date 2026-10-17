// This Windows inbox .NET Framework bootstrap does not need an adjacent runtime.
// The product remains the existing self-contained .NET 10 / WinUI client.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AutumnOS.SingleFile
{
    internal static class Program
    {
        private const string Marker = "AutumnOS.single-file.layout.v1\n";
        private const string SeedName = ".autumnos-single-file-seed";
        private static readonly string Caption = "Lab Chronicles AutumnOS · 制作人：派蒙";
        private sealed class Item { internal string Path, Hash; internal long Bytes; }

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string data = null;
            try
            {
                if (args.Length != 0) throw new IOException("PORTABLE_ARGUMENT_REJECTED");
                string executable = Assembly.GetExecutingAssembly().Location;
                string entry = Path.GetDirectoryName(executable);
                Plain(executable);
                data = Path.Combine(entry, "AutumnOS_Data");
                EnsureDirectory(data);
                string system = Path.Combine(data, "System");
                EnsureDirectory(system);
                string product = Path.Combine(system, "Product-" + BundleIdentity.PayloadSha256.Substring(0, 20));
                List<Item> inventory = ReadInventory();
                string sid = WindowsIdentity.GetCurrent().User.Value;
                string scope = HashText(sid + "\n" + entry.ToUpperInvariant());
                var security = new MutexSecurity();
                security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User, MutexRights.FullControl, AccessControlType.Allow));
                bool created;
                using (var mutex = new Mutex(false, "Local\\LabChronicles.AutumnOS.Extract." + scope, out created, security))
                {
                    bool held;
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(180)); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new IOException("PORTABLE_PREPARATION_BUSY");
                    try
                    {
                        // This record survives a missing extraction directory. An updated
                        // installation must never silently fall back to the embedded seed.
                        EntryBinding binding = ReadEntryBinding(system);
                        if (!Directory.Exists(product))
                        {
                            if (binding != null && binding.BuildId != BundleIdentity.BuildId) throw new IOException("PORTABLE_UPDATED_RUNTIME_MISSING");
                            using (var progress = new PreparationWindow())
                            {
                                // The main thread retains mutex ownership throughout the
                                // normal UI loop and until its worker has stopped writing.
                                progress.Run(delegate
                                {
                                    progress.Report(0, 0, 1);
                                    Extract(executable, system, product, inventory, progress);
                                    ValidateRuntime(product, inventory, progress);
                                });
                            }
                        }
                        else ValidateRuntime(product, inventory, null);
                        ValidateEntryBinding(product, binding);
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                // Only process-local environment; all .NET native extraction stays in the same Data folder.
                string clr = Path.Combine(system, "CLR");
                EnsureDirectory(clr);
                var launch = new ProcessStartInfo(Path.Combine(product, "AutumnOS.exe"));
                launch.UseShellExecute = false;
                launch.WorkingDirectory = product;
                launch.EnvironmentVariables["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = clr;
                using (var child = Process.Start(launch))
                {
                    if (child == null) throw new IOException("PORTABLE_CLIENT_START_FAILED");
                    Console.Error.WriteLine("AUTUMNOS_SINGLE_FILE role=child pid=" + Process.GetCurrentProcess().Id + " child=" + child.Id + " startTicks=" + child.StartTime.ToUniversalTime().Ticks);
                    child.WaitForExit();
                    return child.ExitCode;
                }
            }
            catch (OperationCanceledException) { return 1; }
            catch (Exception error)
            {
                string code = error is IOException && error.Message.StartsWith("PORTABLE_", StringComparison.Ordinal) ? error.Message : "PORTABLE_START_FAILED";
                // No arbitrary exception/path/account text in a user-facing error or report.
                MessageBox.Show(code + "\n用户数据已保留。请确认 EXE 位于可写目录，且程序文件完整。", Caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 41;
            }
        }

        private static void Extract(string executable, string system, string product, List<Item> inventory, PreparationWindow progress)
        {
            string stage = Path.Combine(system, ".prepare-" + Guid.NewGuid().ToString("N"));
            EnsureDirectory(stage);
            // Failed/cancelled staging is retained under Data; never delete unknown files or saves.
            using (var input = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 64) throw new IOException("PORTABLE_PAYLOAD_INVALID");
                input.Position = input.Length - 64;
                byte[] footer = new byte[64]; ReadExactly(input, footer, 64);
                long offset = BitConverter.ToInt64(footer, 0), length = BitConverter.ToInt64(footer, 8);
                if (Encoding.ASCII.GetString(footer, 48, 16) != "AUTUMNOSBUNDLE01" || offset < 1024 || length <= 0 || offset > input.Length - 64 || length != input.Length - 64 - offset || Hex(footer, 16, 32) != BundleIdentity.PayloadSha256)
                    throw new IOException("PORTABLE_PAYLOAD_INVALID");
                using (var region = new RegionStream(input, offset, length))
                {
                    if (HashStream(region, delegate(long read) { progress.Report(0, read, length); }) != BundleIdentity.PayloadSha256) throw new IOException("PORTABLE_PAYLOAD_HASH_FAILED");
                    region.Position = 0;
                    progress.Report(1, 0, inventory.Count);
                    using (var zip = new ZipArchive(region, ZipArchiveMode.Read, true))
                    {
                        var blobs = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                        foreach (var entry in zip.Entries)
                        {
                            if (!IsHash(entry.FullName) || blobs.ContainsKey(entry.FullName)) throw new IOException("PORTABLE_ARCHIVE_INVALID");
                            blobs.Add(entry.FullName, entry);
                        }
                        var written = new Dictionary<string, string>(StringComparer.Ordinal);
                        int prepared = 0;
                        foreach (Item item in inventory)
                        {
                            string destination = Resolve(stage, item.Path);
                            EnsureDirectory(Path.GetDirectoryName(destination));
                            string first;
                            if (written.TryGetValue(item.Hash, out first))
                            {
                                using (var source = File.OpenRead(first))
                                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                    CopyResource(source, output, progress);
                            }
                            else
                            {
                                ZipArchiveEntry blob;
                                if (!blobs.TryGetValue(item.Hash, out blob) || blob.Length != item.Bytes) throw new IOException("PORTABLE_ARCHIVE_INVALID");
                                using (var source = blob.Open())
                                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                { CopyResource(source, output, progress); output.Flush(true); }
                                if (!Matches(destination, item, progress)) throw new IOException("PORTABLE_FILE_HASH_FAILED");
                                written.Add(item.Hash, destination);
                            }
                            progress.Report(1, ++prepared, inventory.Count);
                        }
                        if (blobs.Count != written.Count) throw new IOException("PORTABLE_UNUSED_ARCHIVE_CONTENT");
                    }
                }
                File.WriteAllText(Path.Combine(stage, "autumn.portable"), Marker, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(stage, SeedName), BundleIdentity.PayloadSha256, new UTF8Encoding(false));
                Plain(stage); Plain(product);
                Directory.Move(stage, product);
            }
        }

        private static void CopyResource(Stream source, Stream destination, PreparationWindow progress)
        {
            var buffer = new byte[131072];
            int count;
            while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
            {
                destination.Write(buffer, 0, count);
                progress.Pump();
            }
        }

        private static string HashStream(Stream source, Action<long> report)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[131072];
                long completed = 0; int count;
                while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                    completed += count;
                    if (report != null) report(completed);
                }
                sha.TransformFinalBlock(buffer, 0, 0);
                return Hex(sha.Hash, 0, 32);
            }
        }

        private static List<Item> ReadInventory()
        {
            var items = new List<Item>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AutumnOS.BundleInventory"))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] fields = line.Split('\t'); long bytes;
                    if (fields.Length != 3 || !IsHash(fields[0]) || !long.TryParse(fields[1], out bytes) || bytes < 0 || bytes > 2147483648L || !seen.Add(fields[2])) throw new IOException("PORTABLE_INVENTORY_INVALID");
                    CheckRelative(fields[2]);
                    items.Add(new Item { Hash = fields[0], Bytes = bytes, Path = fields[2] });
                }
            }
            if (items.Count < 10 || items.Count > 20000) throw new IOException("PORTABLE_INVENTORY_INVALID");
            return items;
        }

        private static void ValidateRuntime(string product, List<Item> inventory, PreparationWindow progress)
        {
            if (progress != null) progress.Report(2, 0, inventory.Count);
            Plain(product);
            string marker = Path.Combine(product, "autumn.portable"), seed = Path.Combine(product, SeedName);
            Plain(marker); Plain(seed);
            if (!File.Exists(marker) || new FileInfo(marker).Length != Encoding.UTF8.GetByteCount(Marker) || File.ReadAllText(marker) != Marker || !File.Exists(seed) || new FileInfo(seed).Length != 64 || File.ReadAllText(seed) != BundleIdentity.PayloadSha256) throw new IOException("PORTABLE_CACHE_IDENTITY_INVALID");
            // Stable bootstrap/updater are never overwritten by a payload. Keep independent verification.
            foreach (var item in inventory)
                if ((item.Path == "AutumnOS.exe" || item.Path == "AutumnOS.Updater.exe") && !Matches(Resolve(product, item.Path), item, progress)) throw new IOException("PORTABLE_STABLE_ENTRY_INVALID");
            string journalPath = Path.Combine(product, ".autumnos-update", "journal.json"); Plain(journalPath);
            if (File.Exists(journalPath))
            {
                if (new FileInfo(journalPath).Length > 8 * 1024 * 1024) throw new IOException("PORTABLE_RECOVERY_RECORD_INVALID");
                Journal journal;
                using (var file = File.OpenRead(journalPath)) journal = (Journal)new DataContractJsonSerializer(typeof(Journal)).ReadObject(file);
                if (string.IsNullOrEmpty(journal.Phase)) throw new IOException("PORTABLE_RECOVERY_RECORD_INVALID");
                if (journal.Phase != "committed" && journal.Phase != "rolledBack" && journal.Phase != "aborted") return;
                // The pinned independent bootstrap revalidates and performs recovery; a partial directory is never launched directly.
            }
            string receiptPath = Path.Combine(product, "autumn.install.json"); Plain(receiptPath);
            if (new FileInfo(receiptPath).Length > 8 * 1024 * 1024) throw new IOException("PORTABLE_RECEIPT_INVALID");
            Receipt receipt;
            using (var stream = File.OpenRead(receiptPath)) receipt = (Receipt)new DataContractJsonSerializer(typeof(Receipt)).ReadObject(stream);
            if (receipt.Schema != 1 || string.IsNullOrEmpty(receipt.BuildId) || receipt.Files == null || receipt.Files.Length < 3 || receipt.Files.Length > 20000) throw new IOException("PORTABLE_RECEIPT_INVALID");
            // A successful signed update is preserved. Never seed the old payload over a different installed build.
            if (receipt.BuildId == BundleIdentity.BuildId)
            {
                int verified = 0;
                foreach (Item item in inventory)
                {
                    if (item.Path != "autumn.install.json" && !Matches(Resolve(product, item.Path), item, progress)) throw new IOException("PORTABLE_CACHE_FILE_CHANGED");
                    if (progress != null) progress.Report(2, ++verified, inventory.Count);
                }
            }
            else
            {
                // Recovery may have temporarily moved files. The pinned independent bootstrap owns its journal.
                string journal = Path.Combine(product, ".autumnos-update", "journal.json"); Plain(journal);
                if (!File.Exists(journal)) throw new IOException("PORTABLE_CHANGED_BUILD_WITHOUT_TRANSACTION");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ReceiptFile file in receipt.Files)
                {
                    if (file == null || !IsHash(file.Hash) || !seen.Add(file.Path)) throw new IOException("PORTABLE_RECEIPT_INVALID");
                    string path = Resolve(product, file.Path);
                    if (!Matches(path, new Item { Path = file.Path, Hash = file.Hash, Bytes = file.Bytes }, progress))
                        throw new IOException("PORTABLE_CURRENT_BUILD_FILE_CHANGED");
                }
            }
        }

        private static EntryBinding ReadEntryBinding(string system)
        {
            string path = Path.Combine(system, "Entry-" + BundleIdentity.PayloadSha256.Substring(0, 20) + ".json"); Plain(path);
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 8192) throw new IOException("PORTABLE_ENTRY_BINDING_INVALID");
            EntryBinding binding;
            using (var input = File.OpenRead(path)) binding = (EntryBinding)new DataContractJsonSerializer(typeof(EntryBinding)).ReadObject(input);
            if (binding == null || binding.Schema != 1 || binding.Seed != BundleIdentity.PayloadSha256 || binding.ProductDirectory != "Product-" + BundleIdentity.PayloadSha256.Substring(0, 20) || string.IsNullOrWhiteSpace(binding.BuildId))
                throw new IOException("PORTABLE_ENTRY_BINDING_INVALID");
            return binding;
        }

        private static void ValidateEntryBinding(string product, EntryBinding binding)
        {
            string journalPath = Path.Combine(product, ".autumnos-update", "journal.json"); Plain(journalPath);
            if (File.Exists(journalPath))
            {
                Journal journal;
                using (var input = File.OpenRead(journalPath)) journal = (Journal)new DataContractJsonSerializer(typeof(Journal)).ReadObject(input);
                if (journal.Phase != "committed" && journal.Phase != "rolledBack" && journal.Phase != "aborted") return;
            }
            Receipt receipt;
            using (var input = File.OpenRead(Path.Combine(product, "autumn.install.json"))) receipt = (Receipt)new DataContractJsonSerializer(typeof(Receipt)).ReadObject(input);
            if (binding == null ? receipt.BuildId != BundleIdentity.BuildId : receipt.BuildId != binding.BuildId)
                throw new IOException("PORTABLE_ENTRY_BINDING_INVALID");
        }

        private static string Resolve(string root, string relative)
        {
            CheckRelative(relative);
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("PORTABLE_PATH_REJECTED");
            Plain(full); return full;
        }
        private static void CheckRelative(string relative)
        {
            if (string.IsNullOrEmpty(relative) || relative.Length > 240 || relative.IndexOfAny(new[] { '\\', ':', '\t', '\r', '\n' }) >= 0 || Path.IsPathRooted(relative)) throw new IOException("PORTABLE_PATH_REJECTED");
            foreach (string segment in relative.Split('/'))
                if (segment.Length == 0 || segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal) || segment.Equals("AutumnOS_Data", StringComparison.OrdinalIgnoreCase)) throw new IOException("PORTABLE_PATH_REJECTED");
        }
        private static bool Matches(string path, Item item, PreparationWindow progress)
        {
            Plain(path);
            if (!File.Exists(path) || new FileInfo(path).Length != item.Bytes) return false;
            using (var file = File.OpenRead(path)) return HashStream(file, progress == null ? (Action<long>)null : delegate(long read) { progress.Pump(); }) == item.Hash;
        }
        private static void EnsureDirectory(string path) { Plain(path); Directory.CreateDirectory(path); Plain(path); }
        private static void Plain(string path)
        {
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("PORTABLE_REDIRECTED_PATH"); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        private static bool IsHash(string text)
        {
            if (text == null || text.Length != 64) return false;
            foreach (char c in text) if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }
        private static string HashText(string text) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)), 0, 32); }
        private static string Hex(byte[] bytes, int offset, int count) { return BitConverter.ToString(bytes, offset, count).Replace("-", "").ToLowerInvariant(); }
        private static void ReadExactly(Stream stream, byte[] buffer, int count)
        { int read = 0; while (read < count) { int n = stream.Read(buffer, read, count - read); if (n == 0) throw new IOException("PORTABLE_PAYLOAD_INVALID"); read += n; } }

        // The WinUI payload is not available yet. This inbox native window shares the
        // product's visual language without loading a browser or an extra runtime.
        // Motion is deliberately limited to measured progress, including when Windows
        // animations are disabled. All information is also exposed as native controls.
        private sealed class PreparationWindow : Form
        {
            private readonly Color ink = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(48, 79, 66);
            private readonly Color muted = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(96, 116, 104);
            private readonly Label brand, eyebrow, title, subtitle, status, detail, footer;
            private readonly Label[] steps = new Label[3];
            private readonly ProgressIndicator indicator;
            private readonly Button cancel;
            private readonly List<Font> ownedFonts = new List<Font>();
            private readonly object progressGate = new object();
            private readonly int uiThreadId = Thread.CurrentThread.ManagedThreadId;
            private readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer();
            private long completed, total = 1;
            private int stage = -1;
            private volatile bool cancelled, workerFinished;
            private bool finishing, running, workerStarted;
            private Thread worker;
            private Exception workerError;

            internal PreparationWindow()
            {
                Text = Caption;
                Name = "AutumnPreparationWindow";
                AccessibleName = "AutumnOS 首次启动准备";
                AutoScaleMode = AutoScaleMode.Dpi;
                AutoScaleDimensions = new SizeF(96, 96);
                BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(244, 240, 231);
                ForeColor = ink;
                StartPosition = FormStartPosition.CenterScreen;
                FormBorderStyle = FormBorderStyle.Sizable;
                MinimumSize = new Size(600, 520);
                var workArea = Screen.PrimaryScreen.WorkingArea;
                ClientSize = new Size(Math.Min(1164, workArea.Width - 60), Math.Min(740, workArea.Height - 80));
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

                brand = MakeLabel("L A B   C H R O N I C L E S", "PreparationBrand", 9, FontStyle.Regular);
                eyebrow = MakeLabel("初次见面 · 正在本机准备", "PreparationEyebrow", 10, FontStyle.Regular);
                title = MakeLabel("为探索，留一片空间。", "PreparationTitle", 30, FontStyle.Regular);
                subtitle = MakeLabel("AutumnOS", "PreparationProduct", 19, FontStyle.Regular);
                status = MakeLabel("正在校验内置资源", "PreparationStatus", 13, FontStyle.Regular);
                detail = MakeLabel("读取实际进度…", "PreparationDetail", 10, FontStyle.Regular);
                footer = MakeLabel("所有资源与存档保存在同级 AutumnOS_Data\r\n制作人：派蒙", "PreparationDataNotice", 9, FontStyle.Regular);
                foreach (var label in new[] { eyebrow, subtitle, detail, footer }) label.ForeColor = muted;
                for (int i = 0; i < steps.Length; i++)
                {
                    steps[i] = MakeLabel("", "PreparationStep" + (i + 1), 10, FontStyle.Regular);
                    steps[i].AccessibleRole = AccessibleRole.StaticText;
                }
                indicator = new ProgressIndicator(ink, SystemInformation.HighContrast ? SystemColors.ControlDark : Color.FromArgb(211, 219, 205));
                indicator.Name = "PreparationProgress";
                indicator.AccessibleName = "当前准备步骤的实际进度";
                Controls.Add(indicator);
                cancel = new Button();
                cancel.Name = "CancelPreparationButton";
                cancel.Text = "取消准备";
                cancel.AccessibleName = "取消准备，保留已有数据";
                cancel.FlatStyle = FlatStyle.Flat;
                cancel.FlatAppearance.BorderColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(192, 205, 188);
                cancel.BackColor = BackColor;
                cancel.ForeColor = ink;
                cancel.Font = NewFont(10, FontStyle.Regular);
                cancel.UseVisualStyleBackColor = false;
                cancel.Click += delegate { Close(); };
                Controls.Add(cancel);
                CancelButton = cancel;
                refreshTimer.Interval = 60;
                refreshTimer.Tick += delegate
                {
                    RenderProgress();
                    if (workerFinished) Finish();
                };
                Arrange();
            }

            private Font NewFont(float size, FontStyle style)
            {
                var font = new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
                ownedFonts.Add(font);
                return font;
            }

            private Label MakeLabel(string text, string name, float size, FontStyle style)
            {
                var label = new Label();
                label.Text = text;
                label.Name = name;
                label.Font = NewFont(size, style);
                label.ForeColor = ink;
                label.BackColor = Color.Transparent;
                label.TextAlign = ContentAlignment.MiddleCenter;
                label.UseMnemonic = false;
                Controls.Add(label);
                return label;
            }

            internal void Report(int currentStage, long current, long maximum)
            {
                Pump();
                lock (progressGate)
                {
                    stage = currentStage;
                    completed = Math.Max(0, current);
                    total = Math.Max(1, maximum);
                }
                // Standalone fixture/manual UI calls stay synchronous. Production
                // workers only publish measured data; they never wait for the UI.
                if (!running && Thread.CurrentThread.ManagedThreadId == uiThreadId) RenderProgress();
            }

            internal void Pump()
            {
                if (cancelled) throw new OperationCanceledException();
            }

            internal void Run(Action prepare)
            {
                if (running || IsDisposed || Thread.CurrentThread.ManagedThreadId != uiThreadId)
                    throw new InvalidOperationException("PORTABLE_PREPARATION_STATE_INVALID");
                running = true;
                worker = new Thread(delegate()
                {
                    try { Pump(); prepare(); Pump(); }
                    catch (Exception error) { workerError = error; }
                    finally { workerFinished = true; }
                });
                worker.IsBackground = true;
                worker.Name = "AutumnOS resource preparation";
                Shown += delegate
                {
                    if (workerStarted) return;
                    workerStarted = true;
                    worker.Start();
                    refreshTimer.Start();
                };
                try { Application.Run(this); }
                finally
                {
                    // Even an unexpected UI-loop exit cannot release the outer
                    // extraction mutex while a worker still owns a writable file.
                    if (!workerFinished) cancelled = true;
                    if (workerStarted) worker.Join();
                    refreshTimer.Stop();
                }
                if (workerError != null) ExceptionDispatchInfo.Capture(workerError).Throw();
                if (cancelled) throw new OperationCanceledException();
            }

            private void RenderProgress()
            {
                int currentStage; long current, maximum;
                lock (progressGate) { currentStage = stage; current = completed; maximum = total; }
                string[] names = { "校验资源", "准备桌面", "核对文件" };
                string[] descriptions = { "正在校验内置资源", "正在展开桌面与运行组件", "正在核对已准备的文件" };
                int active = Math.Max(0, Math.Min(currentStage, 2));
                status.Text = descriptions[active];
                detail.Text = active == 0
                    ? string.Format("已读取 {0:N1} / {1:N1} MB", current / 1048576.0, maximum / 1048576.0)
                    : string.Format("已{0} {1:N0} / {2:N0} 个文件", active == 1 ? "准备" : "核对", current, maximum);
                for (int i = 0; i < steps.Length; i++)
                {
                    steps[i].Text = (i + 1).ToString("00") + "  " + names[i] + (i < active ? " · 完成" : i == active ? " · 进行中" : "");
                    steps[i].ForeColor = i <= active ? ink : muted;
                }
                indicator.SetProgress(Math.Min(1.0, (double)current / maximum));
                if (cancelled)
                {
                    status.Text = "正在取消准备";
                    detail.Text = "正在结束当前文件操作，已有数据将保留。";
                    cancel.Enabled = false;
                }
            }

            internal void Finish()
            {
                if (running && !workerFinished) throw new InvalidOperationException("PORTABLE_PREPARATION_STATE_INVALID");
                finishing = true;
                Close();
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (!finishing) cancelled = true;
                if (running && !workerFinished) { e.Cancel = true; RenderProgress(); }
                base.OnFormClosing(e);
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                if (brand != null) Arrange();
            }

            private void Arrange()
            {
                // Work in logical coordinates so the layout stays usable when the OS
                // scales fonts, the window is maximized, or a short display is used.
                float scale;
                using (var graphics = CreateGraphics()) scale = graphics.DpiY / 96f;
                int width = ClientSize.Width, height = ClientSize.Height;
                int pad = (int)(32 * scale);
                int line = (int)(30 * scale);
                int contentWidth = Math.Min(width - pad * 2, (int)(660 * scale));
                int left = (width - contentWidth) / 2;
                int hero = Math.Max((int)(80 * scale), height / 2 - (int)(138 * scale));
                brand.SetBounds(pad, (int)(25 * scale), width - pad * 2, line);
                eyebrow.SetBounds(left, hero, contentWidth, line);
                title.SetBounds(pad, hero + line + (int)(7 * scale), width - pad * 2, (int)(74 * scale));
                subtitle.SetBounds(left, hero + (int)(113 * scale), contentWidth, (int)(43 * scale));
                int progressTop = Math.Min(hero + (int)(207 * scale), height - (int)(210 * scale));
                for (int i = 0; i < steps.Length; i++)
                    steps[i].SetBounds(left + i * contentWidth / 3, progressTop, contentWidth / 3, line);
                indicator.SetBounds(left, progressTop + (int)(44 * scale), contentWidth, (int)(5 * scale));
                status.SetBounds(left, progressTop + (int)(64 * scale), contentWidth, line);
                detail.SetBounds(left, progressTop + (int)(96 * scale), contentWidth, line);
                footer.SetBounds(pad, height - (int)(67 * scale), width - pad * 2 - (int)(116 * scale), (int)(49 * scale));
                footer.TextAlign = ContentAlignment.MiddleLeft;
                cancel.SetBounds(width - pad - (int)(104 * scale), height - (int)(63 * scale), (int)(104 * scale), (int)(36 * scale));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (SystemInformation.HighContrast) return;
                var graphics = e.Graphics;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int width = ClientSize.Width, height = ClientSize.Height;
                using (var peach = new SolidBrush(Color.FromArgb(237, 219, 199)))
                    graphics.FillEllipse(peach, -160, -188, 320, 366);
                using (var sage = new SolidBrush(Color.FromArgb(224, 230, 213)))
                    graphics.FillEllipse(sage, width - 330, height - 260, 600, 590);
                using (var line = new Pen(Color.FromArgb(221, 224, 212)))
                    graphics.DrawLine(line, 32, height - 84, width - 32, height - 84);
                float cx = width / 2f, cy = Math.Max(78, eyebrow.Top - 33);
                using (var star = new SolidBrush(Color.FromArgb(113, 137, 101)))
                    graphics.FillPolygon(star, new[] { new PointF(cx, cy - 14), new PointF(cx + 5, cy - 5), new PointF(cx + 14, cy), new PointF(cx + 5, cy + 5), new PointF(cx, cy + 14), new PointF(cx - 5, cy + 5), new PointF(cx - 14, cy), new PointF(cx - 5, cy - 5) });
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (!workerFinished) cancelled = true;
                    if (workerStarted && worker.IsAlive) worker.Join();
                    refreshTimer.Dispose();
                }
                base.Dispose(disposing);
                if (disposing) foreach (var font in ownedFonts) font.Dispose();
            }
        }

        private sealed class ProgressIndicator : Control
        {
            private readonly Color ink, track;
            private double fraction;
            internal ProgressIndicator(Color foreground, Color background)
            {
                ink = foreground; track = background;
                AccessibleRole = AccessibleRole.ProgressBar;
                SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            }
            internal void SetProgress(double value) { fraction = value; Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                using (var brush = new SolidBrush(track)) e.Graphics.FillRectangle(brush, ClientRectangle);
                using (var brush = new SolidBrush(ink)) e.Graphics.FillRectangle(brush, 0, 0, (int)(Width * fraction), Height);
            }
            protected override AccessibleObject CreateAccessibilityInstance() { return new ProgressAccessibility(this); }
            private sealed class ProgressAccessibility : ControlAccessibleObject
            {
                private readonly ProgressIndicator owner;
                internal ProgressAccessibility(ProgressIndicator control) : base(control) { owner = control; }
                public override string Value { get { return Math.Round(owner.fraction * 100).ToString() + "%"; } set { } }
                public override AccessibleStates State { get { return base.State | AccessibleStates.ReadOnly; } }
            }
        }

        [DataContract] private sealed class Receipt
        {
            [DataMember(Name = "schemaVersion")] public int Schema = 0;
            [DataMember(Name = "buildId")] public string BuildId = null;
            [DataMember(Name = "files")] public ReceiptFile[] Files = null;
        }
        [DataContract] private sealed class ReceiptFile
        {
            [DataMember(Name = "path")] public string Path = null;
            [DataMember(Name = "bytes")] public long Bytes = 0;
            [DataMember(Name = "sha256")] public string Hash = null;
        }
        [DataContract] private sealed class Journal { [DataMember(Name = "phase")] public string Phase = null; }
        [DataContract] private sealed class EntryBinding
        {
            [DataMember(Name = "schemaVersion")] public int Schema = 0;
            [DataMember(Name = "seedSha256")] public string Seed = null;
            [DataMember(Name = "productDirectory")] public string ProductDirectory = null;
            [DataMember(Name = "buildId")] public string BuildId = null;
        }

        private sealed class RegionStream : Stream
        {
            private readonly Stream inner; private readonly long start, length;
            internal RegionStream(Stream stream, long offset, long size) { inner = stream; start = offset; length = size; Position = 0; }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return length; } }
            public override long Position { get { return inner.Position - start; } set { if (value < 0 || value > length) throw new IOException("PORTABLE_PAYLOAD_INVALID"); inner.Position = start + value; } }
            public override int Read(byte[] buffer, int offset, int count) { return inner.Read(buffer, offset, (int)Math.Min(count, length - Position)); }
            public override long Seek(long offset, SeekOrigin origin) { Position = checked((origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : length) + offset); return Position; }
            public override void Flush() { }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
