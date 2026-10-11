using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Dropshelf.Activities.Hud;
using Dropshelf.Clipboard;

namespace Dropshelf.Screenshot;

/// <summary>"Copy text": OCR one or more picture files, put the text on the clipboard and say what happened on the pill.</summary>
internal static class OcrActions
{
    /// <summary>A longer explanation for the user (shown as a tray balloon by the app).</summary>
    public static event Action<string>? Message;

    public static async Task CopyTextAsync(IReadOnlyList<string> imagePaths)
    {
        ToastActivity? toast = ToastActivity.Current;
        toast?.Show(new ToastSpec { Glyph = "", Text = "Reading text...", Duration = TimeSpan.FromSeconds(6) });

        var parts = new List<string>();
        OcrStatus failure = OcrStatus.Empty;
        foreach (string path in imagePaths)
        {
            OcrOutcome outcome = await OcrService.RecognizeFileAsync(path);
            if (outcome.Status == OcrStatus.Ok)
            {
                parts.Add(outcome.Text);
            }
            else if (outcome.Status != OcrStatus.Empty)
            {
                failure = outcome.Status;
            }
        }

        if (parts.Count == 0)
        {
            bool noEngine = failure == OcrStatus.NoEngine;
            toast?.Show(new ToastSpec
            {
                Glyph = "",
                Tone = "WarnBrush",
                Text = noEngine ? "OCR unavailable" : failure == OcrStatus.Failed ? "Could not read the picture" : "No text found",
                Detail = noEngine ? "no language pack" : null,
                Duration = TimeSpan.FromSeconds(noEngine ? 4 : 2.4),
            });
            if (noEngine)
            {
                Message?.Invoke(OcrService.NoEngineMessage);
            }

            return;
        }

        string text = string.Join("\n\n", parts);
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        bool ok = await ClipboardAccess.TrySetDataObjectAsync(data);
        int lines = text.Split('\n').Length;
        toast?.Show(new ToastSpec
        {
            Glyph = ok ? "" : "",
            Tone = ok ? "SuccessBrush" : "WarnBrush",
            Text = ok ? "Text copied" : "Clipboard is busy",
            Detail = ok ? (lines == 1 ? "1 line" : $"{lines} lines") : null,
            Bounce = ok,
            Duration = TimeSpan.FromMilliseconds(2200),
        });
    }
}
