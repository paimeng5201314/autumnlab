using System.Text.Json;
using AutumnOS.Identity;
using AutumnOS.NativeIdentity.Sample;

// Real native process, system browser, memory-only independent credentials. No AutumnOS host data or SDK tokens.
if (args.Length == 2 && args[0] == "--self-test") return await ClientFixtures.RunAsync(args[1]);
if (args.Length != 2 || args[0] != "--config")
{
    Console.WriteLine("SAMPLE_CONFIGURATION_REQUIRED · 使用 --config <公开配置文件>；配置和控制台登记见 README。没有启动监听或浏览器。");
    return 2;
}
using CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(3));
ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    ClientConfiguration config = ClientConfiguration.Load(Path.GetFullPath(args[1]));
    Console.WriteLine("Lab Chronicles AutumnOS · 制作人：派蒙");
    Console.WriteLine("独立开发者客户端示例；即将打开系统浏览器。Ctrl+C 取消。认证成功后仅访问本机 127.0.0.1:5197 示例 API。");
    var flow = new NativeOidcFlow(config.Identity, resourceRequest: config.Resource);
    ProtectedIdentitySession session = await flow.SignInAsync(false, false, cancellation.Token);
    cancellation.Token.ThrowIfCancellationRequested();
    Console.WriteLine("OIDC_IDENTITY_VERIFIED · 身份令牌验证成功；尚未宣称 API 授权成功。");
    using LocalResourceClient client = new();
    await client.VerifyAndConfirmAsync(session, cancellation.Token);
    Console.WriteLine("RESOURCE_API_VERIFIED · 服务器验证身份、权限与一次性挑战成功。凭据未落盘，程序结束后需重新认证。");
    return 0;
}
catch (OperationCanceledException) { Console.WriteLine("USER_CANCELLED_OR_TIMEOUT · 监听器已清理；没有保留凭据。"); return 3; }
catch (IdentityFlowException error) { Console.WriteLine(error.Code); return 4; }
catch (Exception error) when (error is not OutOfMemoryException)
{ Console.WriteLine("SAMPLE_REQUEST_FAILED · 检查公开配置、登记和本机示例服务；未输出原始响应或凭据。"); return 5; }
finally { Console.CancelKeyPress -= cancel; }
