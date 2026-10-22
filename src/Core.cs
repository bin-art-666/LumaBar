using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace LumaBar
{
    public enum Tone { Dark = 0, Light = 1 }

    public sealed class Settings
    {
        public int PollMilliseconds = 350;
        public int StableMilliseconds = 700;
        public int ContentStableMilliseconds = 1400;
        public int MinimumSwitchMilliseconds = 1800;
        public int SourceStableMilliseconds = 180;
        public int SourceMinimumSwitchMilliseconds = 400;
        public double DarkThreshold = 0.44;
        public double LightThreshold = 0.60;
        public bool HoldOwnedDialogs = true;
        public bool HoldMediaPlayers = true;
        public Dictionary<string, string> ApplicationRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string[] IgnoredProcesses = new string[0];

        public void Validate()
        {
            if (PollMilliseconds < 150 || PollMilliseconds > 3000 ||
                StableMilliseconds < 200 || StableMilliseconds > 5000 ||
                ContentStableMilliseconds < StableMilliseconds || ContentStableMilliseconds > 8000 ||
                MinimumSwitchMilliseconds < 800 || MinimumSwitchMilliseconds > 15000 ||
                SourceStableMilliseconds < 80 || SourceStableMilliseconds > 1000 ||
                SourceMinimumSwitchMilliseconds < 200 || SourceMinimumSwitchMilliseconds > 2000 ||
                double.IsNaN(DarkThreshold) || double.IsNaN(LightThreshold) ||
                DarkThreshold < 0.1 || LightThreshold > 0.9 || DarkThreshold >= LightThreshold)
                throw new ArgumentException("settings.json 参数超出范围，请参考 README。");
            if (IgnoredProcesses == null) IgnoredProcesses = new string[0];
            Dictionary<string, string> rules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (ApplicationRules != null)
                foreach (KeyValuePair<string, string> item in ApplicationRules)
                {
                    string name = ProcessKey(item.Key);
                    string rule = (item.Value ?? "").ToLowerInvariant();
                    if (String.IsNullOrWhiteSpace(name) || (rule != "auto" && rule != "light" && rule != "dark" && rule != "hold") || rules.ContainsKey(name))
                        throw new ArgumentException("ApplicationRules 应为不重复的进程名与 auto/light/dark/hold 规则。");
                    rules.Add(name, rule);
                }
            ApplicationRules = rules;
        }
        public string RuleFor(string process)
        {
            string rule;
            return ApplicationRules.TryGetValue(ProcessKey(process), out rule) ? rule : "auto";
        }
        public static string ProcessKey(string process)
        {
            string name = Path.GetFileName((process ?? "").Trim());
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
        }
        public void SetPace(string pace)
        {
            if (pace == "灵敏") { PollMilliseconds = 200; StableMilliseconds = 450; ContentStableMilliseconds = 900; MinimumSwitchMilliseconds = 1200; SourceStableMilliseconds = 120; SourceMinimumSwitchMilliseconds = 250; }
            else if (pace == "舒缓") { PollMilliseconds = 400; StableMilliseconds = 1000; ContentStableMilliseconds = 2000; MinimumSwitchMilliseconds = 2600; SourceStableMilliseconds = 300; SourceMinimumSwitchMilliseconds = 700; }
            else if (pace == "均衡") { PollMilliseconds = 350; StableMilliseconds = 700; ContentStableMilliseconds = 1400; MinimumSwitchMilliseconds = 1800; SourceStableMilliseconds = 180; SourceMinimumSwitchMilliseconds = 400; }
            else throw new ArgumentException("未知切换节奏");
        }
    }

    public static class Brightness
    {
        // Perceptual brightness in encoded sRGB, not WCAG linear-light luminance.
        public static double Of(byte r, byte g, byte b)
        {
            return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0;
        }

        public static double Median(IEnumerable<double> samples)
        {
            double[] values = samples.OrderBy(x => x).ToArray();
            if (values.Length == 0) throw new ArgumentException("No samples");
            int mid = values.Length / 2;
            return values.Length % 2 == 0 ? (values[mid - 1] + values[mid]) / 2 : values[mid];
        }

        public static double Regional(IEnumerable<double> bottom, IEnumerable<double> content, IEnumerable<double> top)
        {
            // The taskbar touches the lower part of the window. Let that region
            // dominate without letting a thin status bar or a dark sidebar win.
            return 0.65 * Median(bottom) + 0.25 * Median(content) + 0.10 * Median(top);
        }
    }

    public sealed class DecisionEngine
    {
        private readonly Settings settings;
        private Tone? candidate;
        private long candidateWindow;
        private long candidateSince;
        private long lastSwitch = -100000;
        private long observedWindow;
        private long windowSince;
        private long lastObservation = -100000;
        private int requiredDwell;
        private bool? lastSource;
        private long fastWindow;
        private long fastUntil = -1;
        private bool candidateFast;
        private readonly List<double> history = new List<double>();

        public DecisionEngine(Settings settings) { this.settings = settings; }
        public void Reset() { candidate = null; history.Clear(); observedWindow = 0; lastObservation = -100000; }

        public Tone? Observe(long window, double brightness, Tone current, long now)
        { return Observe(window, brightness, current, now, false); }

        public Tone? Observe(long window, double brightness, Tone current, long now, bool explicitRule)
        { return ObserveCore(window, brightness, current, now, explicitRule, false, false); }

        public Tone? Observe(long window, double brightness, Tone current, long now, bool explicitRule, bool wallpaperSource)
        { return ObserveCore(window, brightness, current, now, explicitRule, true, wallpaperSource); }

        private Tone? ObserveCore(long window, double brightness, Tone current, long now, bool explicitRule, bool trackSource, bool wallpaperSource)
        {
            if (trackSource)
            {
                if (!lastSource.HasValue || lastSource.Value != wallpaperSource)
                { fastWindow = window; fastUntil = now + 2000; }
                lastSource = wallpaperSource;
            }
            if (window != observedWindow || now - lastObservation > Math.Max(1200, settings.PollMilliseconds * 3))
            {
                candidate = null; history.Clear(); observedWindow = window; windowSince = now;
            }
            lastObservation = now;
            history.Add(brightness);
            if (history.Count > 3) history.RemoveAt(0);
            double filtered = explicitRule ? brightness : Brightness.Median(history);
            Tone desired = current;
            if (filtered <= settings.DarkThreshold) desired = Tone.Dark;
            else if (filtered >= settings.LightThreshold) desired = Tone.Light;
            if (desired == current)
            {
                candidate = null;
                if (history.Count == 3 && fastWindow == window) fastUntil = -1;
                return null;
            }
            // Fluctuating video/image content cannot establish a stable theme.
            if (!explicitRule && history.Count == 3 && history.Max() - history.Min() > 0.22)
            { candidate = null; return null; }
            if (candidate != desired || candidateWindow != window)
            {
                candidate = desired;
                candidateWindow = window;
                candidateSince = now;
                candidateFast = trackSource && fastWindow == window && now <= fastUntil;
                requiredDwell = candidateFast ? settings.SourceStableMilliseconds :
                    explicitRule || now - windowSince < settings.StableMilliseconds ? settings.StableMilliseconds : settings.ContentStableMilliseconds;
                return null;
            }
            if (history.Count < (candidateFast ? 2 : 3) || now - candidateSince < requiredDwell ||
                now - lastSwitch < (candidateFast ? settings.SourceMinimumSwitchMilliseconds : settings.MinimumSwitchMilliseconds)) return null;
            candidate = null;
            fastUntil = -1;
            lastSwitch = now;
            return desired;
        }
    }

    public sealed class ThemeValue
    {
        public bool Exists;
        public int Value;
        public ThemeValue() { }
        public ThemeValue(bool exists, int value) { Exists = exists; Value = value; }
        public bool Same(ThemeValue other)
        {
            return other != null && Exists == other.Exists && (!Exists || Value == other.Value);
        }
        public Tone Tone { get { return Exists && Value == 0 ? Tone.Dark : Tone.Light; } }
    }

    public interface IThemeStore
    {
        ThemeValue Read();
        void Write(ThemeValue value);
        void Refresh();
    }

    public sealed class SessionRecord
    {
        public int Version = 1;
        public ThemeValue Original;
        public ThemeValue LastWritten;
        public ThemeValue Pending;
    }

    public interface ISessionJournal
    {
        SessionRecord Load();
        void Save(SessionRecord record);
        void Delete();
    }

    public sealed class ThemeController
    {
        private readonly IThemeStore store;
        private readonly ISessionJournal journal;
        private SessionRecord session;
        public ThemeController(IThemeStore store, ISessionJournal journal)
        {
            this.store = store;
            this.journal = journal;
        }
        public ThemeValue Current { get { return store.Read(); } }
        public void Refresh() { store.Refresh(); }
        public bool OwnsCurrent()
        {
            return session == null || IsOwned(session, store.Read());
        }
        private static bool IsOwned(SessionRecord record, ThemeValue value)
        {
            return (record.LastWritten != null && value.Same(record.LastWritten)) ||
                (record.Pending != null && value.Same(record.Pending));
        }
        public void Apply(Tone tone)
        {
            ThemeValue current = store.Read();
            if (session != null && !IsOwned(session, current))
                throw new InvalidOperationException("检测到其他程序更改系统主题，已暂停以避免互相切换。");
            ThemeValue next = new ThemeValue(true, (int)tone);
            if (current.Same(next)) return;
            if (session == null) session = new SessionRecord { Original = current };
            // Journal both sides before mutation, including the crash-during-write case.
            session.Pending = next;
            journal.Save(session);
            store.Write(next);
            session.LastWritten = next;
            session.Pending = null;
            journal.Save(session);
            store.Refresh();
        }
        public bool Restore()
        {
            SessionRecord record = session ?? journal.Load();
            if (record == null) return true;
            if (record.Version != 1 || record.Original == null ||
                (record.LastWritten == null && record.Pending == null))
                throw new InvalidOperationException("恢复记录格式无效，请保留 session.json 并检查日志。");
            bool owned = IsOwned(record, store.Read());
            if (owned)
            {
                store.Write(record.Original);
                store.Refresh();
            }
            journal.Delete();
            session = null;
            return owned;
        }
    }
}
