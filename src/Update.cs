using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LumaBar
{
    public sealed class UpdateManifest
    {
        public string Version;
        public Dictionary<string,string> Files = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    }
    public sealed class UpdateFileRecord
    {
        public string Name, Before, After;
        public bool Existed;
    }
    public sealed class UpdateRecord
    {
        public string Root, Phase, Token;
        public List<UpdateFileRecord> Files = new List<UpdateFileRecord>();
    }
    internal static class UpdateFiles
    {
        internal static readonly string[] Managed = { "LumaBar.exe", "LumaBar.exe.config", "README.md", "LICENSE", "LICENSE-LumaBar-MIT", "THIRD_PARTY_NOTICES.md" };
        internal const string Native = "native/ExplorerTAP.dll";
        internal const string StateName = ".lumabar-update";
        internal static string FullRoot(string root)
        {
            string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新目录无效或为目录链接。");
            return full;
        }
        internal static string Hash(string path)
        {
            using (SHA256 hash = SHA256.Create()) using (FileStream file = File.OpenRead(path))
                return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "");
        }
        internal static void Write(string path, object value)
        {
            string temp = path + ".tmp";
            using (FileStream file = new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(value));
                file.Write(bytes,0,bytes.Length); file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
        }
        internal static T Read<T>(string path) { return new JavaScriptSerializer().Deserialize<T>(File.ReadAllText(path)); }
        internal static UpdateManifest Validate(string payload, string root)
        {
            UpdateManifest manifest = Read<UpdateManifest>(Path.Combine(payload,"update.json"));
            Version version;
            if (manifest == null || !Version.TryParse(manifest.Version,out version) || manifest.Files == null) throw new InvalidDataException("更新清单无效。");
            HashSet<string> allowed = new HashSet<string>(Managed,StringComparer.OrdinalIgnoreCase); allowed.Add(Native);
            if (manifest.Files.Count != allowed.Count) throw new InvalidDataException("更新包文件清单不完整。");
            foreach (KeyValuePair<string,string> entry in manifest.Files)
            {
                if (!allowed.Remove(entry.Key)) throw new InvalidDataException("更新包含不支持的文件：" + entry.Key);
                string file = Path.Combine(payload,entry.Key.Replace('/',Path.DirectorySeparatorChar));
                if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || !String.Equals(Hash(file),entry.Value,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包校验失败：" + entry.Key);
            }
            Version executableVersion;
            if (!Version.TryParse(FileVersionInfo.GetVersionInfo(Path.Combine(payload,"LumaBar.exe")).FileVersion,out executableVersion) || version != executableVersion)
                throw new InvalidDataException("更新包版本与程序版本不一致。");
            Version installed;
            if (Version.TryParse(FileVersionInfo.GetVersionInfo(Path.Combine(root,"LumaBar.exe")).FileVersion,out installed) && version < installed)
                throw new InvalidDataException("不能使用较旧的更新包覆盖当前版本。");
            if (!String.Equals(Hash(Path.Combine(root,Native)),manifest.Files[Native],StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包的原生组件版本不同，未停止当前程序。请使用匹配当前组件的更新包。");
            Settings settings = Read<Settings>(Path.Combine(root,"settings.json"));
            if (settings == null) throw new InvalidDataException("现有配置为空。");
            settings.Validate();
            return manifest;
        }
        internal static string Extract(string package, string destination)
        {
            Directory.CreateDirectory(destination);
            if (Directory.Exists(package))
            {
                string root = FindPayload(package);
                foreach (string name in Managed) File.Copy(Path.Combine(root,name),Path.Combine(destination,name));
                Directory.CreateDirectory(Path.Combine(destination,"native"));
                File.Copy(Path.Combine(root,Native),Path.Combine(destination,Native));
                File.Copy(Path.Combine(root,"update.json"),Path.Combine(destination,"update.json"));
                return destination;
            }
            using (ZipArchive archive = ZipFile.OpenRead(package))
            {
                long total = 0;
                if (archive.Entries.Count > 1024) throw new InvalidDataException("更新包文件过多。");
                HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string relative = entry.FullName.Replace('\\','/');
                    if (relative.StartsWith("/",StringComparison.Ordinal) || relative.IndexOf(':') >= 0) throw new InvalidDataException("更新包路径无效。");
                    string path = Path.GetFullPath(Path.Combine(destination,relative));
                    if (!path.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包路径越界。");
                    if (!paths.Add(path)) throw new InvalidDataException("更新包含重复路径。");
                    total += entry.Length;
                    if (total > 64 * 1024 * 1024 || entry.Length > 32 * 1024 * 1024) throw new InvalidDataException("更新包过大。");
                    if (relative.EndsWith("/",StringComparison.Ordinal)) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)); entry.ExtractToFile(path);
                }
            }
            return FindPayload(destination);
        }
        private static string FindPayload(string root)
        {
            if (File.Exists(Path.Combine(root,"update.json"))) return root;
            string found = null;
            foreach (string child in Directory.GetDirectories(root))
                if (File.Exists(Path.Combine(child,"update.json")))
                { if (found != null) throw new InvalidDataException("更新包含多个程序目录。"); found = child; }
            if (found == null) throw new InvalidDataException("未找到 update.json，请选择完整更新包。");
            return found;
        }
        internal static void RemoveState(string root)
        {
            string state = Path.Combine(FullRoot(root),StateName);
            if (Directory.Exists(state))
            {
                if ((File.GetAttributes(state) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新状态目录不能为链接。");
                Directory.Delete(state,true);
            }
        }
    }

    internal interface IUpdateHost
    {
        void StopExisting(string executable);
        bool StartAndCheck(string executable, string token);
        void StopCandidate();
        void RestartPrevious(string executable);
    }
    internal sealed class ProcessUpdateHost : IUpdateHost
    {
        private Process candidate;
        private string restartAfterTransaction;
        public void StopExisting(string executable)
        {
            foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
                using (process)
                {
                    try
                    {
                        if (process.Id == Process.GetCurrentProcess().Id || !String.Equals(process.MainModule.FileName,executable,StringComparison.OrdinalIgnoreCase)) continue;
                        bool requested = false; Stopwatch startup = Stopwatch.StartNew();
                        while (!requested && !process.HasExited && startup.ElapsedMilliseconds < 2000)
                        {
                            try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting(TrayApp.ShutdownName(process.Id))) signal.Set(); requested = true; }
                            catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(50); }
                        }
                        if (!requested && !process.HasExited) throw new IOException("当前版本暂不支持正常退出更新，请先从托盘选择“退出并恢复”。");
                        if (!process.WaitForExit(12000)) throw new IOException("程序未完成退出与恢复；更新已取消，保留当前版本。");
                    }
                    catch (InvalidOperationException) { if (!process.HasExited) throw; }
                    catch (System.ComponentModel.Win32Exception) { if (!process.HasExited) throw; }
                }
        }
        public bool StartAndCheck(string executable, string token)
        {
            using (EventWaitHandle health = new EventWaitHandle(false,EventResetMode.ManualReset,token))
            {
                candidate = Process.Start(new ProcessStartInfo(executable,"--startup --update-health " + token) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable) });
                Stopwatch wait = Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < 20000)
                {
                    if (candidate.HasExited) return false;
                    if (health.WaitOne(100))
                    {
                        // Startup readiness must be followed by an actual stable process.
                        return !candidate.WaitForExit(5000);
                    }
                }
                return false;
            }
        }
        public void StopCandidate()
        {
            if (candidate == null) return;
            if (!candidate.HasExited)
            {
                try { candidate.Kill(); }
                catch (InvalidOperationException) { if (!candidate.HasExited) throw; }
                if (!candidate.WaitForExit(5000)) throw new IOException("新版进程未退出，保留备份等待恢复。");
            }
            candidate.Dispose(); candidate = null;
        }
        public void RestartPrevious(string executable) { restartAfterTransaction = executable; }
        internal void FlushRestart()
        {
            // Release the update mutex before an ordinary startup checks for recovery.
            if (restartAfterTransaction == null) return;
            string executable = restartAfterTransaction; restartAfterTransaction = null;
            Process.Start(new ProcessStartInfo(executable,"--startup") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable) }).Dispose();
        }
    }

    internal static class UpdateTransaction
    {
        internal static string MutexName(string root)
        {
            using (SHA256 hash = SHA256.Create()) return @"Local\LumaBar-Update-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).ToUpperInvariant()))).Replace("-", "");
        }
        private static string State(string root) { return Path.Combine(root,UpdateFiles.StateName); }
        internal static UpdateRecord Load(string root)
        {
            string state = State(root);
            if ((File.GetAttributes(state) & FileAttributes.ReparsePoint) != 0) throw new IOException("更新状态目录不能为链接。");
            UpdateRecord record = UpdateFiles.Read<UpdateRecord>(Path.Combine(state,"transaction.json"));
            if (record == null || !String.Equals(record.Root,UpdateFiles.FullRoot(root),StringComparison.OrdinalIgnoreCase) || record.Files == null || record.Files.Count == 0) throw new IOException("更新恢复记录无效。");
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (UpdateFileRecord file in record.Files)
                if (Array.IndexOf(UpdateFiles.Managed,file.Name) < 0 || !names.Add(file.Name)) throw new IOException("恢复文件清单无效。");
            return record;
        }
        private static void Save(UpdateRecord record) { UpdateFiles.Write(Path.Combine(State(record.Root),"transaction.json"),record); }
        internal static void Execute(string root, string payload, IUpdateHost host)
        {
            root = UpdateFiles.FullRoot(root);
            UpdateManifest manifest = UpdateFiles.Validate(payload,root); // No app exit or target changes before full preflight.
            string state = State(root), executable = Path.Combine(root,"LumaBar.exe");
            if (Directory.Exists(state)) throw new IOException("存在未完成的更新，请先启动程序完成恢复。");
            Directory.CreateDirectory(Path.Combine(state,"old")); Directory.CreateDirectory(Path.Combine(state,"new"));
            UpdateRecord record = new UpdateRecord { Root = root, Phase = "prepared", Token = @"Local\LumaBar-Health-" + Guid.NewGuid().ToString("N") };
            bool stopped = false, journalSaved = false;
            try
            {
                foreach (string name in UpdateFiles.Managed)
                {
                    string target = Path.Combine(root,name), staged = Path.Combine(state,"new",name);
                    File.Copy(Path.Combine(payload,name),staged);
                    if (!String.Equals(UpdateFiles.Hash(staged),manifest.Files[name],StringComparison.OrdinalIgnoreCase)) throw new IOException("暂存文件校验失败。");
                    bool exists = File.Exists(target);
                    if (exists) File.Copy(target,Path.Combine(state,"old",name));
                    record.Files.Add(new UpdateFileRecord { Name = name, Existed = exists, Before = exists ? UpdateFiles.Hash(target) : null, After = manifest.Files[name] });
                }
                Save(record); journalSaved = true;
                host.StopExisting(executable); stopped = true;
                record.Phase = "replacing"; Save(record);
                foreach (UpdateFileRecord file in record.Files)
                {
                    string staged = Path.Combine(state,"new",file.Name), target = Path.Combine(root,file.Name);
                    if (File.Exists(target)) File.Replace(staged,target,null); else File.Move(staged,target);
                }
                record.Phase = "launching"; Save(record);
                if (!host.StartAndCheck(executable,record.Token)) throw new IOException("新版未通过启动确认，正在回退。");
                record.Phase = "committed"; Save(record);
            }
            catch
            {
                if (stopped) { host.StopCandidate(); Rollback(record); host.RestartPrevious(executable); }
                // If exit was refused, none of the installed files were replaced.
                if (!journalSaved || !stopped) UpdateFiles.RemoveState(root);
                throw;
            }
            UpdateFiles.RemoveState(root);
        }
        internal static void Rollback(UpdateRecord record)
        {
            string state = State(record.Root);
            // Validate every backup before restoring any file.
            foreach (UpdateFileRecord file in record.Files)
            {
                string target = Path.Combine(record.Root,file.Name);
                if (file.Existed && !String.Equals(UpdateFiles.Hash(Path.Combine(state,"old",file.Name)),file.Before,StringComparison.OrdinalIgnoreCase)) throw new IOException("旧版备份校验失败，保留文件供恢复。");
                if (File.Exists(target))
                {
                    string current = UpdateFiles.Hash(target);
                    if (!String.Equals(current,file.Before,StringComparison.OrdinalIgnoreCase) && !String.Equals(current,file.After,StringComparison.OrdinalIgnoreCase)) throw new IOException("安装文件被其他程序修改，已停止自动覆盖。");
                }
            }
            foreach (UpdateFileRecord file in record.Files)
            {
                string target = Path.Combine(record.Root,file.Name);
                if (file.Existed)
                {
                    string copy = Path.Combine(state,"new",file.Name);
                    File.Copy(Path.Combine(state,"old",file.Name),copy,true);
                    if (File.Exists(target)) File.Replace(copy,target,null); else File.Move(copy,target);
                }
                else if (File.Exists(target)) File.Delete(target);
            }
            record.Phase = "rolledback"; Save(record);
            UpdateFiles.RemoveState(record.Root);
        }
        internal static void Recover(string root, IUpdateHost host)
        {
            UpdateRecord record = Load(root);
            if (record.Phase == "committed" || record.Phase == "rolledback") { UpdateFiles.RemoveState(root); return; }
            host.StopExisting(Path.Combine(root,"LumaBar.exe")); Rollback(record); host.RestartPrevious(Path.Combine(root,"LumaBar.exe"));
        }
    }

    // Loaded from bytes in Windows PowerShell: the worker has no mapped EXE in
    // either the install folder or the temporary folder, so it can clean itself.
    public static class UpdateEntry
    {
        public static int Run(string root, string payload, string temporary)
        {
            int result = 0;
            ProcessUpdateHost host = new ProcessUpdateHost();
            try
            {
                using (Mutex mutex = new Mutex(false,UpdateTransaction.MutexName(root)))
                {
                    bool acquired; try { acquired = mutex.WaitOne(5000); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("已有更新正在进行。");
                    try
                    {
                        if (String.IsNullOrEmpty(payload)) UpdateTransaction.Recover(root,host);
                        else UpdateTransaction.Execute(root,payload,host);
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                host.FlushRestart();
                File.AppendAllText(Path.Combine(root,"update.log"),DateTime.Now.ToString("s") + " Update/recovery completed." + Environment.NewLine);
                UpdateFiles.Write(Path.Combine(root,"update-result.json"),"更新 / 恢复已完成。");
            }
            catch (Exception error)
            {
                result = 1;
                try { host.FlushRestart(); } catch(Exception restartError) { error = new AggregateException(error,restartError); }
                UpdateFiles.Write(Path.Combine(root,"update-result.json"),"更新未完成：" + error.Message + " 详见 update.log。");
                File.AppendAllText(Path.Combine(root,"update.log"),DateTime.Now.ToString("s") + " " + error + Environment.NewLine);
            }
            finally
            {
                string full = Path.GetFullPath(temporary), prefix = Path.Combine(Path.GetTempPath(),"LumaBar-update-");
                if (full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) && Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                    Directory.Delete(full,true);
            }
            return result;
        }
    }

    internal static class UpdateLauncher
    {
        internal static string TakeNotice(string root)
        {
            string file = Path.Combine(root,"update-result.json");
            try { if (!File.Exists(file)) return null; string notice = UpdateFiles.Read<string>(file); File.Delete(file); return notice; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        internal static string Argument(string[] args, string name)
        { int index = Array.IndexOf(args,name); return index >= 0 && index + 1 < args.Length ? args[index+1] : null; }
        private static string Literal(string text) { return "'" + text.Replace("'","''") + "'"; }
        internal static Process LaunchWorker(string root, string payload, string temporary)
        {
            string engine = Path.Combine(temporary,"engine.bin"); File.Copy(Assembly.GetExecutingAssembly().Location,engine);
            string script = "$ErrorActionPreference='Stop';$a=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes(" + Literal(engine) + "));$r=$a.GetType('LumaBar.UpdateEntry').GetMethod('Run').Invoke($null,@(" + Literal(root) + "," + Literal(payload ?? "") + "," + Literal(temporary) + "));exit [int]$r";
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),@"WindowsPowerShell\v1.0\powershell.exe");
            return Process.Start(new ProcessStartInfo(shell,"-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() });
        }
        internal static void Start(string root, string package)
        {
            root = UpdateFiles.FullRoot(root);
            string temporary = Path.Combine(Path.GetTempPath(),"LumaBar-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            try
            {
                string payload = UpdateFiles.Extract(Path.GetFullPath(package),Path.Combine(temporary,"payload"));
                UpdateFiles.Validate(payload,root);
                LaunchWorker(root,payload,temporary).Dispose();
            }
            catch { Directory.Delete(temporary,true); throw; }
        }
        // Return true when this invocation should yield to an update/recovery worker.
        internal static bool RecoverBeforeStartup(string root, string healthToken)
        {
            string state = Path.Combine(root,UpdateFiles.StateName);
            using (Mutex mutex = new Mutex(false,UpdateTransaction.MutexName(root)))
            {
                bool acquired; try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    if (Directory.Exists(state) && healthToken != null)
                    { UpdateRecord record = UpdateTransaction.Load(root); return record.Phase != "launching" || record.Token != healthToken; }
                    return true;
                }
                try
                {
                    if (!Directory.Exists(state)) return false;
                    if (!File.Exists(Path.Combine(state,"transaction.json"))) { UpdateFiles.RemoveState(root); return false; }
                    UpdateRecord record = UpdateTransaction.Load(root);
                    if (record.Phase == "committed" || record.Phase == "rolledback") { UpdateFiles.RemoveState(root); return false; }
                    string temporary = Path.Combine(Path.GetTempPath(),"LumaBar-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
                    LaunchWorker(root,null,temporary).Dispose(); return true;
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
    }
}
