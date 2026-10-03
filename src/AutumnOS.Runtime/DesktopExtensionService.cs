using System.Text.Json;
using System.Text.RegularExpressions;
using AutumnOS.Contracts;

namespace AutumnOS.Runtime;

public sealed record DesktopShortcut(string Id, string Title, string Action);
public sealed record DesktopWidget(string Id, string Title);
public sealed record DesktopDeclarations(IReadOnlyList<DesktopShortcut> Shortcuts, IReadOnlyList<string> Links,
    IReadOnlyList<DesktopWidget> Widgets);
public sealed record AppearanceSnapshot(string Theme, string Language, double Scale, bool ReduceMotion);
public sealed record DesktopNotification(Guid InstanceId, string AppId, string DisplayName, string Id,
    string Title, string Body, string? Action, DateTimeOffset CreatedUtc);
public sealed record DesktopWidgetValue(Guid InstanceId, string AppId, string Id, string Title, IReadOnlyList<string> Lines);
public sealed record DesktopAppState(Guid InstanceId, string AppId, string DisplayName, int Badge,
    IReadOnlyList<DesktopShortcut> Shortcuts, IReadOnlyList<DesktopWidgetValue> Widgets);

/// <summary>Restricted in-shell desktop extensions. No Windows toast/URI registration or command execution.</summary>
public sealed class DesktopExtensionService
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, Registration> registrations = new();
    private readonly List<DesktopNotification> notifications = [];
    private readonly HashSet<string> muted = new(StringComparer.Ordinal);
    private readonly string settingsPath;
    private readonly TimeProvider clock;
    private readonly Func<IDisposable>? enterCritical;
    private AppearanceSnapshot appearance = new("system", "zh-CN", 1, false);
    public event Action? Changed;
    public event Action<Guid, string, object>? Event;
    public event Action<Guid, string, IReadOnlyDictionary<string, string>>? ActionRequested;

    public DesktopExtensionService(string configurationDirectory, TimeProvider? timeProvider = null, Func<IDisposable>? enterCritical = null)
    {
        this.enterCritical = enterCritical;
        if (!Path.IsPathFullyQualified(configurationDirectory)) throw new ArgumentException("An absolute configuration directory is required.", nameof(configurationDirectory));
        settingsPath = Path.Combine(configurationDirectory, "desktop-notification-settings.v1.json");
        clock = timeProvider ?? TimeProvider.System;
        PermissionService.SafePath(settingsPath, false);
        if (!File.Exists(settingsPath)) return;
        if (new FileInfo(settingsPath).Length > 128 * 1024) throw new RuntimeCapabilityException("SETTINGS_CORRUPT");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
            var root = document.RootElement;
            if (!RuntimeSession.ExactProperties(root, "schemaVersion", "muted") || root.GetProperty("schemaVersion").GetInt32() != 1
                || root.GetProperty("muted").ValueKind != JsonValueKind.Array || root.GetProperty("muted").GetArrayLength() > 1024)
                throw new RuntimeCapabilityException("SETTINGS_CORRUPT");
            foreach (var entry in root.GetProperty("muted").EnumerateArray())
            {
                string? value = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
                if (value is null || value.Length != 64 || !value.All(char.IsAsciiHexDigit) || !muted.Add(value))
                    throw new RuntimeCapabilityException("SETTINGS_CORRUPT");
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        { throw new RuntimeCapabilityException("SETTINGS_CORRUPT"); }
    }

    public void Register(Guid instanceId, RuntimeAccountContext account, RuntimeApplication application,
        IPermissionService permissions, DesktopDeclarations declarations)
    {
        account.EnsureCurrent();
        ValidateDeclarations(declarations);
        lock (gate)
        {
            if (registrations.Count >= 32 || !registrations.TryAdd(instanceId,
                new(account, application, permissions, declarations))) throw new RuntimeCapabilityException("REQUEST_BUSY");
        }
    }

    public void Unregister(Guid instanceId)
    {
        lock (gate)
        {
            registrations.Remove(instanceId);
            notifications.RemoveAll(item => item.InstanceId == instanceId);
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<DesktopNotification> Notifications
    {
        get { lock (gate) return notifications.Where(item => IsCurrent(item.InstanceId)).ToArray(); }
    }

    public IReadOnlyList<DesktopAppState> Apps
    {
        get
        {
            lock (gate) return registrations.Where(pair => IsCurrent(pair.Key)).Select(pair =>
                new DesktopAppState(pair.Key, pair.Value.Application.Identity.AppId, pair.Value.Application.DisplayName,
                    pair.Value.Badge, pair.Value.ActiveShortcuts.ToArray(), pair.Value.WidgetValues.Values.ToArray())).ToArray();
        }
    }

    public AppearanceSnapshot Appearance { get { lock (gate) return appearance; } }
    public void SetAppearance(AppearanceSnapshot value)
    {
        if (value.Theme is not ("light" or "dark" or "system") || value.Language.Length is < 2 or > 32
            || !Regex.IsMatch(value.Language, "^[a-zA-Z]{2,8}(?:-[a-zA-Z0-9]{1,8})*$", RegexOptions.CultureInvariant)
            || !double.IsFinite(value.Scale) || value.Scale is < 0.5 or > 8) throw new ArgumentException("Invalid appearance snapshot.", nameof(value));
        Guid[] targets;
        lock (gate)
        {
            if (appearance == value) return;
            appearance = value;
            targets = registrations.Where(pair => IsCurrent(pair.Key)).Select(pair => pair.Key).ToArray();
        }
        foreach (var id in targets) Emit(id, "appearance.changed", value);
        Changed?.Invoke();
    }

    public bool IsMuted(RuntimeAccountContext account, RuntimeApplication app)
    {
        account.EnsureCurrent();
        lock (gate) return muted.Contains(MuteKey(account, app));
    }

    public void SetMuted(RuntimeAccountContext account, RuntimeApplication app, bool value)
    {
        using var critical = enterCritical?.Invoke();
        account.EnsureCurrent();
        lock (gate)
        {
            using var lease = account.EnterCommitLease?.Invoke();
            account.EnsureCurrent();
            var copy = new HashSet<string>(muted, StringComparer.Ordinal);
            if (value) copy.Add(MuteKey(account, app)); else copy.Remove(MuteKey(account, app));
            if (copy.Count > 1024) throw new RuntimeCapabilityException("QUOTA_EXCEEDED");
            PermissionService.SafePath(settingsPath, true);
            string temp = settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] content = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, muted = copy.Order(StringComparer.Ordinal).ToArray() });
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(content); stream.Flush(true); }
                account.EnsureCurrent();
                PermissionService.SafePath(settingsPath, false);
                if (File.Exists(settingsPath)) File.Replace(temp, settingsPath, null); else File.Move(temp, settingsPath, false);
                muted.Clear(); muted.UnionWith(copy);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            if (value)
                foreach (var pair in registrations.Where(pair => pair.Value.Account.AccountKey == account.AccountKey && pair.Value.Application.BindingKey == app.BindingKey))
                { pair.Value.Badge = 0; notifications.RemoveAll(item => item.InstanceId == pair.Key); }
        }
        Changed?.Invoke();
    }

    public void PermissionChanged(Guid instanceId, PermissionChange change)
    {
        lock (gate)
        {
            if (!registrations.TryGetValue(instanceId, out var registration) || change.AccountKey != registration.Account.AccountKey
                || change.BindingKey != registration.Application.BindingKey) return;
            if (change.Decision != PermissionDecision.Granted)
            {
                if (change.Permission == "notifications")
                { registration.Badge = 0; notifications.RemoveAll(item => item.InstanceId == instanceId); }
                if (change.Permission == "shortcuts") registration.ActiveShortcuts.Clear();
                if (change.Permission == "widgets") registration.WidgetValues.Clear();
            }
        }
        Changed?.Invoke();
    }

    public object Handle(Guid instanceId, string method, JsonElement parameters)
    {
        object result;
        bool changed = false;
        string? dispatch = null;
        IReadOnlyDictionary<string, string>? arguments = null;
        lock (gate)
        {
            var registration = Get(instanceId);
            switch (method)
            {
                case "appearance.get":
                    Exact(parameters);
                    return appearance;
                case "notifications.show":
                    registration.Permissions.Demand(registration.Account, registration.Application, "notifications");
                    Exact(parameters, "id", "title", "body", "action");
                    string id = Text(parameters, "id", 64, true), title = Text(parameters, "title", 100), body = Text(parameters, "body", 500);
                    string? action = parameters.GetProperty("action").ValueKind == JsonValueKind.Null ? null : Text(parameters, "action", 64, true);
                    if (action is not null && !registration.Declarations.Links.Contains(action, StringComparer.Ordinal)) throw new RuntimeCapabilityException("ACTION_NOT_DECLARED");
                    if (muted.Contains(MuteKey(registration.Account, registration.Application))) return new { shown = false, reason = "muted" };
                    PruneNotifications(registration);
                    if (registration.NotificationIds.ContainsKey(id) || notifications.Any(item => item.InstanceId == instanceId && item.Id == id))
                        return new { shown = false, reason = "duplicate" };
                    if (registration.NotificationIds.Count >= 256 || notifications.Count >= 100 || notifications.Count(item => item.InstanceId == instanceId) >= 20)
                        throw new RuntimeCapabilityException("QUOTA_EXCEEDED");
                    registration.NotificationIds.Add(id, clock.GetTimestamp());
                    notifications.Add(new(instanceId, registration.Application.Identity.AppId, registration.Application.DisplayName, id, title, body, action, clock.GetUtcNow()));
                    result = new { shown = true, id }; changed = true;
                    break;
                case "notifications.setBadge":
                    registration.Permissions.Demand(registration.Account, registration.Application, "notifications");
                    Exact(parameters, "count");
                    if (!parameters.GetProperty("count").TryGetInt32(out int badge) || badge is < 0 or > 999) throw new RuntimeCapabilityException("INVALID_REQUEST");
                    registration.Badge = muted.Contains(MuteKey(registration.Account, registration.Application)) ? 0 : badge;
                    result = new { count = registration.Badge }; changed = true;
                    break;
                case "shortcuts.register":
                    registration.Permissions.Demand(registration.Account, registration.Application, "shortcuts");
                    Exact(parameters, "ids");
                    if (parameters.GetProperty("ids").ValueKind != JsonValueKind.Array || parameters.GetProperty("ids").GetArrayLength() > 4) throw new RuntimeCapabilityException("INVALID_REQUEST");
                    var ids = parameters.GetProperty("ids").EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : "").ToArray();
                    if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new RuntimeCapabilityException("INVALID_REQUEST");
                    var selected = ids.Select(selectedId => registration.Declarations.Shortcuts.SingleOrDefault(item => item.Id == selectedId)
                        ?? throw new RuntimeCapabilityException("ACTION_NOT_DECLARED")).ToArray();
                    registration.ActiveShortcuts.Clear(); registration.ActiveShortcuts.AddRange(selected);
                    result = new { shortcuts = selected }; changed = true;
                    break;
                case "widgets.update":
                    registration.Permissions.Demand(registration.Account, registration.Application, "widgets");
                    Exact(parameters, "id", "lines");
                    string widgetId = Text(parameters, "id", 64, true);
                    var declaration = registration.Declarations.Widgets.SingleOrDefault(item => item.Id == widgetId) ?? throw new RuntimeCapabilityException("ACTION_NOT_DECLARED");
                    if (parameters.GetProperty("lines").ValueKind != JsonValueKind.Array || parameters.GetProperty("lines").GetArrayLength() > 4) throw new RuntimeCapabilityException("INVALID_REQUEST");
                    var lines = parameters.GetProperty("lines").EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new RuntimeCapabilityException("INVALID_REQUEST")).ToArray();
                    if (lines.Any(line => line.Length > 160 || line.Any(char.IsControl))) throw new RuntimeCapabilityException("INVALID_REQUEST");
                    registration.WidgetValues[widgetId] = new(instanceId, registration.Application.Identity.AppId, widgetId, declaration.Title, lines);
                    result = new { updated = true, id = widgetId }; changed = true;
                    break;
                case "links.openInternal":
                    registration.Permissions.Demand(registration.Account, registration.Application, "links");
                    Exact(parameters, "action", "arguments");
                    dispatch = Text(parameters, "action", 64, true);
                    if (!registration.Declarations.Links.Contains(dispatch, StringComparer.Ordinal)) throw new RuntimeCapabilityException("ACTION_NOT_DECLARED");
                    arguments = Arguments(parameters.GetProperty("arguments"));
                    result = new { delivered = true, action = dispatch };
                    break;
                default: throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
            }
        }
        if (dispatch is not null)
        {
            Emit(instanceId, "links.opened", new { action = dispatch, arguments });
            ActionRequested?.Invoke(instanceId, dispatch, arguments!);
        }
        if (changed) Changed?.Invoke();
        return result;
    }

    /// <summary>Called only by actual host controls, never from application-supplied instance IDs.</summary>
    public bool ActivateNotification(Guid instanceId, string notificationId)
    {
        string? action;
        lock (gate)
        {
            var registration = Get(instanceId);
            registration.Permissions.Demand(registration.Account, registration.Application, "notifications");
            if (muted.Contains(MuteKey(registration.Account, registration.Application))) return false;
            var notification = notifications.SingleOrDefault(item => item.InstanceId == instanceId && item.Id == notificationId);
            if (notification is null || notification.Action is null) return false;
            action = notification.Action;
            if (!registration.Declarations.Links.Contains(action, StringComparer.Ordinal)) return false;
            notifications.Remove(notification);
        }
        var arguments = new Dictionary<string, string>();
        Emit(instanceId, "links.opened", new { action, arguments });
        ActionRequested?.Invoke(instanceId, action, arguments);
        Changed?.Invoke();
        return true;
    }

    public bool ActivateShortcut(Guid instanceId, string shortcutId)
    {
        string action;
        lock (gate)
        {
            var registration = Get(instanceId);
            registration.Permissions.Demand(registration.Account, registration.Application, "shortcuts");
            var shortcut = registration.ActiveShortcuts.SingleOrDefault(item => item.Id == shortcutId);
            if (shortcut is null) return false;
            action = shortcut.Action;
        }
        Emit(instanceId, "shortcuts.invoked", new { id = shortcutId, action });
        ActionRequested?.Invoke(instanceId, action, new Dictionary<string, string>());
        return true;
    }

    public void DismissNotification(Guid instanceId, string notificationId)
    { lock (gate) notifications.RemoveAll(item => item.InstanceId == instanceId && item.Id == notificationId); Changed?.Invoke(); }

    private Registration Get(Guid instanceId)
    {
        if (!registrations.TryGetValue(instanceId, out var registration)) throw new RuntimeCapabilityException("SESSION_EXPIRED");
        registration.Account.EnsureCurrent();
        return registration;
    }
    private bool IsCurrent(Guid id) => registrations.TryGetValue(id, out var registration)
        && !registration.Account.Invalidated.IsCancellationRequested && registration.Account.IsCurrent();
    private void Emit(Guid id, string name, object value)
    { lock (gate) { if (!IsCurrent(id)) return; Event?.Invoke(id, name, value); } }
    private void PruneNotifications(Registration registration)
    {
        long now = clock.GetTimestamp();
        foreach (var id in registration.NotificationIds.Where(pair => clock.GetElapsedTime(pair.Value, now) >= TimeSpan.FromMinutes(10)).Select(pair => pair.Key).ToArray())
            registration.NotificationIds.Remove(id);
    }
    private static string MuteKey(RuntimeAccountContext account, RuntimeApplication app) => RuntimeApplication.Hash(account.AccountKey + "\0" + app.BindingKey);
    private static void Exact(JsonElement value, params string[] fields)
    { if (!RuntimeSession.ExactProperties(value, fields)) throw new RuntimeCapabilityException("INVALID_REQUEST"); }
    private static string Text(JsonElement value, string field, int maximum, bool identifier = false)
    {
        var item = value.GetProperty(field);
        if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || text.Length is < 1 || text.Length > maximum || text.Any(char.IsControl)
            || (identifier && !ValidId(text))) throw new RuntimeCapabilityException("INVALID_REQUEST");
        return text;
    }
    private static bool ValidId(string value) => Regex.IsMatch(value, "^[a-zA-Z0-9][a-zA-Z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    private static bool DeclaredId(string value) => Regex.IsMatch(value, "^[a-z][a-z0-9_-]{0,39}$", RegexOptions.CultureInvariant);
    private static IReadOnlyDictionary<string, string> Arguments(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() > 8 || RuntimeSession.HasDuplicateProperties(value)) throw new RuntimeCapabilityException("INVALID_REQUEST");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!ValidId(property.Name) || property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { } text
                || text.Length > 256 || text.Any(char.IsControl)) throw new RuntimeCapabilityException("INVALID_REQUEST");
            result.Add(property.Name, text);
        }
        return result;
    }
    private static void ValidateDeclarations(DesktopDeclarations declarations)
    {
        if (declarations.Shortcuts.Count > 4 || declarations.Links.Count > 4 || declarations.Widgets.Count > 4
            || declarations.Shortcuts.Any(item => !DeclaredId(item.Id) || !DeclaredId(item.Action) || !declarations.Links.Contains(item.Action, StringComparer.Ordinal)
                || item.Title.Length is < 1 or > 64 || item.Title.Any(char.IsControl))
            || declarations.Widgets.Any(item => !DeclaredId(item.Id) || item.Title.Length is < 1 or > 64 || item.Title.Any(char.IsControl))
            || declarations.Links.Any(item => !DeclaredId(item))
            || declarations.Shortcuts.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != declarations.Shortcuts.Count
            || declarations.Links.Distinct(StringComparer.Ordinal).Count() != declarations.Links.Count
            || declarations.Widgets.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != declarations.Widgets.Count)
            throw new RuntimeCapabilityException("INVALID_REQUEST");
    }
    private sealed class Registration(RuntimeAccountContext account, RuntimeApplication application, IPermissionService permissions, DesktopDeclarations declarations)
    {
        internal RuntimeAccountContext Account { get; } = account;
        internal RuntimeApplication Application { get; } = application;
        internal IPermissionService Permissions { get; } = permissions;
        internal DesktopDeclarations Declarations { get; } = declarations;
        internal int Badge { get; set; }
        internal List<DesktopShortcut> ActiveShortcuts { get; } = [];
        internal Dictionary<string, DesktopWidgetValue> WidgetValues { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, long> NotificationIds { get; } = new(StringComparer.Ordinal);
    }
}
