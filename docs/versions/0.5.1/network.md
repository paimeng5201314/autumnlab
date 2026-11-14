# 网络接口说明（SDK 0.4.0）

0.5.1 是保留的历史文档目录名；本页接口版本以 SDK 0.4.0 为准，新增网络方法需要支持该版本的宿主。

# SDK 0.4 网络接口

制作人：派蒙。协议仍为1。本文说明当前实现合同；实际执行结果按构建ID记录，不代表完整隔离或Win10/11验收通过。

`platform.getCapabilities().capabilities` 包含 `network` 时，宿主提供以下接口。旧 SDK 0.3 和旧宿主未实现这些接口，应用必须检测能力。`networkSandboxVerified` 仍只表示当前浏览器实例的原生网络策略证明，与SDK代理或互联网可达性分别判断。

## 清单与授权

```json
{
  "permissions": ["network"],
  "networkDomains": ["api.github.com"],
  "permissionPurposes": {"network": "读取公开资料"}
}
```

这三项放进已有 `.autumn` 清单，保留schemaVersion、appId、entry等其他必需字段。network权限与非空networkDomains必须成对出现，1至16个精确小写ASCII域名，国际域名先转成ASCII/Punycode；禁止通配符、URL、端口、IP字面量、尾点、本机和保留后缀。域名集合排序后冻结，不按页面发来的清单授权。新域名集合不能继承旧集合的批准；不会因此改变应用存档命名空间。

应用在用户点击后调用 `permissions.request({name:'network'})`；宿主显示完整域名集合。拒绝和撤销保持有效，不能自动反复弹窗。权限仍绑定账号、安装来源与应用身份，不因PM/SQ分类而放行。

## 接口

```javascript
const capabilities = await autumn.request('platform.getCapabilities');
if (!capabilities.capabilities.includes('network')) throw new Error('此宿主没有网络SDK');
const status = await autumn.request('network.getStatus', {});
// status: { available: boolean, internetAccess: 'unknown' }

// 在真实用户点击处理程序中申请；已有许可可直接使用。
await autumn.request('permissions.request', { name: 'network' });
const response = await autumn.request('network.request', {
  url: 'https://api.github.com/rate_limit',
  method: 'GET',
  headers: { accept: 'application/json' },
  timeoutMs: 10000
});
const bytes = Uint8Array.from(atob(response.body), c => c.charCodeAt(0));
const text = new TextDecoder().decode(bytes);
```

getStatus只查询操作系统是否存在可用链路，并结合本实例的代理、原生证明和预览离线策略；不发送公网探测，不需要network授权，不声称互联网已经连通。实际请求仍需独立通过域名、权限和身份门禁；available=true不保证请求能发出或成功。

request只允许HTTPS、443端口，以及当前清单中已授权的精确主机。方法为GET、HEAD、POST、PUT、PATCH、DELETE；默认GET。`body`是规范base64，最多16KiB解码字节；GET/HEAD不能携带body。请求与完整响应仍受32768字节SDK信封限制。timeoutMs为1至20000，默认10000；每个实例最多同时2个网络请求，仍计入总16并发和120次/分钟限制。SDK的本地AbortSignal只停止等待，不保证撤回已发送的请求或远端副作用，禁止盲目自动重试写操作。

请求头仅允许小写的accept、content-type、authorization、if-none-match、if-modified-since。头值仅允许可打印ASCII字符，单项最长4096字符，应用头名和值合计最多8192字节。content-type仅能与body同时提供。authorization必须由应用显式提供规范Bearer值（token字符限字母、数字和`._~+/-`，可选`=`填充只在末尾）；它是应用自己的凭据。宿主使用固定非个人User-Agent `AutumnOS-Network/0.4`，应用不能覆盖；不填充或共享自己的登录令牌、Cookie、系统凭据、代理凭据、Referer或证书选择。

