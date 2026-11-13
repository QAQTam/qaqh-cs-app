using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Qaqh.Sdk.Contract;

namespace Qaqh_App;

public sealed class TeamAgentRowVm
{
    public string AgentPath { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Status { get; set; } = "";
    public string Residency { get; set; } = "";
}

/// <summary>会话团队面板：roster + 收件箱计数（GET session/team 快照）。</summary>
public partial class TeamPanelVm : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly string _sessionId;

    public TeamPanelVm(ShellViewModel shell, string sessionId)
    {
        _shell = shell;
        _sessionId = sessionId;
    }

    public ObservableCollection<TeamAgentRowVm> Agents { get; } = new();

    [ObservableProperty]
    private string _summary = "";

    public async Task RefreshAsync()
    {
        try
        {
            var team = await _shell.Client.GetTeamSnapshotAsync(_sessionId);
            Agents.Clear();
            foreach (var agent in team.Team.Agents)
            {
                Agents.Add(new TeamAgentRowVm
                {
                    AgentPath = agent.AgentPath,
                    Nickname = agent.Nickname ?? agent.Role ?? "",
                    Status = agent.Status.ToString(),
                    Residency = agent.Residency.ToString(),
                });
            }
            Summary = $"成员 {Agents.Count} · 未读消息 {team.Team.UnreadMessages.Length}";
        }
        catch (Exception ex) when (ex is Qaqh.Sdk.SdkException or JsonException)
        {
            Summary = $"团队快照不可用：{ex.Message}";
        }
    }
}
