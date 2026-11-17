// Download / upload / latency test. Default target: Cloudflare's public speed test
// endpoints (the ones speed.cloudflare.com uses; no sign-up). Traffic goes through the
// system proxy, so the result reflects the path the user's apps actually take.
//
// Method: parallel streams until either the time limit or the data cap is reached.
// The opening phase (first second, or first 15% of the cap) is left out of the figure
// so TCP slow start and proxy set-up do not drag it down.
//
// Cloudflare rate-limits by volume per public IP. Behind a shared proxy every VM shares
// that allowance, so the data cap matters; a 429 is reported plainly with the wait time.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AvdHealthCheck
{
    public class SpeedResult
    {
        public double? DownloadMbps { get; set; }
        public double? UploadMbps { get; set; }
        public double? LatencyMs { get; set; }
        public double? JitterMs { get; set; }
        public string Provider { get; set; }
        public string Server { get; set; }
        public string Route { get; set; }
        public string Error { get; set; }          // whole test could not start
        public string DownloadError { get; set; }
        public string UploadError { get; set; }

        public bool AnyResult { get { return DownloadMbps.HasValue || UploadMbps.HasValue || LatencyMs.HasValue; } }
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

        private int Seconds { get { return Math.Max(3, _config.Seconds ?? 6); } }
        private int Streams { get { return Math.Max(1, Math.Min(8, _config.Streams ?? 4)); } }
        private long DownloadCap { get { return Math.Max(10, _config.MaxDownloadMB ?? 150) * 1000000L; } }
        private long UploadCap { get { return Math.Max(4, _config.MaxUploadMB ?? 40) * 1000000L; } }
        public string Provider { get { return string.IsNullOrWhiteSpace(_config.Provider) ? "speed test server" : _config.Provider; } }

        public async Task<SpeedResult> RunAsync(IProgress<SpeedProgress> progress)
        {
            var result = new SpeedResult { Provider = Provider };
            Uri probe = new Uri(_config.LatencyUrl ?? _config.DownloadUrl);
            string proxy = SystemProxy.Describe(SystemProxy.Create(), probe);
            result.Route = proxy == null ? "direct" : "via proxy " + proxy;

            if (!string.IsNullOrWhiteSpace(_config.LatencyUrl))
            {
                try
                {
                    await MeasureLatencyAsync(result, new Uri(_config.LatencyUrl));
                }
                catch (SpeedTestRefusedException ex)
                {
                    result.Error = ex.Message;
                    return result;
                }
                catch (Exception ex)
                {
                    result.Error = Provider + " unreachable: " + EndpointChecker.Describe(ex);
                    return result;
                }
            }

            progress.Report(new SpeedProgress { Phase = "download" });
            try { result.DownloadMbps = await MeasureDownloadAsync(progress); }
            catch (Exception ex) { result.DownloadError = Friendly(ex); }

            if (!string.IsNullOrWhiteSpace(_config.UploadUrl))
            {
                progress.Report(new SpeedProgress { Phase = "upload" });
                try { result.UploadMbps = await MeasureUploadAsync(progress); }
                catch (Exception ex) { result.UploadError = Friendly(ex); }
            }
            else
            {
                result.UploadError = "No upload target configured";
            }
            return result;
        }

        // ---------------------------------------------------------------- latency

        private async Task MeasureLatencyAsync(SpeedResult result, Uri uri)
        {
            var samples = new List<double>();
            for (int i = 0; i < 8; i++)
            {
                var timer = Stopwatch.StartNew();
                using (HttpResponseMessage response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
                {
                    timer.Stop();
                    EnsureAccepted(response);
                    if (i == 0)
                    {
                        result.Server = ServerLocation(response);
                        continue; // first request includes TCP + TLS set-up
                    }
                    samples.Add(timer.Elapsed.TotalMilliseconds);
                }
            }

            samples.Sort();
            result.LatencyMs = samples[samples.Count / 2];
            double jitter = 0;
            for (int i = 1; i < samples.Count; i++) jitter += Math.Abs(samples[i] - samples[i - 1]);
            result.JitterMs = jitter / Math.Max(1, samples.Count - 1);
        }

        /// <summary>Cloudflare's CF-RAY header ends with the data centre code, e.g. "...-LHR".</summary>
        private static string ServerLocation(HttpResponseMessage response)
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

        // ---------------------------------------------------------------- download

        private async Task<double?> MeasureDownloadAsync(IProgress<SpeedProgress> progress)
        {
            long bytes = 0;
            long cap = DownloadCap;
            var errors = new List<Exception>();
            var stop = new CancellationTokenSource();
            var clock = Stopwatch.StartNew();
            var uri = new Uri(_config.DownloadUrl);

            Func<Task> worker = async () =>
            {
                var buffer = new byte[81920];
                while (!stop.IsCancellationRequested && Interlocked.Read(ref bytes) < cap)
                {
                    try
                    {
                        using (HttpResponseMessage response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, stop.Token))
                        using (stop.Token.Register(() => response.Dispose())) // abort an in-flight read when time is up
                        {
                            EnsureAccepted(response);
                            using (var stream = await response.Content.ReadAsStreamAsync())
                            {
                                int read;
                                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    if (Interlocked.Add(ref bytes, read) >= cap) break;
                                }
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
            double? mbps = await SampleAsync(clock, () => Interlocked.Read(ref bytes), cap, workers, "download", progress);
            stop.Cancel();
            await Task.WhenAny(Task.WhenAll(workers), Task.Delay(3000));

            if (mbps == null && errors.Count > 0) throw errors[0];
            return mbps;
        }

        // ---------------------------------------------------------------- upload

        private async Task<double?> MeasureUploadAsync(IProgress<SpeedProgress> progress)
        {
            long bytes = 0;
            long cap = UploadCap;
            var errors = new List<Exception>();
            var stop = new CancellationTokenSource();
            var clock = Stopwatch.StartNew();
            var uri = new Uri(_config.UploadUrl);

            // Random bytes so a compressing proxy cannot shrink the payload.
            var payload = new byte[Math.Max(256 * 1024, _config.UploadChunkBytes ?? 2000000)];
            new Random().NextBytes(payload);

            Func<Task> worker = async () =>
            {
                while (!stop.IsCancellationRequested && Interlocked.Read(ref bytes) < cap)
                {
                    try
                    {
                        var content = new ByteArrayContent(payload);
                        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                        using (HttpResponseMessage response = await _client.PostAsync(uri, content, stop.Token))
                        {
                            EnsureAccepted(response);
                            // Counted on completion: the request body may be buffered, so bytes "written" are not bytes sent.
                            Interlocked.Add(ref bytes, payload.Length);
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
            double? mbps = await SampleAsync(clock, () => Interlocked.Read(ref bytes), cap, workers, "upload", progress);
            stop.Cancel();
            await Task.WhenAny(Task.WhenAll(workers), Task.Delay(3000));

            if (mbps == null && errors.Count > 0) throw errors[0];
            return mbps;
        }

        // ---------------------------------------------------------------- sampling

        /// <summary>Polls the byte counter until time is up or the cap is hit; returns Mbps after warm-up.</summary>
        private async Task<double?> SampleAsync(Stopwatch clock, Func<long> read, long cap, Task[] workers, string phase, IProgress<SpeedProgress> progress)
        {
            double warmAt = 0;
            long warmBytes = 0;
            bool warmed = false;
            double? latest = null;

            while (true)
            {
                await Task.Delay(100);
                double t = clock.Elapsed.TotalSeconds;
                long b = read();

                if (!warmed && (t >= 1.0 || b >= cap * 0.15))
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
            }

            return latest;
        }

        // ---------------------------------------------------------------- errors

        private void EnsureAccepted(HttpResponseMessage response)
        {
            int code = (int)response.StatusCode;
            if (code >= 200 && code < 300) return;

            if (code == 429)
            {
                string wait = "";
                if (response.Headers.RetryAfter != null && response.Headers.RetryAfter.Delta.HasValue)
                {
                    int minutes = (int)Math.Ceiling(response.Headers.RetryAfter.Delta.Value.TotalMinutes);
                    wait = minutes <= 1 ? " Try again in a minute." : " Try again in about " + minutes + " min.";
                }
                throw new SpeedTestRefusedException(Provider + " is rate-limiting this network's public IP (shared by everyone behind the proxy)." + wait);
            }
            if (code == 407) throw new SpeedTestRefusedException("The proxy asked for credentials (HTTP 407)");
            if (code == 403) throw new SpeedTestRefusedException("Blocked (HTTP 403), probably by the proxy or " + Provider);
            throw new SpeedTestRefusedException(Provider + " returned HTTP " + code);
        }

        private static string Friendly(Exception ex)
        {
            var refused = ex as SpeedTestRefusedException;
            return refused != null ? refused.Message : EndpointChecker.Describe(ex);
        }

        public void Dispose()
        {
            _client.Dispose();
        }
    }
}
