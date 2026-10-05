using AltyaziDB.Player.Core.Models;

namespace AltyaziDB.Player.Core.Interfaces;

public interface IUpdateService
{
    Version CurrentVersion { get; }
    Task<UpdateCheckResult> CheckAsync(string manifestUrl, string channel, CancellationToken cancellationToken = default);
    Task<UpdateDownloadResult> DownloadInstallerAsync(UpdateManifest manifest, string destinationDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task<UpdateDownloadResult> DownloadPortableAsync(UpdateManifest manifest, string destinationDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    void LaunchInstaller(string installerPath, bool silent);
}
