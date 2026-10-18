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

    /// <summary>로그에 남길 메시지 본문의 최대 길이. 넘으면 잘라서 남긴다(이벤트 묶음 등).</summary>
    private const int MaxLoggedChars = 400;

    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public PlayerConnection(WebSocket socket, ILogger<PlayerConnection> logger)
    {
        _socket = socket;
        _logger = logger;
        _logger.LogInformation("[{Connection}] connected", Id);
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
                {
                    _logger.LogInformation("[{Connection}] closed by client", Id);
                    return null;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                    return null;

                if (!result.EndOfMessage)
                    continue;

                if (result.MessageType != WebSocketMessageType.Text)
                    return null;

                string text = Encoding.UTF8.GetString(message.ToArray());
                _logger.LogInformation("[{Connection}] <= {Message}", Id, Shorten(text));
                return text;
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
        {
            _logger.LogInformation("[{Connection}] connection lost: {Reason}", Id, exception.Message);
            return null;
        }
    }

    public async Task SendAsync(ServerMessage message)
    {
        string json = ProtocolJson.Encode(message);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        _logger.LogInformation("[{Connection}] => {Message}", Id, Shorten(json));

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
        _logger.LogInformation("[{Connection}] closing: {Reason}", Id, reason);
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

    private static string Shorten(string text) => text.Length <= MaxLoggedChars ? text : text[..MaxLoggedChars] + $"... ({text.Length} chars)";
}
