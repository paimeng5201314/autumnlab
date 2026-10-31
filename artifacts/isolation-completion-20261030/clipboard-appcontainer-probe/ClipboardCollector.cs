namespace AutumnOS.ClipboardAppContainerProbe;

internal sealed class ClipboardCollector : ProbeForm
{
    private readonly string operation, side, marker;
    private readonly HashSet<int> indexes = [];
    private string? baseline;
    private uint sequenceBefore;
    private bool failed, baselineChecked;
    private string stage = "collector_start";
    internal ClipboardCollector(string station, string desktop, string operation, string side) : base(station, desktop)
    {
        if (side is not ("A" or "B")) throw new InvalidDataException("SIDE_INVALID");
        this.operation = operation; this.side = side; marker = "owned-page-" + Program.Nonce + "-" + operation;
    }
    protected override void Start() => Program.Emit("collector_ready", new { operation, side, station_verified = true,
        desktop_verified = true, noninteractive = true, clipboard_access = "not_yet_attempted" });
    protected override void Tick()
    { if (Program.Elapsed >= 150_000) Failure("collector_observation_budget"); }
    protected override void Command(string command, int? index)
    {
        stage = command;
        if (command == "seed" && baseline is null && index is null)
        {
            // The native-only value is never sent to the browser or page.
            baseline = "owned-native-baseline-" + Guid.NewGuid().ToString("N");
            RequireBinding(); ProbeNative.Seed(Handle, baseline);
            string? read = ProbeNative.Read(Handle); sequenceBefore = ProbeNative.GetClipboardSequenceNumber();
            bool matches = read == baseline && sequenceBefore != 0;
            failed |= !matches;
            Program.Emit("seed_complete", new { operation, side, baseline_hash = Program.Hash(baseline),
                sequence_before = sequenceBefore, seed_readback_matches = matches,
                desktop_window_pids = ProbeNative.DesktopWindowPids() });
        }
        else if (command == "sample" && baseline is not null && index is >= 0 and <= 10 && indexes.Add(index.Value))
        {
            RequireBinding(); Snapshot value = SnapshotNow();
            bool expected = index == 0 || side == "B" || operation == "paste"
                ? value.Sequence == sequenceBefore && value.Value == baseline && value.Stable
                : value.Sequence != sequenceBefore && value.Value == marker && value.Stable;
            if (index == 0) baselineChecked = expected;
            failed |= !expected;
            Program.Emit("collector_sample", new { sample_index = index, sequence = value.Sequence,
                value_sha256 = value.Value is null ? null : Program.Hash(value.Value), value_empty = string.IsNullOrEmpty(value.Value),
                baseline_matches = value.Value == baseline, expected, read_stable = value.Stable,
                desktop_window_pids = ProbeNative.DesktopWindowPids() });
        }
        else if (command == "final" && baseline is not null && index is null)
        {
            RequireBinding(); Snapshot value = SnapshotNow();
            bool unchanged = value.Stable && value.Sequence == sequenceBefore && value.Value == baseline;
            bool writeMarker = value.Stable && value.Sequence != sequenceBefore && value.Value == marker;
            bool expected = side == "B" || operation == "paste" ? unchanged : writeMarker;
            int count = indexes.Count(item => item is >= 1 and <= 10);
            bool passed = !failed && baselineChecked && count == 10 && expected;
            Program.Emit("collector_final", new { passed, side, operation, sequence_before = sequenceBefore,
                sequence_after = value.Sequence, baseline_hash = Program.Hash(baseline),
                final_value_sha256 = value.Value is null ? null : Program.Hash(value.Value), unchanged,
                write_marker_matches = writeMarker, sample_count = count, all_samples_expected = !failed,
                browser_exit_proof = "parent_required", read_stable = value.Stable });
            Finish(passed);
        }
        else if (command == "close") Failure("parent_aborted_collector");
        else throw new InvalidDataException("COLLECTOR_COMMAND_STATE_INVALID");
    }
    private Snapshot SnapshotNow()
    {
        uint before = ProbeNative.GetClipboardSequenceNumber();
        string? value = ProbeNative.Read(Handle);
        uint after = ProbeNative.GetClipboardSequenceNumber();
        return new Snapshot(value, after, before != 0 && before == after);
    }
    protected override void Failure(string reason)
    {
        if (Finished) return;
        failed = true;
        Program.Emit("collector_failed", new { reason, stage, clipboard_rewritten_after_seed = false,
            browser_exit_proof = "parent_required_not_assumed", sample_count = indexes.Count });
        Finish(false);
    }
    private sealed record Snapshot(string? Value, uint Sequence, bool Stable);
}
