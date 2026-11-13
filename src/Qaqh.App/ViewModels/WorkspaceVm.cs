using CommunityToolkit.Mvvm.ComponentModel;

namespace Qaqh_App;

public partial class WorkspaceVm : ObservableObject
{
    public required string Id { get; init; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _path = "";

    public override string ToString() => Name;
}
