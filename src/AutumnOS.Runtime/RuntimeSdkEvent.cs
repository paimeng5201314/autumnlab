namespace AutumnOS.Runtime;

/// <summary>
/// Host-only event captured before invoking subscribers. A native host must retain Revision
/// while queueing and use RuntimeSession.DeliverEvent immediately before posting to the page.
/// This notification is not an authorization to deliver and must not be deserialized from an app.
/// </summary>
public sealed record RuntimeSdkEvent(string Name, object Data, long Revision);
