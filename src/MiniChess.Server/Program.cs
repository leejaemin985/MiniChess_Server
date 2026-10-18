using MiniChess.Server.Connections;
using MiniChess.Server.Matches;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// 한 줄 로그 + 시각. 접속/끊김, 경기 생성, 명령 결과가 남는다.
// 주고받은 메시지 본문까지 보려면 appsettings 의 Logging:LogLevel 에 "MiniChess.Server.Connections": "Debug" 를 넣는다.
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss.fff ";
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Services.AddSingleton<MatchOptions>();
builder.Services.AddSingleton<Lobby>();

WebApplication app = builder.Build();
app.UseWebSockets();

app.MapGet("/", () => "MiniChess server");

// 클라이언트 접속 지점. 메시지 형식은 MiniChess.Protocol.ProtocolJson 참고.
app.Map("/ws", async (HttpContext context, Lobby lobby, MatchOptions options, ILogger<PlayerConnection> connectionLogger) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connection = new PlayerConnection(socket, connectionLogger, options);
    await lobby.RunAsync(connection, context.RequestAborted);
    await connection.FlushAsync();
});

app.Run();

// 통합 테스트(WebApplicationFactory)에서 참조한다.
public partial class Program;
