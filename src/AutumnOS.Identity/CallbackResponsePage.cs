using System.Net;
using System.Security.Cryptography;
using System.Text;
using AutumnOS.Contracts;

namespace AutumnOS.Identity;

internal sealed record CallbackPageContent(string Html, string ContentSecurityPolicy);

/// <summary>A fixed, self-contained handoff page. Never receives callback parameters or authentication results.</summary>
internal static class CallbackResponsePage
{
    private static readonly string Styles = """
        :root{color-scheme:light dark;--bg:#f6f5f1;--ink:#252b30;--muted:#717775;--line:rgba(76,86,80,.12);--glass:rgba(255,255,255,.66);--accent:#7c6a4b;--pill:rgba(232,225,209,.65);--key:rgba(255,255,255,.8);--shadow:0 30px 90px -35px rgba(78,70,50,.23),0 2px 8px rgba(70,76,60,.025)}
        *{box-sizing:border-box}html{min-height:100%;background:var(--bg)}body{margin:0;min-height:100svh;color:var(--ink);font-family:"Segoe UI Variable","Segoe UI","Microsoft YaHei UI",sans-serif;-webkit-font-smoothing:antialiased;overflow-x:hidden}
        .scene{position:fixed;inset:0;overflow:hidden;pointer-events:none;background:radial-gradient(ellipse at 15% 12%,rgba(232,213,185,.52),transparent 55%),radial-gradient(ellipse at 86% 85%,rgba(192,211,204,.48),transparent 52%),radial-gradient(ellipse at 90% 16%,rgba(222,214,238,.5),transparent 48%)}
        .glow{position:absolute;border-radius:50%;filter:blur(55px);opacity:.48;animation:drift 14s ease-in-out 2 alternate}.glow.one{width:40vw;height:40vw;max-width:600px;max-height:600px;background:#edd5b2;left:-10%;top:18%}.glow.two{width:42vw;height:42vw;background:#ccdfd5;right:-12%;bottom:-22%;animation-delay:-4s}
        .contour{position:absolute;width:820px;height:820px;right:-510px;top:-230px;border:1px solid var(--line);border-radius:50%}.contour:after{content:"";position:absolute;inset:30px;border:1px solid var(--line);border-radius:50%}
        .layout{position:relative;min-height:100svh;display:flex;flex-direction:column;align-items:center;padding:38px 28px 24px}
        .masthead{width:min(1120px,100%);display:flex;align-items:center;gap:12px;animation:enter .7s ease-out both}.brand-mark{display:grid;place-items:center;width:34px;height:34px;border-radius:11px;color:var(--accent);background:var(--glass);border:1px solid var(--line)}.brand-mark svg{width:21px;height:21px}.brand-word{font-size:16px;font-weight:650;letter-spacing:.015em}.brand-context{margin-left:auto;font-size:11px;letter-spacing:.18em;color:var(--muted)}
        main{width:100%;flex:1;display:grid;place-items:center;padding:38px 0 44px}
        .card{position:relative;width:min(600px,100%);padding:38px 42px 32px;border:1px solid rgba(255,255,255,.85);border-radius:32px;background:var(--glass);box-shadow:var(--shadow);backdrop-filter:blur(24px);-webkit-backdrop-filter:blur(24px);text-align:center;animation:enter .85s cubic-bezier(.2,.75,.25,1) .1s both}
        .art{position:relative;width:196px;height:160px;margin:0 auto 16px;display:grid;place-items:center}.orbit{position:absolute;width:148px;height:148px;border:1px solid var(--line);border-radius:50%;transform:rotate(-18deg) scaleY(.82)}.orbit.outer{width:192px;height:192px;transform:rotate(22deg) scaleY(.7)}
        .orbit:before{content:"";position:absolute;width:6px;height:6px;border-radius:50%;background:#bca985;left:19px;top:17px;box-shadow:0 0 0 5px rgba(188,169,133,.07)}.orbit.outer:before{width:4px;height:4px;left:auto;right:24px;top:auto;bottom:21px;background:#9cbaad}
        .app-tile{width:88px;height:88px;display:grid;place-items:center;border-radius:26px;background:linear-gradient(145deg,#faf2df,#ddd9c9 62%,#cad8d0);border:1px solid rgba(255,255,255,.9);box-shadow:0 16px 28px -13px rgba(103,111,89,.4),inset 0 1px 1px white;transform:rotate(-8deg);animation:float 6s ease-in-out 2}.app-tile svg{width:47px;height:47px;color:#64776b;filter:drop-shadow(0 1px 0 rgba(255,255,255,.6))}
        .spark{position:absolute;width:6px;height:6px;border-radius:2px;background:#c2ad8c;transform:rotate(45deg);top:18px;right:33px}.spark.small{width:4px;height:4px;top:auto;right:auto;left:29px;bottom:23px;background:#a9b6b0}
        .status{display:inline-flex;align-items:center;gap:8px;margin:0 0 18px;padding:7px 12px;border-radius:30px;background:var(--pill);color:var(--accent);font-size:12px;font-weight:600;letter-spacing:.05em}.status-dot{width:6px;height:6px;border-radius:50%;background:currentColor;box-shadow:0 0 0 3px rgba(173,151,108,.09)}
        h1{margin:0;font-size:clamp(29px,4.3vw,39px);font-weight:620;line-height:1.35;letter-spacing:-.045em}h1 span{display:block}.description{margin:18px 0 0;color:var(--muted);font-size:14px;line-height:1.95}.description span{display:block}
        .handoff{margin:29px auto 0;display:flex;align-items:center;justify-content:center;gap:12px;max-width:355px;padding:17px 18px;border-radius:17px;background:var(--key);border:1px solid var(--line);font-size:13px;color:var(--ink)}.keys{display:inline-flex;align-items:center;gap:5px;color:var(--muted);font-size:11px}kbd{font-family:inherit;font-size:11px;font-weight:600;border:1px solid var(--line);border-bottom-width:2px;border-radius:5px;padding:3px 7px;min-width:27px;line-height:1.25;background:var(--glass);color:var(--ink)}.handoff svg{width:16px;height:16px;opacity:.55;margin-left:auto;flex-shrink:0}
        .footnote{margin:20px 0 0;font-size:12px;line-height:1.8;color:var(--muted)}footer{font-size:11px;line-height:1.9;color:var(--muted);display:flex;gap:10px;justify-content:center;align-items:center;flex-wrap:wrap;animation:enter 1s ease-out .2s both}.separator{width:3px;height:3px;background:currentColor;border-radius:50%;opacity:.5}
        .invalid .status{color:#976a40}.invalid .app-tile{background:linear-gradient(145deg,#faf0df,#e5d2bb 70%,#d7d7ce)}
        @keyframes enter{from{opacity:0;transform:translateY(12px)}to{opacity:1;transform:translateY(0)}}@keyframes float{0%,100%{transform:translateY(0) rotate(-8deg)}50%{transform:translateY(-7px) rotate(-4deg)}}@keyframes drift{from{transform:translate(0,0) scale(1)}to{transform:translate(38px,-24px) scale(1.1)}}
        @media(prefers-color-scheme:dark){:root{--bg:#171d20;--ink:#e9eeeb;--muted:#a4b0ab;--line:rgba(191,210,200,.13);--glass:rgba(31,40,42,.74);--accent:#c8b794;--pill:rgba(134,117,80,.16);--key:rgba(48,59,60,.58);--shadow:0 30px 90px -35px rgba(0,0,0,.7),0 1px 0 rgba(255,255,255,.02)}.scene{background:radial-gradient(ellipse at 12% 15%,rgba(125,100,69,.13),transparent 55%),radial-gradient(ellipse at 86% 85%,rgba(62,100,90,.18),transparent 52%),radial-gradient(ellipse at 92% 15%,rgba(87,77,113,.16),transparent 48%)}.glow{opacity:.09}.card{border-color:rgba(209,226,216,.13)}.app-tile{background:linear-gradient(145deg,#657364,#485e55 62%,#394d4e);border-color:rgba(213,230,214,.25);box-shadow:0 16px 28px -13px rgba(0,0,0,.6),inset 0 1px 1px rgba(255,255,255,.12)}.app-tile svg{color:#dedcc4;filter:none}.invalid .status{color:#d4b28a}.invalid .app-tile{background:linear-gradient(145deg,#776b56,#514c42 70%,#3e4d49)}}
        @media(max-width:520px){.layout{padding:25px 18px 20px}.brand-context{font-size:10px;letter-spacing:.1em}main{padding:30px 0}.card{padding:28px 22px 27px;border-radius:27px}.art{height:142px;margin-bottom:12px}.description{font-size:13px}.handoff{padding:15px 12px;gap:9px}.footnote{font-size:11px}footer{font-size:10px}}
        @media(max-height:800px) and (min-width:521px){.layout{padding-top:22px}main{padding:22px 0}.card{padding-top:20px;padding-bottom:22px}.art{height:115px;margin-bottom:8px}.app-tile{width:72px;height:72px;border-radius:23px}.orbit{width:120px;height:120px}.orbit.outer{width:164px;height:164px}.status{margin-bottom:13px}.description{margin-top:13px}.handoff{margin-top:20px}.footnote{margin-top:15px}}
        @media(prefers-reduced-motion:reduce){*,*:before,*:after{animation:none!important;transition:none!important}}
        @media(forced-colors:active){.scene,.orbit,.spark{display:none}.card,.app-tile,.handoff,.status,.brand-mark{border:1px solid CanvasText;box-shadow:none;background:Canvas;color:CanvasText}.description,.footnote,footer{color:CanvasText}}
        """.ReplaceLineEndings("\n");

