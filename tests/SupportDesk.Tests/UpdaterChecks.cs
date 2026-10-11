using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using SupportDesk.Desktop;
public static class UpdaterChecks
{
    public static void Run(string root, string probe)
    {
        var info = FileVersionInfo.GetVersionInfo(probe);
        var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        void Rejected(Action action, string message) { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, message); }
        var directory = Path.Combine(root, "updates"); Directory.CreateDirectory(directory);
        var zip = Path.Combine(directory, "valid.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) archive.CreateEntryFromFile(probe, "CristalixSupport.exe");
        var extracted = Path.Combine(directory, "extracted"); Directory.CreateDirectory(extracted);
        Rejected(() => UpdateInstaller.ExtractVerified(zip, new string('0', 64), extracted, version), "Tampered package accepted");
        UpdateInstaller.ExtractVerified(zip, Hash(zip), extracted, version);
        Check(Hash(probe) == Hash(Path.Combine(extracted, "CristalixSupport.exe")), "Extraction changed EXE");
        Rejected(() => UpdateInstaller.ExtractVerified(zip, Hash(zip), extracted, new Version(99,0,0)), "Wrong executable version accepted");
        var traversal = Path.Combine(directory, "traversal.zip");
        using (var archive = ZipFile.Open(traversal, ZipArchiveMode.Create)) archive.CreateEntry("../escaped.txt");
        Rejected(() => UpdateInstaller.ExtractVerified(traversal, Hash(traversal), extracted, version), "Archive path traversal accepted");
        Check(!File.Exists(Path.Combine(directory, "escaped.txt")), "Archive escaped destination");
        for (int test = 0; test < 2; test++)
        {
            var install = Path.Combine(directory, "install-" + test); Directory.CreateDirectory(install);
            var stage = Path.Combine(directory, "stage-" + test); Directory.CreateDirectory(stage);
            var target = Path.Combine(install, "CristalixSupport.exe"); File.Copy(probe, target);
            File.WriteAllText(Path.Combine(install, "connection.json"), "custom connection");
            File.Copy(probe, Path.Combine(stage, "CristalixSupport.exe"));
            if (test == 1) File.WriteAllText(Path.Combine(install, "fail-update"), "fail");
            File.WriteAllText(Path.Combine(stage, "plan.json"), JsonSerializer.Serialize(new UpdatePlan(target, int.MaxValue, 0, version.ToString(), Hash(probe))));
            string error = "";
            var code = UpdateInstaller.Apply(stage, text => error = text);
            Check(code == test, "Installer result incorrect: " + code + "; " + error);
            Check(File.ReadAllText(Path.Combine(install, "connection.json")) == "custom connection", "Connection settings overwritten");
            Check(Hash(target) == Hash(probe), "Installed or restored EXE invalid");
            if (test == 0) Check(File.Exists(target + ".previous") && error.Length == 0, "Previous version not retained");
            else
            {
                var watch = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(install, "rollback-started")) && watch.ElapsedMilliseconds < 5000) Thread.Sleep(50);
                Check(error.Length > 0 && File.Exists(Path.Combine(install, "rollback-started")), "Failed startup did not restart previous version");
            }
        }
        Console.WriteLine("PASS: update checksum, version verification, archive isolation, atomic replacement, startup handshake, rollback and preserved connection settings");
    }
}
