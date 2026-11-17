// Wires the runtime-loaded window to the view model and runs the checks.
// Order: device facts and endpoint checks in parallel, then the speed test on its own
// (so a saturated link does not distort the endpoint latencies).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AvdHealthCheck
{
    public sealed class MainController
    {
        private const int Parallelism = 6;

        private readonly MainViewModel _vm = new MainViewModel();
        private readonly Window _window;
        private AppConfig _config;
        private string _configSource;
        private SpeedResult _speed;
        private System.Windows.Controls.Primitives.ToggleButton _issuesToggle;
        private DateTime _startedAt;
        private TimeSpan _took;

        public MainController()
        {
            _window = (Window)XamlReader.Parse(Resources.ReadText("MainWindow.xaml"));
            _window.DataContext = _vm;
            _window.Icon = BitmapFrame.Create(Resources.Open("app.ico"));

            ((Path)_window.FindName("HorseDark")).Data = Geometry.Parse(Resources.ReadText("horse-dark.txt"));
            ((Path)_window.FindName("HorseGrey")).Data = Geometry.Parse(Resources.ReadText("horse-grey.txt"));

            ((Button)_window.FindName("RunButton")).Click += async (s, e) => await RunAllAsync();
            ((Button)_window.FindName("CopyButton")).Click += (s, e) => CopyReport();

            _issuesToggle = (System.Windows.Controls.Primitives.ToggleButton)_window.FindName("IssuesToggle");
            _issuesToggle.Checked += (s, e) => ApplyIssuesFilter(true);
            _issuesToggle.Unchecked += (s, e) => ApplyIssuesFilter(false);

            _window.KeyDown += async (s, e) =>
            {
                if (e.Key == Key.F5 && _vm.IsIdle) await RunAllAsync();
                else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _vm.IsIdle && !(Keyboard.FocusedElement is TextBox)) CopyReport();
            };

            _window.SourceInitialized += (s, e) => DarkTitleBar();
            _window.ContentRendered += async (s, e) => await RunAllAsync();

            LoadConfig();
        }

        public Window Window { get { return _window; } }

        private void LoadConfig()
        {
            _config = ConfigLoader.Load(out _configSource);

            // Local agent checks first: these are the usual suspects in an AVD incident.
            if (_config.ClientApp.Enabled ?? true)
                AddAgent("client.app", ConnectionGroup, "Client app", "how the user connected");
            if (_config.Prisma.Enabled ?? true)
            {
                string exe = string.IsNullOrWhiteSpace(_config.Prisma.ServiceExe) ? "PASrv.exe" : _config.Prisma.ServiceExe;
                List<string> adapters = _config.Prisma.AdapterMatch ?? PrismaConfig.DefaultAdapterMatch;
                AddAgent("prisma.service", PrismaGroup, "Prisma Access Agent", exe + " · service");
                AddAgent("prisma.tunnel", PrismaGroup, "Prisma tunnel", adapters.FirstOrDefault() ?? "tunnel adapter");
            }
            if (_config.Teams.Enabled ?? true)
            {
                AddAgent("teams.media", TeamsGroup, "Media optimisation", "VDI mode reported by Teams");
                AddAgent("teams.client", TeamsGroup, "New Teams", "ms-teams.exe · this session");
                AddAgent("teams.registry", TeamsGroup, "VDI registry flag", "IsWVDEnvironment");
                AddAgent("teams.webrtc", TeamsGroup, "WebRTC Redirector (fallback)", "MsRdcWebRTCSvc.exe · service");
            }

            foreach (EndpointGroup group in _config.Groups)
            {
                foreach (EndpointDef def in group.Endpoints ?? Enumerable.Empty<EndpointDef>())
                {
                    if (string.IsNullOrWhiteSpace(def.Host)) continue;
                    var item = new CheckItem
                    {
                        Def = def,
                        Group = group.Name,
                        Name = string.IsNullOrWhiteSpace(def.Name) ? def.Host : def.Name,
                        Target = def.Host + ":" + def.EffectivePort + (def.Kind == "tcp" ? " · TCP" : ""),
                        Required = def.IsRequired
                    };
                    item.Reset();
                    _vm.Checks.Add(item);
                }
            }

            ICollectionView view = CollectionViewSource.GetDefaultView(_vm.Checks);
            view.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
            _vm.ChecksView = view;

            foreach (string label in SystemInfoCollector.Labels) _vm.Device.Add(new DeviceField(label));
        }

        private const string ConnectionGroup = "Connection";
        private const string PrismaGroup = "Prisma Access";
        private const string TeamsGroup = "Teams optimisation";

        private void AddAgent(string key, string group, string name, string target)
        {
            var item = new CheckItem { Key = key, Group = group, Name = name, Target = target, Required = true };
            item.Reset();
            _vm.Checks.Add(item);
        }

        private CheckItem Agent(string key)
        {
            return _vm.Checks.FirstOrDefault(c => c.Key == key);
        }

        private static void Apply(CheckItem item, AgentResult result)
        {
            if (item == null || result == null) return;
            item.State = result.State;
            item.Detail = result.Detail;
            item.Latency = "";
        }

        private void SetAgentsRunning(string prefix)
        {
            foreach (CheckItem item in _vm.Checks.Where(c => c.Key != null && c.Key.StartsWith(prefix)))
            {
                item.State = CheckState.Running;
                item.Detail = "Checking";
            }
        }

        private async Task RunPrismaAsync()
        {
            if (Agent("prisma.service") == null) return;
            SetAgentsRunning("prisma.");
            _vm.PrismaState = CheckState.Running;
            _vm.PrismaValue = "…";
            _vm.PrismaSub = "Checking agent and tunnel";
            PrismaReport report;
            try
            {
                report = await Task.Run(() => AgentChecker.CheckPrisma(_config.Prisma));
            }
            catch (Exception ex)
            {
                report = new PrismaReport
                {
                    Service = new AgentResult(CheckState.Fail, "Could not check: " + ex.Message),
                    Tunnel = new AgentResult(CheckState.Fail, "Could not check"),
                    CardState = CheckState.Fail, CardValue = "Error", CardSub = ex.Message
                };
            }
            Apply(Agent("prisma.service"), report.Service);
            Apply(Agent("prisma.tunnel"), report.Tunnel);
            _vm.PrismaState = report.CardState;
            _vm.PrismaValue = report.CardValue;
            _vm.PrismaSub = report.CardSub;
        }

        private async Task RunClientAppAsync()
        {
            CheckItem row = Agent("client.app");
            if (row == null) return;
            row.State = CheckState.Running;
            row.Detail = "Checking";
            ClientReport report;
            try
            {
                report = await Task.Run(() => ClientDetector.Detect(_config.ClientApp.Rules));
            }
            catch (Exception ex)
            {
                report = new ClientReport { App = "Unknown", Summary = "could not check", Row = new AgentResult(CheckState.Warn, "Could not check: " + ex.Message) };
            }
            Apply(row, report.Row);
            _vm.ClientAppSummary = report.Summary;
        }

        private async Task RunTeamsAsync()
        {
            if (Agent("teams.media") == null) return;
            SetAgentsRunning("teams.");
            _vm.TeamsState = CheckState.Running;
            _vm.TeamsValue = "…";
            _vm.TeamsSub = "Checking Teams";
            TeamsReport report;
            try
            {
                report = await Task.Run(() => AgentChecker.CheckTeams(_config.Teams));
            }
            catch (Exception ex)
            {
                var failed = new AgentResult(CheckState.Fail, "Could not check: " + ex.Message);
                report = new TeamsReport { Media = failed, Client = failed, Registry = failed, WebRtc = failed,
                                           CardState = CheckState.Fail, CardValue = "Error", CardSub = ex.Message };
            }
            Apply(Agent("teams.media"), report.Media);
            Apply(Agent("teams.client"), report.Client);
            Apply(Agent("teams.registry"), report.Registry);
            Apply(Agent("teams.webrtc"), report.WebRtc);
            _vm.TeamsState = report.CardState;
            _vm.TeamsValue = report.CardValue;
            _vm.TeamsSub = report.CardSub;
        }

        // ---------------------------------------------------------------- run

        private async Task RunAllAsync()
        {
            if (_vm.IsRunning) return;
            _vm.IsRunning = true;
            _startedAt = DateTime.Now;
            var clock = Stopwatch.StartNew();
            _speed = null;

            ResetView();

            bool speedEnabled = _config.SpeedTest.Enabled ?? true;
            int totalUnits = _vm.Checks.Count(c => c.IsEndpoint) + 4 + (speedEnabled ? 3 : 0);
            int doneUnits = 0;
            Action step = () => { doneUnits++; _vm.Progress = Math.Min(99, 100.0 * doneUnits / totalUnits); };

            TaskScheduler ui = TaskScheduler.FromCurrentSynchronizationContext();
            Task device = CollectDeviceAsync().ContinueWith(t => step(), ui);
            Task prisma = RunPrismaAsync().ContinueWith(t => step(), ui);
            Task teams = RunTeamsAsync().ContinueWith(t => step(), ui);
            Task client = RunClientAppAsync().ContinueWith(t => step(), ui);
            using (var checker = new EndpointChecker(_config.PublicIssuers, _config.SlowResponseMs ?? 1500))
            {
                var gate = new SemaphoreSlim(Parallelism);
                Task[] checks = _vm.Checks.Where(c => c.IsEndpoint).Select(async item =>
                {
                    await gate.WaitAsync();
                    try { await checker.RunAsync(item); }
                    finally { gate.Release(); }
                    step();
                    UpdateConnectivitySummary(false);
                }).ToArray();

                await Task.WhenAll(checks);
            }
            await Task.WhenAll(device, prisma, teams, client);
            UpdateConnectivitySummary(true);

            if (speedEnabled) await RunSpeedTestAsync(step);
            else
            {
                _vm.DownSub = _vm.UpSub = _vm.LatSub = "Speed test turned off";
            }

            clock.Stop();
            _took = clock.Elapsed;
            _vm.Progress = 100;
            SetOverall();
            _vm.Footer = "Last run " + Format.Date(_startedAt) + " · took " + (int)Math.Round(_took.TotalSeconds) + " s · endpoints from " + _configSource + " · v" + AppInfo.Version;
            _vm.IsRunning = false;
        }

        private void ApplyIssuesFilter(bool issuesOnly)
        {
            _vm.ChecksView.Filter = issuesOnly
                ? new Predicate<object>(o => ((CheckItem)o).State == CheckState.Fail || ((CheckItem)o).State == CheckState.Warn)
                : null;
        }

        private void ResetView()
        {
            _issuesToggle.IsChecked = false;
            _vm.ClientAppSummary = null;
            _vm.HasIssues = false;
            foreach (CheckItem item in _vm.Checks) item.Reset();
            foreach (DeviceField field in _vm.Device) field.Value = "…";
            _vm.Progress = 0;
            _vm.OverallState = "Running";
            _vm.OverallText = "Running checks";
            _vm.ConnValue = "0/" + _vm.Checks.Count(c => c.IsEndpoint);
            _vm.ConnSub = "Checking endpoints";
            _vm.PrismaState = _vm.TeamsState = CheckState.Pending;
            _vm.PrismaValue = _vm.TeamsValue = "–";
            _vm.PrismaSub = _vm.TeamsSub = "Waiting";
            if (Agent("prisma.service") == null) { _vm.PrismaValue = "Off"; _vm.PrismaSub = "Turned off in endpoints.json"; }
            if (Agent("teams.media") == null) { _vm.TeamsValue = "Off"; _vm.TeamsSub = "Turned off in endpoints.json"; }
            _vm.DownValue = _vm.UpValue = _vm.LatValue = "–";
            _vm.DownSub = _vm.UpSub = _vm.LatSub = "Waiting for endpoint checks";
            _vm.Footer = "Running · endpoints from " + _configSource;
            _vm.CopyLabel = "Copy report";
        }

        private async Task CollectDeviceAsync()
        {
            DeviceInfo info;
            try
            {
                info = await Task.Run(() => SystemInfoCollector.Collect());
            }
            catch (Exception ex)
            {
                foreach (DeviceField field in _vm.Device) field.Value = "Could not read: " + ex.Message;
                return;
            }

            foreach (var pair in info.Fields)
            {
                DeviceField field = _vm.Device.FirstOrDefault(f => f.Label == pair.Key);
                if (field != null) field.Value = pair.Value;
            }
        }

        private void UpdateConnectivitySummary(bool final)
        {
            List<CheckItem> endpoints = _vm.Checks.Where(c => c.IsEndpoint).ToList();
            int total = endpoints.Count;
            int pass = endpoints.Count(c => c.State == CheckState.Pass);
            int warn = endpoints.Count(c => c.State == CheckState.Warn);
            int fail = endpoints.Count(c => c.State == CheckState.Fail);
            int done = pass + warn + fail;

            _vm.ConnValue = pass + "/" + total;
            if (!final)
            {
                _vm.ConnSub = done + " of " + total + " checked";
                return;
            }
            int allIssues = _vm.Checks.Count(c => c.State == CheckState.Fail || c.State == CheckState.Warn);
            _vm.HasIssues = allIssues > 0;
            _vm.IssuesLabel = "Show issues only (" + allIssues + ")";
            if (fail == 0 && warn == 0) _vm.ConnSub = "All endpoints reachable";
            else
            {
                string text = "";
                if (fail > 0) text += fail + " failed";
                if (warn > 0) text += (text.Length > 0 ? " · " : "") + warn + (warn == 1 ? " warning" : " warnings");
                _vm.ConnSub = text;
            }
        }

        private async Task RunSpeedTestAsync(Action step)
        {
            _vm.LatSub = "Measuring";
            _vm.DownSub = _vm.UpSub = "Waiting";
            string phase = "latency";

            var progress = new Progress<SpeedProgress>(p =>
            {
                if (p.Phase != phase)
                {
                    phase = p.Phase;
                    step();
                }
                int secondsLeft = (int)Math.Ceiling((1 - p.Fraction) * (_config.SpeedTest.Seconds ?? 6));
                if (p.Phase == "download")
                {
                    if (p.Mbps > 0) _vm.DownValue = Number(p.Mbps);
                    _vm.DownSub = "Testing · " + secondsLeft + " s";
                }
                else if (p.Phase == "upload")
                {
                    if (p.Mbps > 0) _vm.UpValue = Number(p.Mbps);
                    _vm.UpSub = "Testing · " + secondsLeft + " s";
                }
            });

            SpeedResult result;
            try
            {
                using (var tester = new SpeedTester(_config.SpeedTest))
                    result = await tester.RunAsync(progress);
            }
            catch (Exception ex)
            {
                result = new SpeedResult { Error = "Speed test failed: " + EndpointChecker.Describe(ex) };
            }
            _speed = result;
            step();

            if (result.Error != null)
            {
                _vm.DownValue = _vm.UpValue = _vm.LatValue = "–";
                _vm.DownSub = _vm.UpSub = _vm.LatSub = result.Error;
                return;
            }

            // "(fallback)" when an earlier provider refused that part, e.g. Cloudflare rate-limited.
            Func<string, string> label = source =>
                source + (result.Notes.Count > 0 && !string.Equals(source, result.LatencySource) ? " · fallback" : "") + " · " + result.Route;

            _vm.LatValue = result.LatencyMs.HasValue ? Math.Round(result.LatencyMs.Value).ToString() : "–";
            _vm.LatSub = result.LatencyMs.HasValue
                ? (result.JitterMs.HasValue ? "Jitter " + Math.Round(result.JitterMs.Value) + " ms · " : "") + result.LatencySource
                : "Not measured";
            _vm.DownValue = result.DownloadMbps.HasValue ? Number(result.DownloadMbps.Value) : "–";
            _vm.DownSub = result.DownloadMbps.HasValue ? label(result.DownloadSource) : (result.DownloadError ?? "No data received");
            _vm.UpValue = result.UploadMbps.HasValue ? Number(result.UploadMbps.Value) : "–";
            _vm.UpSub = result.UploadMbps.HasValue ? label(result.UploadSource) : (result.UploadError ?? "Upload did not complete");
        }

        private void SetOverall()
        {
            int fail = _vm.Checks.Count(c => c.State == CheckState.Fail);
            int warn = _vm.Checks.Count(c => c.State == CheckState.Warn);
            bool speedFailed = _speed != null && (_speed.DownloadMbps == null || _speed.UploadMbps == null);
            int issues = fail + warn;   // same count as "Show issues only"; the speed card speaks for itself

            if (fail > 0)
            {
                _vm.OverallState = "Fail";
                _vm.OverallText = issues == 1 ? "1 issue found" : issues + " issues found";
            }
            else if (issues > 0)
            {
                _vm.OverallState = "Warn";
                _vm.OverallText = issues == 1 ? "1 warning" : issues + " warnings";
            }
            else if (speedFailed)
            {
                _vm.OverallState = "Warn";
                _vm.OverallText = "Speed test incomplete";
            }
            else
            {
                _vm.OverallState = "Pass";
                _vm.OverallText = "All checks passed";
            }
        }

        // ---------------------------------------------------------------- copy

        private async void CopyReport()
        {
            string text = Report.Build(_vm, _speed, _startedAt, _took, _configSource);

            // The clipboard can be briefly locked by another app (or RDP clipboard sync).
            bool copied = false;
            for (int attempt = 0; attempt < 5 && !copied; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    copied = true;
                }
                catch (Exception)
                {
                    // C# 5 cannot await inside catch; wait below instead.
                }
                if (!copied) await Task.Delay(120);
            }

            _vm.CopyLabel = copied ? "Copied" : "Clipboard busy, try again";
            await Task.Delay(2200);
            _vm.CopyLabel = "Copy report";
        }

        // ---------------------------------------------------------------- window chrome

        private void DarkTitleBar()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(_window).Handle;
                int on = 1;
                NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));        // DWMWA_USE_IMMERSIVE_DARK_MODE
                int caption = 0x00050505;                                                  // COLORREF (BGR) #050505
                NativeMethods.DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));   // DWMWA_CAPTION_COLOR (Windows 11)
            }
            catch (Exception)
            {
                // Older Windows: keep the default title bar.
            }
        }

        private static string Number(double mbps)
        {
            return mbps.ToString(mbps >= 100 ? "0" : "0.0");
        }
    }
}
