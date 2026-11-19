namespace VortexArena.Server;

public sealed partial class Commands
{
    private MatrixChatBridge? _matrix;

    // The native host calls this before the dedicated world starts ticking.
    public void EnableDedicatedMatrix()
    {
        var cvars = _world.Services.Cvars;
        cvars.Register("matrix_server", MatrixChatBridge.DefaultServer);
        _matrix ??= new MatrixChatBridge(() => cvars.GetString("matrix_server"),
            body => Chat.Say(null, 0, null, body, floodcontrol: false));
        Register("mx", "Send a matrix command", _matrix.Execute);
    }

    internal bool MatrixCanRelay => _matrix?.CanRelay == true;
    internal void PumpMatrix() => _matrix?.Pump();
    internal void SendMatrixChat(string body) => _matrix?.SendChat(body);

    internal void ShutdownMatrix()
    {
        if (_matrix is null)
            return;
        Unregister("mx");
        _matrix.Dispose();
        _matrix = null;
    }

    public static bool IsMatrixTokenCommand(string line)
    {
        string[] args = Tokenize(line);
        int index = args.Length > 0 && args[0].Equals("sv_cmd", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return args.Length > index + 1 && args[index].Equals("mx", StringComparison.OrdinalIgnoreCase)
            && args[index + 1].Equals("token", StringComparison.OrdinalIgnoreCase);
    }
}
