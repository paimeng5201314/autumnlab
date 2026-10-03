// This Windows inbox .NET Framework bootstrap does not need an adjacent runtime.
// The product remains the existing self-contained .NET 10 / WinUI client.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
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
                        if (!Directory.Exists(product)) Extract(executable, system, product, inventory);
                        ValidateRuntime(product, inventory);
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

        private static void Extract(string executable, string system, string product, List<Item> inventory)
        {
            string stage = Path.Combine(system, ".prepare-" + Guid.NewGuid().ToString("N"));
            EnsureDirectory(stage);
            // Failed/cancelled staging is retained under Data; never delete unknown files or saves.
            using (var progress = new Form())
            using (var label = new Label())
            using (var bar = new ProgressBar())
            using (var input = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                progress.Text = Caption;
                progress.Width = 440; progress.Height = 150;
                progress.StartPosition = FormStartPosition.CenterScreen;
                progress.FormBorderStyle = FormBorderStyle.FixedDialog;
                progress.MaximizeBox = false; progress.MinimizeBox = false;
                label.SetBounds(20, 15, 390, 42); label.Text = "首次准备运行资源…\n所有资源和存档保存在 AutumnOS_Data 内。";
                bar.SetBounds(20, 62, 390, 20); bar.Maximum = inventory.Count;
                progress.Controls.Add(label); progress.Controls.Add(bar);
                progress.Show(); Application.DoEvents();
                if (input.Length < 64) throw new IOException("PORTABLE_PAYLOAD_INVALID");
                input.Position = input.Length - 64;
                byte[] footer = new byte[64]; ReadExactly(input, footer, 64);
                long offset = BitConverter.ToInt64(footer, 0), length = BitConverter.ToInt64(footer, 8);
                if (Encoding.ASCII.GetString(footer, 48, 16) != "AUTUMNOSBUNDLE01" || offset < 1024 || length <= 0 || offset > input.Length - 64 || length != input.Length - 64 - offset || Hex(footer, 16, 32) != BundleIdentity.PayloadSha256)
                    throw new IOException("PORTABLE_PAYLOAD_INVALID");
                using (var region = new RegionStream(input, offset, length))
                {
                    using (var sha = SHA256.Create()) if (Hex(sha.ComputeHash(region), 0, 32) != BundleIdentity.PayloadSha256) throw new IOException("PORTABLE_PAYLOAD_HASH_FAILED");
                    region.Position = 0;
                    using (var zip = new ZipArchive(region, ZipArchiveMode.Read, true))
                    {
                        var blobs = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
                        foreach (var entry in zip.Entries)
                        {
                            if (!IsHash(entry.FullName) || blobs.ContainsKey(entry.FullName)) throw new IOException("PORTABLE_ARCHIVE_INVALID");
                            blobs.Add(entry.FullName, entry);
                        }
                        var written = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (Item item in inventory)
                        {
                            string destination = Resolve(stage, item.Path);
                            EnsureDirectory(Path.GetDirectoryName(destination));
                            string first;
                            if (written.TryGetValue(item.Hash, out first)) File.Copy(first, destination, false);
                            else
                            {
                                ZipArchiveEntry blob;
                                if (!blobs.TryGetValue(item.Hash, out blob) || blob.Length != item.Bytes) throw new IOException("PORTABLE_ARCHIVE_INVALID");
                                using (var source = blob.Open())
                                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                                { source.CopyTo(output); output.Flush(true); }
                                if (!Matches(destination, item)) throw new IOException("PORTABLE_FILE_HASH_FAILED");
                                written.Add(item.Hash, destination);
                            }
                            bar.Value++;
                            Application.DoEvents();
                            if (progress.IsDisposed || !progress.Visible) throw new OperationCanceledException();
                        }
                        if (blobs.Count != written.Count) throw new IOException("PORTABLE_UNUSED_ARCHIVE_CONTENT");
                    }
                }
                File.WriteAllText(Path.Combine(stage, "autumn.portable"), Marker, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(stage, SeedName), BundleIdentity.PayloadSha256, new UTF8Encoding(false));
                Plain(stage); Plain(product);
                Directory.Move(stage, product);
                progress.Close();
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

        private static void ValidateRuntime(string product, List<Item> inventory)
        {
            Plain(product);
            string marker = Path.Combine(product, "autumn.portable"), seed = Path.Combine(product, SeedName);
            Plain(marker); Plain(seed);
            if (!File.Exists(marker) || new FileInfo(marker).Length != Encoding.UTF8.GetByteCount(Marker) || File.ReadAllText(marker) != Marker || !File.Exists(seed) || new FileInfo(seed).Length != 64 || File.ReadAllText(seed) != BundleIdentity.PayloadSha256) throw new IOException("PORTABLE_CACHE_IDENTITY_INVALID");
            // Stable bootstrap/updater are never overwritten by a payload. Keep independent verification.
            foreach (var item in inventory)
                if ((item.Path == "AutumnOS.exe" || item.Path == "AutumnOS.Updater.exe") && !Matches(Resolve(product, item.Path), item)) throw new IOException("PORTABLE_STABLE_ENTRY_INVALID");
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
                foreach (Item item in inventory) if (item.Path != "autumn.install.json" && !Matches(Resolve(product, item.Path), item)) throw new IOException("PORTABLE_CACHE_FILE_CHANGED");
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
                    if (!Matches(path, new Item { Path = file.Path, Hash = file.Hash, Bytes = file.Bytes }))
                        throw new IOException("PORTABLE_CURRENT_BUILD_FILE_CHANGED");
                }
            }
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
        private static bool Matches(string path, Item item)
        {
            Plain(path);
            if (!File.Exists(path) || new FileInfo(path).Length != item.Bytes) return false;
            using (var file = File.OpenRead(path)) using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(file), 0, 32) == item.Hash;
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