结果为 `{status, headers, body, encoding:'base64'}`。HTTP 4xx/5xx是实际HTTP结果，不伪装为200；连接、超时、权限或策略失败通过SDK错误返回。body最多16KiB，超限报错而非截断。响应头仅保留content-type、content-language、content-length、content-encoding、etag、last-modified、cache-control、expires、retry-after、location；头总量及完整JSON另有上限，不返回Set-Cookie。不会自动解压，应用按content-encoding解释收到的实际字节。

GET/HEAD最多自动跟随3次跳转；每一跳都重新核对声明域名、HTTPS443、DNS及实际连接地址，并移除authorization。其他方法返回真实3xx，不自动重发请求体；location仅在目标仍符合HTTPS443和声明域名时提供。声明多个域名也不意味着凭据可自动跨站转发。

## 运行边界

解析后只向通过检查的公网地址建立固定连接，禁止回环、私网、链路本地、组播及其他特殊用途地址；不用第二次解析替换已经检查的地址。HTTP固定1.1，关闭自动重定向、Cookie、系统代理和自动解压。TLS保持系统信任根、主机名和服务器用途验证；禁用证书AIA及自动根下载，避免链构建额外访问未声明目标，服务器需提供完整中间证书链。不在线查询吊销状态，保持HttpClient默认行为。直接网页fetch/WebSocket等继续受浏览器原生策略约束，SDK代理不是任意URL或文件代理。

每次连接、读取/写入启动、跳转和响应交付都检查实例、账号和授权代次；异步等待不持有Monitor。撤销/重授、同epoch会话失效、关闭/挂起和预览离线切换会使旧操作失效。已发送的字节无法召回，但过期响应不能借重新授权送回。开发预览offline仅影响该预览，online不复活旧请求，不改变真实账号或操作系统网络。

预览的offline变更必须经过当前原生实例所在协调器的同序屏障，等待旧投递结束或退休再提交；本地检查与IPC入队之间不能作为无保护窗口。重新绑定预览实例后，旧屏障许可也不能修改新实例的策略。

## 错误处理

| code | 含义与处理 |
|---|---|
| INVALID_PARAMS、NETWORK_HEADERS_INVALID、NETWORK_BODY_TOO_LARGE | 修正输入格式、头或请求体；不要原样重试 |
| NETWORK_URL_NOT_ALLOWED、NETWORK_DOMAIN_NOT_ALLOWED | 目标不符合URL或声明域名约束；修改清单域名需要用户重新授权 |
| NETWORK_DESTINATION_BLOCKED、NETWORK_REDIRECT_BLOCKED、NETWORK_REDIRECT_LIMIT | DNS地址或跳转不符合边界；不能退回网页直接联网绕过 |
| NETWORK_TIMEOUT、NETWORK_UNAVAILABLE | 超时或真实连接失败；写操作可能已有副作用，先与应用服务器核对 |
| NETWORK_RESPONSE_INVALID、NETWORK_RESPONSE_TOO_LARGE | 响应不符合格式或预算；宿主不返回截断后的成功结果 |
| NETWORK_OFFLINE、NETWORK_POLICY_CHANGED、PERMISSION_REVOKED、SESSION_EXPIRED、SESSION_SUSPENDED | 当前授权、身份、生命周期或预览策略失效；旧请求不能通过重授复活 |
| REQUEST_BUSY | 本实例网络并发已满；等待已有请求结束，避免无界排队 |

未声明或未授权仍按通用权限错误处理；尚未取得原生网络策略证明时为NETWORK_ISOLATION_REQUIRED。新网络许可单独持久化，旧九种权限文件保持旧版可读；历史授权只能从宿主撤销，不凭历史显示名称或域名摘要补造新的授权。

实现依据：[.NET连接回调](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-10.0)、[自动跳转行为](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.allowautoredirect?view=net-10.0)、[网络链路查询](https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.networkinterface.getisnetworkavailable?view=net-10.0)。这些API说明不替代当前产品的实测证据。

