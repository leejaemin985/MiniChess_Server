using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MiniChess.Core.Characters.Pieces;
using MiniChess.Core.Common;
using MiniChess.Protocol;
using MiniChess.Server.Protocol;

namespace MiniChess.Server.Tests;

/// <summary>실제 WebSocket 으로 두 클라이언트를 붙여 경기 흐름(참가 → 배치 → 턴 종료 → 끊김)을 확인한다.</summary>
public class MatchFlowTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public MatchFlowTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TwoPlayers_Join_Place_EndTurn_ThenDisconnectEndsGame()
    {
        await using TestClient a = await ConnectAsync();
        await using TestClient b = await ConnectAsync();
        await a.SendAsync(new JoinMatchMessage { ProtocolVersion = ProtocolInfo.Version });
        await b.SendAsync(new JoinMatchMessage { ProtocolVersion = ProtocolInfo.Version });

        // 팀은 서버가 정한다(먼저 처리된 참가가 Player1).
        JsonElement aStart = await a.NextAsync("MatchStarted");
        JsonElement bStart = await b.NextAsync("MatchStarted");
        Assert.NotEqual(aStart.GetProperty("YourTeam").GetString(), bStart.GetProperty("YourTeam").GetString());

        // 배치: 각자 자기 시작 칸 4곳에 놓는다(동시 제출).
        await a.SendAsync(Placement(aStart, requestId: 1));
        await b.SendAsync(Placement(bStart, requestId: 1));
        Assert.True((await a.NextAsync("CommandResult")).GetProperty("Success").GetBoolean());
        Assert.True((await b.NextAsync("CommandResult")).GetProperty("Success").GetBoolean());

        // 두 팀 배치가 끝나면 전투가 시작된다.
        JsonElement turnStarted = await a.NextEventAsync("TurnStartedEvent");
        await b.NextEventAsync("TurnStartedEvent");
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

        // 같은 요청 번호를 다시 보내면 무시된다(결과도 오지 않는다). 다음 번호는 처리된다.
        await active.SendAsync(new EndTurnMessage { RequestId = 2, TurnNumber = turnNumber + 1 });
        await active.SendAsync(new EndTurnMessage { RequestId = 3, TurnNumber = turnNumber });
        JsonElement stale = await active.NextAsync("CommandResult");
        Assert.Equal(3, stale.GetProperty("RequestId").GetInt32());
        Assert.Equal(CommandOutcome.StaleTurn, stale.GetProperty("Reason").GetString());

        // 기다리던 쪽이 끊기면 턴을 가진 쪽 승리로 끝난다.
        await waiting.DisposeAsync();
        JsonElement ended = await active.NextEventAsync("GameEndedEvent");
        Assert.Equal(activeTeam, ended.GetProperty("Winner").GetString());
        Assert.Equal("Disconnect", ended.GetProperty("Reason").GetString());
    }

    [Fact]
    public async Task WrongProtocolVersion_IsRejected()
    {
        await using TestClient client = await ConnectAsync();

        await client.SendAsync(new JoinMatchMessage { ProtocolVersion = ProtocolInfo.Version + 1 });

        await client.NextAsync("Error");
    }

    private static PlaceUnitsMessage Placement(JsonElement matchStarted, int requestId)
    {
        JsonElement snapshot = matchStarted.GetProperty("Snapshot");
        string team = matchStarted.GetProperty("YourTeam").GetString()!;
        JsonElement player = snapshot.GetProperty("Players").EnumerateArray().Single(p => p.GetProperty("Team").GetString() == team);
        string[] lineup = { WarriorPiece.PieceId, ArcherPiece.PieceId, GardenerPiece.PieceId, ScythePiece.PieceId };

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

    private async Task<TestClient> ConnectAsync()
    {
        WebSocket socket = await _factory.Server.CreateWebSocketClient()
            .ConnectAsync(new Uri(_factory.Server.BaseAddress, "ws"), CancellationToken.None);
        return new TestClient(socket);
    }

    /// <summary>
    /// 테스트용 클라이언트. 서버와 같은 JSON 형식으로 보낸다.
    /// 받은 메시지는 쌓아 두고, 원하는 종류를 순서대로 꺼낸다(상대 행동 이벤트와 내 결과의 도착 순서는 정해져 있지 않다).
    /// </summary>
    private sealed class TestClient : IAsyncDisposable
    {
        private readonly WebSocket _socket;
        private readonly List<JsonElement> _messages = new();
        private readonly Queue<JsonElement> _events = new();
        private bool _disposed;

        public TestClient(WebSocket socket)
        {
            _socket = socket;
        }

        public Task SendAsync(ClientMessage message)
        {
            string json = JsonSerializer.Serialize(new
            {
                Type = MessageTypes.NameOf(message.GetType()),
                Payload = (object)message,
            }, ProtocolJson.Options);
            return _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        /// <summary>type 메시지 중 가장 먼저 온 것의 본문. 이벤트 메시지는 NextEventAsync 로 읽는다.</summary>
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
            if (message.GetProperty("Type").GetString() == "Events")
            {
                foreach (JsonElement envelope in message.GetProperty("Payload").GetProperty("Events").EnumerateArray())
                    _events.Enqueue(envelope);
            }
            else
            {
                _messages.Add(message);
            }
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
}
