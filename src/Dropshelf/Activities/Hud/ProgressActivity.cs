using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Dropshelf.Services;

namespace Dropshelf.Activities.Hud;

/// <summary>
/// The pill while a long job runs (converting, zipping...): a progress ring at the leading edge and a short label at the trailing edge.
/// A job that finishes within <see cref="ShowDelay"/> never shows up, so quick jobs do not make the pill flicker (they
/// end with a toast instead). Jobs report from worker threads; everything is marshalled to the UI thread.
/// </summary>
internal sealed class ProgressActivity : PillActivityBase
{
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(450);

    /// <summary>One running job. Dispose it when it ends (also on failure).</summary>
    public sealed class Job : IDisposable
    {
        private readonly ProgressActivity _owner;
        private long _lastPostTick;
        private bool _ended;

        internal Job(ProgressActivity owner, string label)
        {
            _owner = owner;
            Label = label;
        }

        internal string Label { get; private set; }
        internal double Fraction { get; private set; } = -1; // -1 = indeterminate

        /// <summary>Updates the ring (0..1; negative = spinner) and optionally the label. Safe from any thread.</summary>
        public void Report(double fraction, string? label = null)
        {
            long now = Environment.TickCount64;
            bool final = fraction >= 1;
            if (!final && now - _lastPostTick < 80)
            {
                return; // the ring does not need more than ~12 updates a second
            }

            _lastPostTick = now;
            _owner._dispatcher.BeginInvoke(new Action(() =>
            {
                if (_ended)
                {
                    return;
                }

                Fraction = fraction;
                if (label != null)
                {
                    Label = label;
                }

                _owner.Refresh();
            }));
        }

        public void Dispose()
        {
            _owner._dispatcher.BeginInvoke(new Action(() =>
            {
                if (_ended)
                {
                    return;
                }

                _ended = true;
                _owner.End(this);
            }));
        }
    }

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<Job> _jobs = new();
    private readonly DispatcherTimer _delay;
    private readonly ProgressRing _ring;
    private readonly TextBlock _label;
    private readonly CompactContent _content;
    private bool _visible;

    public ProgressActivity()
    {
        _ring = new ProgressRing(22, 2.5);
        _ring.SetIndeterminate();
        var left = HudParts.Cell(_ring, 24);

        _label = HudParts.Label(string.Empty, 13, "TextBrush", FontWeights.SemiBold);
        _label.Width = 140;
        var right = HudParts.Cell(_label, 140);
        _content = CompactContent.Of(left, right, extraHeight: 2);

        _delay = new DispatcherTimer { Interval = ShowDelay };
        _delay.Tick += (_, _) =>
        {
            _delay.Stop();
            if (_jobs.Count > 0 && !_visible)
            {
                _visible = true;
                Refresh();
                RaiseStateChanged();
            }
        };
    }

    public override string Id => "work";
    public override int Priority => 500;
    public override bool IsActive => _visible;

    public override CompactContent? CreateCompact() => _content;

    /// <summary>Starts a job (UI thread). The returned handle's <see cref="Job.Report"/> may be called from any thread.</summary>
    public Job Begin(string label)
    {
        var job = new Job(this, label);
        _jobs.Add(job);
        job.Report(-1, label);
        if (!_visible && !_delay.IsEnabled)
        {
            _delay.Start();
        }

        return job;
    }

    private void End(Job job)
    {
        _jobs.Remove(job);
        if (_jobs.Count == 0)
        {
            _delay.Stop();
            if (_visible)
            {
                _visible = false;
                RaiseStateChanged();
            }
        }
        else
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_jobs.Count == 0)
        {
            return;
        }

        Job job = _jobs[^1];
        _label.Text = job.Label;
        if (job.Fraction < 0)
        {
            _ring.SetIndeterminate();
        }
        else
        {
            _ring.SetProgress(job.Fraction);
        }
    }
}
