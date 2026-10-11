using System;

namespace Dropshelf.Animation;

/// <summary>
/// Damped spring integrator (unit mass): <c>a = -k (x - target) - c v</c>. Stepped with a fixed 1/240 s sub-step
/// (semi-implicit Euler) so it stays stable at any frame rate. Overshoot is set by the damping ratio, e.g. 0.7156
/// gives about 4 % overshoot, 1.0 is critically damped (no overshoot).
/// </summary>
internal sealed class Spring
{
    private const double SubStep = 1.0 / 240.0;
    private const double MaxFrame = 0.05; // a long stall (debugger, sleep) must not explode the simulation

    public Spring(double value = 0, double stiffness = 600, double dampingRatio = 1)
    {
        Value = value;
        Target = value;
        Configure(stiffness, dampingRatio);
    }

    public double Value { get; private set; }

    public double Velocity { get; set; }

    public double Target { get; set; }

    public double Stiffness { get; private set; }

    public double Damping { get; private set; }

    /// <summary>Sets stiffness <paramref name="k"/> and the damping from a damping ratio (0 = undamped, 1 = critical).</summary>
    public void Configure(double k, double dampingRatio)
    {
        Stiffness = k;
        Damping = 2 * dampingRatio * Math.Sqrt(k);
    }

    /// <summary>Advances the simulation by <paramref name="dt"/> seconds.</summary>
    public void Step(double dt)
    {
        dt = Math.Clamp(dt, 0, MaxFrame);
        while (dt > 1e-9)
        {
            double h = Math.Min(dt, SubStep);
            double accel = -Stiffness * (Value - Target) - Damping * Velocity;
            Velocity += accel * h;
            Value += Velocity * h;
            dt -= h;
        }
    }

    public bool IsSettled(double positionEpsilon = 0.0005, double velocityEpsilon = 0.01) =>
        Math.Abs(Value - Target) < positionEpsilon && Math.Abs(Velocity) < velocityEpsilon;

    /// <summary>Jumps to <paramref name="value"/> with no motion (also moves the target).</summary>
    public void Snap(double value)
    {
        Value = value;
        Target = value;
        Velocity = 0;
    }

    /// <summary>Moves the spring to <paramref name="value"/> with no velocity but keeps its target, so it springs back from there.</summary>
    public void Displace(double value)
    {
        Value = value;
        Velocity = 0;
    }

    /// <summary>Settles onto the target immediately if the spring is close enough to be indistinguishable.</summary>
    public bool SettleIfClose(double positionEpsilon = 0.0005, double velocityEpsilon = 0.01)
    {
        if (!IsSettled(positionEpsilon, velocityEpsilon))
        {
            return false;
        }

        Value = Target;
        Velocity = 0;
        return true;
    }
}
