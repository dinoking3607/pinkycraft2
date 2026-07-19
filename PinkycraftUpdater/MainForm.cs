using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PinkycraftUpdater;

public sealed class MainForm : Form
{
    private const string LatestReleaseApi = "https://api.github.com/repos/dinoking3607/pinkycraft2/releases/latest";
    private static readonly Regex PartPattern = new(@"^(?<base>.+\.zip)\.(?<part>\d{3,})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ProgressBar _progressBar = new();
    private readonly Label _percentageLabel = new();
    private readonly Button _updateButton = new();
    private readonly HttpClient _httpClient = new();

    public MainForm()
    {
        Text = "Pinkycraft 2 Updater";
        ClientSize = new Size(460, 145);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

        _progressBar.SetBounds(28, 28, 404, 28);
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Value = 0;

        _percentageLabel.SetBounds(28, 61, 404, 24);
        _percentageLabel.Text = "0%";
        _percentageLabel.TextAlign = ContentAlignment.MiddleCenter;

        _updateButton.SetBounds(145, 94, 170, 34);
        _updateButton.Text = "Update Modpack";
        _updateButton.UseVisualStyleBackColor = true;
        _updateButton.Click += UpdateButton_Click;

        Controls.AddRange([_progressBar, _percentageLabel, _updateButton]);

        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PinkycraftUpdater", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.Timeout = TimeSpan.FromHours(2);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _httpClient.Dispose();
        }
        base.Dispose(disposing);
    }

