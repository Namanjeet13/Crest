using System;

namespace Crest.Animation;

/// <summary>
/// A damped spring (mass 1). Underdamped settings give the gentle overshoot
/// that makes the island feel physical instead of tweened.
/// </summary>
public sealed class Spring
{
    public double Value;
    public double Velocity;
    public double Target;
    public double Stiffness = 340;
    public double Damping = 30;

    public Spring(double initial)
    {
        Value = Target = initial;
    }

    public bool IsResting => Velocity == 0 && Value == Target;

    /// <returns>true while the spring is still moving.</returns>
    public bool Step(double dt)
    {
        if (IsResting) return false;
        double force = -Stiffness * (Value - Target) - Damping * Velocity;
        Velocity += force * dt;
        Value += Velocity * dt;
        if (Math.Abs(Value - Target) < 0.02 && Math.Abs(Velocity) < 0.5)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }
        return true;
    }

    public void Configure(double stiffness, double damping)
    {
        Stiffness = stiffness;
        Damping = damping;
    }
}
