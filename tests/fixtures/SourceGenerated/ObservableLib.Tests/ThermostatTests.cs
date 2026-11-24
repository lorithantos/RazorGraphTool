using ObservableLib;
using Xunit;

namespace ObservableLib.Tests;

/// <summary>
/// Fixture only — never executed. Each test assigns a generated property and
/// names nothing else, so anything it covers past the property it reaches
/// through the generated setter.
/// </summary>
public class ThermostatTests
{
    [Fact]
    public void SettingTarget_IsLogged()
    {
        var thermostat = new Thermostat();

        thermostat.Target = 21;
    }

    [Fact]
    public void SettingMode_IsLogged()
    {
        var thermostat = new Thermostat();

        thermostat.Mode = "cool";
    }
}