    private static readonly string Policy = "default-src 'none'; script-src 'none'; style-src 'sha256-" +
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Styles))) +
        "'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    internal static CallbackPageContent Create(bool accepted)
    {
        string display = WebUtility.HtmlEncode(BrandInfo.DisplayName);
        string product = WebUtility.HtmlEncode(BrandInfo.ProductName);
        string producer = WebUtility.HtmlEncode(BrandInfo.ProducerCredit);
        string status = accepted ? "请求已送达" : "请求未被接受";
        string heading = accepted ? $"<span>接下来，</span><span>回到 {display}。</span>" : "<span>回到应用，</span><span>再试一次。</span>";
        string description = accepted ? "<span>请切回应用查看结果。</span><span>你的下一段探索，在桌面继续。</span>"
            : "<span>这个请求可能已失效或已经处理。</span><span>请回到应用，重新发起操作。</span>";
        string html = $$"""
            <!doctype html>
            <html lang="zh-CN">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><meta name="referrer" content="no-referrer"><meta name="color-scheme" content="light dark"><title>{{status}} · {{display}}</title><style>{{Styles}}</style></head>
            <body class="{{(accepted ? "received" : "invalid")}}">
              <div class="scene" aria-hidden="true"><div class="glow one"></div><div class="glow two"></div><div class="contour"></div></div>
              <div class="layout">
                <header class="masthead"><span class="brand-mark" aria-hidden="true"><svg viewBox="0 0 40 40" fill="none"><path d="M10 29C9 16 17 8 31 9C32 22 24 31 10 29Z" fill="currentColor" opacity=".8"/><path d="M8 32L25 15" stroke="currentColor" stroke-width="2.4" stroke-linecap="round"/></svg></span><span class="brand-word">{{display}}</span><span class="brand-context">ACCOUNT CONNECTION</span></header>
                <main><section class="card" aria-labelledby="page-title">
                  <div class="art" aria-hidden="true"><div class="orbit outer"></div><div class="orbit"></div><span class="spark"></span><span class="spark small"></span><div class="app-tile"><svg viewBox="0 0 48 48" fill="none"><path d="M11 35C9 19 19 9 38 10C40 27 29 38 11 35Z" fill="currentColor" opacity=".85"/><path d="M9 39L31 17" stroke="currentColor" stroke-width="2.6" stroke-linecap="round"/><path d="M19 29L19 19M26 23L35 23" stroke="#d4ddcf" stroke-opacity=".55" stroke-width="1.5" stroke-linecap="round"/></svg></div></div>
                  <p class="status"><span class="status-dot" aria-hidden="true"></span>{{status}}</p>
                  <h1 id="page-title">{{heading}}</h1><p class="description">{{description}}</p>
                  <div class="handoff"><span class="keys" aria-label="Alt 加 Tab"><kbd>Alt</kbd><span aria-hidden="true">+</span><kbd>Tab</kbd></span><span>切回 {{display}}</span><svg aria-hidden="true" viewBox="0 0 20 20" fill="none"><path d="M4 10h11m-4-4 4 4-4 4" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round"/></svg></div>
                  <p class="footnote">你可以关闭此标签页，回到应用继续。</p>
                </section></main>
                <footer><span>{{product}}</span><span class="separator" aria-hidden="true"></span><span>{{producer}}</span></footer>
              </div>
            </body></html>
            """;
        return new(html, Policy);
    }
}
