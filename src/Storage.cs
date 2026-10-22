using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace LumaBar
{
    internal static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal const string ValueName = "LumaBar";
        internal static string Command(string executable)
        {
            if (String.IsNullOrWhiteSpace(executable) || executable.IndexOf('"') >= 0 || !Path.IsPathRooted(executable))
                throw new ArgumentException("自启程序必须使用完整路径。");
            return "\"" + executable + "\" --startup";
        }
        internal static string Read()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key == null ? null : key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        internal static void Set(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                Write(key, enabled ? Command(System.Windows.Forms.Application.ExecutablePath) : null);
        }
        internal static void Write(RegistryKey key, string command)
        {
            if (command == null) key.DeleteValue(ValueName, false);
            else key.SetValue(ValueName, command, RegistryValueKind.String);
        }
    }

    internal static class Files
    {
        internal static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        internal static void AtomicWrite(string path, string content)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        internal static Settings ReadSettings()
        {
            string path = Path.Combine(Root, "settings.json");
            Settings settings = File.Exists(path) ? Json.Deserialize<Settings>(File.ReadAllText(path)) : new Settings();
            if (settings == null) throw new InvalidDataException("settings.json 不能为空。");
            settings.Validate();
            return settings;
        }
        internal static void SaveSettings(Settings settings)
        {
            settings.Validate();
            AtomicWrite(Path.Combine(Root, "settings.json"), Json.Serialize(settings));
        }
        internal static void Log(string message)
        {
            try
            {
                string path = Path.Combine(Root, "LumaBar.log");
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                {
                    File.Copy(path, path + ".old", true);
                    File.WriteAllText(path, "");
                }
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class FileJournal : ISessionJournal
    {
        private readonly string path;
        internal FileJournal() { path = Path.Combine(Files.Root, "session.json"); }
        public SessionRecord Load()
        {
            return File.Exists(path) ? Files.Json.Deserialize<SessionRecord>(File.ReadAllText(path)) : null;
        }
        public void Save(SessionRecord record) { Files.AtomicWrite(path, Files.Json.Serialize(record)); }
        public void Delete() { if (File.Exists(path)) File.Delete(path); }
    }

    internal sealed class RegistryThemeStore : IThemeStore
    {
        private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string Name = "SystemUsesLightTheme";
        public ThemeValue Read()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(Key))
            {
                object value = key == null ? null : key.GetValue(Name);
                if (value == null) return new ThemeValue(false, 1);
                if (!(value is int) || ((int)value != 0 && (int)value != 1))
                    throw new InvalidDataException("SystemUsesLightTheme 不是有效的 0/1 DWORD，未修改。");
                return new ThemeValue(true, (int)value);
            }
        }
        public void Write(ThemeValue value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (value.Exists) key.SetValue(Name, value.Value, RegistryValueKind.DWord);
                else key.DeleteValue(Name, false);
            }
        }
        public void Refresh() { Native.RefreshTaskbars(); }
    }
}
