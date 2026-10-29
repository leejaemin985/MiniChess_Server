using MiniChess.Core.Controller;
using MiniChess.Protocol;

namespace MiniChess.Server.Matches;

/// <summary>경기 운영 설정. 기본값은 확정 규칙(Protocol)을 따르며, 테스트에서만 바꾼다.</summary>
public sealed class MatchOptions
{
    /// <summary>한 턴의 제한 시간. 다 되면 서버가 그 팀의 턴을 끝낸다.</summary>
    public TimeSpan TurnTime { get; set; } = TimeSpan.FromSeconds(OnlineMatch.TurnTimeSeconds);

    /// <summary>배치 단계의 제한 시간. 다 되면 서버가 아직 배치하지 않은 팀을 무작위로 배치한다.</summary>
    public TimeSpan SetupTime { get; set; } = TimeSpan.FromSeconds(OnlineMatch.SetupTimeSeconds);

    /// <summary>이 시간 동안 클라이언트에게서 아무 메시지도 받지 못하면 끊긴 것으로 본다.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(ProtocolInfo.ConnectionTimeoutSeconds);

    /// <summary>보내기 하나가 이 시간 안에 끝나지 않으면(상대가 받지 않음) 연결을 끊는다.</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>경기를 만든다(시드). 클라이언트와 같은 설정이어야 한다(ConfigHash).</summary>
    public Func<int, GameController> CreateGame { get; set; } = OnlineMatch.Create;

    /// <summary>클라이언트가 참가할 때 보내야 하는 설정 지문.</summary>
    public string ConfigHash { get; set; } = OnlineMatch.ConfigHash();
}
