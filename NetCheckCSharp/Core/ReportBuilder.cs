using System.Text;

namespace NetCheck.Core;

/// <summary>
/// 把 DiagState 渲染成报告行。
/// GUI 用它做带样式的插入，文本导出用它拼字符串 —— 只有一份内容描述，不会出现两边不一致。
/// </summary>
public static class ReportBuilder
{
    public const string AppName = "NetCheck 网络体检";

    /// <summary>
    /// 报告头里的版本号。以前这里是硬编码的常量，csproj 里的 Version 升到 2.2.0 之后
    /// 导出的报告还印着 v2.0，所以改成直接读程序集版本，永远跟发布版本一致。
    /// </summary>
    public static string Version
    {
        get
        {
            var v = typeof(ReportBuilder).Assembly.GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static List<ReportLine> Build(DiagState d)
    {
        var it = new List<ReportLine>();

        void H(string s) => it.Add(new ReportLine(LineKind.Header, s));

        // 按「值列起始位置」补白，而不是把 key 补到固定长度：
        // 这样 key 自带缩进（如 "  114.114.114.114"）或特别长时也不会挤进值列。
        void KV(string k, string v, LineKind kind = LineKind.Value, int width = 18)
        {
            var padCount = Math.Max(2, width - DisplayWidth(k));
            it.Add(new ReportLine(LineKind.Label, "  " + k + new string(' ', padCount), false));
            it.Add(new ReportLine(kind, v));
        }
        void Line(string s, LineKind kind = LineKind.Label) => it.Add(new ReportLine(kind, "  " + s));
        void Missing(string s) => it.Add(new ReportLine(LineKind.Label, "  " + s));

        // 归属地就地拼在 IP 后面。纯文本没有「旁边一小字」这种排版，
        // 用全角括号圈起来才不会和后面的延迟、端口连成一串看不懂的东西。
        // 空串表示这里不该有归属地（私有地址由 IpGeo 自己给「局域网」，查不到的则不外显）。
        string G(string? ip)
        {
            var t = IpGeo.Of(d, ip);
            return t.Length > 0 ? "（" + t + "）" : "";
        }

        // 行尾版本。用在上面的行已经排好列、再往 IP 后面塞括号会挤乱列宽的地方
        // （比如「3.2 ms   答案 2 条（江苏省南京市）」，看着像答案条数被限定了一样）。
        string GTail(string? ip)
        {
            var t = IpGeo.Of(d, ip);
            return t.Length > 0 ? "   · " + t : "";
        }

        // ---------------- 一、本机网络配置 ----------------
        H("一、本机网络配置");
        if (d.Adapters is { } adapters)
        {
            KV("主机名", d.Hostname);
            foreach (var a in adapters)
            {
                var head = $"{a.Name}   {a.IPv4}";
                if (a.Type.Length > 0) head += $"   {a.Type}";
                head += $"   {a.Status}";
                KV("网卡", head);

                if (a.Description.Length > 0) KV("  型号", a.Description);
                if (a.Mask.Length > 0) KV("  掩码", a.Mask);
                if (a.Gateway.Length > 0) KV("  网关", a.Gateway);
                if (a.Mac.Length > 0) KV("  MAC", a.Mac);
                if (a.Dns.Count > 0) KV("  DNS", string.Join(", ", a.Dns.Select(x => x + G(x))));
                if (a.SpeedMbps > 0) KV("  速率", $"{a.SpeedMbps} Mbps");
                KV("  DHCP", a.DhcpEnabled ? "已启用" : "未启用");
            }

            if (d.Proxy is { Count: > 0 } px)
            {
                foreach (var kv in px)
                    KV("代理", $"{kv.Key} = {kv.Value}", LineKind.Warn);
                Line("走 HTTP 的检测会经过代理，与原生 UDP 结果可能不同");
            }
        }
        else Missing("尚未检测 · 点击「一键检测」");

        // ---------------- 二、IPv6 ----------------
        // 排在「本机网络配置」之后、「公网出口」之前：它读的是和本机配置同一个 API 的同一个集合，
        // 属于「我有什么地址」的下半场；先看清地址栈，再谈通不通。
        H("二、IPv6");
        if (d.Ipv6 is { } v6)
        {
            KV("结论", v6.Verdict);

            if (v6.Addresses.Count > 0)
            {
                Line("本机 IPv6 地址：");
                foreach (var a in v6.Addresses)
                    Line($"  {a.Kind}{(a.IsTemporary ? "（临时）" : "")}  {a.Address}   [{a.Adapter}]");
            }

            if (v6.EgressIp.Length > 0)
                KV("IPv6 出口", v6.EgressIp + G(v6.EgressIp), LineKind.Ok);
            else if (v6.Enabled)
                KV("IPv6 出口", "未获取到" + (v6.EgressError.Length > 0 ? $"（{v6.EgressError}）" : ""),
                   LineKind.Warn);

            if (v6.Pings.Count > 0)
            {
                Line("可达性（各 1.5 秒超时）：");
                foreach (var p in v6.Pings)
                {
                    var head = $"{p.Host,-28} ";
                    if (p.Ok) KV("  " + p.Region + " " + p.Name, head + $"可达  {p.Ms:F0} ms", LineKind.Ok);
                    else KV("  " + p.Region + " " + p.Name, head + $"不可达（{p.Error}）", LineKind.Warn);
                }
            }

            if (v6.Detail.Length > 0) Line(v6.Detail);
        }
        else Missing("尚未检测 · 点击「IPv6 检测」");

        // ---------------- 三、公网出口 IP ----------------
        H("三、公网出口 IP");
        if (d.PublicIpDirect != null)
        {
            KV("UDP/STUN", d.PublicIpDirect + G(d.PublicIpDirect));

            void RenderHttp(List<PublicIpResult>? list, string label)
            {
                if (list == null) return;
                foreach (var r in list)
                {
                    if (!r.Ok)
                        KV(label, $"{r.Service} 失败（{(r.Error.Length > 0 ? r.Error : "无响应")}）", LineKind.Warn);
                    else
                    {
                        var extra = r.Extra.Length > 0 ? "  " + r.Extra : "";
                        KV(label, $"{r.Service} → {r.Ip}{G(r.Ip)}  ({r.Seconds:F2}s){extra}");
                    }
                }
            }

            RenderHttp(d.PublicIpHttpProxy, "HTTP(代理)");
            RenderHttp(d.PublicIpHttpDirect, "HTTP(直连)");

            var ips = d.AllPublicIps();
            if (ips.Count > 1)
            {
                Line($"检测到 {ips.Count} 个不同的公网 IP："
                   + string.Join("、", ips.Select(x => x + G(x))), LineKind.Warn);
                Line("说明链路存在多出口或中间代理，部分应用可能出现 IP 不一致问题");
            }
        }
        else Missing("尚未检测 · 点击「一键检测」");

        // ---------------- 四、网络延迟 ----------------
        H("四、网络延迟");
        if (d.Latency is { } lat)
        {
            if (lat.Gateway is { } g && g.Ok)
            {
                // Windows 的 ICMP RTT 以整毫秒返回，直连网关常为 0，实际是亚毫秒
                var gAvg = g.Avg < 1 ? "<1" : g.Avg.ToString("F1");
                KV("网关", $"{g.Host}   平均 {gAvg} ms   抖动 {LatencyReport.Jitter(g.Samples):F1} ms   丢包 {g.Loss:F0}%");
            }
            else if (lat.Gateway != null)
                KV("网关", $"{lat.Gateway.Host}   不可达", LineKind.Warn);

            Line("各目标延迟（各 4 次）：");

            void EmitTargets(IEnumerable<LatencyTarget> items)
            {
                foreach (var t in items)
                {
                    if (!t.Ok)
                    {
                        KV("  " + t.Name, "不可达（丢包 100%）", LineKind.Warn);
                        continue;
                    }
                    var jitter = LatencyReport.Jitter(t.Samples);
                    var flag = t.Loss > 0 ? LineKind.Warn : LineKind.Ok;
                    KV("  " + t.Name,
                       $"{t.Host,-24} 最快 {t.Min,6:F1}  平均 {t.Avg,6:F1}  最慢 {t.Max,6:F1} ms  "
                       + $"抖动 {jitter,5:F1} ms  丢包 {t.Loss,3:F0}%  [{t.Method}]", flag);
                }
            }

            // 按用途分组，每组带上自己的判定阈值 —— 游戏的 50 ms 和网页的 50 ms 不是一个概念
            foreach (var cat in LatencyProbe.Categories)
            {
                var group = lat.Targets.Where(x => x.Category == cat.Name).ToList();
                if (group.Count == 0) continue;
                Line($"  【{cat.Name}】{cat.Hint} · ≤{cat.GoodMs:F0} ms 良好 · ≤{cat.WarnMs:F0} ms 可用");
                EmitTargets(group);
            }

            var orphans = lat.Targets
                .Where(x => LatencyProbe.Categories.All(c => c.Name != x.Category)).ToList();
            if (orphans.Count > 0)
            {
                Line("  【其他目标】未归入已知用途分类");
                EmitTargets(orphans);
            }
        }
        else Missing("尚未检测 · 点击「一键检测」");

        // ---------------- 五、DNS 服务器 ----------------
        // 「当前 DNS」和「对比 DNS」在同一章里分成两块，和界面上的两段一一对应：
        // 前者是本机网卡上配的，后者只是拿来比一比的公共 DNS。
        // 两边都用 DnsProbe.CompareTestDomain，数字才能横向比。
        // 用 Line 而不是 H —— 这里是章内的小标题，不该再占一个章号。
        H("五、DNS 服务器");
        if (d.DnsServers != null)
        {
            KV("服务器", d.DnsServers.Count > 0
                ? string.Join("、", d.DnsServers.Select(x => x + G(x)))
                : "未从网卡配置中读到 DNS");
            if (d.DnsServerTests is { Count: > 0 } tests)
            {
                Line("各 DNS 响应速度（直接查询，不走系统缓存）：");
                foreach (var t in tests)
                {
                    if (t.Ok)
                        KV("  " + t.Server, $"{t.Ms,6:F1} ms   答案 {t.Answers} 条" + GTail(t.Server), LineKind.Ok);
                    else
                        KV("  " + t.Server, $"失败（{t.Error}）" + GTail(t.Server), LineKind.Warn);
                }
            }
        }
        else Missing("尚未检测 · 点击「一键检测」");

        if (d.DnsCompareTests is { Count: > 0 } cmps)
        {
            Line("");
            Line($"对比公共 DNS（你没有在用它们 · 同样解析 {DnsProbe.CompareTestDomain}）：");
            foreach (var t in cmps)
            {
                if (t.Ok)
                    KV("  " + t.Server, $"{t.Ms,6:F1} ms   答案 {t.Answers} 条" + GTail(t.Server));
                else
                    KV("  " + t.Server, $"失败（{t.Error}）" + GTail(t.Server), LineKind.Warn);
            }
            Line("这两组数字可以直接比 —— 用的是同一个域名。");
        }

        // ---------------- 六、DNS 解析性能 ----------------
        H("六、DNS 解析性能");
        if (d.DnsResolve != null)
        {
            Line("系统解析器耗时（各 3 次，可能命中缓存）：");
            foreach (var r in d.DnsResolve)
            {
                if (!r.Ok) KV("  " + r.Domain, "解析失败", LineKind.Warn);
                else KV("  " + r.Domain,
                        $"最快 {r.Min,5:F1}  平均 {r.Avg,5:F1}  最慢 {r.Max,5:F1} ms   → {r.Address}");
            }
        }
        else Missing("尚未检测 · 点击「DNS 检测」");

        // ---------------- 七、DNS 劫持检测 ----------------
        H("七、DNS 劫持检测");
        if (d.Hijack is { } hj)
        {
            Line($"探针域名：{hj.ProbeDomain}（保证不存在，正常应返回 NXDOMAIN）");
            foreach (var c in hj.Checks)
            {
                if (!c.Ok) KV("  " + c.Server, $"探测失败（{c.Error}）", LineKind.Warn);
                else if (c.Hijacked)
                    KV("  " + c.Server, $"被劫持！对不存在的域名返回了 {string.Join(", ", c.Answers)}", LineKind.Warn);
                else
                    KV("  " + c.Server, $"正常（rcode={c.RCode}，无答案）", LineKind.Ok);
            }

            if (hj.AnyHijacked)
            {
                Line("结论：至少一个 DNS 服务器存在劫持或广告注入行为，建议更换 DNS。", LineKind.Warn);
            }
            else
            {
                Line("结论：未发现劫持迹象。", LineKind.Ok);
            }

            foreach (var cmp in hj.Comparisons)
            {
                Line($"「{cmp.Domain}」在各 DNS 上的解析结果：");
                foreach (var e in cmp.Entries)
                {
                    if (!e.Ok) KV("  " + e.Server, $"失败（{e.Error}）", LineKind.Warn);
                    else KV("  " + e.Server, e.Addresses.Count > 0 ? string.Join(", ", e.Addresses) : "无 A 记录");
                }
                if (!cmp.AllAgree)
                    Line("  注：各服务器结果不一致。可能是 CDN 就近调度，也可能是劫持，需结合上面探针结果判断。");
            }
        }
        else Missing("尚未检测 · 点击「DNS 检测」");

        // ---------------- 八、NAT 类型 ----------------
        H("八、NAT 类型");
        if (d.Nat is { } nat)
        {
            KV("判定结果", nat.Type, LineKind.Ok);
            if (nat.LocalPort > 0) KV("本地端口", nat.LocalPort.ToString());
            foreach (var o in nat.Observations)
            {
                // STUN 服务器名较长（stun.l.google.com:19302 有 23 字符），单独放宽列宽
                if (o.Ok) KV(o.Server, $"{o.MappedIp}:{o.MappedPort}", LineKind.Value, 34);
                else KV(o.Server, $"失败（{o.Error}）", LineKind.Warn, 34);
            }
            if (nat.Detail.Length > 0) Line(nat.Detail);
            Line("注：单机无法区分 Full Cone 与 Restricted Cone，结论为简化判定");
        }
        else Missing("尚未检测 · 点击「NAT 类型检测」");

        // ---------------- 九、下载带宽 ----------------
        H("九、下载带宽");
        if (d.BandwidthDirect is { } bw)
        {
            if (bw.Ok)
            {
                KV("直连", $"{bw.Mbps:F2} Mbps  ({bw.MBps:F2} MB/s, 源：{bw.Source})", LineKind.Ok);
                KV("  样本", $"{bw.Seconds:F1} 秒内下载 {bw.Bytes / 1_048_576.0:F1} MB");
                if (bw.FileExhausted)
                    Line("注：测速文件已下载完，样本量偏小，结果仅供参考");
            }
            else KV("直连", $"失败：{bw.Error}", LineKind.Warn);

            if (d.BandwidthProxy is { Ok: true } bwp)
                KV("经代理", $"{bwp.Mbps:F2} Mbps  (源：{bwp.Source})");
        }
        else Missing("尚未检测 · 点击「带宽测速」");

        // ---------------- 十、路由追踪 ----------------
        H("十、路由追踪");
        if (d.Traceroute is { } tr)
        {
            if (tr.Error.Length > 0)
            {
                Line($"追踪失败：{tr.Error}", LineKind.Warn);
            }
            else
            {
                var target = tr.TargetIp.Length > 0 ? $"{tr.Target} ({tr.TargetIp})" : tr.Target;
                KV("目标", target);
                // 归属地是这张表最有价值的一列 —— 「第几跳出境」只能靠它看出来。
                // 列宽用 Pad 而不是 string.Format 的 ,-22：后者按字符数补，
                // 中文地名一个字符顶两个位，混着 ASCII 地址会排得歪歪扭扭。
                Line("跳数   地址                  归属地                 延迟         状态");
                foreach (var h in tr.Hops)
                {
                    if (h.Timeout)
                    {
                        Line($"  {h.Ttl,2}   {Pad("*", 20)} {Pad("", 22)} {"—",10}   {h.Status}", LineKind.Warn);
                    }
                    else
                    {
                        var geo = IpGeo.Of(d, h.Address);
                        Line($"  {h.Ttl,2}   {Pad(h.Address, 20)} {Pad(geo, 22)} {h.Ms,7:F1} ms   {h.Status}",
                             h.Reached ? LineKind.Ok : LineKind.Value);
                    }
                }
                if (tr.Reached) Line($"已到达目标，共 {tr.Hops.Count} 跳。", LineKind.Ok);
                else if (tr.StoppedEarly)
                    Line("连续多跳无响应，已提前结束。中间设备可能屏蔽了 ICMP，这不代表网络不通。");
                else Line($"达到最大跳数仍未到达目标，共 {tr.Hops.Count} 跳。", LineKind.Warn);
            }
        }
        else Missing("尚未检测 · 点击「路由追踪」");

        // ---------------- 十一、端口连通性 ----------------
        H("十一、端口连通性");
        if (d.PortScan is { } ps)
        {
            KV("目标", ps.Host);
            KV("结果", $"开放 {ps.OpenCount} 个 / 共 {ps.Results.Count} 个",
               ps.OpenCount > 0 ? LineKind.Ok : LineKind.Value);
            Line("端口    服务            状态       耗时");
            foreach (var r in ps.Results)
            {
                var svc = r.Service.Length > 0 ? r.Service : "—";
                if (r.Open)
                {
                    var text = $"{r.Port,-7} {svc,-15} 开放     {r.Ms,7:F1} ms";
                    if (r.Banner.Length > 0) text += $"   {r.Banner}";
                    Line(text, LineKind.Ok);
                }
                else
                {
                    Line($"{r.Port,-7} {svc,-15} 关闭     {r.Error}", LineKind.Warn);
                }
            }
        }
        else Missing("尚未检测 · 点击「端口检测」");

        // ---------------- 十二、路径 MTU ----------------
        H("十二、路径 MTU");
        if (d.Mtu is { } mtu)
        {
            if (!mtu.Ok)
            {
                Line(mtu.Error, LineKind.Warn);
            }
            else
            {
                KV("目标", mtu.Target);
                KV("路径 MTU", $"{mtu.PathMtu} 字节（最大 ICMP 负载 {mtu.MaxPayload} 字节）",
                   mtu.PathMtu >= 1500 ? LineKind.Ok : LineKind.Warn);
                Line(mtu.Assessment);
                if (mtu.PathMtu < 1500)
                    Line("提示：路径 MTU 小于 1500 时，若中间设备丢弃 ICMP「需要分片」报文，"
                       + "会出现 TCP 握手正常但大文件传输卡死的情况（PMTUD 黑洞）。", LineKind.Warn);
            }
        }
        else Missing("尚未检测 · 点击「MTU 探测」");

        // ---------------- 十三、HTTP / HTTPS ----------------
        // 这一节回答的不是「网站能不能打开」，而是**打不开的话卡在哪一层**。
        // 四层是严格串行的，第一个失败的那一层就是结论 —— 所以失败的目标要把它标出来。
        H("十三、HTTP / HTTPS");
        if (d.Web is { Targets.Count: > 0 } web)
        {
            KV("结论", web.Verdict);
            Line(web.Detail);
            Line("");
            foreach (var g in web.Targets)
            {
                var head = $"{g.Name}  ({g.Region} · {g.Host}"
                         + (g.ResolvedIp.Length > 0 ? " → " + g.ResolvedIp : "") + ")";
                Line(head, g.Ok ? LineKind.Ok : LineKind.Warn);

                foreach (var st in g.Steps)
                {
                    var mark = st.Ok ? "通过" : "失败";
                    var extra = st.Ok ? st.Detail : st.Error;
                    Line($"    {Pad(st.Name, 6)}{mark}   {st.Ms,7:F0} ms   {extra}",
                         st.Ok ? LineKind.Value : LineKind.Warn);
                }

                if (!g.Ok)
                    Line($"    → 卡在 {g.FailedStep} 层：{g.Blame}", LineKind.Warn);
                else if (g.HttpStatus >= 400)
                    Line($"    → 服务端返回 {g.HttpStatus}：链路通畅，是这个请求被拒绝。", LineKind.Warn);
            }
        }
        else Missing("尚未检测 · 点击「网页连通性检测」");

        return it;
    }

    /// <summary>渲染成纯文本，用于导出与复制。</summary>
    public static string RenderText(DiagState d)
    {
        var sb = new StringBuilder();
        sb.AppendLine(new string('=', 62));
        sb.AppendLine($"  {AppName}  v{Version}");
        sb.AppendLine($"  检测时间：{(d.Timestamp.Length > 0 ? d.Timestamp : "-")}");
        sb.AppendLine(new string('=', 62));

        var cur = new StringBuilder();
        bool firstHeader = true;

        foreach (var line in Build(d))
        {
            if (line.Kind == LineKind.Header)
            {
                if (!firstHeader) sb.AppendLine();
                firstHeader = false;
                if (cur.Length > 0) { sb.AppendLine(cur.ToString()); cur.Clear(); }
                sb.AppendLine(line.Text);
                continue;
            }
            cur.Append(line.Text);
            if (line.NewLine) { sb.AppendLine(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) sb.AppendLine(cur.ToString());

        sb.AppendLine();
        sb.AppendLine(new string('=', 62));
        return sb.ToString();
    }

    /// <summary>中文等全角字符按 2 个宽度计，保证等宽字体下的列对齐。</summary>
    public static int DisplayWidth(string s)
    {
        int w = 0;
        foreach (var c in s) w += c > 0x2E80 ? 2 : 1;
        return w;
    }

    public static string Pad(string s, int width)
        => s + new string(' ', Math.Max(0, width - DisplayWidth(s)));
}
