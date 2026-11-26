using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lifeline;

internal static class LifelineUpdater
{
    private const string GithubApiUrl = "https://api.github.com/repos/idragknight/Lifeline/releases/latest";

    public class GithubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("assets")] public List<GithubAsset> Assets { get; set; } = new();
    }

    public class GithubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    }

    public static async Task<(bool UpdateAvailable, string LatestVersion, string DownloadUrl)> CheckForUpdateAsync(string currentVersion)
    {
        try
        {
            using HttpClient client = new();
            client.DefaultRequestHeaders.Add("User-Agent", "Lifeline-Plugin");
            string json = await client.GetStringAsync(GithubApiUrl);
            var release = JsonSerializer.Deserialize<GithubRelease>(json);

            if (release == null || string.IsNullOrEmpty(release.TagName)) return (false, currentVersion, "");

            string latestVersion = release.TagName.TrimStart('v');
            string downloadUrl = release.Assets.FirstOrDefault(a => a.Name == "Lifeline.zip")?.DownloadUrl ?? "";

            if (Version.TryParse(latestVersion, out var latest) && Version.TryParse(currentVersion, out var current))
            {
                return (latest > current, latestVersion, downloadUrl);
            }
        }
        catch (Exception ex)
        {
            LifelineLog.Write("updater", $"Update check failed: {ex.Message}");
        }
        return (false, currentVersion, "");
    }

    public static async Task<bool> DownloadAndTriggerUpdateAsync(string downloadUrl, Action<string> log)
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string stagingDir = Path.Combine(appData, "VibeSkua", "lifeline", "update_staging");

            if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);

            log("Downloading update from GitHub...");
            using HttpClient client = new();
            byte[] zipData = await client.GetByteArrayAsync(downloadUrl);
            string zipPath = Path.Combine(stagingDir, "Lifeline.zip");
            File.WriteAllBytes(zipPath, zipData);

            log("Extracting files...");
            ZipFile.ExtractToDirectory(zipPath, stagingDir, true);
            File.Delete(zipPath);

            // Write the special update request for the Helper
            string requestsDir = Path.Combine(appData, "VibeSkua", "lifeline", "requests");
            Directory.CreateDirectory(requestsDir);
            
            string username = Skua.Core.Interfaces.IScriptInterface.Instance?.Player?.Username ?? "unknown";
            string[] args = Environment.GetCommandLineArgs().Skip(1).ToArray();

            var req = new RestartRequest
            {
                Action = "update",
                Pid = Environment.ProcessId,
                Username = username,
                Exe = Environment.ProcessPath ?? "",
                Args = args,
                Reason = "User triggered auto-update",
                LoadedScript = Skua.Core.Interfaces.IScriptInterface.Instance?.Manager?.LoadedScript,
                ScriptWasRunning = Skua.Core.Interfaces.IScriptInterface.Instance?.Manager?.ScriptRunning ?? false,
                StagingDir = stagingDir,
                WrittenAt = DateTime.UtcNow.ToString("O")
            };

            string reqPath = Path.Combine(requestsDir, $"{Environment.ProcessId}.restart.json");
            File.WriteAllText(reqPath, JsonSerializer.Serialize(req, new JsonSerializerOptions { WriteIndented = true }));
            
            log("Update downloaded. Closing client to let Helper install...");
            return true;
        }
        catch (Exception ex)
        {
            log($"Update preparation failed: {ex.Message}");
            return false;
        }
    }
}