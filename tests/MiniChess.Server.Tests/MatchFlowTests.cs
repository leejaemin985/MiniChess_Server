using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MiniChess.Core.Characters.Pieces;
using MiniChess.Core.Common;
using MiniChess.Core.Controller;
using MiniChess.Core.Skills;
using MiniChess.Core.Skills.Areas;
using MiniChess.Core.Skills.Targeting;
using MiniChess.Core.Snapshots;
using MiniChess.Protocol;
using MiniChess.Server.Matches;

namespace MiniChess.Server.Tests;

/// <summary>
/// 실제 WebSocket 으로 두 클라이언트를 붙여 경기 흐름과 실서비스 방어(잘못된 입력, 설정 불일치, 턴 시간 초과, 무응답 연결, 코어 예외)를 확인한다.
/// </summary>
public class MatchFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public MatchFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    #region Normal flow

    [Fact]
    public async Task TwoPlayers_Join_Place_EndTurn_ThenDisconnectEndsGame()
    {
        WebApplicationFactory<Program> server = _factory;
        var (a, b, aStart, bStart) = await StartMatchAsync(server);
        await using var _a = a;
        await using var _b = b;

        // 배치: 각자 자기 시작 칸 4곳에 놓는다(동시 제출).
        await a.SendAsync(Placement(aStart, requestId: 1));
        await b.SendAsync(Placement(bStart, requestId: 1));
        Assert.True((await a.NextAsync("CommandResult")).GetProperty("Success").GetBoolean());
        Assert.True((await b.NextAsync("CommandResult")).GetProperty("Success").GetBoolean());

        // 두 팀 배치가 끝나면 전투가 시작되고, 턴 남은 시간이 온다.
        JsonElement turnStarted = await a.NextEventAsync("TurnStartedEvent");
        await b.NextEventAsync("TurnStartedEvent");
        Assert.InRange(a.LatestUpdate!.Value.GetProperty("TurnTimeLeftMs").GetInt32(), 1, OnlineMatch.TurnTimeSeconds * 1000);
        int turnNumber = turnStarted.GetProperty("TurnNumber").GetInt32();
        bool aFirst = turnStarted.GetProperty("Team").GetString() == aStart.GetProperty("YourTeam").GetString();
        TestClient active = aFirst ? a : b;
        TestClient waiting = aFirst ? b : a;
        string activeTeam = turnStarted.GetProperty("Team").GetString()!;

        // 남의 턴에 턴 종료 → 거절, 자기 턴에 턴 종료 → 성공 + 양쪽에 이벤트.
        await waiting.SendAsync(new EndTurnMessage { RequestId = 2, TurnNumber = turnNumber });
        JsonElement rejected = await waiting.NextAsync("CommandResult");
        Assert.False(rejected.GetProperty("Success").GetBoolean());
        Assert.Equal("NotYourTurn", rejected.GetProperty("Reason").GetString());

        await active.SendAsync(new EndTurnMessage { RequestId = 2, TurnNumber = turnNumber });
        Assert.True((await active.NextAsync("CommandResult")).GetProperty("Success").GetBoolean());
        await active.NextEventAsync("TurnEndedEvent");
        await waiting.NextEventAsync("TurnEndedEvent");

        // 같은 요청 번호를 다시 보내면 실행하지 않고 저장해 둔 결과를 다시 보낸다. 다음 번호는 처리된다.
        await active.SendAsync(new EndTurnMessage { RequestId = 2, TurnNumber = turnNumber + 1 });
        JsonElement resent = await active.NextAsync("CommandResult");
        Assert.Equal(2, resent.GetProperty("RequestId").GetInt32());
        Assert.True(resent.GetProperty("Success").GetBoolean());
        await active.SendAsync(new EndTurnMessage { RequestId = 3, TurnNumber = turnNumber });
        JsonElement stale = await active.NextAsync("CommandResult");
        Assert.Equal(3, stale.GetProperty("RequestId").GetInt32());
        Assert.Equal(CommandOutcome.StaleTurn, stale.GetProperty("Reason").GetString());

        // 받은 스냅샷으로 경기를 다시 만들 수 있고, 턴 번호가 서버를 따라간다.
        Assert.False(aStart.TryGetProperty("Seed", out _));
        GameSnapshot snapshot = ReadSnapshot(a.LatestUpdate!.Value);
        Assert.Equal(turnNumber + 1, snapshot.TurnNumber);
        var restored = OnlineMatch.Restore(OnlineMatch.CreateConfig(), snapshot);
        Assert.Equal(8, restored.State.AllUnits().Count(unit => unit.IsOnBoard));

        // 기다리던 쪽이 끊기면 턴을 가진 쪽 승리로 끝난다.
        await waiting.DisposeAsync();
        JsonElement ended = await active.NextEventAsync("GameEndedEvent");
        Assert.Equal(activeTeam, ended.GetProperty("Winner").GetString());
        Assert.Equal("Disconnect", ended.GetProperty("Reason").GetString());
    }

    // 숨김 규칙이 실제 전송에도 적용되는지: 상대 배치는 전투 전까지, 덫은 상대에게 위치 없이 스킬 사용만 간다.
    [Fact]
    public async Task OpponentPlacementAndTrap_AreNotSentToOpponent()
    {
        var (a, b, aStart, bStart) = await StartMatchAsync(_factory);
        await using var _a = a;
        await using var _b = b;

        await a.SendAsync(Placement(aStart, requestId: 1));
        await a.NextAsync("CommandResult");
        JsonElement bView = (await NextUpdateWithAsync(b, "UnitsPlacedEvent")).GetProperty("Snapshot");
        Assert.Empty(bView.GetProperty("Units").EnumerateArray());

        await b.SendAsync(Placement(bStart, requestId: 1));
        await b.NextAsync("CommandResult");
        JsonElement turnStarted = await a.NextEventAsync("TurnStartedEvent");
        await b.NextEventAsync("TurnStartedEvent");
        Assert.Equal(8, b.LatestUpdate!.Value.GetProperty("Snapshot").GetProperty("Units").GetArrayLength());

        bool aFirst = turnStarted.GetProperty("Team").GetString() == aStart.GetProperty("YourTeam").GetString();
        TestClient active = aFirst ? a : b;
        TestClient waiting = aFirst ? b : a;
        JsonElement result = await UseTrapAsync(active, turnStarted, aFirst ? aStart : bStart);
        Assert.True(result.GetProperty("Success").GetBoolean(), result.GetProperty("Reason").GetString());

        JsonElement mine = await NextUpdateWithAsync(active, "SkillUsedEvent");
        JsonElement theirs = await NextUpdateWithAsync(waiting, "SkillUsedEvent");
        Assert.Contains("CellEffectPlacedEvent", EventTypesOf(mine));
        Assert.Equal(new[] { "SkillUsedEvent" }, EventTypesOf(theirs));
        Assert.Empty(theirs.GetProperty("Events")[0].GetProperty("Payload").GetProperty("Targets").EnumerateArray());
        Assert.Single(mine.GetProperty("Snapshot").GetProperty("CellEffects").EnumerateArray());
        Assert.Empty(theirs.GetProperty("Snapshot").GetProperty("CellEffects").EnumerateArray());
    }

    #endregion

    #region Hardening

    [Fact]
    public async Task WrongProtocolVersion_IsRejected()
    {
        await using TestClient client = await ConnectAsync(_factory);

        await client.SendAsync(new JoinMatchMessage { ProtocolVersion = ProtocolInfo.Version + 1, ConfigHash = OnlineMatch.ConfigHash() });

        await client.NextAsync("Error");
    }

    [Fact]
    public async Task DifferentConfigHash_IsRejected()
    {
        await using TestClient client = await ConnectAsync(_factory);

        await client.SendAsync(new JoinMatchMessage { ProtocolVersion = ProtocolInfo.Version, ConfigHash = "other build" });

        Assert.Contains("설정", (await client.NextAsync("Error")).GetProperty("Message").GetString());
    }

    // 잘못된 메시지는 그 메시지만 거절하고, 방과 연결은 계속 동작한다.
    [Theory]
    [InlineData("{\"Type\":\"Move\",\"Payload\":{\"Destination\":{}}}")]
    [InlineData("{\"Type\":\"UseSkill\",\"Payload\":{\"Rotation\":7}}")]
    [InlineData("{\"Type\":\"Move\",\"Payload\":{\"UnitId\":\"x\"}}")]
    [InlineData("not json")]
    public async Task MalformedMessage_IsRejected_AndTheMatchGoesOn(string malformed)
    {
        var (a, b, aStart, bStart) = await StartMatchAsync(_factory);
        await using var _a = a;
        await using var _b = b;

        await a.SendRawAsync(malformed);
        await a.NextAsync("Error");

        await a.SendAsync(Placement(aStart, requestId: 1));
        Assert.True((await a.NextAsync("CommandResult")).GetProperty("Success").GetBoolean());
        await b.SendAsync(Placement(bStart, requestId: 1));
        Assert.True((await b.NextAsync("CommandResult")).GetProperty("Success").GetBoolean());
    }

    [Fact]
    public async Task Ping_IsAnsweredWithPong()
    {
        await using TestClient client = await ConnectAsync(_factory);
        await client.SendAsync(new JoinMatchMessage { ProtocolVersion = ProtocolInfo.Version, ConfigHash = OnlineMatch.ConfigHash() });

        await client.SendAsync(new PingMessage());

        await client.NextAsync("Pong");
    }

    // 턴 시간이 다 되면 서버가 그 팀의 턴을 끝낸다.
    [Fact]
    public async Task TurnTimeout_EndsTheTurn()
    {
        WebApplicationFactory<Program> server = WithOptions(options => options.TurnTime = TimeSpan.FromMilliseconds(300));
        var (a, b, aStart, bStart) = await StartMatchAsync(server);
        await using var _a = a;
        await using var _b = b;
        await a.SendAsync(Placement(aStart, requestId: 1));
        await b.SendAsync(Placement(bStart, requestId: 1));
        JsonElement started = await a.NextEventAsync("TurnStartedEvent");

        JsonElement ended = await a.NextEventAsync("TurnEndedEvent");

        Assert.Equal(started.GetProperty("Team").GetString(), ended.GetProperty("Team").GetString());
        await b.NextEventAsync("TurnEndedEvent");
    }

    // 아무것도 보내지 않는(반쯤 끊긴) 연결은 제한 시간 뒤 끊긴 것으로 보고, 상대가 이긴다.
    [Fact]
    public async Task SilentConnection_TimesOut_AndOpponentWins()
    {
        WebApplicationFactory<Program> server = WithOptions(options => options.ConnectionTimeout = TimeSpan.FromMilliseconds(600));
        var (a, b, _, bStart) = await StartMatchAsync(server);
        await using var _a = a;
        await using var _b = b;
        using var keepAlive = new CancellationTokenSource();
        Task pinging = b.PingUntilAsync(TimeSpan.FromMilliseconds(150), keepAlive.Token);

        JsonElement ended = await b.NextEventAsync("GameEndedEvent");

        keepAlive.Cancel();
        await pinging;
        Assert.Equal(bStart.GetProperty("YourTeam").GetString(), ended.GetProperty("Winner").GetString());
        Assert.Equal("Disconnect", ended.GetProperty("Reason").GetString());
    }

    // 코어가 명령 처리 중 예외를 던지면 경기를 무효 처리하고 양쪽에 알린다.
    [Fact]
    public async Task CoreException_AbortsTheMatch()
    {
        WebApplicationFactory<Program> server = WithOptions(options => options.CreateGame = GameWithExplodingTrap);
        var (a, b, aStart, bStart) = await StartMatchAsync(server);
        await using var _a = a;
        await using var _b = b;
        await a.SendAsync(Placement(aStart, requestId: 1));
        await b.SendAsync(Placement(bStart, requestId: 1));
        await a.NextAsync("CommandResult");
        await b.NextAsync("CommandResult");
        JsonElement turnStarted = await a.NextEventAsync("TurnStartedEvent");
        await b.NextEventAsync("TurnStartedEvent");
        bool aFirst = turnStarted.GetProperty("Team").GetString() == aStart.GetProperty("YourTeam").GetString();
        TestClient active = aFirst ? a : b;
        TestClient waiting = aFirst ? b : a;

        JsonElement result = await UseTrapAsync(active, turnStarted, aFirst ? aStart : bStart);

        Assert.Equal(CommandOutcome.ServerError, result.GetProperty("Reason").GetString());
        foreach (TestClient client in new[] { active, waiting })
        {
            JsonElement ended = await client.NextEventAsync("GameEndedEvent");
            Assert.Equal("Aborted", ended.GetProperty("Reason").GetString());
            Assert.Equal(JsonValueKind.Null, ended.GetProperty("Winner").ValueKind);
        }
    }

    private sealed class ExplodingEffect : SkillEffect
    {
        public override void Apply(SkillContext context) => throw new InvalidOperationException("boom");
    }

    /// <summary>화학자의 덫 스킬이 실행 도중 예외를 던지는 경기.</summary>
    private static GameController GameWithExplodingTrap(int seed)
    {
        GameConfig config = OnlineMatch.CreateConfig(seed);
        var skills = new SkillCatalog();
        foreach (SkillDefinition skill in config.Skills.All.Where(skill => skill.Id != ChemistPiece.RootTrapId))
            skills.Add(skill);
        skills.Add(new SkillDefinition(
            ChemistPiece.RootTrapId, "폭발", 1, SkillActionKind.Combat,
            new CellTargeting(2, CellRequirement.Empty), PatternArea.SingleCell, new SkillEffect[] { new ExplodingEffect() }));

        config.Skills = skills;
        return GameController.Create(config);
    }

    #endregion

    #region Helpers

    private WebApplicationFactory<Program> WithOptions(Action<MatchOptions> configure)
    {
        var options = new MatchOptions();
        configure(options);
        return _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddSingleton(options)));
    }

    /// <summary>두 클라이언트를 붙여 경기를 시작한다(팀은 서버가 정한다).</summary>
    private static async Task<(TestClient A, TestClient B, JsonElement AStart, JsonElement BStart)> StartMatchAsync(WebApplicationFactory<Program> server)
    {
        TestClient a = await ConnectAsync(server);
        TestClient b = await ConnectAsync(server);
        await a.SendAsync(new JoinMatchMessage { ProtocolVersion = ProtocolInfo.Version, ConfigHash = OnlineMatch.ConfigHash() });
        await b.SendAsync(new JoinMatchMessage { ProtocolVersion = ProtocolInfo.Version, ConfigHash = OnlineMatch.ConfigHash() });

        JsonElement aStart = await a.NextAsync("MatchStarted");
        JsonElement bStart = await b.NextAsync("MatchStarted");
        Assert.NotEqual(aStart.GetProperty("YourTeam").GetString(), bStart.GetProperty("YourTeam").GetString());
        return (a, b, aStart, bStart);
    }

    /// <summary>현재 턴 팀의 화학자로 시작 구역 바로 앞 칸에 덫을 놓고 결과를 기다린다.</summary>
    private static async Task<JsonElement> UseTrapAsync(TestClient active, JsonElement turnStarted, JsonElement activeStart)
    {
        JsonElement chemist = active.LatestUpdate!.Value.GetProperty("Snapshot").GetProperty("Units").EnumerateArray()
            .Single(unit => unit.GetProperty("CharacterId").GetString() == ChemistPiece.PieceId
                && unit.GetProperty("Team").GetString() == turnStarted.GetProperty("Team").GetString());

        await active.SendAsync(new UseSkillMessage
        {
            RequestId = 2,
            TurnNumber = turnStarted.GetProperty("TurnNumber").GetInt32(),
            UnitId = chemist.GetProperty("Id").GetInt32(),
            SkillId = ChemistPiece.RootTrapId,
            Targets = new List<Position> { Forward(chemist.GetProperty("Position"), activeStart) },
        });
        return await active.NextAsync("CommandResult");
    }

    private static GameSnapshot ReadSnapshot(JsonElement stateUpdate)
    {
        var update = (StateUpdateMessage)ProtocolJson.DecodeServer(
            JsonSerializer.Serialize(new { Type = "StateUpdate", Payload = stateUpdate }))!;
        return update.Snapshot;
    }

    /// <summary>eventType 이벤트가 든 상태 변화가 나올 때까지 앞의 상태 변화를 넘긴다.</summary>
    private static async Task<JsonElement> NextUpdateWithAsync(TestClient client, string eventType)
    {
        while (true)
        {
            JsonElement update = await client.NextAsync("StateUpdate");
            if (EventTypesOf(update).Contains(eventType))
                return update;
        }
    }

    private static string[] EventTypesOf(JsonElement stateUpdate)
    {
        return stateUpdate.GetProperty("Events").EnumerateArray().Select(e => e.GetProperty("Type").GetString()!).ToArray();
    }

    /// <summary>position 과 같은 열에서 시작 구역 바로 앞의 빈 칸(프리셋 7x7 맵: Player1 은 y=2, Player2 는 y=4).</summary>
    private static Position Forward(JsonElement position, JsonElement matchStarted)
    {
        bool bottom = matchStarted.GetProperty("YourTeam").GetString() == nameof(Team.Player1);
        return new Position(position.GetProperty("X").GetInt32(), bottom ? 2 : 4);
    }

    private static PlaceUnitsMessage Placement(JsonElement matchStarted, int requestId)
    {
        JsonElement snapshot = matchStarted.GetProperty("Snapshot");
        string team = matchStarted.GetProperty("YourTeam").GetString()!;
        JsonElement player = snapshot.GetProperty("Players").EnumerateArray().Single(p => p.GetProperty("Team").GetString() == team);
        string[] lineup = { ChemistPiece.PieceId, ArcherPiece.PieceId, GardenerPiece.PieceId, ScythePiece.PieceId };

        return new PlaceUnitsMessage
        {
            RequestId = requestId,
            TurnNumber = snapshot.GetProperty("TurnNumber").GetInt32(),
            Placements = player.GetProperty("SpawnCells").EnumerateArray().Take(lineup.Length)
                .Select((cell, i) => new PlacementEntry
                {
                    CharacterId = lineup[i],
                    Position = new Position(cell.GetProperty("X").GetInt32(), cell.GetProperty("Y").GetInt32()),
                })
                .ToList(),
        };
    }

    private static async Task<TestClient> ConnectAsync(WebApplicationFactory<Program> server)
    {
        WebSocket socket = await server.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri(server.Server.BaseAddress, "ws"), CancellationToken.None);
        return new TestClient(socket);
    }

    /// <summary>
    /// 테스트용 클라이언트. 공용 형식(ProtocolJson)으로 보내고, 받은 메시지는 JSON 그대로 쌓아 두고 원하는 종류를 순서대로 꺼낸다.
    /// </summary>
    private sealed class TestClient : IAsyncDisposable
    {
        private readonly WebSocket _socket;
        private readonly List<JsonElement> _messages = new();
        private readonly Queue<JsonElement> _events = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private bool _disposed;

        /// <summary>마지막으로 받은 경기 상태(MatchStarted 또는 StateUpdate 의 본문).</summary>
        public JsonElement? LatestUpdate { get; private set; }

        public TestClient(WebSocket socket)
        {
            _socket = socket;
        }

        public Task SendAsync(ClientMessage message) => SendRawAsync(ProtocolJson.Encode(message));

        public async Task SendRawAsync(string json)
        {
            await _sendLock.WaitAsync();
            try
            {
                await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task PingUntilAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    await SendAsync(new PingMessage());
                    await Task.Delay(interval, cancellationToken);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
            }
        }

        /// <summary>type 메시지 중 가장 먼저 온 것의 본문. 이벤트는 NextEventAsync 로도 읽을 수 있다.</summary>
        public async Task<JsonElement> NextAsync(string type)
        {
            while (true)
            {
                int index = _messages.FindIndex(m => m.GetProperty("Type").GetString() == type);
                if (index >= 0)
                {
                    JsonElement found = _messages[index];
                    _messages.RemoveAt(index);
                    return found.GetProperty("Payload");
                }

                await ReceiveAsync();
            }
        }

        /// <summary>type 이벤트가 나올 때까지 이벤트를 순서대로 넘기고, 그 이벤트 본문을 돌려준다.</summary>
        public async Task<JsonElement> NextEventAsync(string type)
        {
            while (true)
            {
                while (_events.Count > 0)
                {
                    JsonElement envelope = _events.Dequeue();
                    if (envelope.GetProperty("Type").GetString() == type)
                        return envelope.GetProperty("Payload");
                }

                await ReceiveAsync();
            }
        }

        private async Task ReceiveAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var buffer = new byte[64 * 1024];
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, timeout.Token);
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            JsonElement message = JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
            string? type = message.GetProperty("Type").GetString();
            JsonElement payload = message.GetProperty("Payload");
            if (type == "StateUpdate")
            {
                foreach (JsonElement envelope in payload.GetProperty("Events").EnumerateArray())
                    _events.Enqueue(envelope);
            }

            if (type is "StateUpdate" or "MatchStarted")
                LatestUpdate = payload;

            _messages.Add(message);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            try
            {
                if (_socket.State == WebSocketState.Open)
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or WebSocketException or ObjectDisposedException)
            {
                // 서버가 먼저 닫았다(경기 종료/거절).
            }

            _socket.Dispose();
        }
    }

    #endregion
}
