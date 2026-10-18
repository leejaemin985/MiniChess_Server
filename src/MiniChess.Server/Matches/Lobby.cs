using MiniChess.Protocol;
using MiniChess.Server.Connections;
using MiniChess.Server.Protocol;

namespace MiniChess.Server.Matches;

/// <summary>
/// 접속한 클라이언트를 먼저 온 순서대로 둘씩 짝지어 경기를 연다(방 코드/매칭 조건 없음).
/// 연결 하나의 수명: 참가 요청 → (대기) → 경기 → 끊김.
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
    private Session? _waiting;

    public Lobby(ILogger<Lobby> logger)
    {
        _logger = logger;
    }

    public async Task RunAsync(PlayerConnection connection, CancellationToken cancellationToken)
    {
        string? first = await connection.ReceiveAsync(cancellationToken);
        if (first == null)
            return;

        if (ProtocolJson.DecodeClient(first) is not JoinMatchMessage join || join.ProtocolVersion != ProtocolInfo.Version)
        {
            _logger.LogWarning("[{Connection}] rejected: first message must be JoinMatch v{Version}", connection.Id, ProtocolInfo.Version);
            await connection.SendAsync(new ErrorMessage { Message = $"첫 메시지는 JoinMatch(ProtocolVersion={ProtocolInfo.Version})여야 합니다" });
            await connection.CloseAsync("bad join");
            return;
        }

        var session = new Session { Connection = connection };
        MatchRoom? room = Pair(session);
        if (room != null)
            await room.StartAsync();

        while (await connection.ReceiveAsync(cancellationToken) is { } text)
        {
            ClientMessage? message = ProtocolJson.DecodeClient(text);
            MatchRoom? current = session.Room;

            if (message == null)
                _logger.LogWarning("[{Connection}] could not decode message", connection.Id);

            if (message is CommandMessage command && current != null)
                await current.HandleAsync(connection, command);
            else
                await connection.SendAsync(new ErrorMessage { Message = current == null ? "아직 상대를 기다리는 중입니다" : "해석할 수 없는 메시지입니다" });
        }

        await LeaveAsync(session);
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

            var room = new MatchRoom(opponent.Connection, session.Connection, _logger);
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

        if (session.Room != null)
            await session.Room.DisconnectAsync(session.Connection);

        _logger.LogInformation("{Connection} left", session.Connection.Id);
    }
}
