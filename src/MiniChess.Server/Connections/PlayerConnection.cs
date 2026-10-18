using System.Net.WebSockets;
using System.Text;
using MiniChess.Protocol;
using MiniChess.Server.Protocol;

namespace MiniChess.Server.Connections;

/// <summary>
/// 클라이언트 WebSocket 연결 하나. 텍스트 메시지 단위로 주고받는다.
/// 보내기는 여러 곳(자기 명령 결과, 상대 행동 이벤트)에서 동시에 일어날 수 있어 한 번에 하나씩 보낸다.
/// </summary>
public sealed class PlayerConnection
{
    private const int MaxMessageBytes = 64 * 1024;

    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public PlayerConnection(WebSocket socket)
    {
        _socket = socket;
    }

    /// <summary>다음 텍스트 메시지. 연결이 끊겼거나 닫혔으면 null.</summary>
    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();

        try
        {
            while (true)
            {
                WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                    return null;

                if (result.EndOfMessage)
                    return result.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(message.ToArray()) : null;
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            return null;
        }
    }

    public async Task SendAsync(ServerMessage message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(ProtocolJson.Encode(message));

        await _sendLock.WaitAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // 상대가 이미 끊겼다. 끊김 처리는 받기 쪽에서 한다.
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(string reason)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
