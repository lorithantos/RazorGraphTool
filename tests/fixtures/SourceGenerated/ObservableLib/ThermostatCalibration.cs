using System.ComponentModel;
using System.Text.RegularExpressions;

namespace ObservableLib;

/// <summary>
/// Partial members whose definition is authored here. Calibrate and Offset are
/// implemented in ThermostatCalibrationBody.cs, another authored file, so
/// their nodes point there, at the body. Digits is implemented by the regex
/// generator, so its node stays here, on the only part a reader can open.
/// Named to sort after Thermostat.cs, so the type's first declaration, and
/// with it the class node's location, stays in Thermostat.cs.
/// </summary>
public partial class Thermostat
{
    /// <summary>Definition of a partial method with two authored parts.</summary>
    partial void Calibrate([Description("degrees")] int offset);

    /// <summary>Definition of a partial property with two authored parts.</summary>
    public partial int Offset { get; set; }

    /// <summary>Authored definition, generated implementation.</summary>
    [GeneratedRegex(@"^\d+$")]
    private static partial Regex Digits();
}
