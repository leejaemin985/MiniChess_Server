using MiniChess.Core.Common;
using MiniChess.Core.Controller;
using MiniChess.Core.Events;
using MiniChess.Protocol;
using MiniChess.Server.Connections;
using MiniChess.Server.Protocol;

namespace MiniChess.Server.Matches;

/// <summary>
/// 경기 하나. 코어 GameController 를 소유하고, 두 연결의 명령을 한 번에 하나씩 처리한다(경기 상태는 이 방만 바꾼다).
/// 연결의 팀은 방이 정하며, 각 연결에는 자기 팀 시점으로 거른 이벤트/스냅샷만 보낸다.
/// 끊김: 남은 쪽 승리로 바로 끝낸다(재접속 없음). 경기가 끝나면 두 연결을 닫는다.
/// </summary>
public sealed class MatchRoom
{
    private sealed class Seat
    {
        public required PlayerConnection Connection { get; init; }
        public required Team Team { get; init; }

        /// <summary>처리한 마지막 요청 번호. 이 이하의 번호는 재전송으로 보고 무시한다.</summary>
        public int LastRequestId { get; set; }
    }

    private readonly GameController _game;
    private readonly int _seed;
    private readonly Seat[] _seats;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger _logger;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public MatchRoom(PlayerConnection player1, PlayerConnection player2, ILogger logger)
    {
        _logger = logger;
        _seats = new[]
        {
            new Seat { Connection = player1, Team = Team.Player1 },
            new Seat { Connection = player2, Team = Team.Player2 },
        };

        _seed = Random.Shared.Next();
        _game = OnlineMatch.Create(_seed);

        // 시드와 명령 순서만 있으면 경기를 그대로 재현할 수 있다.
        _logger.LogInformation("Match {Match} created: seed={Seed}, P1={P1}, P2={P2}", Id, _seed, player1.Id, player2.Id);
    }

    public async Task StartAsync()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (Seat seat in _seats)
                await seat.Connection.SendAsync(new MatchStartedMessage { YourTeam = seat.Team, Seed = _seed, Snapshot = _game.GetSnapshot(seat.Team) });
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task HandleAsync(PlayerConnection from, CommandMessage command)
    {
        await _lock.WaitAsync();
        try
        {
            Seat seat = SeatOf(from);
            if (command.RequestId <= seat.LastRequestId)
                return;
            seat.LastRequestId = command.RequestId;

            CommandOutcome outcome = _game.State.IsGameOver
                ? CommandOutcome.Fail(CommandOutcome.GameOver)
                : CommandExecutor.Execute(_game, seat.Team, command);

            _logger.LogInformation("Match {Match} {Team} {Command}#{Request}: {Result}",
                Id, seat.Team, MessageTypes.NameOf(command.GetType()), command.RequestId, outcome.Success ? "ok" : outcome.Reason);

            await from.SendAsync(new CommandResultMessage { RequestId = command.RequestId, Success = outcome.Success, Reason = outcome.Reason });
            if (outcome.Success)
            {
                await BroadcastAppliedAsync(seat.Team, command);
                await BroadcastAsync(outcome.Events);
            }
        }
        finally
        {
            _lock.Release();
        }

        await CloseIfOverAsync();
    }

    /// <summary>연결이 끊겼다. 진행 중이면 남은 쪽 승리로 끝낸다.</summary>
    public async Task DisconnectAsync(PlayerConnection from)
    {
        await _lock.WaitAsync();
        try
        {
            IReadOnlyList<GameEvent> events = _game.EndByDisconnect(SeatOf(from).Team);
            if (events.Count > 0)
            {
                _logger.LogInformation("Match {Match}: {Connection} disconnected, game ended", Id, from.Id);
                await BroadcastAsync(events);
            }
        }
        finally
        {
            _lock.Release();
        }

        await CloseIfOverAsync();
    }

    /// <summary>[임시: 클라 로컬 복제 방식] 실행한 명령을 양쪽에 알린다. 클라이언트는 같은 명령을 자기 경기에 실행한다.</summary>
    private async Task BroadcastAppliedAsync(Team team, CommandMessage command)
    {
        var applied = new CommandAppliedMessage
        {
            Team = team,
            CommandType = MessageTypes.NameOf(command.GetType()),
            Command = command,
            LastEventSequence = _game.State.Events.Count - 1,
        };

        foreach (Seat seat in _seats)
            await seat.Connection.SendAsync(applied);
    }

    private async Task BroadcastAsync(IReadOnlyList<GameEvent> events)
    {
        foreach (Seat seat in _seats)
        {
            IReadOnlyList<GameEvent> visible = _game.GetVisibleEvents(seat.Team, events);
            if (visible.Count > 0)
                await seat.Connection.SendAsync(new EventsMessage { Events = visible.Select(ProtocolJson.Wrap).ToList() });
        }
    }

    private async Task CloseIfOverAsync()
    {
        if (!_game.State.IsGameOver)
            return;

        foreach (Seat seat in _seats)
            await seat.Connection.CloseAsync("game over");
    }

    private Seat SeatOf(PlayerConnection connection) => _seats.First(seat => seat.Connection == connection);
}
