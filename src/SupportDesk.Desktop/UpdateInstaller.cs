using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
namespace SupportDesk.Desktop;

public sealed record UpdatePlan(string Target, int ParentId, long ParentStarted, string Version, string Sha256);
public static class UpdateInstaller
{
    const long MaxPackage = 160 * 1024 * 1024;
    const long MaxExtracted = 240 * 1024 * 1024;
    static readonly string[] AllowedFiles = ["CristalixSupport.exe", "connection.json", "release.json", "README.md"];

    public static async Task<string> Download(AppRelease release, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var package = release.Package ?? throw new InvalidOperationException("В релизе нет Windows-архива.");
        if (!ReleaseClient.IsAssetUrl(release, package) || package.Size <= 0 || package.Size > MaxPackage)
            throw new InvalidOperationException("Некорректный архив обновления.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var ct = timeout.Token;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5), MaxResponseContentBufferSize = 128 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CristalixSupport/" + ReleaseClient.CurrentLabel);
        string? expectedHash = package.Sha256;
        if (expectedHash is null && release.Checksums is { } checksums && checksums.Size <= 128 * 1024 && ReleaseClient.IsAssetUrl(release, checksums))
        {
            var text = await http.GetStringAsync(checksums.Url, ct);
            expectedHash = text.Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length == 2 && p[1].TrimStart('*') == package.Name).Select(p => p[0]).SingleOrDefault();
        }
        if (expectedHash is null || !Regex.IsMatch(expectedHash, "^[a-fA-F0-9]{64}$"))
            throw new InvalidOperationException("В релизе отсутствует контрольная сумма SHA-256.");
        var stage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CristalixSupportPublic", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            var zip = Path.Combine(stage, "package.zip");
            using var response = await http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != "https") throw new InvalidOperationException("Требуется защищенное соединение.");
            if (response.Content.Headers.ContentLength is long length && length != package.Size) throw new InvalidOperationException("Размер обновления изменился. Повторите проверку версий.");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using (var output = File.Create(zip))
            {
                var buffer = new byte[65536]; long received = 0; int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    received += count;
                    if (received > package.Size) throw new InvalidOperationException("Превышен размер обновления.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    progress?.Report((int)(100 * received / package.Size));
                }
                if (received != package.Size) throw new InvalidOperationException("Обновление скачалось не полностью.");
            }
            await Task.Run(() => ExtractVerified(zip, expectedHash, stage, release.Version), ct);
            File.Delete(zip);
            return stage;
        }
        catch { DeleteStage(stage); throw; }
    }

    public static void ExtractVerified(string zip, string sha256, string directory, Version version)
    {
        using (var stream = File.OpenRead(zip))
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Контрольная сумма обновления не совпала.");
        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > AllowedFiles.Length || archive.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != archive.Entries.Count ||
            archive.Entries.Any(e => !AllowedFiles.Contains(e.FullName, StringComparer.Ordinal)) || archive.Entries.Sum(e => e.Length) > MaxExtracted ||
            archive.GetEntry("CristalixSupport.exe") is null)
            throw new InvalidOperationException("Некорректное содержимое обновления.");
        foreach (var entry in archive.Entries)
        {
            using var input = entry.Open();
            using var output = File.Create(Path.Combine(directory, entry.FullName));
            var buffer = new byte[65536]; long received = 0; int count;
            while ((count = input.Read(buffer)) > 0)
            {
                received += count;
                if (received > entry.Length || received > MaxExtracted) throw new InvalidOperationException("Превышен размер файлов обновления.");
                output.Write(buffer, 0, count);
            }
            if (received != entry.Length) throw new InvalidOperationException("Файл обновления поврежден.");
        }
        var executable = FileVersionInfo.GetVersionInfo(Path.Combine(directory, "CristalixSupport.exe"));
        if (executable.FileMajorPart != version.Major || executable.FileMinorPart != version.Minor || executable.FileBuildPart != Math.Max(0, version.Build))
            throw new InvalidOperationException("Версия EXE не соответствует релизу.");
    }

    public static async Task Start(string directory, Version version)
    {
        var target = Environment.ProcessPath!;
        if (!Path.GetFileName(target).Equals("CristalixSupport.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Установка обновлений доступна в готовой EXE-сборке.");
        string probe = Path.Combine(Path.GetDirectoryName(target)!, ".update-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
        using var parent = Process.GetCurrentProcess();
        var exe = Path.Combine(directory, "CristalixSupport.exe");
        using var stream = File.OpenRead(exe);
        var plan = new UpdatePlan(target, parent.Id, parent.StartTime.ToUniversalTime().Ticks, version.ToString(), Convert.ToHexString(SHA256.HashData(stream)));
        File.WriteAllText(Path.Combine(directory, "plan.json"), JsonSerializer.Serialize(plan));
        var helper = Path.Combine(directory, "updater.exe");
        File.Copy(target, helper);
        var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        info.ArgumentList.Add("--apply-update");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить установку.");
        var watch = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, "ready")))
        {
            if (process.HasExited || watch.Elapsed > TimeSpan.FromSeconds(20))
            {
                if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
                throw new InvalidOperationException("Не удалось подготовить установку. Приложение остается открытым.");
            }
            await Task.Delay(50);
        }
    }

    public static int Apply(string? stageDirectory = null, Action<string>? reportError = null)
    {
        string directory = stageDirectory ?? AppContext.BaseDirectory, target = "", backup = "", replacement = "";
        bool parentExited = false, replaced = false;
        Process? restarted = null;
        try
        {
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(Path.Combine(directory, "plan.json"))) ?? throw new InvalidOperationException();
            target = Path.GetFullPath(plan.Target);
            if (!Path.IsPathFullyQualified(plan.Target) || Path.GetFileName(target) != "CristalixSupport.exe" || !File.Exists(target) || Path.GetDirectoryName(target) == Path.TrimEndingDirectorySeparator(directory)) throw new InvalidOperationException("Некорректная папка установки.");
            var executable = Path.Combine(directory, "CristalixSupport.exe");
            using (var input = File.OpenRead(executable))
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(plan.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Скачанный файл изменен.");
            File.WriteAllText(Path.Combine(directory, "ready"), "ok");
            try
            {
                using var parent = Process.GetProcessById(plan.ParentId);
                if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStarted) throw new InvalidOperationException("Процесс приложения изменился.");
                if (!parent.WaitForExit(60000)) throw new InvalidOperationException("Приложение не завершилось. Закройте его и повторите обновление.");
            }
            catch (ArgumentException) { /* The expected parent has already exited. */ }
            parentExited = true;
            backup = target + ".previous";
            replacement = target + ".update-" + Guid.NewGuid().ToString("N");
            File.Copy(executable, replacement);
            ReplaceExecutable(replacement, target, backup);
            replaced = true;
            // Connection settings and the user profile are deliberately kept in place.
            if (File.Exists(Path.Combine(directory, "release.json")))
            {
                try { File.Copy(Path.Combine(directory, "release.json"), Path.Combine(Path.GetDirectoryName(target)!, "release.json"), true); }
                catch (IOException) { }
            }
            var start = new ProcessStartInfo(target) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(target)! };
            start.ArgumentList.Add("--update-ready"); start.ArgumentList.Add(Path.Combine(directory, "started"));
            restarted = Process.Start(start) ?? throw new InvalidOperationException("Не удалось перезапустить приложение.");
            var watch = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, "started")))
            {
                if (restarted.HasExited || watch.Elapsed > TimeSpan.FromSeconds(20)) throw new InvalidOperationException("Новая версия не запустилась.");
                Thread.Sleep(50);
            }
            if (File.ReadAllText(Path.Combine(directory, "started")) != plan.Version) throw new InvalidOperationException("Запустилась другая версия.");
            DeleteStage(directory);
            return 0;
        }
        catch (Exception)
        {
            if (restarted is not null) try { if (!restarted.HasExited) { restarted.Kill(); restarted.WaitForExit(5000); } } catch { }
            if (replaced && File.Exists(backup))
                try { File.Replace(backup, target, null, true); } catch { }
            if (parentExited && File.Exists(target))
                try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
            const string error = "Не удалось установить обновление. Прежняя версия сохранена. Проверьте доступ к папке приложения и повторите попытку.";
            if (reportError is null) MessageBox.Show(error, "Обновление Cristalix Support"); else reportError(error);
            return 1;
        }
        finally { restarted?.Dispose(); if (replacement.Length > 0 && File.Exists(replacement)) try { File.Delete(replacement); } catch { } }
    }

    public static void ReplaceExecutable(string replacement, string target, string backup)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.Replace(replacement, target, backup, true); return; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(200); }
        }
    }

    public static void DeleteStage(string directory)
    {
        foreach (string name in AllowedFiles.Concat(["package.zip", "plan.json", "ready", "started", "started.tmp", "updater.exe"]))
            try { File.Delete(Path.Combine(directory, name)); } catch { }
        try { Directory.Delete(directory); } catch { }
    }

    public static void SignalStarted(string[] args)
    {
        if (args.Length != 2 || args[0] != "--update-ready") return;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CristalixSupportPublic", "updates");
        string path = Path.GetFullPath(args[1]);
        var folder = Path.GetDirectoryName(path)!;
        if (Path.GetDirectoryName(folder) == root && Regex.IsMatch(Path.GetFileName(folder), "^[a-f0-9]{32}$") && Path.GetFileName(path) == "started")
            { File.WriteAllText(path + ".tmp", ReleaseClient.CurrentLabel); File.Move(path + ".tmp", path, true); }
    }

    public static void CleanOldStages()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CristalixSupportPublic", "updates");
        if (!Directory.Exists(root)) return;
        foreach (var folder in new DirectoryInfo(root).EnumerateDirectories())
            if (Regex.IsMatch(folder.Name, "^[a-f0-9]{32}$") && (folder.Attributes & FileAttributes.ReparsePoint) == 0 && folder.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-2))
                DeleteStage(folder.FullName);
    }
}
