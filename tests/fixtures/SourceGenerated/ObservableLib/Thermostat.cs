using CommunityToolkit.Mvvm.ComponentModel;

namespace ObservableLib;

/// <summary>
/// Source-generator fixture. Neither property below has an accessor body in
/// this file: CommunityToolkit.Mvvm's generator writes them, and each generated
/// setter calls the partial On...Changed hook implemented here. Those hooks are
/// the only path from a property assignment to Record, so an edge into Record
/// proves the generated setter was walked.
/// </summary>
public partial class Thermostat : ObservableObject
{
    /// <summary>
    /// The field form: the whole Target property is generated, declaration and
    /// all. Nothing in user code declares it.
    /// </summary>
    [ObservableProperty]
    private int _target;

    /// <summary>
    /// The partial-property form: the declaration is here, the accessor bodies
    /// are generated.
    /// </summary>
    [ObservableProperty]
    public partial string? Mode { get; set; }

    public List<string> Log { get; } = new();

    partial void OnTargetChanged(int value) => Record($"target {value}");

    partial void OnModeChanged(string? value) => Record($"mode {value}");

    private void Record(string entry) => Log.Add(entry);
}
