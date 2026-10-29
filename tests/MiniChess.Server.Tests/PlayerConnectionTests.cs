using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MiniChess.Protocol;
using MiniChess.Server.Connections;
using MiniChess.Server.Matches;

namespace MiniChess.Server.Tests;

/// <summary>연결 하나의 보내기 큐: 닫기 전에 넣은 메시지는 모두 나가고, 닫은 뒤의 Send 는 연결을 끊지 않는다.</summary>
public class PlayerConnectionTests
{
    // 경기 종료(Close) 직후 상대 Ping 의 응답(Send)이 오면, 앞서 넣은 경기 종료 메시지를 보내기 전에 연결을 끊으면 안 된다.
    [Fact]
    public async Task SendAfterClose_IsDropped_AndQueuedMessagesStillGoOut()
    {
        var socket = new SlowSocket(TimeSpan.FromMilliseconds(100));
        var connection = new PlayerConnection(socket, NullLogger<PlayerConnection>.Instance, new MatchOptions());

        connection.Send(new ErrorMessage { Message = "game ended" });
        connection.Close("game over");
        connection.Send(new PongMessage());
        await connection.FlushAsync();

        Assert.False(socket.Aborted);
        Assert.Single(socket.Sent);
        Assert.Contains("game ended", socket.Sent[0]);
        Assert.Equal("game over", socket.ClosedWith);
    }

    /// <summary>보내기마다 지연이 있는 소켓. 보낸 메시지와 닫기/강제 종료를 기록한다.</summary>
    private sealed class SlowSocket : WebSocket
    {
        private readonly TimeSpan _delay;
        private WebSocketState _state = WebSocketState.Open;

        public List<string> Sent { get; } = new();
        public string? ClosedWith { get; private set; }
        public bool Aborted { get; private set; }

        public SlowSocket(TimeSpan delay)
        {
            _delay = delay;
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => ClosedWith;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            await Task.Delay(_delay, cancellationToken);
            Sent.Add(Encoding.UTF8.GetString(buffer));
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            ClosedWith = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Abort()
        {
            Aborted = true;
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override void Dispose()
        {
        }
    }
}
