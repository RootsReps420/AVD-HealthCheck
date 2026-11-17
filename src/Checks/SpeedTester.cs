// Download / upload / latency test with a provider chain.
//
//   1. Cloudflare (speed.cloudflare.com endpoints): highest capacity, but rate-limited per
//      public IP. Behind the corporate proxy every VM shares that IP.
//   2. LibreSpeed (open-source speed test, public server list): used automatically for
//      any phase Cloudflare refuses. The nearest preferred server is chosen by ping.
//   A bank-hosted LibreSpeed server, or any fixed URLs, can be added in endpoints.json.
//
// Traffic goes through the system proxy, so the result reflects the user's real path.
// Method: parallel streams until the time limit or the data cap; the opening phase
// (first second, or first 15% of the cap) is left out so TCP slow start does not skew it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace AvdHealthCheck
{
    public class SpeedResult
    {
        public double? DownloadMbps { get; set; }
        public double? UploadMbps { get; set; }
        public double? LatencyMs { get; set; }
        public double? JitterMs { get; set; }
        public string DownloadSource { get; set; }   // e.g. "Cloudflare LHR" or "LibreSpeed London (Clouvider)"
        public string UploadSource { get; set; }
        public string LatencySource { get; set; }
        public string Route { get; set; }
        public string Error { get; set; }           // nothing could be measured at all
        public string DownloadError { get; set; }
        public string UploadError { get; set; }
        public List<string> Notes { get; private set; }   // e.g. "Cloudflare rate-limited; used LibreSpeed"

        public SpeedResult() { Notes = new List<string>(); }
    }

    /// <summary>Live reading for the summary cards. Phase: latency | download | upload.</summary>
    public class SpeedProgress
    {
        public string Phase { get; set; }
        public double Mbps { get; set; }
        public double Fraction { get; set; }
    }

    /// <summary>The speed test server refused us (rate limit, proxy block, ...). Message is user-facing.</summary>
    public class SpeedTestRefusedException : Exception
    {
        public SpeedTestRefusedException(string message) : base(message) { }
    }

    /// <summary>A provider with concrete URLs, ready to test against.</summary>
    internal class ResolvedProvider
    {
        public string Name;              // "Cloudflare", "LibreSpeed London, England (Clouvider)"
        public Uri Latency;
        public Func<Uri> Download;       // a new URI per request (cache-busting where needed)
        public Func<Uri> Upload;         // null = no upload target
        public int UploadChunk;
        public bool ServerFromRay;       // Cloudflare: read the data centre from CF-RAY
    }

    public sealed class SpeedTester : IDisposable
    {
        private readonly SpeedTestConfig _config;
        private readonly HttpClient _client;

        public SpeedTester(SpeedTestConfig config)
        {
            _config = config;
            var handler = new HttpClientHandler { UseProxy = true, Proxy = SystemProxy.Create(), UseCookies = false };
            _client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("AVDHealthCheck/" + AppInfo.Version);
        }

        // Like fast.com: several connections, run until the rate levels off, with a time and data ceiling.
        private const double WarmUpSeconds = 1.5;   // ignore TCP slow start / proxy set-up
        private const double MinMeasureSeconds = 3; // never stop earlier than this after warm-up
        private const double StableWithin = 0.05;   // three 1 s readings within 5% = stable
        private int Seconds { get { return Math.Max(4, _config.Seconds ?? 10); } }
        private int Streams { get { return Math.Max(1, Math.Min(16, _config.Streams ?? 8)); } }
        private long DownloadCap { get { return Math.Max(50, _config.MaxDownloadMB ?? 1500) * 1000000L; } }
        private long UploadCap { get { return Math.Max(20, _config.MaxUploadMB ?? 400) * 1000000L; } }

        public async Task<SpeedResult> RunAsync(IProgress<SpeedProgress> progress)
        {
            var result = new SpeedResult();
            var failures = new List<string>();

            foreach (SpeedProviderConfig config in _config.EffectiveProviders())
            {
                if (result.DownloadMbps.HasValue && result.UploadMbps.HasValue) break;
                string label = string.IsNullOrWhiteSpace(config.Name) ? "Speed test server" : config.Name;

                ResolvedProvider provider;
                try
                {
                    provider = await ResolveAsync(config);
                }
                catch (Exception ex)
                {
                    failures.Add(label + " unavailable (" + Friendly(ex) + ")");
                    continue;
                }

                if (result.Route == null)
                {
                    string proxy = SystemProxy.Describe(SystemProxy.Create(), provider.Latency);
                    result.Route = proxy == null ? "direct" : "via proxy " + proxy;
                }

                // Latency: first provider that answers.
                if (!result.LatencyMs.HasValue)
                {
                    progress.Report(new SpeedProgress { Phase = "latency" });
                    try
                    {
                        await MeasureLatencyAsync(result, provider);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(provider.Name + ": " + Friendly(ex));
                        continue; // cannot reach this provider at all; try the next
                    }
                }

                if (!result.DownloadMbps.HasValue)
                {
                    progress.Report(new SpeedProgress { Phase = "download" });
                    try
                    {
                        result.DownloadMbps = await MeasureAsync(provider, true, progress);
                        result.DownloadSource = provider.Name;
                        result.DownloadError = null;
                    }
                    catch (Exception ex)
                    {
                        result.DownloadError = Friendly(ex);
                        failures.Add(provider.Name + " download: " + result.DownloadError);
                    }
                }

                if (!result.UploadMbps.HasValue && provider.Upload != null)
                {
                    progress.Report(new SpeedProgress { Phase = "upload" });
                    try
                    {
                        result.UploadMbps = await MeasureAsync(provider, false, progress);
                        result.UploadSource = provider.Name;
                        result.UploadError = null;
                    }
                    catch (Exception ex)
                    {
                        result.UploadError = Friendly(ex);
                        failures.Add(provider.Name + " upload: " + result.UploadError);
                    }
                }
            }

            if (!result.LatencyMs.HasValue && !result.DownloadMbps.HasValue && !result.UploadMbps.HasValue)
            {
                result.Error = failures.Count > 0 ? "No speed test server reachable · " + string.Join(" · ", failures) : "No speed test server configured";
                return result;
            }
            if (!result.UploadMbps.HasValue && result.UploadError == null) result.UploadError = "No upload target configured";

            // Say plainly what was refused before a fallback was used, so the numbers are read in context.
            result.Notes.AddRange(failures);
            return result;
        }

        // ================================================================ providers

        private async Task<ResolvedProvider> ResolveAsync(SpeedProviderConfig config)
        {
            if (config.Kind == "librespeed") return await ResolveLibreSpeedAsync(config);

            if (string.IsNullOrWhiteSpace(config.DownloadUrl)) throw new InvalidOperationException("no downloadUrl");
            var download = new Uri(config.DownloadUrl);
            Uri upload = string.IsNullOrWhiteSpace(config.UploadUrl) ? null : new Uri(config.UploadUrl);
            return new ResolvedProvider
            {
                Name = string.IsNullOrWhiteSpace(config.Name) ? download.Host : config.Name,
                Latency = new Uri(string.IsNullOrWhiteSpace(config.LatencyUrl) ? config.DownloadUrl : config.LatencyUrl),
                Download = () => download,
                Upload = upload == null ? (Func<Uri>)null : () => upload,
                UploadChunk = Math.Max(256 * 1024, config.UploadChunkBytes ?? 2000000),
                ServerFromRay = true
            };
        }

        /// <summary>
        /// Picks the lowest-latency server among the preferred LibreSpeed servers.
        /// Server list format: [{ name, server, dlURL, ulURL, pingURL }, ...].
        /// </summary>
        private async Task<ResolvedProvider> ResolveLibreSpeedAsync(SpeedProviderConfig config)
        {
            var servers = new List<LibreServer>();
            if (!string.IsNullOrWhiteSpace(config.ServerList))
            {
                try
                {
                    string json;
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                    using (HttpResponseMessage response = await _client.GetAsync(config.ServerList, cts.Token))
                    {
                        EnsureAccepted(response, "LibreSpeed");
                        json = await response.Content.ReadAsStringAsync();
                    }
                    servers.AddRange(LibreServer.ParseList(json));
                }
                catch (Exception)
                {
                    // fall through to the fixed fallback servers
                }
            }
            if (config.FallbackServers != null)
                foreach (string url in config.FallbackServers)
                    if (!servers.Any(s => s.Base == LibreServer.Normalise(url)))
                        servers.Add(LibreServer.Fixed(url));

            List<string> prefer = config.PreferServers ?? new List<string>();
            List<LibreServer> candidates = prefer.Count == 0
                ? servers.Take(4).ToList()
                : prefer.SelectMany(p => servers.Where(s => s.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                        .Distinct().Take(4).ToList();
            if (candidates.Count == 0) throw new InvalidOperationException("no LibreSpeed servers found");

            // Ping candidates in parallel; keep the quickest that answers.
            var timed = await Task.WhenAll(candidates.Select(async s =>
            {
                var clock = Stopwatch.StartNew();
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    using (HttpResponseMessage r = await _client.GetAsync(s.Ping(), HttpCompletionOption.ResponseHeadersRead, cts.Token))
                    {
                        if (!r.IsSuccessStatusCode) return new { Server = s, Ms = double.MaxValue };
                        return new { Server = s, Ms = clock.Elapsed.TotalMilliseconds };
                    }
                }
                catch (Exception)
                {
                    return new { Server = s, Ms = double.MaxValue };
                }
            }));

            var best = timed.OrderBy(t => t.Ms).First();
            if (best.Ms == double.MaxValue) throw new InvalidOperationException("no LibreSpeed server answered");

            LibreServer chosen = best.Server;
            // "London, England (Clouvider)" -> "London (Clouvider)"
            string city = System.Text.RegularExpressions.Regex.Replace(chosen.Name, @",[^(]*(?=\()", " ").Replace("  ", " ").Trim();
            string label = (string.IsNullOrWhiteSpace(config.Name) ? "LibreSpeed" : config.Name) + " " + city;
            return new ResolvedProvider
            {
                Name = label,
                Latency = chosen.Ping(),
                Download = () => chosen.Download(100),   // 100 MiB per request
                Upload = () => chosen.Upload(),
                UploadChunk = Math.Max(256 * 1024, config.UploadChunkBytes ?? 2000000),
                ServerFromRay = false
            };
        }

        // ================================================================ latency

        private async Task MeasureLatencyAsync(SpeedResult result, ResolvedProvider provider)
        {
            var samples = new List<double>();
            string server = null;
            for (int i = 0; i < 8; i++)
            {
                var timer = Stopwatch.StartNew();
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                using (HttpResponseMessage response = await _client.GetAsync(provider.Latency, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                {
                    timer.Stop();
                    EnsureAccepted(response, provider.Name);
                    if (i == 0)
                    {
                        if (provider.ServerFromRay) server = DataCentre(response);
                        continue; // first request includes TCP + TLS set-up
                    }
                    samples.Add(timer.Elapsed.TotalMilliseconds);
                }
            }

            // Cloudflare's name gains its data centre code, e.g. "Cloudflare LHR".
            if (server != null) provider.Name = provider.Name + " " + server;

            samples.Sort();
            result.LatencyMs = samples[samples.Count / 2];
            double jitter = 0;
            for (int i = 1; i < samples.Count; i++) jitter += Math.Abs(samples[i] - samples[i - 1]);
            result.JitterMs = jitter / Math.Max(1, samples.Count - 1);
            result.LatencySource = provider.Name;
        }

        /// <summary>Cloudflare's CF-RAY header ends with the data centre code, e.g. "...-LHR".</summary>
        private static string DataCentre(HttpResponseMessage response)
        {
            IEnumerable<string> values;
            if (response.Headers.TryGetValues("CF-RAY", out values))
            {
                string ray = values.FirstOrDefault() ?? "";
                int dash = ray.LastIndexOf('-');
                if (dash > 0 && dash < ray.Length - 1) return ray.Substring(dash + 1);
            }
            return null;
        }

        // ================================================================ throughput

        private async Task<double?> MeasureAsync(ResolvedProvider provider, bool download, IProgress<SpeedProgress> progress)
        {
            long bytes = 0;
            long cap = download ? DownloadCap : UploadCap;
            var errors = new List<Exception>();
            var stop = new CancellationTokenSource();
            var clock = Stopwatch.StartNew();

            byte[] payload = null;
            if (!download)
            {
                // Random bytes so a compressing proxy cannot shrink the payload.
                payload = new byte[provider.UploadChunk];
                new Random().NextBytes(payload);
            }

            Func<Task> worker = async () =>
            {
                var buffer = new byte[81920];
                while (!stop.IsCancellationRequested && Interlocked.Read(ref bytes) < cap)
                {
                    try
                    {
                        if (download)
                        {
                            using (HttpResponseMessage response = await _client.GetAsync(provider.Download(), HttpCompletionOption.ResponseHeadersRead, stop.Token))
                            using (stop.Token.Register(() => response.Dispose())) // abort an in-flight read when time is up
                            {
                                EnsureAccepted(response, provider.Name);
                                using (var stream = await response.Content.ReadAsStreamAsync())
                                {
                                    int read;
                                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                        if (Interlocked.Add(ref bytes, read) >= cap) break;
                                }
                            }
                        }
                        else
                        {
                            var content = new ByteArrayContent(payload);
                            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                            using (HttpResponseMessage response = await _client.PostAsync(provider.Upload(), content, stop.Token))
                            {
                                EnsureAccepted(response, provider.Name);
                                // Counted on completion: the body may be buffered, so bytes "written" are not bytes sent.
                                Interlocked.Add(ref bytes, payload.Length);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (stop.IsCancellationRequested) return;
                        lock (errors) errors.Add(ex);
                        if (ex is SpeedTestRefusedException || errors.Count > 6) return;
                    }
                }
            };

            Task[] workers = Enumerable.Range(0, Streams).Select(_ => Task.Run(worker)).ToArray();
            Sample sample = await SampleAsync(clock, () => Interlocked.Read(ref bytes), cap, workers, download ? "download" : "upload", progress);
            stop.Cancel();
            await Task.WhenAny(Task.WhenAll(workers), Task.Delay(3000));

            // A provider that cut us off before a steady reading gives no trustworthy figure: let the next one try.
            Exception refusal = errors.FirstOrDefault(e => e is SpeedTestRefusedException);
            if (sample.Mbps == null || (refusal != null && sample.Window < 2))
                throw refusal ?? errors.FirstOrDefault() ?? new InvalidOperationException("no data transferred");
            return sample.Mbps;
        }

        private class Sample
        {
            public double? Mbps;
            public double Window;   // seconds measured after warm-up
        }

        /// <summary>
        /// Polls the byte counter every 100 ms. Stops at the time limit, the data cap, or once the
        /// rate is stable (three consecutive 1 s readings within 5%) after at least 3 s of measuring.
        /// Returns the average rate from the end of warm-up.
        /// </summary>
        private async Task<Sample> SampleAsync(Stopwatch clock, Func<long> read, long cap, Task[] workers, string phase, IProgress<SpeedProgress> progress)
        {
            var times = new List<double>();
            var totals = new List<long>();
            double warmAt = 0;
            long warmBytes = 0;
            bool warmed = false;
            double? latest = null;

            while (true)
            {
                await Task.Delay(100);
                double t = clock.Elapsed.TotalSeconds;
                long b = read();
                times.Add(t);
                totals.Add(b);

                if (!warmed && (t >= WarmUpSeconds || b >= cap * 0.1))
                {
                    warmed = true;
                    warmAt = t;
                    warmBytes = b;
                }

                double mbps = warmed && t - warmAt > 0.3
                    ? (b - warmBytes) * 8 / (t - warmAt) / 1e6
                    : b * 8 / t / 1e6;
                if (b > 0) latest = mbps;

                double fraction = Math.Min(1, Math.Max(t / Seconds, (double)b / cap));
                progress.Report(new SpeedProgress { Phase = phase, Mbps = latest ?? 0, Fraction = fraction });

                if (t >= Seconds || b >= cap) break;
                if (workers.All(w => w.IsCompleted)) break; // every stream stopped (cap reached or gave up)
                if (warmed && t - warmAt >= MinMeasureSeconds && IsStable(times, totals, t)) break;
            }

            return new Sample { Mbps = latest, Window = warmed ? clock.Elapsed.TotalSeconds - warmAt : 0 };
        }

        private static bool IsStable(List<double> times, List<long> totals, double now)
        {
            double a = RateBetween(times, totals, now - 3, now - 2);
            double b = RateBetween(times, totals, now - 2, now - 1);
            double c = RateBetween(times, totals, now - 1, now);
            double max = Math.Max(a, Math.Max(b, c)), min = Math.Min(a, Math.Min(b, c));
            return min > 0 && (max - min) / max <= StableWithin;
        }

        private static double RateBetween(List<double> times, List<long> totals, double from, double to)
        {
            int i = times.FindLastIndex(t => t <= from);
            int j = times.FindLastIndex(t => t <= to);
            if (i < 0 || j <= i) return 0;
            return (totals[j] - totals[i]) * 8 / (times[j] - times[i]) / 1e6;
        }

        // ================================================================ errors

        private static void EnsureAccepted(HttpResponseMessage response, string provider)
        {
            int code = (int)response.StatusCode;
            if (code >= 200 && code < 300) return;

            if (code == 429)
            {
                string wait = "";
                if (response.Headers.RetryAfter != null && response.Headers.RetryAfter.Delta.HasValue)
                {
                    int minutes = (int)Math.Ceiling(response.Headers.RetryAfter.Delta.Value.TotalMinutes);
                    wait = minutes <= 1 ? ", about a minute" : ", about " + minutes + " min";
                }
                throw new SpeedTestRefusedException("rate-limited (HTTP 429" + wait + ")");
            }
            if (code == 407) throw new SpeedTestRefusedException("proxy asked for credentials (HTTP 407)");
            if (code == 403) throw new SpeedTestRefusedException("blocked (HTTP 403), by the proxy or " + provider);
            throw new SpeedTestRefusedException("HTTP " + code);
        }

        private static string Friendly(Exception ex)
        {
            var refused = ex as SpeedTestRefusedException;
            if (refused != null) return refused.Message;
            if (ex is InvalidOperationException) return ex.Message;
            return EndpointChecker.Describe(ex);
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }

    /// <summary>One LibreSpeed backend. URLs are relative to Base unless absolute.</summary>
    internal class LibreServer
    {
        public string Name;
        public string Base;
        public string DlPath = "garbage.php";
        public string UlPath = "empty.php";
        public string PingPath = "empty.php";

        public Uri Download(int chunkMiB) { return Build(DlPath, "ckSize=" + chunkMiB); }
        public Uri Upload() { return Build(UlPath, null); }
        public Uri Ping() { return Build(PingPath, null); }

        // A unique query on every request so the proxy cannot serve a cached copy.
        private Uri Build(string path, string query)
        {
            string url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : Base + "/" + path.TrimStart('/');
            string bust = "r=" + Guid.NewGuid().ToString("N");
            url += (url.Contains("?") ? "&" : "?") + (query == null ? bust : query + "&" + bust);
            return new Uri(url);
        }

        public static string Normalise(string server)
        {
            server = (server ?? "").Trim();
            if (server.StartsWith("//")) server = "https:" + server;
            return server.TrimEnd('/');
        }

        public static LibreServer Fixed(string baseUrl)
        {
            string normal = Normalise(baseUrl);
            return new LibreServer { Name = new Uri(normal).Host, Base = normal };
        }

        public static IEnumerable<LibreServer> ParseList(string json)
        {
            var list = new JavaScriptSerializer().DeserializeObject(json) as object[];
            if (list == null) yield break;
            foreach (object entry in list)
            {
                var map = entry as Dictionary<string, object>;
                if (map == null || !map.ContainsKey("server")) continue;
                string server = Normalise(map["server"] as string);
                if (!server.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue; // HTTPS only
                yield return new LibreServer
                {
                    Name = (map.ContainsKey("name") ? map["name"] as string : null) ?? new Uri(server).Host,
                    Base = server,
                    DlPath = (map.ContainsKey("dlURL") ? map["dlURL"] as string : null) ?? "garbage.php",
                    UlPath = (map.ContainsKey("ulURL") ? map["ulURL"] as string : null) ?? "empty.php",
                    PingPath = (map.ContainsKey("pingURL") ? map["pingURL"] as string : null) ?? "empty.php"
                };
            }
        }
    }
}
