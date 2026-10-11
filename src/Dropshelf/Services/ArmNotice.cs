using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dropshelf.Activities.Hud;
using Dropshelf.Interop;

namespace Dropshelf.Services;

/// <summary>
/// The x64 build running emulated on an Arm PC works, but the Arm build is faster and kinder to the battery. This detects that
/// case (<c>IsWow64Process2</c> says the native machine is Arm64 while this process is x64), tells the user once on the pill,
/// keeps a line with a button in Settings (until "Don't show again"), and on the click downloads <c>Dropshelf-arm64.exe</c>
/// from the latest release to %TEMP% with a progress ring on the pill and runs it: the installer in that exe replaces the
/// installed copy, because its version is equal or newer. Nothing is ever downloaded without that click. The MSIX build is a
/// bundle, so Windows already picks the Arm package there and this never applies.
/// </summary>
internal sealed class ArmNotice
{
    public const string DownloadUrl = "https://github.com/Nikolay833/Practice1/releases/latest/download/Dropshelf-arm64.exe";

    private const long MinPlausibleBytes = 1_000_000;

    private readonly SettingsService _settings;
    private readonly ProgressActivity _progress;
    private readonly ToastActivity _toast;
    private bool _busy;

    public ArmNotice(SettingsService settings, ProgressActivity progress, ToastActivity toast)
    {
        _settings = settings;
        _progress = progress;
        _toast = toast;
        Current = this;
    }

    public static ArmNotice? Current { get; private set; }

    /// <summary>An x64 process on an Arm64 Windows (emulated). Never true for the MSIX build.</summary>
    public static bool IsX64OnArm { get; } = Detect();

    public bool IsBusy => _busy;

    /// <summary>Show the persistent line in Settings > General.</summary>
    public bool OfferInSettings => IsX64OnArm && !_settings.Current.ArmNoticeHidden;

    private static bool Detect()
    {
        if (PackageIdentity.IsPackaged || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return false;
        }

        try
        {
            return NativeMethods.IsWow64Process2(NativeMethods.GetCurrentProcess(), out _, out ushort native)
                   && native == NativeMethods.IMAGE_FILE_MACHINE_ARM64;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return false; // Windows older than 10 1709: no Arm64 editions
        }
    }

    /// <summary>Shows the pill notice the first time only (it is remembered even if the user ignores it; Settings keeps the line).</summary>
    public void ShowOnce()
    {
        if (!OfferInSettings || _settings.Current.ArmNoticeShown)
        {
            return;
        }

        _settings.Current.ArmNoticeShown = true;
        _toast.Show(new ToastSpec
        {
            Glyph = "", // download
            Text = "You're on an Arm PC",
            Detail = "The Arm version is faster",
            ButtonText = "Get Arm version",
            ButtonClick = () => _ = DownloadAndRunAsync(),
            Duration = TimeSpan.FromSeconds(9),
        });
    }

    /// <summary>"Don't show again": the pill notice stays gone and the line leaves Settings.</summary>
    public void Hide() => _settings.Current.ArmNoticeHidden = true;

    /// <summary>Downloads the Arm exe with progress on the pill and starts it (UI thread). Failures end in a toast, never an exception.</summary>
    public async Task DownloadAndRunAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        using ProgressActivity.Job job = _progress.Begin("Downloading Arm version");
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "Dropshelf-Setup");
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "Dropshelf-arm64.exe");
            string partial = target + ".download";

            // A dedicated client: the shared one cuts requests off after 10 seconds, which is too short for an exe of this size.
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Dropshelf", "1.0"));
            using HttpResponseMessage response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? -1;
            long received = 0;
            await using (Stream source = await response.Content.ReadAsStreamAsync())
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    job.Report(total > 0 ? Math.Min(0.99, (double)received / total) : -1);
                }
            }

            if (!LooksLikeExe(partial))
            {
                File.Delete(partial);
                throw new InvalidDataException("The download is not a Windows program");
            }

            File.Move(partial, target, overwrite: true);
            job.Report(1);
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = false }); // it installs itself and asks this copy to quit
            Log.Info("Started the downloaded Arm version");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException
                                       or InvalidDataException or Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not get the Arm version", ex);
            _toast.Show(new ToastSpec
            {
                Glyph = "", // error
                Tone = "DangerBrush",
                Text = "Couldn't get the Arm version",
                Detail = "Check your connection",
                Duration = TimeSpan.FromSeconds(4),
            });
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>A real download is far bigger than an error page and starts with the "MZ" header every Windows executable has.</summary>
    private static bool LooksLikeExe(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.Length >= MinPlausibleBytes && stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
    }
}