    private async void UpdateButton_Click(object? sender, EventArgs e)
    {
        _updateButton.Enabled = false;
        _updateButton.Text = "Updating...";
        SetProgress(0);

        try
        {
            await RunUpdateAsync();
            SetProgress(100);
            _updateButton.Text = "Close";
            _updateButton.Enabled = true;
            _updateButton.Click -= UpdateButton_Click;
            _updateButton.Click += (_, _) => Close();
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            _updateButton.Text = "Try Again";
            _updateButton.Enabled = true;
            MessageBox.Show(
                $"Update failed.\n\n{ex.Message}\n\nDetails were saved to:\n{Program.LogPath}",
                "Pinkycraft 2 Updater",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task RunUpdateAsync()
    {
        string installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        string updaterPath = Environment.ProcessPath ?? Application.ExecutablePath;
        string updaterFileName = Path.GetFileName(updaterPath);
        string tempRoot = Path.Combine(Path.GetTempPath(), "PinkycraftUpdater", Guid.NewGuid().ToString("N"));
        string partsDirectory = Path.Combine(tempRoot, "parts");
        string stagingDirectory = Path.Combine(tempRoot, "staging");
        Directory.CreateDirectory(partsDirectory);
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            SetProgress(2);
            ReleaseInfo release = await GetLatestReleaseAsync();
            List<ArchivePart> parts = FindArchiveParts(release.Assets);
            long totalDownloadBytes = parts.Sum(p => p.Size);
            long downloadedBytes = 0;

            for (int i = 0; i < parts.Count; i++)
            {
                ArchivePart part = parts[i];
                string destination = Path.Combine(partsDirectory, part.Name);
                long beforePart = downloadedBytes;

                await DownloadFileAsync(part.DownloadUrl, destination, bytesForPart =>
                {
                    long currentTotal = beforePart + bytesForPart;
                    int percent = totalDownloadBytes > 0
                        ? 5 + (int)Math.Clamp(currentTotal * 65L / totalDownloadBytes, 0, 65)
                        : 5 + (int)((i + 1) * 65.0 / parts.Count);
                    SetProgress(percent);
                });

                downloadedBytes += new FileInfo(destination).Length;
            }

            SetProgress(72);
            string joinedZip = Path.Combine(tempRoot, "Pinkycraft2-joined.zip");
            await JoinPartsAsync(parts.Select(p => Path.Combine(partsDirectory, p.Name)), joinedZip);

            SetProgress(78);
            await Task.Run(() => ZipFile.ExtractToDirectory(joinedZip, stagingDirectory, overwriteFiles: true));

            SetProgress(84);
            List<string> files = Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories).ToList();
            for (int i = 0; i < files.Count; i++)
            {
                string source = files[i];
                string relative = Path.GetRelativePath(stagingDirectory, source);

                // Never replace the currently running updater or another root-level updater executable.
                if (string.Equals(relative, updaterFileName, StringComparison.OrdinalIgnoreCase) ||
                    (!relative.Contains(Path.DirectorySeparatorChar) && relative.EndsWith("Updater.exe", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string destination = Path.Combine(installDirectory, relative);
                string? destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                await CopyWithRetryAsync(source, destination);
                int percent = 84 + (files.Count == 0 ? 15 : (int)((i + 1) * 15.0 / files.Count));
                SetProgress(Math.Min(percent, 99));
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch (Exception cleanupError)
            {
                Program.Log(cleanupError);
            }
        }
    }

    private async Task<ReleaseInfo> GetLatestReleaseAsync()
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseHeadersRead);
        string json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}. Make sure the release is published and marked as the latest release.");
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        List<ReleaseAsset> assets = [];
        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            assets.Add(new ReleaseAsset(
                asset.GetProperty("name").GetString() ?? string.Empty,
                asset.GetProperty("browser_download_url").GetString() ?? string.Empty,
                asset.TryGetProperty("size", out JsonElement size) ? size.GetInt64() : 0));
        }

        return new ReleaseInfo(assets);
    }

    private static List<ArchivePart> FindArchiveParts(IEnumerable<ReleaseAsset> assets)
    {
        var matches = assets
            .Select(asset => (asset, match: PartPattern.Match(asset.Name)))
            .Where(x => x.match.Success)
            .Select(x => new
            {
                BaseName = x.match.Groups["base"].Value,
                PartNumber = int.Parse(x.match.Groups["part"].Value),
                x.asset
            })
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvalidOperationException("The latest release has no split ZIP assets. Expected names like Pinkycraft2.zip.001, Pinkycraft2.zip.002, and so on.");
        }

        var selectedGroup = matches
            .GroupBy(x => x.BaseName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .First();

        List<ArchivePart> parts = selectedGroup
            .OrderBy(x => x.PartNumber)
            .Select(x => new ArchivePart(x.asset.Name, x.asset.DownloadUrl, x.asset.Size, x.PartNumber))
            .ToList();

        if (parts[0].PartNumber != 1)
        {
            throw new InvalidOperationException("The first archive part (.001) is missing.");
        }

        for (int i = 0; i < parts.Count; i++)
        {
            int expected = i + 1;
            if (parts[i].PartNumber != expected)
            {
                throw new InvalidOperationException($"Archive part .{expected:000} is missing.");
            }
        }

        return parts;
    }

    private async Task DownloadFileAsync(string url, string destination, Action<long> progress)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using Stream input = await response.Content.ReadAsStreamAsync();
        await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        byte[] buffer = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            total += read;
            progress(total);
        }
    }

    private static async Task JoinPartsAsync(IEnumerable<string> partPaths, string destination)
    {
        await using FileStream output = new(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        byte[] buffer = new byte[1024 * 1024];
        foreach (string partPath in partPaths)
        {
            await using FileStream input = new(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
            int read;
            while ((read = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
            }
        }
    }

    private static async Task CopyWithRetryAsync(string source, string destination)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException ex)
            {
                lastError = ex;
                await Task.Delay(attempt * 300);
            }
            catch (UnauthorizedAccessException ex)
            {
                lastError = ex;
                await Task.Delay(attempt * 300);
            }
        }

        throw new IOException($"Could not replace '{destination}'. Close Minecraft and any program using the modpack files, then try again.", lastError);
    }

    private void SetProgress(int value)
    {
        value = Math.Clamp(value, 0, 100);
        if (InvokeRequired)
        {
            BeginInvoke(() => SetProgress(value));
            return;
        }

        _progressBar.Value = value;
        _percentageLabel.Text = $"{value}%";
    }

    private sealed record ReleaseInfo(List<ReleaseAsset> Assets);
    private sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);
    private sealed record ArchivePart(string Name, string DownloadUrl, long Size, int PartNumber);
}
