using AutumnOS.Contracts;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

internal static class PermissionRevisionTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("T03 permission revision: grant revoke regrant is monotonic and scoped to one permission", () => Fixture(service =>
        {
            RuntimeAccountContext account = Account(); RuntimeApplication app = Application();
            Assert(service.GetRevision(account, app, "saves") == 0, "New permission had a nonzero revision.");
            long previous = 0;
            foreach (PermissionDecision decision in new[] { PermissionDecision.Granted, PermissionDecision.Revoked, PermissionDecision.Granted })
            {
                service.SetDecision(account, app, "saves", decision);
                long current = service.GetRevision(account, app, "saves");
                Assert(current > previous && service.Query(account, app, "saves") == decision, "Decision and its monotonic revision were not published together.");
                previous = current;
            }
            long binding = service.GetRevision(account, app);
            service.SetDecision(account, app, "storage", PermissionDecision.Granted);
            Assert(service.GetRevision(account, app, "saves") == previous, "Unrelated permission invalidated saves.");
            Assert(service.GetRevision(account, app) > binding && service.GetRevision(account, app, "storage") == 1, "Binding revision lost its all-permissions meaning.");
            RuntimeAccountContext otherAccount = Account("controlled-account-b");
            RuntimeApplication otherSource = app with { Source = "local-test:other-source" };
            service.SetDecision(otherAccount, app, "saves", PermissionDecision.Granted);
            service.SetDecision(account, otherSource, "saves", PermissionDecision.Granted);
            Assert(service.GetRevision(account, app, "saves") == previous, "Another account or source changed the permission revision.");
        }));
        yield return ("T03 permission revision: committed revision is visible while change delivery is delayed", () => Fixture(service =>
        {
            RuntimeAccountContext account = Account(); RuntimeApplication app = Application();
            using ManualResetEventSlim committed = new(), releaseNotification = new();
            service.Changed += _ => { committed.Set(); if (!releaseNotification.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Notification release was not signaled."); };
            Task mutation = Task.Run(() => service.SetDecision(account, app, "saves", PermissionDecision.Granted));
            try
            {
                Assert(committed.Wait(TimeSpan.FromSeconds(5)), "Grant did not reach the delayed notification.");
                Assert(service.Query(account, app, "saves") == PermissionDecision.Granted && service.GetRevision(account, app, "saves") == 1,
                    "Committed grant or revision depended on notification completion.");
                Assert(!mutation.IsCompleted, "Positive control did not hold the notification.");
            }
            finally { releaseNotification.Set(); mutation.GetAwaiter().GetResult(); }
        }));
        yield return ("T03 permission revision: unrelated permission does not cancel a pending approval", () => Fixture(service =>
        {
            RuntimeAccountContext account = Account(); RuntimeApplication app = Application();
            TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<PermissionDecision> pending = service.RequestAsync(account, app, "saves", true, (_, _) => answer.Task, CancellationToken.None);
            service.SetDecision(account, app, "storage", PermissionDecision.Granted);
            answer.SetResult(true);
            Assert(pending.GetAwaiter().GetResult() == PermissionDecision.Granted && service.GetRevision(account, app, "saves") == 1,
                "Unrelated permission prevented the pending approval.");
        }));
    }

    private static RuntimeAccountContext Account(string key = "controlled-account-a") => new(key, 1, false, () => true, CancellationToken.None);
    private static RuntimeApplication Application() => new(new("cn.labchronicles.permissionrevision", null, null), "local-test:permission-revision", "Permission revision fixture", ["saves", "storage"]);
    private static void Fixture(Action<PermissionService> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "autumnos-permission-revision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(new PermissionService(directory)); }
        finally { Directory.Delete(directory, true); }
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
