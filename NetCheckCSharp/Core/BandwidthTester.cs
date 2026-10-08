using System.Diagnostics;
using System.Net.Http;

namespace NetCheck.Core;

/// <summary>
/// 下载带宽测速。
/// 默认绕过代理 —— 否则测到的是代理服务器的带宽，不是你的带宽。
/// </summary>
public static class BandwidthTester
{
    /// <summary>测速源。流式读取固定时长后主动断开，所以文件越大越好。</summary>
    public static readonly (string Name, string Url)[] Sources =
    {
        ("清华镜像",   "https://mirrors.tuna.tsinghua.edu.cn/ubuntu/ls-lR.gz"),
        ("阿里云镜像", "https://mirrors.aliyun.com/ubuntu/ls-lR.gz"),
        ("中科大镜像", "https://mirrors.ustc.edu.cn/ubuntu/ls-lR.gz"),
        ("Cloudflare", "https://speed.cloudflare.com/__down?bytes=100000000"),
    };

    public static async Task<BandwidthReport> TestAsync(
        double durationSeconds, bool useProxy, IProgress<string>? progress, CancellationToken ct)
    {
        using var client = PublicIpProbe.CreateClient(
            useProxy, TimeSpan.FromSeconds(durationSeconds + 15));

        var failures = new List<string>();

        foreach (var (name, url) in Sources)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"尝试测速源 {name}…");

            var r = await TryOneAsync(client, name, url, durationSeconds, ct).ConfigureAwait(false);
            if (r.Ok) return r;
            failures.Add($"{name}: {r.Error}");
        }

        return new BandwidthReport
        {
            Source = "-",
            Error = failures.Count > 0 ? string.Join("；", failures) : "无可用测速源",
        };
    }

    private static async Task<BandwidthReport> TryOneAsync(
        HttpClient client, string name, string url, double durationSeconds, CancellationToken ct)
    {
        var r = new BandwidthReport { Source = name };
        try
        {
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                                         .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                r.Error = $"HTTP {(int)resp.StatusCode}";
                return r;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            var buffer = new byte[128 * 1024];
            long total = 0;
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed.TotalSeconds < durationSeconds)
            {
                int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) { r.FileExhausted = true; break; }
                total += n;
            }
            sw.Stop();

            r.Bytes = total;
            r.Seconds = sw.Elapsed.TotalSeconds;
            if (total == 0) r.Error = "未读到数据";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            r.Error = ex.Message.Length > 60 ? ex.Message[..60] : ex.Message;
        }
        return r;
    }
}
