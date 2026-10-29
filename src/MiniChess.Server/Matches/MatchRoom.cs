using MiniChess.Core.Common;
using MiniChess.Core.Controller;
using MiniChess.Core.Events;
using MiniChess.Core.State;
using MiniChess.Protocol;
using MiniChess.Server.Connections;

namespace MiniChess.Server.Matches;

/// <summary>
/// 경기 하나. 코어 GameController 를 소유하고, 두 연결의 명령과 턴 시간 초과를 한 번에 하나씩 처리한다(경기 상태는 이 방만 바꾼다).
/// 연결의 팀은 방이 정하며, 각 연결에는 자기 팀 시점으로 거른 스냅샷/이벤트만 보낸다(상대 덫, 포격 조준 칸, 배치 단계의 상대 배치 등은 빠진다).
///   - 끊김: 남은 쪽 승리로 바로 끝낸다(재접속 없음).
///   - 턴 시간 초과(MatchOptions.TurnTime): 서버가 그 팀의 턴을 끝낸다.
///   - 배치 시간 초과(MatchOptions.SetupTime): 서버가 아직 배치하지 않은 팀을 무작위로 배치한다(경기 시드 기반, OnlineMatch.RandomPlacement).
///   - 코어 예외: 상태가 처리 도중에 멈췄을 수 있으므로 경기를 무효 처리하고(승자 없음) 양쪽에 알린다.
/// 경기가 끝나면 두 연결을 닫는다. 보내기는 큐에 넣기만 하므로 잠금을 쥔 채 네트워크를 기다리지 않는다.
/// </summary>
public sealed class MatchRoom
{
    private sealed class Seat
    {
        public required PlayerConnection Connection { get; init; }
        public required Team Team { get; init; }

        /// <summary>처리한 마지막 요청 번호. 같은 번호가 다시 오면 저장해 둔 결과를 다시 보내고, 더 작은 번호는 무시한다.</summary>
        public int LastRequestId { get; set; }

        public CommandResultMessage? LastResult { get; set; }
    }

    private readonly GameController _game;
    private readonly Seat[] _seats;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly MatchOptions _options;
    private readonly ILogger _logger;

    /// <summary>배치 시간 초과 때 무작위 배치에 쓰는 난수(경기 시드에서 만든다. 경기 안의 난수와는 따로).</summary>
    private readonly Random _placementRandom;

    /// <summary>시간을 재고 있는 턴 번호(배치 단계는 0)와 마감 시각. 경기가 끝났으면 null.</summary>
    private int? _timedTurn;
    private DateTime _turnDeadline;
    private CancellationTokenSource? _turnTimer;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public MatchRoom(PlayerConnection player1, PlayerConnection player2, MatchOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
        _seats = new[]
        {
            new Seat { Connection = player1, Team = Team.Player1 },
            new Seat { Connection = player2, Team = Team.Player2 },
        };

        // 시드와 명령 순서만 있으면 경기를 그대로 재현할 수 있다. 시드는 클라이언트에 보내지 않는다(무작위 결과를 미리 알 수 있다).
        int seed = Random.Shared.Next();
        _game = options.CreateGame(seed);
        _placementRandom = new Random(seed);
        _logger.LogInformation("Match {Match} created: seed={Seed}, P1={P1}, P2={P2}", Id, seed, player1.Id, player2.Id);
    }

