// Local agent checks: Prisma Access Agent (service + tunnel) and Teams media optimisation.
// All read-only and local: WMI, registry, network adapters, and the user's own Teams logs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AvdHealthCheck
{
    public class AgentResult
    {
        public AgentResult(CheckState state, string detail) { State = state; Detail = detail; }
        public CheckState State { get; private set; }
        public string Detail { get; private set; }
    }

    public class PrismaReport
    {
        public AgentResult Service { get; set; }
        public AgentResult Tunnel { get; set; }
        public string Version { get; set; }
        public string CardValue { get; set; }
        public string CardSub { get; set; }
        public CheckState CardState { get; set; }
    }

    public class TeamsReport
    {
        public AgentResult Media { get; set; }
        public AgentResult Client { get; set; }
        public AgentResult Registry { get; set; }
        public AgentResult WebRtc { get; set; }
        public string CardValue { get; set; }
        public string CardSub { get; set; }
        public CheckState CardState { get; set; }
    }

    public static class AgentChecker
    {
        // ================================================================ Prisma Access Agent

        public static PrismaReport CheckPrisma(PrismaConfig config)
        {
            var report = new PrismaReport();
            string exe = string.IsNullOrWhiteSpace(config.ServiceExe) ? "PASrv.exe" : config.ServiceExe.Trim();

            // 1. The agent service (found by its executable, so the service name does not matter).
            ServiceInfo service = ServiceInfo.FindByExecutable(exe);
            bool running = false;
            if (service != null)
            {
                report.Version = FileVersion(service.ExePath);
                running = service.State == "Running";
                string about = service.State + Ver(report.Version) + " · service " + service.Name +
                               " (" + service.StartMode.ToLowerInvariant() + " start)";
                report.Service = new AgentResult(running ? CheckState.Pass : CheckState.Fail, about);
            }
            else
            {
                Process process = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).FirstOrDefault();
                if (process != null)
                {
                    running = true;
                    report.Version = Safe(() => FileVersion(process.MainModule.FileName));
                    report.Service = new AgentResult(CheckState.Pass, "Running" + Ver(report.Version) + " · " + exe + " (no service registered)");
                }
                else
                {
                    report.Service = new AgentResult(CheckState.Fail, "Not installed · no " + exe + " service or process");
                }
            }

            // 2. The tunnel: a Prisma virtual adapter that is up and has an address.
            report.Tunnel = CheckTunnel(config.AdapterMatch);

            // Summary card
            if (service == null && !running)
            {
                report.CardState = CheckState.Fail; report.CardValue = "Not found"; report.CardSub = exe + " is not installed";
            }
            else if (!running)
            {
                report.CardState = CheckState.Fail; report.CardValue = "Stopped"; report.CardSub = "Agent service is " + service.State.ToLowerInvariant();
            }
            else if (report.Tunnel.State != CheckState.Pass)
            {
                report.CardState = CheckState.Fail; report.CardValue = "No tunnel"; report.CardSub = "Agent running" + Ver(report.Version) + " · tunnel down";
            }
            else
            {
                report.CardState = CheckState.Pass; report.CardValue = "Connected"; report.CardSub = "Tunnel up" + Ver(report.Version);
            }
            return report;
        }

        private static AgentResult CheckTunnel(List<string> patterns)
        {
            if (patterns == null || patterns.Count == 0) patterns = PrismaConfig.DefaultAdapterMatch;
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();

            List<NetworkInterface> matches = nics.Where(n => patterns.Any(p =>
                n.Description.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();

            if (matches.Count == 0)
            {
                // Help the first rollout: show which virtual adapters do exist, so adapterMatch can be set.
                var others = nics.Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                             (n.Description.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              n.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                                              n.NetworkInterfaceType == NetworkInterfaceType.Ppp))
                                 .Select(n => n.Description).Distinct().Take(4).ToList();
                return new AgentResult(CheckState.Fail, "No tunnel · no adapter named " + string.Join(" / ", patterns) +
                    (others.Count > 0 ? " · adapters present: " + string.Join("; ", others) : ""));
            }

            NetworkInterface up = matches.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up);
            if (up == null)
                return new AgentResult(CheckState.Fail, "Disconnected · " + matches[0].Description + " is " + matches[0].OperationalStatus.ToString().ToLowerInvariant());

            string ip = up.GetIPProperties().UnicastAddresses
                          .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                          .Select(a => a.Address.ToString()).FirstOrDefault();
            if (ip == null)
                return new AgentResult(CheckState.Warn, "Up, no IPv4 address · " + up.Description);

            return new AgentResult(CheckState.Pass, "Connected · " + up.Description + " · " + ip);
        }

        // ================================================================ Teams media optimisation

        private const string TeamsPackage = "MSTeams_8wekyb3d8bbwe";
        // Matches "VDI Mode: 5200", "vdiMode":"slimcore", "currentStack=remote", "connectedStack":"remote".
        private static readonly Regex VdiMode = new Regex(@"(?:VDI Mode|vdiMode)""?\s*[:=]\s*""?([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
        private static readonly Regex Stack = new Regex(@"(?:currentStack|connectedStack)""?\s*[:=]\s*""?([A-Za-z]+)", RegexOptions.IgnoreCase);
        private static readonly Regex PackageVersion = new Regex(@"MSTeams_([\d.]+)_", RegexOptions.IgnoreCase);

        public static TeamsReport CheckTeams(TeamsConfig config)
        {
            var report = new TeamsReport();
            bool remote = NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0;

            // 1. The VDI flag new Teams reads to switch on optimisation.
            object flag = null;
            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Teams"))
                if (key != null) flag = key.GetValue("IsWVDEnvironment");
            bool flagOn = flag is int && (int)flag == 1;
            report.Registry = flagOn
                ? new AgentResult(CheckState.Pass, "Set (IsWVDEnvironment = 1)")
                : new AgentResult(CheckState.Fail, flag == null
                    ? "Missing · Teams will not optimise without it"
                    : "Wrong value · IsWVDEnvironment is " + flag + ", needs DWORD 1");

            // 2. New Teams in this user's session (multi-session hosts run one copy per user).
            int session = Process.GetCurrentProcess().SessionId;
            List<Process> teams = Process.GetProcessesByName("ms-teams").Where(p => SafeSession(p) == session).ToList();
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            bool installed = Directory.Exists(Path.Combine(localData, "Packages", TeamsPackage));
            string teamsVersion = null;
            DateTime? teamsStarted = null;
            if (teams.Count > 0)
            {
                teamsVersion = Safe(() =>
                {
                    string path = teams[0].MainModule.FileName;
                    Match m = PackageVersion.Match(path);
                    return m.Success ? m.Groups[1].Value : FileVersion(path);
                });
                teamsStarted = teams.Select(p => Safe(() => (DateTime?)p.StartTime)).Where(t => t.HasValue).Min();
                report.Client = new AgentResult(CheckState.Pass, "Running" + Ver(teamsVersion));
            }
            else if (installed)
            {
                report.Client = new AgentResult(CheckState.Warn, "Installed, not running · open Teams, then run again");
            }
            else
            {
                report.Client = new AgentResult(CheckState.Fail, "Not installed for this user");
            }

            // 3. WebRTC Redirector: Microsoft's fallback path when SlimCore is unavailable on the user's device.
            ServiceInfo webRtc = ServiceInfo.FindByExecutable("MsRdcWebRTCSvc.exe");
            if (webRtc == null)
                report.WebRtc = new AgentResult(CheckState.Warn, "Not installed · Microsoft recommends it as the fallback when SlimCore is unavailable");
            else
            {
                string version = FileVersion(webRtc.ExePath);
                report.WebRtc = new AgentResult(webRtc.State == "Running" ? CheckState.Pass : CheckState.Warn,
                    webRtc.State + Ver(version) + " · service " + webRtc.Name);
            }

            // 4. What Teams itself reports: "VDI Mode: slimcore" / "currentStack=remote" in its logs.
            TeamsLogState log = teams.Count > 0
                ? ReadTeamsLogs(Path.Combine(localData, "Packages", TeamsPackage, "LocalCache", "Microsoft", "MSTeams", "Logs"),
                                (teamsStarted ?? DateTime.Now.AddHours(-12)).AddMinutes(-1))
                : null;

            // Verdict
            if (!remote)
            {
                report.Media = new AgentResult(CheckState.Warn, "N/A · not a remote session");
                report.CardState = CheckState.Warn; report.CardValue = "N/A"; report.CardSub = "Not a remote session";
            }
            else if (!flagOn)
            {
                report.Media = new AgentResult(CheckState.Fail, "Not optimised · VDI registry flag is missing on this VM");
                report.CardState = CheckState.Fail; report.CardValue = "Not optimised"; report.CardSub = "VDI registry flag missing";
            }
            else if (teams.Count == 0)
            {
                bool absent = report.Client.State == CheckState.Fail;
                report.Media = new AgentResult(absent ? CheckState.Fail : CheckState.Warn,
                    absent ? "New Teams not installed" : "Teams not running · open Teams and run again");
                report.CardState = report.Media.State; report.CardValue = absent ? "No Teams" : "Teams closed"; report.CardSub = "Open Teams, then run again";
            }
            else
            {
                MediaStack stack = Classify(log, config);
                string mode = log != null && log.Mode != null ? "VDI mode " + log.Mode : null;
                string seen = log != null && log.Mode != null ? " · " + mode + (log.Stack != null ? ", stack " + log.Stack : "") + ", logged " + Format.Date(log.When) : "";
                switch (stack)
                {
                    case MediaStack.SlimCore:
                        report.Media = new AgentResult(CheckState.Pass, "Optimised · SlimCore, media runs on the user's device" + seen);
                        report.CardState = CheckState.Pass; report.CardValue = "Optimised"; report.CardSub = "SlimCore" + (mode != null ? " · " + mode : "");
                        break;
                    case MediaStack.Remote:
                        report.Media = new AgentResult(CheckState.Pass, "Optimised · media runs on the user's device" + seen);
                        report.CardState = CheckState.Pass; report.CardValue = "Optimised"; report.CardSub = mode ?? "Media on the user's device";
                        break;
                    case MediaStack.WebRtc:
                        report.Media = new AgentResult(CheckState.Warn, "Fallback · legacy WebRTC, not SlimCore" + seen +
                                                       " · update Windows App on the user's device so SlimCore can load");
                        report.CardState = CheckState.Warn; report.CardValue = "Fallback"; report.CardSub = "WebRTC (legacy)" + (mode != null ? " · " + mode : "");
                        break;
                    case MediaStack.Local:
                        report.Media = new AgentResult(CheckState.Fail, "Not optimised · media is running inside the VM" + seen);
                        report.CardState = CheckState.Fail; report.CardValue = "Not optimised"; report.CardSub = "Media running in the VM";
                        break;
                    default:
                        report.Media = new AgentResult(CheckState.Warn, (log != null && log.Mode != null
                            ? "Unrecognised Teams VDI mode (" + log.Mode + ") · add it to teams.slimCoreModes or teams.webRtcModes in endpoints.json"
                            : "Could not confirm from the Teams logs") + " · in Teams, Settings > About should show \"AVD SlimCore Media Optimized\"");
                        report.CardState = CheckState.Warn; report.CardValue = "Unconfirmed"; report.CardSub = mode ?? "Check Teams > Settings > About";
                        break;
                }
            }
            return report;
        }

        private enum MediaStack { Unknown, SlimCore, WebRtc, Remote, Local }

        /// <summary>
        /// Teams logs the mode as a word ("slimcore", "webrtc") or a number. The numbers are not
        /// documented; observed values fit "second digit = media stack": x2xx = SlimCore (5200 on AVD,
        /// confirmed against Teams > About; 7200 on WorkSpaces), x1xx = legacy WebRTC (3100).
        /// Exact codes in endpoints.json (teams.slimCoreModes / teams.webRtcModes) take priority.
        /// </summary>
        private static MediaStack Classify(TeamsLogState log, TeamsConfig config)
        {
            if (log == null) return MediaStack.Unknown;
            if (string.Equals(log.Stack, "local", StringComparison.OrdinalIgnoreCase)) return MediaStack.Local;

            string mode = (log.Mode ?? "").Trim();
            if (mode.Length > 0)
            {
                if (config != null && config.SlimCoreModes != null && config.SlimCoreModes.Any(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase))) return MediaStack.SlimCore;
                if (config != null && config.WebRtcModes != null && config.WebRtcModes.Any(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase))) return MediaStack.WebRtc;
                if (mode.IndexOf("slimcore", StringComparison.OrdinalIgnoreCase) >= 0) return MediaStack.SlimCore;
                if (mode.IndexOf("webrtc", StringComparison.OrdinalIgnoreCase) >= 0) return MediaStack.WebRtc;
                if (Regex.IsMatch(mode, @"^\d{4}$"))
                {
                    if (mode[1] == '2') return MediaStack.SlimCore;
                    if (mode[1] == '1') return MediaStack.WebRtc;
                }
            }
            if (string.Equals(log.Stack, "remote", StringComparison.OrdinalIgnoreCase)) return MediaStack.Remote;
            return MediaStack.Unknown;
        }

        private class TeamsLogState
        {
            public string Mode;
            public string Stack;
            public DateTime When;
        }

        /// <summary>Newest "VDI Mode" / "currentStack" values from Teams logs written since Teams started.</summary>
        private static TeamsLogState ReadTeamsLogs(string folder, DateTime since)
        {
            if (!Directory.Exists(folder)) return null;
            IEnumerable<FileInfo> files = new DirectoryInfo(folder).GetFiles("*.log", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTime >= since)
                .OrderByDescending(f => f.LastWriteTime)
                .Take(8);

            var state = new TeamsLogState();
            foreach (FileInfo file in files)
            {
                string text = Safe(() => ReadTail(file.FullName, 4 * 1024 * 1024));
                if (text == null) continue;
                if (state.Mode == null)
                {
                    MatchCollection modes = VdiMode.Matches(text);
                    if (modes.Count > 0) { state.Mode = modes[modes.Count - 1].Groups[1].Value; state.When = file.LastWriteTime; }
                }
                if (state.Stack == null)
                {
                    MatchCollection stacks = Stack.Matches(text);
                    if (stacks.Count > 0) state.Stack = stacks[stacks.Count - 1].Groups[1].Value;
                }
                if (state.Mode != null && state.Stack != null) break;
            }
            return state;
        }

        /// <summary>Reads the end of a file Teams may still have open for writing.</summary>
        private static string ReadTail(string path, int maxBytes)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, stream.Length - maxBytes);
                stream.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[stream.Length - start];
                int read = 0, n;
                while (read < buffer.Length && (n = stream.Read(buffer, read, buffer.Length - read)) > 0) read += n;
                return Encoding.UTF8.GetString(buffer, 0, read);
            }
        }

        // ================================================================ helpers

        private static string FileVersion(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            string v = (info.ProductVersion ?? info.FileVersion ?? "").Trim();
            return v.Length == 0 ? null : v;
        }

        private static string Ver(string version, string prefix = "")
        {
            return string.IsNullOrEmpty(version) ? "" : " · " + prefix + "v" + version;
        }

        private static int SafeSession(Process p)
        {
            try { return p.SessionId; } catch (Exception) { return -1; }
        }

        private static T Safe<T>(Func<T> read)
        {
            try { return read(); }
            catch (Exception) { return default(T); }
        }
    }

    /// <summary>A Windows service located by its executable name (service names vary by vendor and version).</summary>
    public class ServiceInfo
    {
        public string Name { get; set; }
        public string State { get; set; }
        public string StartMode { get; set; }
        public string ExePath { get; set; }

        public static ServiceInfo FindByExecutable(string exe)
        {
            string query = "SELECT Name, State, StartMode, PathName FROM Win32_Service WHERE PathName LIKE '%" + exe.Replace("'", "") + "%'";
            using (var searcher = new ManagementObjectSearcher(query))
            {
                foreach (ManagementBaseObject row in searcher.Get())
                {
                    return new ServiceInfo
                    {
                        Name = row["Name"] as string ?? "?",
                        State = row["State"] as string ?? "Unknown",
                        StartMode = row["StartMode"] as string ?? "Unknown",
                        ExePath = ExecutableFrom(row["PathName"] as string)
                    };
                }
            }
            return null;
        }

        /// <summary>"\"C:\Program Files\X\PASrv.exe\" -k" → C:\Program Files\X\PASrv.exe</summary>
        private static string ExecutableFrom(string pathName)
        {
            if (string.IsNullOrWhiteSpace(pathName)) return null;
            pathName = pathName.Trim();
            if (pathName.StartsWith("\""))
            {
                int end = pathName.IndexOf('"', 1);
                return end > 1 ? pathName.Substring(1, end - 1) : pathName.Trim('"');
            }
            int exe = pathName.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe > 0 ? pathName.Substring(0, exe + 4) : pathName;
        }
    }
}
