using MiniChess.Protocol;
using MiniChess.Server.Connections;

namespace MiniChess.Server.Matches;

/// <summary>
/// 접속한 클라이언트를 먼저 온 순서대로 둘씩 짝지어 경기를 연다(방 코드/매칭 조건 없음).
/// 연결 하나의 수명: 참가 요청(버전/설정 확인) → (대기) → 경기 → 끊김.
/// 연결 처리 중 어떤 예외가 나도 퇴장 처리(대기열 정리, 진행 중인 경기는 상대 승리)는 반드시 한다.
/// </summary>
public sealed class Lobby
{
    private sealed class Session
    {
        public required PlayerConnection Connection { get; init; }
        public volatile MatchRoom? Room;
    }

    private readonly object _gate = new();
    private readonly ILogger<Lobby> _logger;
    private readonly MatchOptions _options;
    private Session? _waiting;

    public Lobby(ILogger<Lobby> logger, MatchOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public async Task RunAsync(PlayerConnection connection, CancellationToken cancellationToken)
    {
        string? first = await connection.ReceiveAsync(cancellationToken);
        if (first == null)
            return;

        string? rejection = CheckJoin(ProtocolJson.DecodeClient(first));
        if (rejection != null)
        {
            _logger.LogWarning("[{Connection}] rejected: {Reason}", connection.Id, rejection);
            connection.Send(new ErrorMessage { Message = rejection });
            connection.Close("bad join");
            return;
        }

        var session = new Session { Connection = connection };
        try
        {
            MatchRoom? room = Pair(session);
            if (room != null)
                await room.StartAsync();

            while (await connection.ReceiveAsync(cancellationToken) is { } text)
                await HandleAsync(session, ProtocolJson.DecodeClient(text));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[{Connection}] unexpected error; treating as disconnect", connection.Id);
        }
        finally
        {
            await LeaveAsync(session);
        }
    }

    /// <summary>첫 메시지가 맞는 참가 요청인지. 맞으면 null, 아니면 거절 사유.</summary>
    private string? CheckJoin(ClientMessage? message)
    {
        if (message is not JoinMatchMessage join || join.ProtocolVersion != ProtocolInfo.Version)
            return $"첫 메시지는 JoinMatch(ProtocolVersion={ProtocolInfo.Version})여야 합니다. 클라이언트를 업데이트하세요.";
        if (join.ConfigHash != _options.ConfigHash)
            return "경기 설정(밸런스 수치)이 서버와 다릅니다. 클라이언트를 업데이트하세요.";

        return null;
    }

    private async Task HandleAsync(Session session, ClientMessage? message)
    {
        PlayerConnection connection = session.Connection;
        MatchRoom? room = session.Room;

        switch (message)
        {
            case PingMessage:
                connection.Send(new PongMessage());
                break;
            case CommandMessage command when room != null:
                await room.HandleAsync(connection, command);
                break;
            case CommandMessage:
                connection.Send(new ErrorMessage { Message = "아직 상대를 기다리는 중입니다" });
                break;
            case null:
                _logger.LogWarning("[{Connection}] could not decode message", connection.Id);
                connection.Send(new ErrorMessage { Message = "해석할 수 없는 메시지입니다" });
                break;
            default:
                connection.Send(new ErrorMessage { Message = "지금 받을 수 없는 메시지입니다" });
                break;
        }
    }

    /// <summary>대기자가 있으면 짝지어 방을 만들고, 없으면 대기자가 된다.</summary>
    private MatchRoom? Pair(Session session)
    {
        lock (_gate)
        {
            if (_waiting == null)
            {
                _waiting = session;
                _logger.LogInformation("{Connection} waiting for opponent", session.Connection.Id);
                return null;
            }

            Session opponent = _waiting;
            _waiting = null;

            var room = new MatchRoom(opponent.Connection, session.Connection, _options, _logger);
            opponent.Room = room;
            session.Room = room;
            return room;
        }
    }

    private async Task LeaveAsync(Session session)
    {
        lock (_gate)
        {
            if (_waiting == session)
                _waiting = null;
        }

        try
        {
            if (session.Room != null)
                await session.Room.DisconnectAsync(session.Connection);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "[{Connection}] error while leaving the match", session.Connection.Id);
        }

        _logger.LogInformation("{Connection} left", session.Connection.Id);
    }
}
