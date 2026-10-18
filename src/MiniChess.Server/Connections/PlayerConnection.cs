using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using MiniChess.Protocol;
using MiniChess.Server.Matches;

namespace MiniChess.Server.Connections;

/// <summary>
/// 클라이언트 WebSocket 연결 하나. 텍스트 메시지 단위로 주고받는다.
///   - 보내기: Send 는 보낼 메시지를 큐에 넣기만 하고 바로 돌아온다(경기 잠금을 쥔 채 네트워크를 기다리지 않는다).
///     보내기 루프가 하나씩 보내며, 제한 시간 안에 못 보내거나 큐가 넘치면(상대가 받지 않음) 연결을 끊는다.
///   - 받기: 제한 시간(MatchOptions.ConnectionTimeout) 동안 아무 메시지도 오지 않으면 끊긴 것으로 본다.
/// 메시지 본문은 Debug 로그로만 남긴다(appsettings 에서 "MiniChess.Server.Connections": "Debug" 로 켠다).
/// </summary>
public sealed class PlayerConnection
{
    /// <summary>로그에 남길 메시지 본문의 최대 길이. 넘으면 잘라서 남긴다.</summary>
    private const int MaxLoggedChars = 400;

    /// <summary>보내지 못하고 쌓인 메시지가 이만큼이면 상대가 받지 않는 것으로 보고 끊는다.</summary>
    private const int MaxQueuedMessages = 256;

    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly MatchOptions _options;
    private readonly Channel<Outgoing> _outbox = Channel.CreateBounded<Outgoing>(MaxQueuedMessages);
    private readonly Task _sendLoop;

    /// <summary>보낼 것 하나: 메시지(Json) 또는 닫기 요청(CloseReason).</summary>
    private readonly record struct Outgoing(string? Json, string? CloseReason);

    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public PlayerConnection(WebSocket socket, ILogger<PlayerConnection> logger, MatchOptions options)
    {
        _socket = socket;
        _logger = logger;
        _options = options;
        _logger.LogInformation("[{Connection}] connected", Id);
        _sendLoop = Task.Run(SendLoopAsync);
    }

    /// <summary>다음 텍스트 메시지. 연결이 끊겼거나, 닫혔거나, 제한 시간 동안 아무것도 오지 않았으면 null.</summary>
    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConnectionTimeout);

        try
        {
            while (true)
            {
                WebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("[{Connection}] closed by client", Id);
                    return null;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > ProtocolInfo.MaxMessageBytes)
                {
                    _logger.LogWarning("[{Connection}] message too large ({Bytes} bytes)", Id, message.Length);
                    return null;
                }

                if (!result.EndOfMessage)
                    continue;

                if (result.MessageType != WebSocketMessageType.Text)
                    return null;

                string text = Encoding.UTF8.GetString(message.ToArray());
                _logger.LogDebug("[{Connection}] <= {Message}", Id, Shorten(text));
                return text;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("[{Connection}] timed out: nothing received for {Seconds}s", Id, _options.ConnectionTimeout.TotalSeconds);
            return null;
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogInformation("[{Connection}] connection lost: {Reason}", Id, exception.Message);
            return null;
        }
    }

    /// <summary>메시지를 보낼 큐에 넣는다. 큐가 넘치면(상대가 받지 않음) 연결을 끊는다.</summary>
    public void Send(ServerMessage message)
    {
        string json = ProtocolJson.Encode(message);
        _logger.LogDebug("[{Connection}] => {Message}", Id, Shorten(json));

        if (!_outbox.Writer.TryWrite(new Outgoing(json, null)))
            Abort("send queue full");
    }

    /// <summary>앞서 넣은 메시지를 모두 보낸 뒤 닫는다.</summary>
    public void Close(string reason)
    {
        _outbox.Writer.TryWrite(new Outgoing(null, reason));
        _outbox.Writer.TryComplete();
    }

    /// <summary>보내기 루프가 남은 메시지를 다 보낼 때까지 기다린다(연결 처리를 끝내기 전에 호출).</summary>
    public async Task FlushAsync()
    {
        _outbox.Writer.TryComplete();
        await Task.WhenAny(_sendLoop, Task.Delay(_options.SendTimeout));
    }

    private async Task SendLoopAsync()
    {
        await foreach (Outgoing outgoing in _outbox.Reader.ReadAllAsync())
        {
            using var timeout = new CancellationTokenSource(_options.SendTimeout);
            try
            {
                if (_socket.State != WebSocketState.Open && _socket.State != WebSocketState.CloseReceived)
                    continue;

                if (outgoing.Json != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(outgoing.Json);
                    await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeout.Token);
                }
                else
                {
                    _logger.LogInformation("[{Connection}] closing: {Reason}", Id, outgoing.CloseReason);
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, outgoing.CloseReason, timeout.Token);
                }
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // 상대가 받지 않거나 이미 끊겼다. 끊김 처리는 받기 쪽(ReceiveAsync → null)에서 한다.
                Abort($"send failed: {exception.Message}");
                return;
            }
        }
    }

    private void Abort(string reason)
    {
        _logger.LogInformation("[{Connection}] aborting: {Reason}", Id, reason);
        _outbox.Writer.TryComplete();
        _socket.Abort();
    }

    private static string Shorten(string text) => text.Length <= MaxLoggedChars ? text : text[..MaxLoggedChars] + $"... ({text.Length} chars)";
}
