using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia.Controls;
using ProjectBetterUpdater.Helpers.Json;

namespace ProjectBetterUpdater.Views;

public partial class MainWindow : Window
{
    private readonly string BaseDirectory = AppContext.BaseDirectory;
    private readonly string SettingsFile;
    private readonly string LogFile;
    private readonly string TokenFile;

    private AppSettings _currentSettings;

    private const string GithubOwner = "Rized-Official";
    private const string GithubRepo = "Project-Better-Launcher";

    private static readonly TimeSpan MinCheckInterval = TimeSpan.FromMinutes(5);

    private static bool _hasStarted;
    private static bool _hasLaunched;

    private static readonly HttpClient HttpClient = new HttpClient();

    private static readonly Dictionary<string, JsonNode?> DefaultLauncherSettingsSchema = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DiscordRPC"] = true,
        ["AcceptedInstalled"] = false,
        ["BackgroundName"] = "defaultbackground.jpg",
        ["GameLocation"] = "",
        ["SharedOpinion"] = false,
        ["InstalledVersion"] = "NONE",
        ["BackgroundAnimation"] = true,
        ["HibernationMode"] = true,
        ["SoundVolume"] = 100,
        ["LastSeenChangelogVersion"] = "NONE"
    };

    public MainWindow()
    {
        InitializeComponent();

        string dataDirectory = Path.Combine(BaseDirectory, "Data");
        Directory.CreateDirectory(dataDirectory);

        SettingsFile = Path.Combine(dataDirectory, "Data.json");
        LogFile = Path.Combine(dataDirectory, "updater.log");
        TokenFile = Path.Combine(dataDirectory, "github_token.txt");

        _currentSettings = JsonStorageService.Load(SettingsFile, new AppSettings());

        HttpClient.DefaultRequestHeaders.UserAgent.Clear();
        HttpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ProjectBetterUpdater", "1.0"));

        HttpClient.DefaultRequestHeaders.Accept.Clear();
        HttpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        TryApplyGithubToken();
    }

    private void TryApplyGithubToken()
    {
        try
        {
            if (!File.Exists(TokenFile))
                return;

            string token = File.ReadAllText(TokenFile).Trim();

            if (string.IsNullOrWhiteSpace(token))
                return;

            HttpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            Log("GitHub token loaded from file.");
        }
        catch (Exception ex)
        {
            Log($"Could not load GitHub token: {ex.Message}");
        }
    }

    private void Window_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_hasLaunched)
        {
            e.Cancel = true;
        }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_hasStarted)
            return;

        _hasStarted = true;

        Log("=== Updater started ===");

        await CheckForUpdates();
    }

    private void Log(string message)
    {
        try
        {
            File.AppendAllText(
                LogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    public async Task CheckForUpdates()
    {
        string launcherDirectory = Path.Combine(BaseDirectory, "Data", "Launcher");
        string exePath = Path.Combine(launcherDirectory, "ProjectBetterLauncher.exe");
        string zipFilePath = Path.Combine(BaseDirectory, "Data", "update_temp.zip");

        Directory.CreateDirectory(launcherDirectory);

        try
        {
            bool skipCheck =
                File.Exists(exePath) &&
                _currentSettings.LastCheckUtc.HasValue &&
                (DateTimeOffset.UtcNow - _currentSettings.LastCheckUtc.Value) < MinCheckInterval;

            if (skipCheck)
            {
                Log($"Skipping update check, last check was at {_currentSettings.LastCheckUtc:u}.");
                WaitText.Text = "READY";
                StatusText.Text = "Starting Project: Better...";
                await Task.Delay(150);
                TryLaunchExeAndClose(exePath);
                return;
            }

            WaitText.Text = "CHECKING GITHUB";
            StatusText.Text = "Checking for updates...";
            Log("Checking for updates...");

            GitHubRelease? latestRelease = await GetLatestReleaseInfo();

            _currentSettings.LastCheckUtc = DateTimeOffset.UtcNow;
            JsonStorageService.Save(SettingsFile, _currentSettings);

            if (latestRelease == null)
            {
                Log("No release info returned (API failed or empty). Launching existing exe.");
                await FailAndLaunch(exePath, "Could not reach GitHub. Launching current version...");
                return;
            }

            string latestVersion = (latestRelease.TagName ?? latestRelease.Name ?? string.Empty).Trim();

            Log($"Latest release found: tag='{latestRelease.TagName}' name='{latestRelease.Name}' assets={latestRelease.Assets?.Length ?? 0}");

            if (string.IsNullOrWhiteSpace(latestVersion))
            {
                Log("Latest version string is empty. Launching existing exe.");
                await FailAndLaunch(exePath, "Could not determine latest version...");
                return;
            }

            Log($"Local version: '{_currentSettings.Version}' | Remote version: '{latestVersion}' | Exe exists: {File.Exists(exePath)}");

            bool updateRequired =
                !File.Exists(exePath) ||
                string.IsNullOrWhiteSpace(_currentSettings.Version) ||
                _currentSettings.Version == "no version" ||
                !string.Equals(
                    _currentSettings.Version.Trim(),
                    latestVersion,
                    StringComparison.OrdinalIgnoreCase);

            Log($"Update required: {updateRequired}");

            if (updateRequired)
            {
                WaitText.Text = $"UPDATE FOUND ({latestVersion})";
                StatusText.Text = $"Downloading {latestVersion}...";

                GitHubAsset? asset =
                    latestRelease.Assets?
                        .Where(a =>
                            !string.IsNullOrWhiteSpace(a.Name) &&
                            a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(a => IsLauncherAsset(a.Name!))
                        .FirstOrDefault();

                Log($"Chosen asset: name='{asset?.Name}' url='{asset?.BrowserDownloadUrl}'");

                if (asset == null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                {
                    Log("No matching zip asset found in release.");
                    await FailAndLaunch(exePath, "No update package found on GitHub...");
                    return;
                }

                if (File.Exists(zipFilePath))
                {
                    try { File.Delete(zipFilePath); }
                    catch (Exception ex) { Log($"Could not delete old temp zip: {ex.Message}"); }
                }

                try
                {
                    await DownloadFileAsync(asset.BrowserDownloadUrl, zipFilePath);
                }
                catch (Exception ex)
                {
                    Log($"Download failed: {ex}");
                    await FailAndLaunch(exePath, "Download failed. Launching current version...");
                    return;
                }

                if (!File.Exists(zipFilePath))
                {
                    Log("Zip file missing after download.");
                    await FailAndLaunch(exePath, "Download failed. Launching current version...");
                    return;
                }

                Log($"Downloaded zip size: {new FileInfo(zipFilePath).Length} bytes");

                WaitText.Text = "INSTALLING UPDATE";
                StatusText.Text = $"Installing version {latestVersion}...";
                ProgressBorder.IsVisible = false;

                EnsureLauncherClosed("ProjectBetterLauncher");

                try
                {
                    SmartExtractAndMergeUserData(zipFilePath, launcherDirectory);
                }
                catch (Exception ex)
                {
                    Log($"Extraction failed: {ex}");
                    await FailAndLaunch(exePath, "Installation failed. Launching current version...");
                    return;
                }

                try
                {
                    File.Delete(zipFilePath);
                }
                catch (Exception ex)
                {
                    Log($"Could not delete zip after extraction: {ex.Message}");
                }

                if (!File.Exists(exePath))
                {
                    Log("Exe still missing after install step.");
                    await FailAndLaunch(exePath, "Installation failed. Launching current version...");
                    return;
                }

                _currentSettings.Version = latestVersion;
                JsonStorageService.Save(SettingsFile, _currentSettings);

                Log($"Update applied successfully. New local version: {latestVersion}");
            }

            WaitText.Text = "READY";
            StatusText.Text = $"Version {latestVersion} ready!";

            await Task.Delay(300);

            TryLaunchExeAndClose(exePath);
        }
        catch (Exception ex)
        {
            Log($"Unhandled error in CheckForUpdates: {ex}");
            await FailAndLaunch(exePath, "Unexpected error. Launching current version...");
        }
    }

    private void SmartExtractAndMergeUserData(string zipFilePath, string launcherDirectory)
    {
        string settingsRelPath = Path.Combine("Data", "Settings.json");
        string backgroundRelPath = Path.Combine("Assets", "background.jpg");

        string existingSettingsPath = Path.Combine(launcherDirectory, settingsRelPath);
        string existingBgPath = Path.Combine(launcherDirectory, backgroundRelPath);

        string? oldSettingsJson = File.Exists(existingSettingsPath)
            ? File.ReadAllText(existingSettingsPath)
            : null;

        byte[]? oldBackgroundBytes = File.Exists(existingBgPath)
            ? File.ReadAllBytes(existingBgPath)
            : null;

        string tempExtractDir = Path.Combine(BaseDirectory, "Data", "_extract_temp");
        if (Directory.Exists(tempExtractDir))
        {
            Directory.Delete(tempExtractDir, true);
        }
        Directory.CreateDirectory(tempExtractDir);

        try
        {
            ZipFile.ExtractToDirectory(zipFilePath, tempExtractDir, true);

            string extractedExePath = FindLauncherExe(tempExtractDir);
            string sourceRootDir = !string.IsNullOrEmpty(extractedExePath)
                ? Path.GetDirectoryName(extractedExePath)!
                : tempExtractDir;

            string? zipSettingsJson = null;
            string zipSettingsFile = Path.Combine(sourceRootDir, settingsRelPath);
            if (File.Exists(zipSettingsFile))
            {
                zipSettingsJson = File.ReadAllText(zipSettingsFile);
            }

            foreach (string sourceFile in Directory.GetFiles(sourceRootDir, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(sourceRootDir, sourceFile);
                string normalizedRel = relativePath.Replace('/', '\\').TrimStart('\\');
                string targetFile = Path.Combine(launcherDirectory, relativePath);

                if (string.Equals(normalizedRel, backgroundRelPath, StringComparison.OrdinalIgnoreCase) && oldBackgroundBytes != null)
                {
                    continue;
                }

                if (string.Equals(normalizedRel, settingsRelPath, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(oldSettingsJson))
                {
                    continue;
                }

                string? targetFolder = Path.GetDirectoryName(targetFile);
                if (!string.IsNullOrEmpty(targetFolder))
                {
                    Directory.CreateDirectory(targetFolder);
                }

                File.Copy(sourceFile, targetFile, overwrite: true);
            }

            if (oldBackgroundBytes != null)
            {
                string? bgDir = Path.GetDirectoryName(existingBgPath);
                if (!string.IsNullOrEmpty(bgDir))
                    Directory.CreateDirectory(bgDir);

                File.WriteAllBytes(existingBgPath, oldBackgroundBytes);
            }

            MergeAndSaveLauncherSettings(existingSettingsPath, oldSettingsJson, zipSettingsJson);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempExtractDir))
                    Directory.Delete(tempExtractDir, true);
            }
            catch (Exception ex)
            {
                Log($"Could not delete temp extract folder: {ex.Message}");
            }
        }
    }

    private void MergeAndSaveLauncherSettings(string targetSettingsPath, string? existingUserJson, string? newReleaseJson)
    {
        try
        {
            JsonObject userObj = new JsonObject();

            if (!string.IsNullOrWhiteSpace(existingUserJson))
            {
                var parsedUser = JsonNode.Parse(existingUserJson) as JsonObject;
                if (parsedUser != null)
                {
                    userObj = parsedUser;
                }
            }

            if (!string.IsNullOrWhiteSpace(newReleaseJson))
            {
                var parsedRelease = JsonNode.Parse(newReleaseJson) as JsonObject;
                if (parsedRelease != null)
                {
                    foreach (var kvp in parsedRelease)
                    {
                        if (!ContainsKeyIgnoreCase(userObj, kvp.Key))
                        {
                            userObj[kvp.Key] = kvp.Value?.DeepClone();
                            Log($"Added new JSON entry from release: {kvp.Key}");
                        }
                    }
                }
            }

            foreach (var defaultEntry in DefaultLauncherSettingsSchema)
            {
                if (!ContainsKeyIgnoreCase(userObj, defaultEntry.Key))
                {
                    userObj[defaultEntry.Key] = defaultEntry.Value?.DeepClone();
                    Log($"Added missing default JSON entry: {defaultEntry.Key}");
                }
            }

            string? dir = Path.GetDirectoryName(targetSettingsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(targetSettingsPath, userObj.ToJsonString(options));
        }
        catch (Exception ex)
        {
            Log($"MergeAndSaveLauncherSettings error: {ex.Message}");
            if (!string.IsNullOrWhiteSpace(existingUserJson) && !File.Exists(targetSettingsPath))
            {
                File.WriteAllText(targetSettingsPath, existingUserJson);
            }
        }
    }

    private static bool ContainsKeyIgnoreCase(JsonObject obj, string key)
    {
        return obj.Any(kvp => string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureLauncherClosed(string processName)
    {
        try
        {
            Process[] processes = Process.GetProcessesByName(processName);
            foreach (Process proc in processes)
            {
                try
                {
                    proc.Kill();
                    proc.WaitForExit(2000);
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log($"EnsureLauncherClosed warning: {ex.Message}");
        }
    }

    private async Task FailAndLaunch(string exePath, string message)
    {
        WaitText.Text = "OFFLINE / FALLBACK";
        StatusText.Text = message;
        ProgressBorder.IsVisible = false;
        await Task.Delay(1200);
        TryLaunchExeAndClose(exePath);
    }

    private async Task<GitHubRelease?> GetLatestReleaseInfo()
    {
        try
        {
            string url =
                $"https://api.github.com/repos/{GithubOwner}/{GithubRepo}/releases?per_page=30&nocache={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);

            request.Headers.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true,
                MustRevalidate = true
            };

            using HttpResponseMessage response = await HttpClient.SendAsync(request);

            Log($"GitHub API response: {(int)response.StatusCode} {response.StatusCode}");

            if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining))
                Log($"Rate limit remaining: {string.Join(",", remaining)}");

            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                Log($"GitHub API error body: {body}");
                return null;
            }

            string json = await response.Content.ReadAsStringAsync();

            GitHubRelease[]? releases =
                JsonSerializer.Deserialize<GitHubRelease[]>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (releases == null || releases.Length == 0)
            {
                Log("Releases array empty.");
                return null;
            }

            return releases
                .Where(r => !string.IsNullOrWhiteSpace(r.TagName))
                .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log($"GetLatestReleaseInfo exception: {ex}");
            return null;
        }
    }

    private async Task DownloadFileAsync(string url, string destinationPath)
    {
        using HttpResponseMessage response =
            await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

        Log($"Download response: {(int)response.StatusCode} {response.StatusCode}");

        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength;

        ProgressBorder.IsVisible = true;
        DownloadProgressBar.Value = 0;
        ProgressPercentText.Text = "0%";

        await using Stream contentStream = await response.Content.ReadAsStreamAsync();
        await using FileStream fileStream =
            new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        byte[] buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalRead += bytesRead;

            if (totalBytes.HasValue && totalBytes.Value > 0)
            {
                double pct = (double)totalRead / totalBytes.Value * 100.0;
                double readMb = totalRead / 1024d / 1024d;
                double totalMb = totalBytes.Value / 1024d / 1024d;

                DownloadProgressBar.Value = pct;
                ProgressPercentText.Text = $"{pct:0}% ({readMb:0.1}/{totalMb:0.1} MB)";
            }
            else
            {
                double readMb = totalRead / 1024d / 1024d;
                ProgressPercentText.Text = $"{readMb:0.1} MB";
            }
        }
    }

    private static bool IsLauncherAsset(string fileName)
    {
        string normalized = new string(
            fileName.Where(char.IsLetterOrDigit).ToArray()
        ).ToLowerInvariant();

        return normalized.Contains("projectbetterlauncher");
    }

    private string FindLauncherExe(string directory)
    {
        if (!Directory.Exists(directory))
            return string.Empty;

        string directPath = Path.Combine(directory, "ProjectBetterLauncher.exe");

        if (File.Exists(directPath))
            return directPath;

        string[] files = Directory.GetFiles(
            directory,
            "ProjectBetterLauncher.exe",
            SearchOption.AllDirectories);

        return files.FirstOrDefault() ?? string.Empty;
    }

    private void TryLaunchExeAndClose(string exePath)
    {
        if (_hasLaunched)
            return;

        try
        {
            if (!File.Exists(exePath))
            {
                Log("TryLaunchExeAndClose: exe does not exist.");
                return;
            }

            string processName = Path.GetFileNameWithoutExtension(exePath);

            if (Process.GetProcessesByName(processName).Length > 0)
            {
                Log("Process already running, exiting updater.");
                Environment.Exit(0);
                return;
            }

            _hasLaunched = true;

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
                UseShellExecute = true
            });

            Log($"Launched: {exePath}");

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Log($"TryLaunchExeAndClose exception: {ex}");
            _hasLaunched = false;
        }
    }
}

public class GitHubRelease
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("assets")]
    public GitHubAsset[]? Assets { get; set; }
}

public class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}