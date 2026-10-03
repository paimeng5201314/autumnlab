using System.ComponentModel;
using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Packages;
using AutumnOS.Store;

namespace AutumnOS.Developer.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h") { Help(); return 0; }
        try
        {
            if (args.Length is < 1 or > 24 || args.Any(a => a.Length > 4096 || a.Any(char.IsControl))) throw new CliException("CLI_ARGUMENTS_INVALID");
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                switch (args[0])
                {
                    case "create":
                        Create(args, cancellation.Token);
                        break;
                    case "validate":
                        if (args.Length != 2) throw new CliException("CLI_ARGUMENTS_INVALID");
                        using (DeveloperToolsService tools = Enabled())
                        {
                            PackageInspection result = tools.ValidateProject(Absolute(args[1]), cancellation.Token);
                            Print(new { status = "passed", command = "validate", appId = result.Manifest.AppId, version = result.Manifest.Version,
                                fileCount = result.FileCount, bytes = result.ExpandedBytes, outputCreated = false });
                        }
                        break;
                    case "preview":
                    case "debug":
                        HostCommand(args, cancellation.Token);
                        break;
                    case "pack":
                        if (args.Length != 3) throw new CliException("CLI_ARGUMENTS_INVALID");
                        using (DeveloperToolsService tools = new())
                        {
                            // Explicit invocation activates only this tool process; host developer-mode configuration is not changed.
                            tools.SetEnabled(true);
                            DeveloperBuild built = tools.BuildProject(Absolute(args[1]), Absolute(args[2]), cancellation.Token);
                            Print(new { status = "passed", command = "pack", packagePath = built.PackagePath, appId = built.Inspection.Manifest.AppId,
                                version = built.Inspection.Manifest.Version, sha256 = built.Inspection.Sha256, bytes = new FileInfo(built.PackagePath).Length });
                        }
                        break;
                    case "generate-release":
                        Generate(args, cancellation.Token);
                        break;
                    case "validate-publication":
                        if (args.Length != 4) throw new CliException("CLI_ARGUMENTS_INVALID");
                        Validate(args[1], args[2], args[3], cancellation.Token);
                        break;
                    default: throw new CliException("CLI_COMMAND_UNSUPPORTED");
                }
                return 0;
            }
            finally { Console.CancelKeyPress -= handler; }
        }
        catch (Exception error) when (error is PackageException or CatalogException or CliException or IOException or UnauthorizedAccessException or OperationCanceledException or ArgumentException or Win32Exception or InvalidOperationException)
        {
            string code = error switch
            {
                PackageException package => package.Code, CatalogException catalog => catalog.Code, CliException cli => cli.Code,
                OperationCanceledException => "USER_CANCELLED", UnauthorizedAccessException => "CLI_ACCESS_DENIED",
                IOException => "CLI_IO_ERROR", Win32Exception or InvalidOperationException => "DEVELOPER_HOST_UNAVAILABLE", _ => "CLI_ARGUMENTS_INVALID"
            };
            Print(new { status = "failed", code }); return 2;
        }
    }

    private static DeveloperToolsService Enabled() { DeveloperToolsService tools = new(); tools.SetEnabled(true); return tools; }
    private static void Create(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length is not (7 or 9)) throw new CliException("CLI_ARGUMENTS_INVALID");
        Dictionary<string, string> options = Options(args, 3, ["--app-id", "--name", "--resources"]);
        if (!options.TryGetValue("--app-id", out string? appId) || !options.TryGetValue("--name", out string? name)) throw new CliException("CLI_ARGUMENTS_INVALID");
        string resources = options.TryGetValue("--resources", out string? path) ? Absolute(path) : AppContext.BaseDirectory;
        using DeveloperToolsService tools = Enabled();
        DeveloperProject result = tools.CreateProject(Path.Combine(resources, "Templates"), Path.Combine(resources, "SDK", "autumn-sdk.js"),
            args[1], Absolute(args[2]), appId, name, cancellationToken);
        Print(new { status = "passed", command = "create", projectDirectory = result.ProjectDirectory, template = result.Template,
            appId = result.Inspection.Manifest.AppId, version = result.Inspection.Manifest.Version, fileCount = result.Inspection.FileCount });
    }
    private static void HostCommand(string[] args, CancellationToken cancellationToken)
    {
        bool preview = args[0] == "preview";
        if (args.Length != (preview ? 4 : 5) || args[preview ? 2 : 3] != "--host") throw new CliException("CLI_ARGUMENTS_INVALID");
        string hostRoot = Absolute(args[^1]);
        DeveloperPreviewRequest request;
        if (preview)
        {
            string packagePath = Absolute(args[1]);
            using DeveloperToolsService tools = Enabled();
            PackageInspection inspection = tools.InspectPackage(packagePath, cancellationToken);
            request = new(1, "preview", packagePath, inspection.Sha256);
        }
        else request = new(1, args[2], SessionId: args[1]);
        DeveloperPreviewResponse response = DeveloperPreviewProtocol.SendAsync(hostRoot, request, cancellationToken).GetAwaiter().GetResult();
        if (!response.Ok) throw new PackageException(response.Code);
        Print(new { status = "passed", command = args[0], action = request.Command, response.Code,
            response.SessionId, response.AppId, response.State, response.Trace, response.Simulation, host = "native-internal-preview", arbitraryCommands = false });
    }
    private static Dictionary<string, string> Options(string[] args, int first, string[] allowed)
    {
        if ((args.Length - first) % 2 != 0) throw new CliException("CLI_ARGUMENTS_INVALID");
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        for (int index = first; index < args.Length; index += 2)
            if (!allowed.Contains(args[index], StringComparer.Ordinal) || !options.TryAdd(args[index], args[index + 1])) throw new CliException("CLI_ARGUMENTS_INVALID");
        return options;
    }

    private static void Generate(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length < 7 || (args.Length - 3) % 2 != 0) throw new CliException("CLI_ARGUMENTS_INVALID");
        string package = Absolute(args[1]), output = Absolute(args[2]);
        Dictionary<string, string> options = Options(args, 3, ["--developer", "--description", "--category", "--offline", "--min-host", "--min-sdk"]);
        if (!options.TryGetValue("--developer", out var developer) || !options.TryGetValue("--description", out var description)) throw new CliException("CLI_ARGUMENTS_INVALID");
        string offline = options.GetValueOrDefault("--offline", "false");
        if (offline is not ("true" or "false")) throw new CliException("CLI_ARGUMENTS_INVALID");
        using DeveloperToolsService tools = Enabled();
        DeveloperPublication result = new DeveloperPublicationService(tools).Generate(package, output,
            new(developer, description, options.GetValueOrDefault("--category", "sq"), offline == "true",
                options.GetValueOrDefault("--min-host", "0.3.0"), options.GetValueOrDefault("--min-sdk", "0.3.0")), cancellationToken);
        Print(new { status = "passed", command = "generate-release", result.OutputDirectory, result.AppId,
            result.Version, result.Channel, result.PackagePath, result.StorePath, result.ReleasePath,
            result.Bytes, result.Sha256, result.ReleaseMetadataSha256, result.StoreContentSha256, uploaded = false });
    }

    private static void Validate(string storeArgument, string releaseArgument, string packageArgument, CancellationToken cancellationToken)
    {
        using DeveloperToolsService tools = Enabled();
        DeveloperPublicationCheck result = new DeveloperPublicationService(tools).Validate(Absolute(storeArgument), Absolute(releaseArgument), Absolute(packageArgument), cancellationToken);
        Print(new { status = "passed", command = "validate-publication", result.AppId, result.Version,
            result.Channel, result.Sha256, result.Bytes,
            publishing = "not_performed", liveGitHubRegistration = "not_verified" });
    }

    private static string Absolute(string path) => Path.GetFullPath(path);
    private static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
    private static void Help()
    {
        Console.WriteLine($"{BrandInfo.DisplayName} · {BrandInfo.ProducerCredit}");
        Console.WriteLine("Local tools only. No upload, Git mutation or arbitrary command execution.");
        Console.WriteLine("create <hello-app|identity-app|save-game|desktop-extension> <new-directory> --app-id <id> --name <name> [--resources <Developer-directory>]");
        Console.WriteLine("validate <project-directory>");
        Console.WriteLine("pack <project-directory> <output-directory>");
        Console.WriteLine("preview <package.autumn> --host <AutumnOS-directory>  (running native host, developer mode and per-package confirmation required)");
        Console.WriteLine("debug <preview-session-id> <status|trace|clear-trace|foreground|background|close|deny-permissions|restore-permissions|account-a|account-b|account-guest|offline|online|reset-simulation> --host <AutumnOS-directory>");
        Console.WriteLine("Simulation affects only the confirmed preview. Account changes require native confirmation and return a NEW session ID. No real login tokens, account changes or persistent grant writes.");
        Console.WriteLine("generate-release <package.autumn> <new-output-directory> --developer <name> --description <text> [--category sq|pm|unclassified] [--offline true|false] [--min-host 0.3.0] [--min-sdk 0.3.0]");
        Console.WriteLine("validate-publication <autumn.store.json> <autumn.release.json> <package.autumn>");
        Console.WriteLine("Exit 0: checked/generated. Exit 2: failed. Ctrl+C cancels before the next safe checkpoint.");
    }
    private sealed class CliException(string code) : Exception(code) { internal string Code { get; } = code; }
}
