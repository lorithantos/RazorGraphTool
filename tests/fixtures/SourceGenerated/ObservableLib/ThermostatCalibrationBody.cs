namespace ObservableLib;

/// <summary>
/// The implementation parts of the partial members ThermostatCalibration.cs
/// defines.
/// </summary>
public partial class Thermostat
{
    private int _offset;

    /// <summary>Implementation of Calibrate: the body its node points at.</summary>
    partial void Calibrate(int offset)
    {
        if (offset > 10) throw new ArgumentOutOfRangeException(nameof(offset));
        Offset = offset;
    }

    /// <summary>Implementation of Offset: the accessor bodies its node points at.</summary>
    public partial int Offset
    {
        get => _offset;
        set => _offset = value;
    }
}
