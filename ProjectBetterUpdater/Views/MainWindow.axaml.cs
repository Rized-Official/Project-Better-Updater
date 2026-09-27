using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;
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
                DateTimeOffset.UtcNow - _currentSettings.LastCheckUtc.Value < MinCheckInterval;

            if (skipCheck)
            {
                Log($"Skipping update check, last check was at {_currentSettings.LastCheckUtc:u}.");
                StatusText.Text = "Starting...";
                await Task.Delay(150);
                TryLaunchExeAndClose(exePath);
                return;
            }

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
                StatusText.Text = $"Downloading version {latestVersion}...";

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
                    try
                    {
                        File.Delete(zipFilePath);
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not delete old temp zip: {ex.Message}");
                    }
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

                StatusText.Text = $"Installing version {latestVersion}...";

                try
                {
                    ZipFile.ExtractToDirectory(zipFilePath, launcherDirectory, true);
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

                string extractedExe = FindLauncherExe(launcherDirectory);

                Log($"Extracted exe path found: '{extractedExe}'");

                if (!string.IsNullOrWhiteSpace(extractedExe) &&
                    !string.Equals(extractedExe, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (File.Exists(exePath))
                            File.Delete(exePath);

                        File.Move(extractedExe, exePath);
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not move extracted exe into place: {ex.Message}");
                    }
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

            StatusText.Text = $"Version {latestVersion} ready.";

            await Task.Delay(300);

            TryLaunchExeAndClose(exePath);
        }
        catch (Exception ex)
        {
            Log($"Unhandled error in CheckForUpdates: {ex}");
            await FailAndLaunch(exePath, "Unexpected error. Launching current version...");
        }
    }

    private async Task FailAndLaunch(string exePath, string message)
    {
        StatusText.Text = message;
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

            if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
                long.TryParse(resetValues.FirstOrDefault(), out long resetUnix))
            {
                DateTimeOffset resetTime = DateTimeOffset.FromUnixTimeSeconds(resetUnix);
                Log($"Rate limit resets at: {resetTime.ToLocalTime():u}");
            }

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

        await using FileStream fileStream =
            new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        await response.Content.CopyToAsync(fileStream);
    }

    private static bool IsLauncherAsset(string fileName)
    {
        string normalized = new string(
            fileName.Where(char.IsLetterOrDigit).ToArray()
        ).ToLowerInvariant();

        return normalized.Contains("projectbetterlauncher");
    }

    private string FindLauncherExe(string launcherDirectory)
    {
        if (!Directory.Exists(launcherDirectory))
            return string.Empty;

        string directPath = Path.Combine(launcherDirectory, "ProjectBetterLauncher.exe");

        if (File.Exists(directPath))
            return directPath;

        string[] files = Directory.GetFiles(
            launcherDirectory,
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