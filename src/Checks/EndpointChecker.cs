// Connectivity checks.
//   https / http : a real request through the system proxy (PAC/WPAD aware, signed-in
//                  user's credentials). Any HTTP status from the service counts as reachable.
//                  The TLS issuer is recorded, so a proxy re-signing traffic is reported.
//   tcp          : a direct TCP connect (cannot go through a web proxy).
//   imds         : Azure Instance Metadata Service, always direct, never via a proxy.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AvdHealthCheck
{
    public sealed class EndpointChecker : IDisposable
    {
        public const string ImdsUrl = "http://169.254.169.254/metadata/instance?api-version=2021-02-01";

        private readonly HttpClient _proxied;
        private readonly HttpClient _direct;
        private readonly IWebProxy _proxy;
        private readonly ConcurrentDictionary<string, string> _issuers = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _publicIssuers;
        private readonly int _slowMs;

        public EndpointChecker(IEnumerable<string> publicIssuers, int slowMs)
        {
            _publicIssuers = new HashSet<string>(publicIssuers ?? new string[0], StringComparer.OrdinalIgnoreCase);
            _slowMs = slowMs > 0 ? slowMs : 1500;

            _proxy = SystemProxy.Create();
            var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = _proxy,
                AllowAutoRedirect = false,
                UseCookies = false
            };
            handler.ServerCertificateCustomValidationCallback = ValidateCertificate;
            _proxied = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            _proxied.DefaultRequestHeaders.UserAgent.ParseAdd("AVDHealthCheck/" + AppInfo.Version);

            var direct = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
            _direct = new HttpClient(direct) { Timeout = TimeSpan.FromSeconds(4) };
        }

        public async Task RunAsync(CheckItem item)
        {
            item.State = CheckState.Running;
            item.Detail = "Checking";
            try
            {
                switch (item.Def.Kind)
                {
                    case "tcp": await CheckTcpAsync(item); break;
                    case "imds": await CheckImdsAsync(item); break;
                    case "http": await CheckHttpAsync(item, false); break;
                    default: await CheckHttpAsync(item, true); break;
                }
            }
            catch (Exception ex)
            {
                Finish(item, CheckState.Fail, Describe(ex), null);
            }

            // An optional endpoint failing is worth knowing about, but it is not a fault.
            if (item.State == CheckState.Fail && !item.Required)
            {
                item.State = CheckState.Warn;
                item.Detail = "Optional · " + item.Detail;
            }
        }

        // ---------------------------------------------------------------- HTTP(S)

        private async Task CheckHttpAsync(CheckItem item, bool tls)
        {
            EndpointDef def = item.Def;
            int defaultPort = tls ? 443 : 80;
            string url = (tls ? "https://" : "http://") + def.Host +
                         (def.EffectivePort != defaultPort ? ":" + def.EffectivePort : "") +
                         (string.IsNullOrEmpty(def.Path) ? "/" : def.Path);
            var uri = new Uri(url);
            string proxyName = SystemProxy.Describe(_proxy, uri);

            var timer = Stopwatch.StartNew();
            HttpResponseMessage response = null;
            Exception error = null;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                {
                    response = await _proxied.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
            timer.Stop();

            string issuer;
            _issuers.TryGetValue(def.Host, out issuer);
            bool inspected = tls && issuer != null && !IsPublicIssuer(issuer);
            string route = proxyName == null ? "direct" : "via proxy " + proxyName;

            if (error != null)
            {
                string reason = Describe(error);
                if (inspected) reason += " · certificate issued by " + issuer + " (TLS inspection)";
                Finish(item, CheckState.Fail, reason + " · " + route, null);
                return;
            }

            using (response)
            {
                int code = (int)response.StatusCode;
                long ms = timer.ElapsedMilliseconds;

                if (code == 407)
                {
                    Finish(item, CheckState.Fail, "Proxy asked for credentials (HTTP 407) · " + route, ms);
                    return;
                }

                var parts = new List<string> { "HTTP " + code };
                CheckState state = CheckState.Pass;

                if (inspected)
                {
                    state = CheckState.Warn;
                    parts.Add("TLS inspected by " + issuer);
                    if (code == 403) parts.Add("403 may be a proxy block page");
                }
                else if (tls && issuer != null)
                {
                    parts.Add("cert " + issuer);
                }

                if (ms > _slowMs && state == CheckState.Pass)
                {
                    state = CheckState.Warn;
                    parts.Add("slow response");
                }

                parts.Add(route);
                Finish(item, state, string.Join(" · ", parts), ms);
            }
        }

        private bool ValidateCertificate(HttpRequestMessage request, X509Certificate2 cert, X509Chain chain, SslPolicyErrors errors)
        {
            if (cert != null && request != null && request.RequestUri != null)
                _issuers[request.RequestUri.Host] = IssuerOrganisation(cert);
            return errors == SslPolicyErrors.None;
        }

        private bool IsPublicIssuer(string issuer)
        {
            return _publicIssuers.Contains(issuer) || _publicIssuers.Any(p => issuer.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        private static string IssuerOrganisation(X509Certificate2 cert)
        {
            Match org = Regex.Match(cert.Issuer, @"(?:^|,\s*)O=(""[^""]*""|[^,]*)");
            if (org.Success) return org.Groups[1].Value.Trim('"', ' ');
            return cert.GetNameInfo(X509NameType.SimpleName, true);
        }

        // ---------------------------------------------------------------- TCP

        private async Task CheckTcpAsync(CheckItem item)
        {
            EndpointDef def = item.Def;
            IPAddress address;
            if (!IPAddress.TryParse(def.Host, out address))
            {
                IPAddress[] addresses;
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(def.Host);
                }
                catch (Exception)
                {
                    Finish(item, CheckState.Fail, "DNS lookup failed for " + def.Host, null);
                    return;
                }
                address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
                if (address == null)
                {
                    Finish(item, CheckState.Fail, "DNS returned no addresses", null);
                    return;
                }
            }

            var timer = Stopwatch.StartNew();
            using (var client = new TcpClient(address.AddressFamily))
            {
                Task connect = client.ConnectAsync(address, def.EffectivePort);
                Task finished = await Task.WhenAny(connect, Task.Delay(5000));
                timer.Stop();
                if (finished != connect)
                {
                    Observe(connect);
                    Finish(item, CheckState.Fail, "No answer within 5 s on TCP " + def.EffectivePort + " · " + address + " (direct)", null);
                    return;
                }
                if (connect.IsFaulted)
                {
                    Finish(item, CheckState.Fail, Describe(connect.Exception) + " · " + address + " (direct)", null);
                    return;
                }
                Finish(item, CheckState.Pass, "TCP " + def.EffectivePort + " open · " + address + " (direct)", timer.ElapsedMilliseconds);
            }
        }

        // ---------------------------------------------------------------- IMDS

        private async Task CheckImdsAsync(CheckItem item)
        {
            var timer = Stopwatch.StartNew();
            using (var request = new HttpRequestMessage(HttpMethod.Get, ImdsUrl))
            {
                request.Headers.Add("Metadata", "true");
                try
                {
                    using (HttpResponseMessage response = await _direct.SendAsync(request))
                    {
                        timer.Stop();
                        if (!response.IsSuccessStatusCode)
                        {
                            Finish(item, CheckState.Fail, "HTTP " + (int)response.StatusCode + " from metadata service", timer.ElapsedMilliseconds);
                            return;
                        }
                        string body = await response.Content.ReadAsStringAsync();
                        AzureVm vm = AzureVm.Parse(body);
                        string summary = vm == null ? "Metadata returned" : vm.Size + " · " + vm.Location;
                        Finish(item, CheckState.Pass, "Azure VM · " + summary + " (direct)", timer.ElapsedMilliseconds);
                    }
                }
                catch (Exception ex)
                {
                    Finish(item, CheckState.Fail, Describe(ex) + " · not an Azure VM, or metadata is blocked", null);
                }
            }
        }

        // ---------------------------------------------------------------- helpers

        private static void Finish(CheckItem item, CheckState state, string detail, long? ms)
        {
            item.State = state;
            item.Detail = detail;
            item.Latency = ms.HasValue ? ms.Value + " ms" : "";
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>Turns a network exception into a short reason an engineer can act on.</summary>
        public static string Describe(Exception ex)
        {
            var aggregate = ex as AggregateException;
            if (aggregate != null && aggregate.InnerExceptions.Count > 0) ex = aggregate.InnerExceptions[0];
            if (ex is TaskCanceledException) return "Timed out";

            Exception inner = ex;
            while (inner != null)
            {
                var web = inner as WebException;
                if (web != null)
                {
                    switch (web.Status)
                    {
                        case WebExceptionStatus.NameResolutionFailure: return "DNS lookup failed";
                        case WebExceptionStatus.ProxyNameResolutionFailure: return "Could not resolve the proxy";
                        case WebExceptionStatus.ConnectFailure: return "Connection refused or blocked";
                        case WebExceptionStatus.TrustFailure: return "Certificate not trusted";
                        case WebExceptionStatus.SecureChannelFailure: return "TLS handshake failed";
                        case WebExceptionStatus.Timeout: return "Timed out";
                        case WebExceptionStatus.ConnectionClosed:
                        case WebExceptionStatus.ReceiveFailure:
                        case WebExceptionStatus.SendFailure: return "Connection dropped";
                    }
                }

                var socket = inner as SocketException;
                if (socket != null)
                {
                    switch (socket.SocketErrorCode)
                    {
                        case SocketError.ConnectionRefused: return "Connection refused";
                        case SocketError.TimedOut: return "Timed out";
                        case SocketError.HostUnreachable:
                        case SocketError.NetworkUnreachable: return "No route to host";
                        case SocketError.AccessDenied: return "Blocked by local firewall";
                        case SocketError.HostNotFound: return "DNS lookup failed";
                    }
                }

                if (inner.InnerException == null) return Shorten(inner.Message);
                inner = inner.InnerException;
            }
            return Shorten(ex.Message);
        }

        private static string Shorten(string message)
        {
            if (string.IsNullOrEmpty(message)) return "Failed";
            message = message.Trim().TrimEnd('.');
            return message.Length > 120 ? message.Substring(0, 117) + "…" : message;
        }

        public void Dispose()
        {
            _proxied.Dispose();
            _direct.Dispose();
        }
    }

    /// <summary>The Windows proxy settings (PAC, WPAD or fixed) with the signed-in user's credentials.</summary>
    public static class SystemProxy
    {
        public static IWebProxy Create()
        {
            IWebProxy proxy = WebRequest.GetSystemWebProxy();
            proxy.Credentials = CredentialCache.DefaultCredentials;
            return proxy;
        }

        /// <summary>Returns "host:port" of the proxy used for a URL, or null when it goes direct.</summary>
        public static string Describe(IWebProxy proxy, Uri uri)
        {
            try
            {
                if (proxy == null || proxy.IsBypassed(uri)) return null;
                Uri via = proxy.GetProxy(uri);
                if (via == null || via == uri || string.Equals(via.Host, uri.Host, StringComparison.OrdinalIgnoreCase)) return null;
                return via.Host + ":" + via.Port;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
