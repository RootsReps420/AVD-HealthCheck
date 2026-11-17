// Plain-text report for pasting into a ServiceNow incident. Problems first, then the detail.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AvdHealthCheck
{
    public static class AppInfo
    {
        public const string Name = "AVD Health Check";
        public const string Version = "1.0.0";
    }

    public static class Report
    {
        public static string Build(MainViewModel vm, SpeedResult speed, DateTime startedAt, TimeSpan took, string configSource)
        {
            var sb = new StringBuilder();
            List<CheckItem> checks = vm.Checks.ToList();
            List<CheckItem> endpoints = checks.Where(c => c.IsEndpoint).ToList();
            int pass = endpoints.Count(c => c.State == CheckState.Pass);
            List<CheckItem> problems = checks.Where(c => c.State == CheckState.Fail || c.State == CheckState.Warn)
                                             .OrderBy(c => c.State == CheckState.Fail ? 0 : 1).ToList();

            sb.AppendLine("AVD HEALTH CHECK");
            sb.AppendLine("Run: " + Format.Date(startedAt) + " (took " + (int)Math.Round(took.TotalSeconds) + " s)");
            sb.AppendLine("Result: " + vm.OverallText);
            sb.AppendLine();

            sb.AppendLine("SUMMARY");
            if (!string.IsNullOrEmpty(vm.ClientAppSummary))
                sb.AppendLine("Client app: " + vm.ClientAppSummary);
            if (checks.Any(c => c.Key != null && c.Key.StartsWith("prisma.")))
                sb.AppendLine("Prisma Access: " + vm.PrismaValue + " · " + vm.PrismaSub);
            if (checks.Any(c => c.Key != null && c.Key.StartsWith("teams.")))
                sb.AppendLine("Teams optimisation: " + vm.TeamsValue + " · " + vm.TeamsSub);
            sb.AppendLine("Connectivity: " + pass + " of " + endpoints.Count + " endpoints passed" + Counts(endpoints));
            sb.AppendLine("Speed test: " + SpeedLine(speed));
            sb.AppendLine();

            if (problems.Count > 0)
            {
                sb.AppendLine("ISSUES");
                foreach (CheckItem c in problems) sb.AppendLine(Line(c));
                sb.AppendLine();
            }

            sb.AppendLine("DEVICE");
            foreach (DeviceField f in vm.Device) sb.AppendLine(f.Label + ": " + f.Value);
            sb.AppendLine();

            sb.AppendLine("ALL CHECKS");
            foreach (IGrouping<string, CheckItem> group in checks.GroupBy(c => c.Group))
            {
                sb.AppendLine(group.Key);
                foreach (CheckItem c in group) sb.AppendLine("  " + Line(c));
            }
            sb.AppendLine();
            sb.AppendLine(AppInfo.Name + " v" + AppInfo.Version + " · endpoints from " + configSource);
            return sb.ToString();
        }

        public static string SpeedLine(SpeedResult speed)
        {
            if (speed == null) return "not run";
            if (speed.Error != null) return speed.Error;

            var parts = new List<string>
            {
                "Download " + (speed.DownloadMbps.HasValue ? Mbps(speed.DownloadMbps) + " (" + speed.DownloadSource + ")" : "failed: " + speed.DownloadError),
                "Upload " + (speed.UploadMbps.HasValue ? Mbps(speed.UploadMbps) + " (" + speed.UploadSource + ")" : "failed: " + speed.UploadError),
                "Latency " + (speed.LatencyMs.HasValue ? Math.Round(speed.LatencyMs.Value) + " ms" +
                    (speed.JitterMs.HasValue ? ", jitter " + Math.Round(speed.JitterMs.Value) + " ms" : "") + " (" + speed.LatencySource + ")" : "n/a")
            };
            string line = string.Join(" | ", parts) + " · " + speed.Route;
            if (speed.Notes.Count > 0) line += Environment.NewLine + "  Fallback used: " + string.Join("; ", speed.Notes);
            return line;
        }

        public static string Mbps(double? value)
        {
            if (!value.HasValue) return "n/a";
            return value.Value.ToString(value.Value >= 100 ? "0" : "0.0") + " Mbps";
        }

        private static string Counts(List<CheckItem> checks)
        {
            int warn = checks.Count(c => c.State == CheckState.Warn);
            int fail = checks.Count(c => c.State == CheckState.Fail);
            var parts = new List<string>();
            if (fail > 0) parts.Add(fail + " failed");
            if (warn > 0) parts.Add(warn + (warn == 1 ? " warning" : " warnings"));
            return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
        }

        private static string Line(CheckItem c)
        {
            string tag = c.State == CheckState.Pass ? "[PASS]" : c.State == CheckState.Warn ? "[WARN]" : c.State == CheckState.Fail ? "[FAIL]" : "[ -- ]";
            string latency = string.IsNullOrEmpty(c.Latency) ? "" : " · " + c.Latency;
            return tag + " " + c.Name + " (" + c.Target + ")" + latency + " · " + c.Detail;
        }
    }
}
