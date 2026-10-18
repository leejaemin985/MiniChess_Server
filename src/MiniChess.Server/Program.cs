using MiniChess.Server.Connections;
using MiniChess.Server.Matches;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<Lobby>();

WebApplication app = builder.Build();
app.UseWebSockets();

app.MapGet("/", () => "MiniChess server");

// 클라이언트 접속 지점. 메시지 형식은 ProtocolJson 참고.
app.Map("/ws", async (HttpContext context, Lobby lobby) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await lobby.RunAsync(new PlayerConnection(socket), context.RequestAborted);
});

app.Run();

// 통합 테스트(WebApplicationFactory)에서 참조한다.
public partial class Program;
