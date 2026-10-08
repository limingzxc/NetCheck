using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace NetCheck.Core;

/// <summary>
/// 公网出口 IP 探测。
/// 同时走「原生 UDP(STUN)」和「HTTP」两条路径：存在代理或多出口时两者结果会不一致，
/// 这个差异本身就是最有价值的诊断信息。
/// </summary>
public static class PublicIpProbe
{
    private static readonly (string Name, string Url, bool Json)[] Services =
    {
        ("ipify", "https://api.ipify.org?format=json", true),
        ("ipip.net", "https://myip.ipip.net", false),
        ("ifconfig.me", "https://ifconfig.me/ip", false),
    };

    /// <summary>创建 HttpClient。useProxy=false 时绕过系统代理与 *_proxy 环境变量。</summary>
    public static HttpClient CreateClient(bool useProxy, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            UseProxy = useProxy,
            AllowAutoRedirect = true,
        };
        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(8) };

        // 必须带 User-Agent：不少镜像站会直接 403 掉没有 UA 的请求，
        // .NET 的 HttpClient 默认不发 UA，很容易在这里踩坑。
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NetCheck/2.0");

        return client;
    }

    public static async Task<List<PublicIpResult>> QueryHttpAsync(bool useProxy, CancellationToken ct)
    {
        var list = new List<PublicIpResult>();
        using var client = CreateClient(useProxy);

        foreach (var (name, url, isJson) in Services)
        {
            ct.ThrowIfCancellationRequested();
            var r = new PublicIpResult { Service = name };
            var sw = Stopwatch.StartNew();
            try
            {
                var text = await client.GetStringAsync(url, ct).ConfigureAwait(false);
                r.Seconds = sw.Elapsed.TotalSeconds;

                if (isJson)
                {
                    using var doc = JsonDocument.Parse(text);
                    r.Ip = doc.RootElement.TryGetProperty("ip", out var ipEl)
                        ? ipEl.GetString() ?? "" : "";
                }
                else
                {
                    var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d{1,3}(?:\.\d{1,3}){3})");
                    r.Ip = m.Success ? m.Groups[1].Value : "";
                    var mm = System.Text.RegularExpressions.Regex.Match(text, @"来自于[：:]\s*(.+)");
                    if (mm.Success) r.Extra = mm.Groups[1].Value.Trim();
                }

                if (r.Ip.Length == 0) r.Ip = "解析失败";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                r.Error = "超时";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                r.Error = ex.Message.Length > 60 ? ex.Message[..60] : ex.Message;
                r.Seconds = sw.Elapsed.TotalSeconds;
            }
            list.Add(r);
        }
        return list;
    }
}
