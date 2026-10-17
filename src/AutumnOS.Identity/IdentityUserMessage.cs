namespace AutumnOS.Identity;

/// <summary>Finite, local messages shared by setup and account UI. Never echoes provider text or callback data.</summary>
public static class IdentityUserMessage
{
    public static string ForError(string? code) => code switch
    {
        "USER_CANCELLED" => "登录已取消。你可以重新登录，也可以先以游客身份继续。",
        "AUTH_TIMEOUT" => "登录等待超时。请检查网络后重试，并在浏览器完成登录后返回 AutumnOS。",
        "OFFLINE" => "暂时无法连接账号服务。请检查网络后重试；也可以先以游客身份继续。",
        "AUTH_PROVIDER_UNAVAILABLE" => "账号服务暂时不可用，请稍后重试。你也可以先跳过登录。",
        "AUTH_METADATA_UNAVAILABLE" => "暂时无法读取账号服务配置。请检查网络或稍后重试；游戏和存档不受影响。",
        "AUTH_NOT_CONFIGURED" or "AUTH_CONFIGURATION_ERROR" => "账号接入配置未通过检查。请查看设置中的账号诊断，暂时可使用游客功能。",
        "AUTH_CLIENT_REJECTED" => "账号服务未接受当前应用。需要核对 Native 应用类型、公开 Client ID 和已登记的登录回调。",
        "AUTH_REDIRECT_REJECTED" => "账号服务未接受登录回调。需要核对已登记的完整回调地址，包括末尾斜杠。",
        "AUTH_PORT_IN_USE" or "AUTH_CALLBACK_PORT_IN_USE" => "登录回调端口 17853 已被占用。请关闭其他正在登录的 AutumnOS 窗口后重试。",
        "AUTH_BROWSER_UNAVAILABLE" => "无法打开系统浏览器。请检查 Windows 默认浏览器设置后重试。",
        "AUTH_INVALID_CALLBACK" or "AUTH_CALLBACK_INVALID" => "登录返回请求未通过校验。请关闭旧的登录标签页，并在此重新发起登录。",
        "AUTH_INVALID_TOKEN" or "AUTH_TOKEN_INVALID" or "AUTH_INVALID_RESPONSE" or "AUTH_PROTOCOL_ERROR" or "AUTH_UNTRUSTED_ENDPOINT" => "账号服务返回的身份信息未通过安全校验。请重新登录；如果仍失败，请查看账号诊断。",
        "AUTH_CREDENTIALS_WRITE_FAILED" => "无法安全保存本机登录状态。请检查程序数据目录是否可写，再尝试保持登录。",
        "AUTH_CREDENTIALS_UNREADABLE" => "本机保存的登录状态无法恢复，请重新登录。已有游戏和存档会保留。",
        "SESSION_EXPIRED" => "登录会话已失效，请重新登录。已有游戏和存档会保留。",
        "AUTH_BROWSER_LOGOUT_INCOMPLETE" => "本客户端已结束退出流程，但浏览器会话退出未完成。请检查账号状态。",
        "MAINTENANCE_IN_PROGRESS" => "程序正在更新或维护，请完成后再登录。",
        "REQUEST_BUSY" => "账号操作正在进行，请稍候或取消当前登录后重试。",
        "AUTH_REQUIRED" => "请先登录账号，也可以继续使用游客功能。",
        "AUTH_PROVIDER_ERROR" => "账号服务未完成本次登录。请回到应用重试，也可以暂时跳过。",
        _ => "账号操作未完成。请重试，或先以游客身份继续；已有存档会保留。"
    };
}
