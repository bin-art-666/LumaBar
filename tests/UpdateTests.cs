using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;

namespace LumaBar
{
    internal sealed class FakeUpdateHost : IUpdateHost
    {
        internal int Stops, Starts, Restarts;
        internal bool Healthy = true, RefuseExit;
        internal Action OnStop, OnFailed;
        public void StopExisting(string executable) { Stops++; if (RefuseExit) throw new IOException("exit refused"); if (OnStop != null) OnStop(); }
        public bool StartAndCheck(string executable, string token) { Starts++; return Healthy; }
        public void StopCandidate() { if (OnFailed != null) OnFailed(); }
        public void RestartPrevious(string executable) { Restarts++; }
    }
    internal sealed class UpdateFixture : IDisposable
    {
        internal readonly string DirectoryPath, Root, Payload;
        internal readonly string SettingsText = "{\"Glass\":{\"Desktop\":{\"TintOpacity\":7}},\"TimingVersion\":3}";
        internal UpdateFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(),"LumaBar-update-tests-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(DirectoryPath,"installed"); Payload = Path.Combine(DirectoryPath,"package");
            Directory.CreateDirectory(Path.Combine(Root,"native")); Directory.CreateDirectory(Path.Combine(Payload,"native"));
            foreach(string name in UpdateFiles.Managed)
            { File.WriteAllText(Path.Combine(Root,name),"old-" + name); File.WriteAllText(Path.Combine(Payload,name),"new-" + name); }
            File.Copy(Assembly.GetExecutingAssembly().Location,Path.Combine(Payload,"LumaBar.exe"),true);
            File.WriteAllText(Path.Combine(Payload,"LumaBar.exe.config"),"<configuration><startup><supportedRuntime version=\"v4.0\" sku=\".NETFramework,Version=v4.8\"/></startup></configuration>");
            File.WriteAllText(Path.Combine(Root,"settings.json"),SettingsText);
            File.WriteAllText(Path.Combine(Root,"session.json"),"restore journal must stay intact");
            File.WriteAllText(Path.Combine(Root,UpdateFiles.Native),"fixed-native"); File.WriteAllText(Path.Combine(Payload,UpdateFiles.Native),"fixed-native");
            Manifest();
        }
        internal void Manifest()
        {
            UpdateManifest manifest = new UpdateManifest { Version = FileVersionInfo.GetVersionInfo(Path.Combine(Payload,"LumaBar.exe")).FileVersion };
            foreach(string name in UpdateFiles.Managed) manifest.Files.Add(name,UpdateFiles.Hash(Path.Combine(Payload,name)));
            manifest.Files.Add(UpdateFiles.Native,UpdateFiles.Hash(Path.Combine(Payload,UpdateFiles.Native)));
            UpdateFiles.Write(Path.Combine(Payload,"update.json"),manifest);
        }
        internal bool Preserved { get { return File.ReadAllText(Path.Combine(Root,"settings.json")) == SettingsText && File.ReadAllText(Path.Combine(Root,"session.json")) == "restore journal must stay intact"; } }
        internal void Interrupted(string phase)
        {
            string state = Path.Combine(Root,UpdateFiles.StateName);
            Directory.CreateDirectory(Path.Combine(state,"old")); Directory.CreateDirectory(Path.Combine(state,"new"));
            UpdateRecord record = new UpdateRecord { Root = Root, Phase = phase, Token = "test" };
            foreach(string name in UpdateFiles.Managed)
            {
                File.Copy(Path.Combine(Root,name),Path.Combine(state,"old",name));
                record.Files.Add(new UpdateFileRecord { Name = name, Existed = true, Before = UpdateFiles.Hash(Path.Combine(Root,name)), After = UpdateFiles.Hash(Path.Combine(Payload,name)) });
            }
            File.Copy(Path.Combine(Payload,"LumaBar.exe"),Path.Combine(Root,"LumaBar.exe"),true);
            UpdateFiles.Write(Path.Combine(state,"transaction.json"),record);
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(DirectoryPath).StartsWith(Path.Combine(Path.GetTempPath(),"LumaBar-update-tests-"),StringComparison.OrdinalIgnoreCase)) throw new Exception("Unexpected fixture cleanup path");
            Directory.Delete(DirectoryPath,true);
        }
    }
    internal static partial class Tests
    {
        private static void CompileUpdateStub(string output, bool healthy, bool reportReady = true)
        {
            string source = output + ".cs";
            string code = @"using System; using System.IO; using System.Threading; using System.Diagnostics; using System.Security.Principal;
[assembly:System.Reflection.AssemblyFileVersion(""0.6.7.0"")]
class Stub { static int Main(string[] args) {
if(Array.IndexOf(args,""--update-health"")<0) using(var updateLock=new Mutex(false,@""UPDATE_MUTEX"")) { if(!updateLock.WaitOne(0)) return 4; updateLock.ReleaseMutex(); }
File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,""starts.log""),""MARKER"" + Environment.NewLine);
if(FAIL) return 3;
using(var stop=new EventWaitHandle(false,EventResetMode.AutoReset,@""Local\LumaBar-Exit-""+WindowsIdentity.GetCurrent().User.Value+""-""+Process.GetCurrentProcess().Id)) {
int index=Array.IndexOf(args,""--update-health""); if(index>=0) using(var ready=EventWaitHandle.OpenExisting(args[index+1])) ready.Set();
stop.WaitOne(45000); return 0; } } }";
            code=code.Replace("if(index>=0)",reportReady?"if(index>=0)":"if(index>=0 && false)").Replace("UPDATE_MUTEX",UpdateTransaction.MutexName(Path.Combine(Directory.GetParent(Path.GetDirectoryName(output)).FullName,"installed"))).Replace("MARKER",healthy?"GOOD":"FAIL").Replace("if(FAIL) return 3;",healthy?"":"return 3;");
            if(!healthy) code=code.Substring(0,code.IndexOf("using(var stop=",StringComparison.Ordinal))+"} }";
            File.WriteAllText(source,code);
            string compiler=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
            using(Process process=Process.Start(new ProcessStartInfo(compiler,"/nologo /target:winexe /out:\""+output+"\" \""+source+"\"") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true }))
            { string message=process.StandardOutput.ReadToEnd(); if(!process.WaitForExit(10000)||process.ExitCode!=0)throw new Exception("Stub compilation failed: "+message); }
        }
        private static bool StubRestarted(string root)
        {
            try { return File.ReadAllText(Path.Combine(root,"starts.log")).Trim().EndsWith("GOOD",StringComparison.Ordinal); }
            catch(IOException) { return false; }
        }
        private static void UpdateWorkerTest()
        {
            using(UpdateFixture fixture=new UpdateFixture())
            {
                ProcessUpdateHost cleanup=new ProcessUpdateHost();
                try
                {
                    CompileUpdateStub(Path.Combine(fixture.Payload,"LumaBar.exe"),true); fixture.Manifest();
                    string temporary=Path.Combine(Path.GetTempPath(),"LumaBar-update-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
                    using(Process worker=UpdateLauncher.LaunchWorker(fixture.Root,fixture.Payload,temporary))
                    { bool ended=worker.WaitForExit(30000); if(!ended||worker.ExitCode!=0) Console.WriteLine(File.Exists(Path.Combine(fixture.Root,"update.log"))?File.ReadAllText(Path.Combine(fixture.Root,"update.log")):"Worker produced no update log."); Check(ended&&worker.ExitCode==0,"real memory-loaded update worker confirms a healthy child process"); }
                    Check(!Directory.Exists(temporary)&&!Directory.Exists(Path.Combine(fixture.Root,UpdateFiles.StateName)),"real updater removes its own temporary engine and rollback files");
                    Check(fixture.Preserved,"real update worker preserves existing settings and restore journal byte-for-byte");
                    string good=UpdateFiles.Hash(Path.Combine(fixture.Root,"LumaBar.exe"));
                    CompileUpdateStub(Path.Combine(fixture.Payload,"LumaBar.exe"),false); fixture.Manifest();
                    temporary=Path.Combine(Path.GetTempPath(),"LumaBar-update-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
                    using(Process worker=UpdateLauncher.LaunchWorker(fixture.Root,fixture.Payload,temporary))
                    { Check(worker.WaitForExit(30000)&&worker.ExitCode==1,"real updater detects a newly launched executable exiting before readiness"); }
                    WaitUntil(delegate { return StubRestarted(fixture.Root); });
                    Check(UpdateFiles.Hash(Path.Combine(fixture.Root,"LumaBar.exe"))==good && StubRestarted(fixture.Root),"failed real launch restores and restarts the old executable");
                    Check(!Directory.Exists(temporary)&&fixture.Preserved,"failed real update also cleans temporary engine and preserves user files");
                    CompileUpdateStub(Path.Combine(fixture.Payload,"LumaBar.exe"),true,false); fixture.Manifest();
                    temporary=Path.Combine(Path.GetTempPath(),"LumaBar-update-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temporary);
                    using(Process worker=UpdateLauncher.LaunchWorker(fixture.Root,fixture.Payload,temporary))
                    { Check(worker.WaitForExit(35000)&&worker.ExitCode==1,"unresponsive startup without readiness is terminated after its bounded timeout"); }
                    WaitUntil(delegate { return StubRestarted(fixture.Root); });
                    Check(UpdateFiles.Hash(Path.Combine(fixture.Root,"LumaBar.exe"))==good && !Directory.Exists(temporary) && fixture.Preserved,"startup timeout restores the old executable and removes its worker files");
                    cleanup.StopExisting(Path.Combine(fixture.Root,"LumaBar.exe"));
                    fixture.Interrupted("launching");
                    File.Delete(Path.Combine(fixture.Root,"update-result.json"));
                    Check(UpdateLauncher.RecoverBeforeStartup(fixture.Root,null),"normal startup delegates interrupted update recovery to a separate worker");
                    Stopwatch wait=Stopwatch.StartNew();
                    while((Directory.Exists(Path.Combine(fixture.Root,UpdateFiles.StateName))||!File.Exists(Path.Combine(fixture.Root,"update-result.json")))&&wait.ElapsedMilliseconds<15000)Thread.Sleep(50);
                    Check(!Directory.Exists(Path.Combine(fixture.Root,UpdateFiles.StateName))&&UpdateFiles.Hash(Path.Combine(fixture.Root,"LumaBar.exe"))==good,"real interrupted-update recovery restores the verified previous executable");
                }
                finally { cleanup.StopExisting(Path.Combine(fixture.Root,"LumaBar.exe")); Thread.Sleep(200); cleanup.StopExisting(Path.Combine(fixture.Root,"LumaBar.exe")); }
            }
        }
        private static void EfficiencyTests()
        {
            Settings settings = new Settings(); settings.SetPace("灵敏");
            WindowSample sample = new WindowSample { Window = new IntPtr(9), Valid = true, Brightness = .1, Scene = MaterialScene.Maximized };
            Rectangle frame = new Rectangle(0,0,1000,800);
            SamplingCadence cadence = new SamplingCadence();
            Check(cadence.Observe(sample,frame,Tone.Dark,settings,0,false,true) == 75, "fresh source keeps the fast sampling burst");
            Check(cadence.Observe(sample,frame,Tone.Dark,settings,3500,false,true) == 600, "stable fullscreen content reduces expensive sample frequency");
            Check(cadence.Observe(sample,frame,Tone.Dark,settings,3600,false,false) == 200, "unavailable native hooks retain the configured polling fallback");
            cadence.Wake(3700);
            Check(cadence.Observe(sample,frame,Tone.Dark,settings,3700,false,true) == 75, "foreground activity wakes low-power sampling immediately");
            cadence.Observe(sample,frame,Tone.Dark,settings,7000,false,true);
            sample.Brightness = .9;
            Check(cadence.Observe(sample,frame,Tone.Dark,settings,7100,true,true) == 75, "content-only brightness change restores fast confirmation");
            sample.IsWallpaper = true;
            cadence.Observe(sample,frame,Tone.Dark,settings,7200,false,true);
            Check(cadence.Observe(sample,frame,Tone.Dark,settings,11000,false,true) == 1000, "stable wallpaper reduces timer wakeups to one per second");
            sample.Scene = MaterialScene.Window; frame.Width = 900;
            Check(cadence.Observe(sample,frame,Tone.Dark,settings,11100,true,true) == 75, "geometry and material-scene changes are never slowed by idle backoff");
            int steadySamples = 0, oldSamples = 0;
            cadence = new SamplingCadence(); sample.IsWallpaper = false; sample.Scene = MaterialScene.Maximized;
            for(long at=0; at<30000;) { steadySamples++; at += cadence.Observe(sample,frame,Tone.Dark,settings,at,false,true); }
            for(int at=0; at<30000; at+=settings.PollMilliseconds) oldSamples++;
            Check(steadySamples < oldSamples * .6, "30-second stable sampling schedule performs over 40 percent fewer reads");
            FakeMaterialBackend backend = new FakeMaterialBackend();
            using(MaterialController materials = new MaterialController(backend))
            {
                MaterialPlan plan = TestPlan(Tone.Dark); long sequence = materials.Apply(plan,true);
                WaitUntil(delegate { return materials.AppliedSequence == sequence; });
                int applied = backend.Applies;
                Thread.Sleep(2050);
                Check(materials.Apply(plan) == sequence && backend.Applies == applied, "unchanged applied material is not resubmitted every two seconds");
                backend.Pid++;
                long reconnect = materials.Apply(plan);
                WaitUntil(delegate { return materials.AppliedSequence == reconnect; });
                Check(reconnect > sequence && backend.Applies > applied, "Explorer process change still reapplies an identical material plan");
                sequence = materials.Apply(plan,true);
                WaitUntil(delegate { return materials.AppliedSequence == sequence; });
                Check(backend.Applies > applied + 2, "explicit resync still forces an unchanged material to apply");
            }
        }
        private static void UpdateTests()
        {
            using(UpdateFixture fixture = new UpdateFixture())
            {
                Check(UpdateTransaction.MutexName(fixture.Root) == UpdateTransaction.MutexName(fixture.Root + Path.DirectorySeparatorChar), "update lock canonicalizes trailing directory separators used by AppDomain startup paths");
                fixture.Interrupted("launching");
                using(ManualResetEvent entered = new ManualResetEvent(false))
                using(ManualResetEvent release = new ManualResetEvent(false))
                {
                    Thread owner = new Thread(delegate() {
                        using(Mutex updateLock = new Mutex(false,UpdateTransaction.MutexName(fixture.Root)))
                        { updateLock.WaitOne(); entered.Set(); release.WaitOne(5000); updateLock.ReleaseMutex(); }
                    }); owner.Start();
                    Check(entered.WaitOne(2000), "update worker owns its installation lock before launching candidate");
                    bool yielded;
                    try { yielded = UpdateLauncher.RecoverBeforeStartup(fixture.Root + Path.DirectorySeparatorChar,"test"); }
                    finally { release.Set(); owner.Join(2000); }
                    Check(!yielded && Directory.Exists(Path.Combine(fixture.Root,UpdateFiles.StateName)), "valid candidate startup joins an active update instead of launching a competing recovery worker");
                }
            }
            using(UpdateFixture fixture = new UpdateFixture())
            {
                FakeUpdateHost host = new FakeUpdateHost();
                UpdateTransaction.Execute(fixture.Root,fixture.Payload,host);
                Check(UpdateFiles.Hash(Path.Combine(fixture.Root,"LumaBar.exe")) == UpdateFiles.Hash(Path.Combine(fixture.Payload,"LumaBar.exe")) && host.Starts == 1,
                    "successful update installs verified executable and checks startup");
                Check(fixture.Preserved && !Directory.Exists(Path.Combine(fixture.Root,UpdateFiles.StateName)), "successful update preserves settings and journal and removes its backup");
            }
            using(UpdateFixture fixture = new UpdateFixture())
            {
                FakeUpdateHost host = new FakeUpdateHost { Healthy = false }; bool failed = false;
                try { UpdateTransaction.Execute(fixture.Root,fixture.Payload,host); } catch(IOException) { failed = true; }
                Check(failed && File.ReadAllText(Path.Combine(fixture.Root,"LumaBar.exe")) == "old-LumaBar.exe" && host.Restarts == 1,
                    "new process failing readiness restores and restarts the previous executable");
                Check(fixture.Preserved && !Directory.Exists(Path.Combine(fixture.Root,UpdateFiles.StateName)), "rollback preserves user files and removes temporary backup after restoration");
            }
            using(UpdateFixture fixture = new UpdateFixture())
            {
                FakeUpdateHost host = new FakeUpdateHost(); File.WriteAllText(Path.Combine(fixture.Payload,"README.md"),"corrupt");
                bool failed = false; try { UpdateTransaction.Execute(fixture.Root,fixture.Payload,host); } catch(InvalidDataException) { failed = true; }
                Check(failed && host.Stops == 0 && File.ReadAllText(Path.Combine(fixture.Root,"LumaBar.exe")) == "old-LumaBar.exe", "corrupt package is rejected before stopping or replacing the installed app");
            }
            using(UpdateFixture fixture = new UpdateFixture())
            {
                FakeUpdateHost host = new FakeUpdateHost { RefuseExit = true }; bool failed = false;
                try { UpdateTransaction.Execute(fixture.Root,fixture.Payload,host); } catch(IOException) { failed = true; }
                Check(failed && host.Starts == 0 && host.Restarts == 0 && File.ReadAllText(Path.Combine(fixture.Root,"LumaBar.exe")) == "old-LumaBar.exe",
                    "refused graceful exit cancels update without killing the original app");
                Check(!Directory.Exists(Path.Combine(fixture.Root,UpdateFiles.StateName)), "cancelled preparation leaves no stale recovery state");
            }
            using(UpdateFixture fixture = new UpdateFixture())
            {
                FileStream locked = null;
                FakeUpdateHost host = new FakeUpdateHost { OnStop = delegate { locked = new FileStream(Path.Combine(fixture.Root,"README.md"),FileMode.Open,FileAccess.Read,FileShare.Read); }, OnFailed = delegate { if(locked != null) locked.Dispose(); } };
                bool failed = false; try { UpdateTransaction.Execute(fixture.Root,fixture.Payload,host); } catch(IOException) { failed = true; }
                finally { if(locked != null) locked.Dispose(); }
                Check(failed && host.Restarts == 1 && File.ReadAllText(Path.Combine(fixture.Root,"LumaBar.exe")) == "old-LumaBar.exe", "mid-replacement file lock rolls back already-replaced files");
            }
            using(UpdateFixture fixture = new UpdateFixture())
            {
                fixture.Interrupted("replacing"); FakeUpdateHost host = new FakeUpdateHost();
                UpdateTransaction.Recover(fixture.Root,host);
                Check(host.Restarts == 1 && File.ReadAllText(Path.Combine(fixture.Root,"LumaBar.exe")) == "old-LumaBar.exe" && fixture.Preserved,
                    "interrupted multi-file update recovers the previous version before normal startup");
            }
            using(UpdateFixture fixture = new UpdateFixture())
            {
                fixture.Interrupted("committed"); FakeUpdateHost host = new FakeUpdateHost();
                string installed = UpdateFiles.Hash(Path.Combine(fixture.Root,"LumaBar.exe")); UpdateTransaction.Recover(fixture.Root,host);
                Check(host.Stops == 0 && UpdateFiles.Hash(Path.Combine(fixture.Root,"LumaBar.exe")) == installed && !Directory.Exists(Path.Combine(fixture.Root,UpdateFiles.StateName)),
                    "interrupted cleanup of a committed update retains the confirmed new version");
            }
            using(UpdateFixture fixture = new UpdateFixture())
            {
                Directory.CreateDirectory(Path.Combine(fixture.Root,UpdateFiles.StateName));
                Check(!UpdateLauncher.RecoverBeforeStartup(fixture.Root,null) && !Directory.Exists(Path.Combine(fixture.Root,UpdateFiles.StateName)), "interrupted staging before journal creation is safely discarded");
                File.WriteAllText(Path.Combine(fixture.Payload,UpdateFiles.Native),"changed-component"); fixture.Manifest();
                bool failed = false; try { UpdateFiles.Validate(fixture.Payload,fixture.Root); } catch(InvalidDataException) { failed = true; }
                Check(failed && fixture.Preserved, "different native component is rejected without touching loaded Explorer DLL");
            }
            using(UpdateFixture fixture = new UpdateFixture())
            {
                string zip = Path.Combine(fixture.DirectoryPath,"unsafe.zip");
                using(ZipArchive archive = ZipFile.Open(zip,ZipArchiveMode.Create)) using(StreamWriter writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open())) writer.Write("escape");
                bool rejected = false; try { UpdateFiles.Extract(zip,Path.Combine(fixture.DirectoryPath,"extract")); } catch(InvalidDataException) { rejected = true; }
                Check(rejected && !File.Exists(Path.Combine(fixture.DirectoryPath,"outside.txt")), "archive traversal is rejected before an escaped file can be written");
            }
        }
    }
}
