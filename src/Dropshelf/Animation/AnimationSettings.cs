using System.Windows;
using Dropshelf.Services;

namespace Dropshelf.Animation;

/// <summary>How much motion the app uses. Chosen in Settings; Windows' "Animation effects" switch can lower the default.</summary>
public enum AnimationMode
{
    /// <summary>Everything: fluid, slightly bouncy springs like the Dynamic Island.</summary>
    Full,

    /// <summary>Short, critically damped motion without bounce (like Reduce Motion).</summary>
    Subtle,

    /// <summary>No animation at all; everything jumps to its final state.</summary>
    Off,
}

/// <summary>
/// Resolves the effective <see cref="AnimationMode"/>. When the user never picked a mode (<see cref="AppSettings.Animations"/>
/// is null) the default is Full, but Windows' "Animation effects" being off lowers it to Subtle. An explicit choice wins.
/// </summary>
internal static class AnimationSettings
{
    private static AppSettings? _settings;

    public static void Attach(AppSettings settings) => _settings = settings;

    public static AnimationMode Mode
    {
        get
        {
            AnimationMode? chosen = _settings?.Animations;
            if (chosen.HasValue)
            {
                return chosen.Value;
            }

            return SystemParameters.ClientAreaAnimation ? AnimationMode.Full : AnimationMode.Subtle;
        }
    }

    public static bool IsFull => Mode == AnimationMode.Full;

    public static bool IsOff => Mode == AnimationMode.Off;

    public static bool IsSubtle => Mode == AnimationMode.Subtle;

    /// <summary>Duration multiplier for time-based animations.</summary>
    public static double TimeScale => Mode switch
    {
        AnimationMode.Full => 1.0,
        AnimationMode.Subtle => 0.6,
        _ => 0.0,
    };

    /// <summary>
    /// Configures <paramref name="spring"/> for the current mode: Full keeps the requested stiffness and damping ratio;
    /// Subtle makes it stiffer and at least critically damped (shorter, no overshoot).
    /// </summary>
    public static void Configure(Spring spring, double stiffness, double dampingRatio)
    {
        if (IsFull)
        {
            spring.Configure(stiffness, dampingRatio);
        }
        else
        {
            spring.Configure(stiffness * 1.8, System.Math.Max(dampingRatio, 1.0));
        }
    }
}