    public async Task StartAsync()
    {
        await _lock.WaitAsync();
        try
        {
            RestartTurnTimerIfNeeded();
            foreach (Seat seat in _seats)
            {
                seat.Connection.Send(new MatchStartedMessage
                {
                    YourTeam = seat.Team, Snapshot = _game.GetSnapshot(seat.Team), TurnTimeLeftMs = TurnTimeLeftMs(),
                });
            }
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
            if (command.RequestId < seat.LastRequestId)
                return;
            if (command.RequestId == seat.LastRequestId && seat.LastResult != null)
            {
                from.Send(seat.LastResult);
                return;
            }

            seat.LastRequestId = command.RequestId;
            CommandOutcome outcome = Execute(seat.Team, command);

            _logger.LogInformation("Match {Match} {Team} {Command}#{Request}: {Result}",
                Id, seat.Team, MessageTypes.NameOf(command.GetType()), command.RequestId, outcome.Success ? "ok" : outcome.Reason);

            // 바뀐 상태를 먼저 보내, 보낸 쪽이 결과를 받았을 때는 이미 새 상태로 그려져 있게 한다.
            if (outcome.Events.Count > 0)
                BroadcastUpdate(outcome.Events);

            seat.LastResult = new CommandResultMessage { RequestId = command.RequestId, Success = outcome.Success, Reason = outcome.Reason };
            from.Send(seat.LastResult);
        }
        finally
        {
            _lock.Release();
        }

        CloseIfOver();
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
                BroadcastUpdate(events);
            }
        }
        finally
        {
            _lock.Release();
        }

        CloseIfOver();
    }

    /// <summary>명령을 실행한다. 코어가 예외를 던지면 경기를 무효 처리하고 그 종료 이벤트를 결과로 돌려준다.</summary>
    private CommandOutcome Execute(Team team, CommandMessage command)
    {
        if (_game.State.IsGameOver)
            return CommandOutcome.Fail(CommandOutcome.GameOver);

        try
        {
            return CommandExecutor.Execute(_game, team, command);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Match {Match}: core error while running {Team} {Command}; match aborted",
                Id, team, MessageTypes.NameOf(command.GetType()));
            return CommandOutcome.Aborted(_game.Abort());
        }
    }

    /// <summary>
    /// 턴(배치 단계면 배치) 시간이 다 되었다. 그 턴이 아직 진행 중이면 현재 팀의 턴을 끝낸다.
    /// 배치 단계면 아직 배치하지 않은 팀을 무작위로 배치한다(두 팀 모두 배치되면 전투가 시작된다).
    /// </summary>
    private async Task OnTurnTimeoutAsync(int turnNumber)
    {
        await _lock.WaitAsync();
        try
        {
            if (_game.State.IsGameOver || _game.State.TurnNumber != turnNumber)
                return;

            IReadOnlyList<GameEvent> events;
            try
            {
                events = _game.State.Phase == GamePhase.Setup ? PlaceRemainingTeams() : EndCurrentTurn(turnNumber);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Match {Match}: core error while handling timeout of turn {Turn}; match aborted", Id, turnNumber);
                events = _game.Abort();
            }

            if (events.Count > 0)
                BroadcastUpdate(events);
        }
        finally
        {
            _lock.Release();
        }

        CloseIfOver();
    }

    private IReadOnlyList<GameEvent> EndCurrentTurn(int turnNumber)
    {
        Team team = _game.State.CurrentTeam;
        _logger.LogInformation("Match {Match}: turn {Turn} ({Team}) timed out, ending turn", Id, turnNumber, team);
        return _game.EndTurn(new EndTurnRequest(team)).Events;
    }

    /// <summary>배치하지 않은 팀을 무작위로 배치한다. 배치 규칙에 맞게 만들므로 실패하면 코어 이상으로 본다.</summary>
    private IReadOnlyList<GameEvent> PlaceRemainingTeams()
    {
        var events = new List<GameEvent>();
        foreach (Seat seat in _seats.Where(seat => !_game.State.GetPlayer(seat.Team).HasPlaced))
        {
            _logger.LogInformation("Match {Match}: setup timed out, placing {Team} at random", Id, seat.Team);
            var response = _game.PlaceUnits(OnlineMatch.RandomPlacement(_game, seat.Team, _placementRandom));
            if (!response.Success)
                throw new InvalidOperationException($"무작위 배치 실패({seat.Team}): {response.Reason}");

            events.AddRange(response.Events);
        }

        return events;
    }

    /// <summary>
    /// 바뀐 상태를 양쪽에 각자의 시점으로 걸러 보낸다(잠금 안에서 호출). 턴이 바뀌었으면 턴 시간을 새로 잰다.
    /// 시점 만들기가 실패하면(코어 상태 이상) 경기를 무효 처리하고 오류만 알린다.
    /// </summary>
    private void BroadcastUpdate(IReadOnlyList<GameEvent> events)
    {
        RestartTurnTimerIfNeeded();

        List<(Seat Seat, StateUpdateMessage Update)> updates;
        try
        {
            updates = _seats.Select(seat => (seat, new StateUpdateMessage
            {
                Events = _game.GetVisibleEvents(seat.Team, events).Select(ProtocolJson.Wrap).ToList(),
                Snapshot = _game.GetSnapshot(seat.Team),
                TurnTimeLeftMs = TurnTimeLeftMs(),
            })).ToList();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Match {Match}: could not build state updates; match aborted", Id);
            if (!_game.State.IsGameOver)
                _game.Abort();
            foreach (Seat seat in _seats)
                seat.Connection.Send(new ErrorMessage { Message = "서버 오류로 경기가 무효 처리되었습니다" });
            return;
        }

        foreach ((Seat seat, StateUpdateMessage update) in updates)
            seat.Connection.Send(update);
    }

    private void RestartTurnTimerIfNeeded()
    {
        GameState state = _game.State;
        int? turn = state.IsGameOver ? null : state.TurnNumber;
        if (turn == _timedTurn)
            return;

        _turnTimer?.Cancel();
        _turnTimer = null;
        _timedTurn = turn;
        if (turn == null)
            return;

        var timer = new CancellationTokenSource();
        _turnTimer = timer;
        TimeSpan limit = state.Phase == GamePhase.Setup ? _options.SetupTime : _options.TurnTime;
        _turnDeadline = DateTime.UtcNow + limit;
        int turnNumber = turn.Value;
        _ = Task.Delay(limit, timer.Token).ContinueWith(
            delay => delay.IsCompletedSuccessfully ? OnTurnTimeoutAsync(turnNumber) : Task.CompletedTask,
            TaskScheduler.Default).Unwrap();
    }

    private int? TurnTimeLeftMs()
    {
        if (_timedTurn == null)
            return null;

        return Math.Max(0, (int)(_turnDeadline - DateTime.UtcNow).TotalMilliseconds);
    }

    private void CloseIfOver()
    {
        if (!_game.State.IsGameOver)
            return;

        _turnTimer?.Cancel();
        foreach (Seat seat in _seats)
            seat.Connection.Close("game over");
    }

    private Seat SeatOf(PlayerConnection connection) => _seats.First(seat => seat.Connection == connection);
}
