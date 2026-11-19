"""Observe chase state and applied model alpha in a disposable native client build."""
import sys
from pathlib import Path

project = Path(sys.argv[1])


def insert(file, anchor, addition):
    path = project / file
    source = path.read_text()
    assert source.count(anchor) == 1, (file, anchor)
    path.write_text(source.replace(anchor, anchor + addition))


insert("game/hud/CrosshairPanel.cs", "    internal bool ClassicChase { get; set; }\n", """
    // Diagnostic only. The panel uses its ordinary centre when it has no projected point.
    internal string FollowProbePosition => _chaseScreenPoint?.ToString() ?? "centre";
""")
insert("game/client/PlayerModel.cs", "    private float _lastAlphaApplied = float.NaN;\n",
       "    internal float FollowProbeAlpha => 1f - _lastAlphaApplied;\n")
insert("game/client/ClientWorld.cs", "    private float _chaseBodyTargetAlpha = 1f;\n", """
    internal string FollowProbe(int previousFollowId)
    {
        float alpha = _playerModels.TryGetValue(previousFollowId, out PlayerModel? body)
            ? body.FollowProbeAlpha : float.NaN;
        return System.FormattableString.Invariant($"fade={_chaseBodyId} alpha={alpha:F3}");
    }
""")
insert("game/net/NetGame.cs", "            UpdateCrosshairGate();\n", """
            string probeLabel = ClientViewCvars.GetString("crosshair_follow_probe");
            if (probeLabel.Length != 0 && probeLabel != _lastFollowProbe)
            {
                _lastFollowProbe = probeLabel;
                int followed = _client.SpectatingNetId;
                if (followed != 0)
                    _lastFollowProbeId = followed;
                var panel = _fullHud.Crosshair;
                bool sampled = followed != 0 && _client.SampleRemote(followed, _client.LatestServerTime, out _, out _);
                bool raw = followed != 0 && _client.TryGetViewedState(followed, out _);
                GD.Print($"FOLLOW_PROBE {probeLabel} local={_client.LocalNetId} follow={followed} "
                    + $"nolerp={_client.NoLerp} sample={sampled} raw={raw} camera={_view.Spectating} "
                    + $"visible={panel.Visible} anchor={panel.FollowProbePosition} {_render.FollowProbe(_lastFollowProbeId)}");
            }
""")
insert("game/net/NetGame.cs", "    private Player? _lastHudPlayer;\n", """
    private string _lastFollowProbe = "";
    private int _lastFollowProbeId;
""")


insert("game/net/ClientNet.cs",
       "    public bool SampleRemote(int netId, float now, out NVec3 origin, out NVec3 angles)\n    {\n", """
        // Withhold the interpolated pose from the follow consumers.
        if (SpectatingNetId != 0 && netId == SpectatingNetId
            && Menu.MenuState.Cvars.GetString("crosshair_follow_drop_pose") == "1")
        {
            origin = default;
            angles = default;
            return false;
        }
""")

insert("game/net/ClientNet.cs",
       "    public bool TryGetViewedState(int netId, out NetEntityState state)\n    {\n", """
        // Withhold the raw view pose too. The graphical entity stream keeps the model
        // available so the fixture can read its restored opacity during the outage.
        if (SpectatingNetId != 0 && netId == SpectatingNetId
            && Menu.MenuState.Cvars.GetString("crosshair_follow_drop_pose") == "1")
        {
            state = default;
            return false;
        }
""")
