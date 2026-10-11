using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dropshelf.Activities.Hud;
using Dropshelf.Services;

namespace Dropshelf.Tools;

internal enum ToolKind
{
    ConvertPng,
    ConvertJpg,
    Resize50,
    Resize25,
    ResizeToWidth,
    CompressJpg80,
    CompressJpg60,
    Zip,
    Unzip,
}

/// <summary>What a run produced. <see cref="Outputs"/> are new files / folders to put on the shelf.</summary>
internal sealed class ToolResult
{
    public List<string> Outputs { get; } = new();
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long BytesSaved { get; set; }
}

/// <summary>
/// Runs the convert / resize / compress / zip tools off the UI thread. A long run shows a progress ring on the pill
/// (<see cref="ProgressActivity"/>); the end of every run raises a toast ("Converted 3 files"). The caller puts the outputs on
/// the shelf. Originals are never touched: every result is a new, uniquely named file.
/// </summary>
internal sealed class ToolsService
{
    private readonly ProgressActivity _progress;
    private readonly ToastActivity _toast;

    public ToolsService(ProgressActivity progress, ToastActivity toast)
    {
        _progress = progress;
        _toast = toast;
        Current = this;
    }

    public static ToolsService? Current { get; private set; }

    /// <summary>Runs a tool over <paramref name="paths"/> (UI thread; completes on the UI thread).</summary>
    public async Task<ToolResult> RunAsync(ToolKind kind, IReadOnlyList<string> paths, int maxWidth = 0)
    {
        var result = new ToolResult();
        if (paths.Count == 0)
        {
            return result;
        }

        using ProgressActivity.Job job = _progress.Begin(LabelFor(kind));
        try
        {
            await Task.Run(() => Execute(kind, paths, maxWidth, result, job));
        }
        catch (Exception ex)
        {
            Log.Error($"Tool {kind} failed", ex);
            result.Failed = Math.Max(result.Failed, 1);
        }

        ShowToast(kind, paths.Count, result);
        return result;
    }

    private static void Execute(ToolKind kind, IReadOnlyList<string> paths, int maxWidth, ToolResult result, ProgressActivity.Job job)
    {
        if (kind == ToolKind.Zip)
        {
            try
            {
                ZipResult zip = ZipOps.Create(paths, f => job.Report(f));
                result.Outputs.Add(zip.Path);
                result.Skipped += zip.Skipped;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Error("Zip failed", ex);
                result.Failed++;
            }

            return;
        }

        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i];
            double Part(double f) => (i + f) / paths.Count;
            job.Report(Part(0), paths.Count > 1 ? $"{LabelFor(kind)} {i + 1}/{paths.Count}" : null);
            try
            {
                if (kind == ToolKind.Unzip)
                {
                    ZipResult extracted = ZipOps.Extract(path, f => job.Report(Part(f)));
                    result.Outputs.Add(extracted.Path);
                    result.Skipped += extracted.Skipped;
                    continue;
                }

                string? output = ImageOps.Run(path, JobFor(kind, maxWidth));
                if (output == null)
                {
                    result.Skipped++;
                    continue;
                }

                result.Outputs.Add(output);
                result.BytesSaved += SizeOf(path) - SizeOf(output);
            }
            catch (Exception ex)
            {
                // A file WIC cannot decode (HEIC without the codec, a corrupt file...) must not stop the rest.
                Log.Error($"{kind} failed for {path}", ex);
                result.Failed++;
            }
        }

        job.Report(1);
    }

    private static ImageJob JobFor(ToolKind kind, int maxWidth) => kind switch
    {
        ToolKind.ConvertPng => new ImageJob { Suffix = "converted", Format = ImageFormat.Png },
        ToolKind.ConvertJpg => new ImageJob { Suffix = "converted", Format = ImageFormat.Jpeg, JpegQuality = 92 },
        ToolKind.Resize50 => new ImageJob { Suffix = "50%", ScalePercent = 50 },
        ToolKind.Resize25 => new ImageJob { Suffix = "25%", ScalePercent = 25 },
        ToolKind.ResizeToWidth => new ImageJob { Suffix = $"{maxWidth}px", MaxWidth = maxWidth },
        ToolKind.CompressJpg80 => new ImageJob { Suffix = "compressed", Format = ImageFormat.Jpeg, JpegQuality = 80 },
        _ => new ImageJob { Suffix = "compressed", Format = ImageFormat.Jpeg, JpegQuality = 60 },
    };

    private static string LabelFor(ToolKind kind) => kind switch
    {
        ToolKind.ConvertPng or ToolKind.ConvertJpg => "Converting",
        ToolKind.Resize50 or ToolKind.Resize25 or ToolKind.ResizeToWidth => "Resizing",
        ToolKind.CompressJpg80 or ToolKind.CompressJpg60 => "Compressing",
        ToolKind.Zip => "Zipping",
        _ => "Extracting",
    };

    private void ShowToast(ToolKind kind, int requested, ToolResult result)
    {
        int ok = result.Outputs.Count;
        string verb = kind switch
        {
            ToolKind.ConvertPng or ToolKind.ConvertJpg => "Converted",
            ToolKind.Resize50 or ToolKind.Resize25 or ToolKind.ResizeToWidth => "Resized",
            ToolKind.CompressJpg80 or ToolKind.CompressJpg60 => "Compressed",
            ToolKind.Zip => "Zipped",
            _ => "Extracted",
        };

        if (ok == 0)
        {
            string detail = result.Skipped > 0 && result.Failed == 0 ? "already small enough" : "unsupported or unreadable";
            _toast.Show(new ToastSpec
            {
                Glyph = "",
                Tone = "WarnBrush",
                Text = result.Skipped > 0 && result.Failed == 0 ? "Nothing to do" : $"Could not {BaseVerb(kind)} {Plural(requested, "file")}",
                Detail = detail,
                Duration = TimeSpan.FromMilliseconds(2600),
            });
            return;
        }

        string text;
        if (kind == ToolKind.Zip)
        {
            text = $"Created {Path.GetFileName(result.Outputs[0])}";
        }
        else if (kind == ToolKind.Unzip)
        {
            text = $"Extracted {Plural(ok, "archive")}";
        }
        else
        {
            text = $"{verb} {Plural(ok, "file")}";
        }

        string? extra = null;
        if (result.Failed > 0)
        {
            extra = $"{result.Failed} failed";
        }
        else if (result.Skipped > 0)
        {
            extra = $"{result.Skipped} skipped";
        }
        else if (kind is ToolKind.CompressJpg80 or ToolKind.CompressJpg60 && result.BytesSaved > 0)
        {
            extra = $"saved {OutputNaming.FormatSize(result.BytesSaved)}";
        }

        _toast.Show(new ToastSpec
        {
            Glyph = "",
            Tone = result.Failed > 0 ? "WarnBrush" : "SuccessBrush",
            Text = text,
            Detail = extra,
            Bounce = true,
            Duration = TimeSpan.FromMilliseconds(2200),
        });
    }

    private static string BaseVerb(ToolKind kind) => kind switch
    {
        ToolKind.ConvertPng or ToolKind.ConvertJpg => "convert",
        ToolKind.Resize50 or ToolKind.Resize25 or ToolKind.ResizeToWidth => "resize",
        ToolKind.CompressJpg80 or ToolKind.CompressJpg60 => "compress",
        ToolKind.Zip => "zip",
        _ => "extract",
    };

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
